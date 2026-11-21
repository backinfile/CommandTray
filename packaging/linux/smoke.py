"""Focused smoke check for a published Linux binary."""
import json
import os
from pathlib import Path
import select
import shutil
import socket
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


binary = str(Path(sys.argv[1]).resolve())


def eventually(check, seconds=8):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if check():
            return
        time.sleep(.06)
    raise AssertionError("condition not reached in time")


with tempfile.TemporaryDirectory(prefix="ct-linux-") as root:
    env = {**os.environ, "COMMANDTRAY_HOME": root}

    def command(*args):
        return subprocess.check_output([binary, *args], env=env, text=True)

    def alive(pid):
        try:
            return Path(f"/proc/{pid}/stat").read_text().split(") ", 1)[1][0] != "Z"
        except FileNotFoundError:
            return False

    daemon = subprocess.Popen([binary, "daemon"], env=env)
    decoy = subprocess.Popen(["sleep", "45"])
    try:
        control = Path(root) / "run" / "control.sock"
        eventually(control.exists)
        assert stat.S_IMODE(control.stat().st_mode) == 0o600
        command("workspace", "create", "Smoke")
        component = json.dumps({"id": "note", "kind": "note", "x": 5, "y": 6,
                                "width": 150, "height": 100, "data": {"text": "local draft"}})
        command("workspace", "put-item", "Smoke", component)
        assert "local draft" in command("workspace", "show", "Smoke")

        command("service", "create", "probe", "--cwd", root, "--stop-timeout", "1", "--",
                "/bin/sh", "-c", "sleep 45 & echo $!; wait")
        command("service", "start", "probe")
        eventually(lambda: command("service", "logs", "probe").strip().isdigit())
        child = int(command("service", "logs", "probe"))
        command("service", "stop", "probe")
        eventually(lambda: not alive(child))
        assert decoy.poll() is None
        assert not json.loads(command("service", "get", "probe"))["running"]

        terminal = json.loads(command("terminal", "open", root, "/bin/sh"))["id"]
        command("terminal", "write", terminal, "printf 'pty-%s\\n' works\n")
        eventually(lambda: "pty-works" in command("terminal", "read", terminal))
        command("terminal", "close", terminal)

        if ssh_keygen := shutil.which("ssh-keygen"):
            key = Path(root) / "fixture-key"
            subprocess.run([ssh_keygen, "-q", "-t", "ed25519", "-N", "", "-f", str(key)], check=True)
            env["COMMANDTRAY_VAULT_PASSPHRASE"] = "test-only passphrase"
            command("identity", "import", "fixture", str(key))
            stored = command("workspace", "get")
            assert "-----BEGIN OPENSSH PRIVATE KEY-----" not in stored
            assert "sshKeyEnvelopes" in stored
            command("identity", "install", "fixture")
            assert json.loads(command("identity", "list"))[0]["installed"]

        mcp = subprocess.Popen([binary, "mcp", "serve", "--stdio"], env=env, text=True,
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            def rpc(msg):
                mcp.stdin.write(json.dumps({"jsonrpc": "2.0", **msg}) + "\n")
                mcp.stdin.flush()
                if "id" not in msg:
                    return None
                assert select.select([mcp.stdout], [], [], 8)[0], msg
                response = json.loads(mcp.stdout.readline())
                assert response["id"] == msg["id"] and "error" not in response, response
                return response["result"]

            rpc({"id": 1, "method": "initialize", "params": {"protocolVersion": "2025-11-25",
                 "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}}})
            rpc({"method": "notifications/initialized"})
            tools = rpc({"id": 2, "method": "tools/list"})["tools"]
            assert {"list_services", "open_terminal", "read_workspace", "list_shared_files"}.issubset(
                t["name"] for t in tools)
            response = rpc({"id": 3, "method": "tools/call", "params": {"name": "read_workspace", "arguments": {}}})
            assert not response.get("isError") and "local draft" in json.dumps(response)
        finally:
            mcp.terminate()
            mcp.wait(timeout=8)

        with socket.socket() as reserve:
            reserve.bind(("127.0.0.1", 0))
            port = reserve.getsockname()[1]

        command("config", "set", "mcp-port", str(port))
        token = command("mcp", "token").strip()
        listener = subprocess.Popen([binary, "mcp", "serve", "--http"], env=env)
        try:
            endpoint = f"http://127.0.0.1:{port}/mcp"
            message = json.dumps({"id": 7, "jsonrpc": "2.0", "method": "initialize",
                                  "params": {"protocolVersion": "2025-11-25", "capabilities": {},
                                  "clientInfo": {"name": "smoke", "version": "1"}}}).encode()

            def status(headers=None):
                all_headers = {"Accept": "application/json,text/event-stream", "Content-Type": "application/json"}
                all_headers.update(headers or {})
                request = urllib.request.Request(endpoint, data=message, headers=all_headers)
                try:
                    with urllib.request.urlopen(request, timeout=1) as response:
                        return response.status
                except urllib.error.HTTPError as error:
                    return error.code
                except urllib.error.URLError:
                    return None

            eventually(lambda: status() == 401)
            assert status({"Origin": "http://invalid.local", "Authorization": f"Bearer {token}"}) == 403
            assert status({"Authorization": f"Bearer {token}"}) == 200
        finally:
            listener.terminate()
            listener.wait(timeout=8)
    finally:
        decoy.terminate()
        decoy.wait(timeout=8)
        daemon.terminate()
        daemon.wait(timeout=12)

print("Linux smoke passed: private socket, process group, PTY, workspace and local MCP.")
