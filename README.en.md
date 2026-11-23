# CommandTray Suite

[中文](README.md) · [Suite downloads](https://github.com/backinfile/CommandTray/releases?q=suite-v) · [Original Windows CommandTray](https://github.com/backinfile/CommandTray/tree/dev)

[![Suite Release](https://github.com/backinfile/CommandTray/actions/workflows/release-suite.yml/badge.svg)](https://github.com/backinfile/CommandTray/actions/workflows/release-suite.yml)

CommandTray uses one Avalonia UI across Windows, Linux desktop, and Android. Desktop workspaces show multiple folders and terminals together. Android puts notes, checklists, boards, and shared files first. Linux also has a headless CLI.

## Highlights

- Manage multiple CMD commands or local development services from one tray menu.
- Start, stop, restart, open service URLs, and inspect logs.
- Search commands and edit their configuration, startup and shutdown behavior, and environment variables.
- Choose Chinese or English under Settings.
- Search and copy logs, wrap lines, or pause scrolling while output continues to arrive.
- Configure working directories, environment variables, stop commands, and stop timeouts.
- Start selected services with CommandTray and restart them after unexpected exits.
- Track only process trees launched by CommandTray; unrelated processes are never killed by name.
- Capture stdout and stderr in rotating log files.
- Encrypt secret environment values with Windows DPAPI for the current user.
- Let trusted AI clients manage services through a built-in local MCP server.

## Download and install

Choose a tag beginning with `suite-v` from the [suite releases](https://github.com/backinfile/CommandTray/releases?q=suite-v).

- `CommandTray-Suite-X.Y.Z-win-x64-setup.exe` is the Windows installer.
- `CommandTray-Suite-X.Y.Z-win-x64.zip` is the Windows portable build. Extract it and run `CommandTray.exe`.
- The same release includes the Linux CLI and desktop app, the Android APK, and the relay with its Compose configuration.
- `SHA256SUMS.txt`: checksums for verifying downloads.

Current release packages are not yet Authenticode-signed, so Windows may display a SmartScreen warning on first launch. Download only from the official Releases page and verify the included SHA-256 checksums. See the [code signing policy](CODE_SIGNING_POLICY.md) for status.

CommandTray stays in the notification area after launch. Closing the settings window only hides it; use **Exit** from the tray menu to quit completely.

## Quick start

1. Start CommandTray and double-click its tray icon.
2. Open **Commands** and select **Add command**.
3. Enter a name and CMD command, then configure any optional fields.
4. Select **Save**.
5. Start the command from its row or the tray menu.

Choose **English** or **简体中文** under **Settings → Language**. Service names, commands, and log output keep their original text.

Each command row shows its state and a start or stop action. Its menu opens logs, configuration, or the configured service address.

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

MCP is enabled by default. Toggle **Enable local MCP server** under **Application settings → AI connection · MCP**, where you can also copy its address. The Streamable HTTP endpoint is:

```text
http://127.0.0.1:58629/mcp
```

A typical MCP client configuration looks like this; consult your client's documentation for its exact schema:

```json
{
  "mcpServers": {
    "commandtray": {
      "url": "http://127.0.0.1:58629/mcp",
      "headers": {"Authorization": "Bearer <local MCP credential>"}
    }
  }
}
```

The server binds only to local address `127.0.0.1`. It can inspect, create, update, delete, start, stop, and restart services, and read logs. Secret environment values are never returned, although logs may contain sensitive output written by managed commands. Connect only trusted MCP clients because these tools can execute your configured commands.

## Data and logs

- Configuration: `%LOCALAPPDATA%\CommandTray\config.json`
- Logs: `%LOCALAPPDATA%\CommandTray\logs\<service-id>\current.log`
- A log rotates at 5 MB and retains the four newest historical files.

Import or export JSON under **Application settings → Configuration files**. Import replaces the current service list and requires all services to be stopped. Export includes saved configuration only and removes secret environment values.

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

## License and project policies

- [MIT License](LICENSE)
- [Code signing policy](CODE_SIGNING_POLICY.md)
- [Privacy notice](PRIVACY.md)
- [Security policy](SECURITY.md)
