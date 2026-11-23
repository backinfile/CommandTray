using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using CommandTray.Linux;

[assembly: SupportedOSPlatform("linux")]

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    Console.WriteLine("""
        CommandTray Linux

          daemon
          service list|get|start|stop|restart|logs|delete <name>
          service create|update <name> [--name <new-name>] [--cwd <path>] [--stop-timeout <seconds>]
                   [--auto-start|--no-auto-start] -- <executable> [args...]
          terminal list|open|read|write|resize|close
          shell [cwd] [executable]
          config get | config set server|mcp-port <value>
          workspace list|get|create|show|use|rename|remove|load|replace|put-item|move-item|remove-item
          sync pair <server> [device-name] | sync status|push|pull|devices|remove-device | sync resolve local|remote
          clip put [text] | clip get|status
          share list | share put <file> [folder] | share get <id> <destination> | share remove <id>
          host list|get|put|remove|connect
          identity list|import|install|remove
          mcp serve --stdio|--http | mcp token [--rotate]

        Pairing uses COMMANDTRAY_PAIRING_CODE. SSH key import or install uses
        COMMANDTRAY_VAULT_PASSPHRASE. Without an argument, clip put reads stdin.
        """);
    return 0;
}

if (args[0] == "--version")
{
    Console.WriteLine(typeof(LocalState).Assembly.GetName().Version?.ToString(3));
    return 0;
}

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Run this executable on Linux.");
    return 1;
}

using var shutdown = new CancellationTokenSource();
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    shutdown.Cancel();
});
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    if (args is ["process-host", var command, var status, var pid] && int.TryParse(pid, out var supervisorPid))
        return await ProcessGroup.HostAsync(command, status, supervisorPid);
    if (args is ["daemon"])
    {
        await DaemonTransport.RunAsync(shutdown.Token);
        return 0;
    }
    if (args is ["mcp", "serve", "--stdio"] or ["mcp", "serve", "--http"])
    {
        await McpHost.RunAsync(args[2] == "--http", shutdown.Token);
        return 0;
    }
    if (args is ["mcp", "token"] or ["mcp", "token", "--rotate"])
    {
        Console.WriteLine(McpHost.GetToken(args.Length == 3));
        return 0;
    }
    if (args[0] == "shell" || args is ["host", "connect", ..])
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profile = args[0] == "shell"
            ? new CommandProfile("", "shell", args.ElementAtOrDefault(2) ?? ServiceRuntime.DefaultShell(), [],
                ServiceRuntime.ValidateDirectory(args.ElementAtOrDefault(1) ?? Directory.GetCurrentDirectory()))
            : LinuxCommands.SshProfile(ServiceRuntime.Required(args, 2, "host ID"), args.ElementAtOrDefault(3) ?? home);
        using var process = Process.Start(ProcessGroup.Command(profile)) ?? throw new InvalidOperationException("Cannot start the requested shell.");
        // Interactive children share the invoking terminal and receive its interrupt directly.
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
    if (args is ["clip", "put"])
        args = [.. args, await Console.In.ReadToEndAsync(shutdown.Token)];

    var result = await new LinuxCommands().ExecuteAsync(args, shutdown.Token);
    if (args is ["service", "logs", ..] or ["terminal", "read", ..] && result is JsonElement log)
        Console.Write(log.GetProperty("content").GetString());
    else if (result is string text)
        Console.Write(text);
    else if (result is not null)
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(LocalState.Json) { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 130;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or
    UnauthorizedAccessException or JsonException or TimeoutException or System.ComponentModel.Win32Exception or
    System.Net.Http.HttpRequestException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
{
    Console.Error.WriteLine("CommandTray: " + error.Message);
    return 1;
}
