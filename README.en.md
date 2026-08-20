# CommandTray

[中文](README.md) · [Download](https://github.com/backinfile/CommandTray/releases/latest)

[![Release](https://github.com/backinfile/CommandTray/actions/workflows/release.yml/badge.svg)](https://github.com/backinfile/CommandTray/actions/workflows/release.yml)

CommandTray is a lightweight Windows 10/11 x64 system-tray utility for starting, stopping, and monitoring long-running command-line services.

## Highlights

- Manage multiple CMD commands or local development services from one tray menu.
- Start, stop, restart, open service URLs, and inspect logs.
- Configure working directories, environment variables, stop commands, and stop timeouts.
- Start selected services with CommandTray and restart them after unexpected exits.
- Track only process trees launched by CommandTray; unrelated processes are never killed by name.
- Capture stdout and stderr in rotating log files.
- Encrypt secret environment values with Windows DPAPI for the current user.
- Let trusted AI clients manage services through a built-in local MCP server.

## Download and install

Download the latest version from [Releases](https://github.com/backinfile/CommandTray/releases/latest):

- `CommandTray-X.Y.Z-win-x64-setup.exe`: recommended installer.
- `CommandTray-X.Y.Z-win-x64.zip`: portable build; extract it and run `CommandTray.exe`.
- `SHA256SUMS.txt`: checksums for verifying downloads.

CommandTray stays in the notification area after launch. Closing the settings window only hides it; use **Exit** from the tray menu to quit completely.

## Quick start

1. Start CommandTray and double-click its tray icon.
2. Select **“+ Add / 添加”**.
3. Enter a name and CMD command, then configure any optional fields.
4. Select **“Save / 保存”**.
5. Select **“Start / 启动”**, or start the service from the tray menu.

| Field | Description |
| --- | --- |
| Name | Name shown in the service list and tray menu. |
| CMD command | CMD command used to start the service. |
| Working directory | Directory used for start and stop commands; blank uses CommandTray's current directory. |
| Open URL | Optional HTTP/HTTPS address available from the tray menu. |
| Stop command | Optional command run before termination; `${PID}` is replaced with the process ID. |
| Stop timeout | Graceful-stop wait time before CommandTray terminates the owned process tree. |
| Start with manager | Starts this service when CommandTray starts. |
| Restart on unexpected exit | Restarts unexpected exits; manual stops do not trigger it. |
| Environment | One `NAME=value` entry per line; prefix secret names with `*`. |

## AI / MCP integration

MCP is enabled by default. Clear **“Enable MCP / 启用 MCP”** in the settings window to turn it off. The Streamable HTTP endpoint is:

```text
http://127.0.0.1:58629/mcp
```

A typical MCP client configuration looks like this; consult your client's documentation for its exact schema:

```json
{
  "mcpServers": {
    "commandtray": {
      "url": "http://127.0.0.1:58629/mcp"
    }
  }
}
```

The server binds only to local address `127.0.0.1`. It can inspect, create, update, delete, start, stop, and restart services, and read logs. Secret environment values are never returned, although logs may contain sensitive output written by managed commands. Connect only trusted MCP clients because these tools can execute your configured commands.

## Data and logs

- Configuration: `%LOCALAPPDATA%\CommandTray\config.json`
- Logs: `%LOCALAPPDATA%\CommandTray\logs\<service-id>\current.log`
- A log rotates at 5 MB and retains the four newest historical files.

## Troubleshooting

**Why is CommandTray still running after I close the window?**

It is a tray application. Closing settings hides the window; choose **Exit** from the tray menu to quit.

**Why did a service start again after it stopped?**

Check **Restart on unexpected exit**. Manual stops do not restart; commands that exit on their own use bounded automatic retries.

**How do I protect an API key or token?**

Prefix its environment variable name with `*`, for example `*API_KEY=value`. CommandTray encrypts it with DPAPI for the current Windows user when saving.

## Development

Requires Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet test .\CommandTray.slnx
dotnet run --project .\src\CommandTray\CommandTray.csproj
```

See [`AGENTS.md`](AGENTS.md) for contribution and release rules.
