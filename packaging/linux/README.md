# CommandTray Linux

Install the self-contained archive for the device architecture. The installed binary can run in a shell or a user service. Interactive terminal sessions require util-linux `script`. SSH connections use OpenSSH and its host key checks.

```sh
sh ./packaging/install.sh
systemctl --user enable --now commandtray
~/.local/bin/commandtray --help
```

For a foreground daemon, run `commandtray daemon` in one shell and issue commands from another. Persistent configuration uses `$XDG_STATE_HOME/commandtray` or `~/.local/state/commandtray`. Set `COMMANDTRAY_HOME` to a private dedicated directory to isolate a client and daemon.

```sh
commandtray service create dev --cwd "$PWD" -- /usr/bin/python3 -m http.server 8080
commandtray service start dev
commandtray service logs dev 40
commandtray service stop dev
commandtray terminal open "$PWD" /bin/bash
commandtray terminal list
```

Read or update workspace content with `workspace`, set up the relay with `sync pair <url> <name>`, and copy text or files with `clip` or `share`. Set `COMMANDTRAY_PAIRING_CODE` in the local environment before pairing. Use `sync push` and `sync pull` to exchange workspace changes. A conflicting remote version is written beside the local workspace. `sync pull --replace` archives pending local edits before replacing the workspace.

Use `host put <id> <name> <address> <username> [port] [identity-id]` and `host connect <id>` for SSH. Set `COMMANDTRAY_VAULT_PASSPHRASE` locally for `identity import <id> <path> [name]` and `identity install <id>`. Import stages the encrypted envelope. Install writes the decrypted key to the private device directory for OpenSSH.

## Local MCP

Set the MCP client command to `commandtray mcp serve --stdio`. For a foreground HTTP listener, use `commandtray mcp serve --http` and connect to `http://127.0.0.1:58629/mcp`. Read the device-only bearer token with `commandtray mcp token` and rotate it with `commandtray mcp token --rotate`. Technical logs stay off the standard output MCP stream.
