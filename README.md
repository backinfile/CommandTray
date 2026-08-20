# CommandTray

CommandTray is a Windows 10/11 x64 system-tray supervisor for long-running CMD commands and local HTTP tools.

## Features

- Add multiple commands with working directories, environment variables, optional stop commands, and web URLs.
- Start, stop, restart, open, and inspect logs from the tray.
- Track only processes launched by CommandTray using a Windows Job Object; it never kills generic process names.
- Capture stdout/stderr into rolling logs under `%LOCALAPPDATA%\CommandTray\logs`.
- Encrypt environment values marked as secrets with Windows DPAPI for the current user.
- Optional per-service start-on-launch and crash restart with bounded exponential backoff.
- Optional per-user Windows login startup, disabled by default.
- Built-in local MCP server for AI control, enabled by default and available at `http://127.0.0.1:58629/mcp`.

## MCP integration

CommandTray starts a stateless Streamable HTTP MCP server with the application. It binds only to the IPv4 loopback address, validates the HTTP host as `127.0.0.1`, and does not enable browser CORS. You can turn it off with **Enable MCP / 启用 MCP** in the settings window.

Use this Streamable HTTP endpoint in an MCP client:

```text
http://127.0.0.1:58629/mcp
```

The server exposes tools to:

- inspect the manager and list or read services;
- create, replace, and delete service definitions;
- start, stop, and restart services;
- read the newest 1–1000 lines of a service log.

Secret environment values are never returned by the MCP tools. When updating a service, omit an existing secret's value to keep its current value. Log content is returned as written by the managed command and may therefore contain sensitive output.

MCP clients can execute configured commands through CommandTray. Keep the endpoint local and connect only trusted AI clients.

## Build and test

Requirements: Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet test .\CommandTray.slnx
dotnet run --project .\src\CommandTray\CommandTray.csproj
```

The integration test starts this owned fixture on `127.0.0.1:58628` and stops it through its Job Object:

```cmd
python -c "from http.server import ThreadingHTTPServer,SimpleHTTPRequestHandler; ThreadingHTTPServer(('127.0.0.1',58628),SimpleHTTPRequestHandler).serve_forever()"
```

Port `127.0.0.1:58627` is explicitly outside the test scope. Tests do not connect to it, adopt it, or terminate its owner.

## Publish

```powershell
.\scripts\publish.ps1
```

This creates a self-contained portable ZIP. If Inno Setup is installed, it also creates a per-user installer.
