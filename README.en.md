# CommandTray

[中文](README.md) · [Download](https://github.com/backinfile/CommandTray/releases/latest)

[![Release](https://github.com/backinfile/CommandTray/actions/workflows/release.yml/badge.svg)](https://github.com/backinfile/CommandTray/actions/workflows/release.yml)

CommandTray is a lightweight Windows 10/11 x64 system-tray utility for starting, stopping, and monitoring long-running command-line services.

## Highlights

- Manage multiple CMD commands or local development services from one tray menu.
- Start, stop, restart, open service URLs, and inspect logs.
- Search services and edit Configuration, Startup & shutdown, and Environment tabs with validation and unsaved-change prompts.
- Complete Chinese and English interfaces with immediate language switching or system-language selection.
- Search and copy logs, wrap lines, or pause scrolling while output continues to arrive.
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

Current release packages are not yet Authenticode-signed, so Windows may display a SmartScreen warning on first launch. Download only from the official Releases page and verify the included SHA-256 checksums. See the [code signing policy](CODE_SIGNING_POLICY.md) for status.

CommandTray stays in the notification area after launch. Closing the settings window only hides it; use **Exit** from the tray menu to quit completely.

## Quick start

1. Start CommandTray and double-click its tray icon.
2. Select **“+ Add service”**.
3. Enter a name and CMD command, then configure any optional fields.
4. Select **“Save changes”**, or press **Ctrl+S**.
5. Select **“Start”**, or start the service from the tray menu.

Choose **Follow system / English / 简体中文** under **Application settings → Display language**. Open windows and the tray menu update immediately; service names, commands, and log output remain as entered.

Shortcuts: **Ctrl+N** adds a service, **Ctrl+F** searches services (or logs in the log viewer), **Ctrl+L** opens logs, and **Ctrl+Tab / Ctrl+Shift+Tab** cycle configuration tabs. When a tab has focus, use the arrow keys or **Home / End** to select a tab. Switching services prompts you to save unfinished edits. Closing the manager hides it and preserves your draft.

The primary service button shows **Start** or **Stop** according to service state, displays progress, and prevents duplicate operations while busy. **Logs** and **Website** remain visible. **Delete…** is separated on the right and requires confirmation.

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
