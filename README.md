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
