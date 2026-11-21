using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CommandTray.Linux;

/// <summary>A session leader stays alive until its entire process group is disposed.</summary>
internal static partial class ProcessGroup
{
    private const int SignalTerm = 15;
    private const int SignalKill = 9;

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int CreateSession();

    [LibraryImport("libc", EntryPoint = "getsid", SetLastError = true)]
    private static partial int GetSession(int process);

    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int ParentId();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int target, int signal);

    public static ProcessStartInfo EntryPoint(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false
        };
        if (string.Equals(Path.GetFileName(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "commandtray.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        RemoveCredentials(start);
        return start;
    }

    public static ProcessStartInfo Command(CommandProfile profile)
    {
        var start = new ProcessStartInfo(profile.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = profile.WorkingDirectory
        };
        foreach (var argument in profile.Arguments) start.ArgumentList.Add(argument);
        RemoveCredentials(start);
        return start;
    }

    private static void RemoveCredentials(ProcessStartInfo start)
    {
        start.Environment.Remove("COMMANDTRAY_PAIRING_CODE");
        start.Environment.Remove("COMMANDTRAY_VAULT_PASSPHRASE");
    }

    public static void Signal(Process leader, bool force)
    {
        // The child holds its session until cleanup, so its PID identifies the owned group.
        try
        {
            if (leader.HasExited) return;
            if (GetSession(leader.Id) == leader.Id)
                _ = Kill(-leader.Id, force ? SignalKill : SignalTerm);
            else if (force)
                leader.Kill();
        }
        catch (InvalidOperationException)
        {
            // The observer may have already reaped and disposed the leader.
        }
    }

    public static async Task<int> HostAsync(string commandFile, string statusFile, int supervisorPid)
    {
        if (ParentId() != supervisorPid) return 1;
        if (CreateSession() < 0)
        {
            LocalState.Write(statusFile, new RunStatus(false, Error: "Unable to create a Linux process session."));
            return 1;
        }

        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        using var parentLost = new CancellationTokenSource();
        var parentWatcher = WatchParentAsync(supervisorPid, parentLost.Token);
        try
        {
            var profile = LocalState.Read<CommandProfile?>(commandFile, null) ??
                throw new InvalidOperationException("Missing process launch profile.");
            using var command = Process.Start(Command(profile)) ??
                throw new InvalidOperationException("The profile could not be started.");
            LocalState.Write(statusFile, new RunStatus(true));
            await command.WaitForExitAsync().ConfigureAwait(false);
            LocalState.Write(statusFile, new RunStatus(true, command.ExitCode));
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            LocalState.Write(statusFile, new RunStatus(false, Error: ex.Message));
            // The supervisor owns cleanup, including commands started before a status write error.
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            await parentLost.CancelAsync().ConfigureAwait(false);
            await parentWatcher.ConfigureAwait(false);
        }
    }

    private static async Task WatchParentAsync(int expected, CancellationToken cancellationToken)
    {
        try
        {
            while (ParentId() == expected)
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            _ = Kill(-Environment.ProcessId, SignalKill);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
