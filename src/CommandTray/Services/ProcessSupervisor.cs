using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CommandTray.Models;

namespace CommandTray.Services;

public sealed class ProcessSupervisor : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, ManagedRuntime> _runtimes = new();
    private readonly string? _logRoot;
    private volatile bool _shuttingDown;

    public event EventHandler<ServiceSnapshot>? StateChanged;
    public event EventHandler<(ServiceDefinition Service, string Message)>? Failure;

    public ProcessSupervisor(string? logRoot = null) => _logRoot = logRoot;

    public ServiceSnapshot GetSnapshot(ServiceDefinition service)
    {
        var runtime = GetRuntime(service);
        return runtime.Snapshot();
    }

    public string GetLogPath(ServiceDefinition service) => GetRuntime(service).Log.PathName;

    public async Task StartAsync(ServiceDefinition service, CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntime(service);
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            runtime.Definition = service;
            if (runtime.Process is { HasExited: false }) return;
            runtime.IntentionalStop = false;
            runtime.RestartCancellation?.Cancel();
            SetState(runtime, ServiceState.Starting, null);

            if (string.IsNullOrWhiteSpace(service.Command)) throw new InvalidOperationException("Command is required.");
            if (!string.IsNullOrWhiteSpace(service.WorkingDirectory) && !Directory.Exists(service.WorkingDirectory))
                throw new DirectoryNotFoundException($"Working directory does not exist: {service.WorkingDirectory}");

            var gateName = $@"Local\CommandTray.Start.{Guid.NewGuid():N}";
            var startGate = new EventWaitHandle(false, EventResetMode.ManualReset, gateName);
            runtime.StartGate?.Dispose();
            runtime.StartGate = startGate;
            var start = CreateHostStartInfo(gateName, service.Command);
            start.WorkingDirectory = string.IsNullOrWhiteSpace(service.WorkingDirectory) ? Environment.CurrentDirectory : service.WorkingDirectory;
            foreach (var variable in service.Environment)
                start.Environment[variable.Name] = variable.Value;

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) runtime.Log.Write("OUT", e.Data); };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (e.Data.StartsWith(CommandHost.ChildPidPrefix, StringComparison.Ordinal) &&
                    int.TryParse(e.Data[CommandHost.ChildPidPrefix.Length..], out var childPid))
                {
                    runtime.ChildProcessId = childPid;
                    runtime.StartGate?.Dispose();
                    runtime.StartGate = null;
                }
                else runtime.Log.Write("ERR", e.Data);
            };
            process.Exited += (_, _) => _ = HandleUnexpectedExitAsync(runtime);

            var job = new WindowsJob();
            try
            {
                if (!process.Start()) throw new InvalidOperationException("The command process did not start.");
                job.Add(process);
                runtime.Process = process;
                runtime.Job = job;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                startGate.Set();
                runtime.Log.Write("SYS", $"Started PID {process.Id}: {service.Command}");
                SetState(runtime, ServiceState.Running, null);
                StartStableTimer(runtime);
            }
            catch
            {
                job.Dispose();
                process.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            SetState(runtime, ServiceState.Faulted, ex.Message);
            runtime.Log.Write("ERR", ex.ToString());
            Failure?.Invoke(this, (service, ex.Message));
            throw;
        }
        finally { runtime.Gate.Release(); }
    }

    public async Task StopAsync(ServiceDefinition service, CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntime(service);
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            runtime.IntentionalStop = true;
            runtime.RestartCancellation?.Cancel();
            if (runtime.Process is not { } process || process.HasExited)
            {
                CleanupProcess(runtime);
                SetState(runtime, ServiceState.Stopped, null);
                return;
            }

            SetState(runtime, ServiceState.Stopping, null);
            var ownedTree = OwnedProcessTree.Capture(process.Id);
            if (!string.IsNullOrWhiteSpace(service.StopCommand))
                await RunStopCommandAsync(service, runtime.ChildProcessId ?? process.Id, cancellationToken);
            else
            {
                try
                {
                    if (runtime.ChildProcessId is int childPid)
                        using (var child = Process.GetProcessById(childPid)) child.CloseMainWindow();
                }
                catch { /* child may already be exiting */ }
            }

            var timeout = TimeSpan.FromSeconds(Math.Clamp(service.StopTimeoutSeconds, 1, 300));
            if (!await WaitForExitAsync(process, timeout, cancellationToken))
            {
                runtime.Log.Write("SYS", "Graceful stop timed out; terminating the tracked job.");
                runtime.Job?.Terminate();
                await WaitForExitAsync(process, TimeSpan.FromSeconds(5), cancellationToken);
            }
            // Some command shims (notably app-execution aliases) may explicitly
            // break away from a Job. Only terminate PIDs proven to be descendants
            // of this owned host, and only if their start time still matches.
            OwnedProcessTree.TerminateStillMatching(ownedTree);
            runtime.Log.Write("SYS", $"Stopped PID {process.Id}.");
            CleanupProcess(runtime);
            runtime.RestartAttempts = 0;
            SetState(runtime, ServiceState.Stopped, null);
        }
        finally { runtime.Gate.Release(); }
    }

    public async Task RestartAsync(ServiceDefinition service, CancellationToken cancellationToken = default)
    {
        await StopAsync(service, cancellationToken);
        await StartAsync(service, cancellationToken);
    }

    public async Task StartConfiguredServicesAsync(IEnumerable<ServiceDefinition> services)
    {
        foreach (var service in services.Where(x => x.StartWithManager))
        {
            try { await StartAsync(service); }
            catch { /* surfaced through Failure */ }
        }
    }

    public async Task StopAllAsync(
        IEnumerable<ServiceDefinition> services,
        CancellationToken cancellationToken = default)
    {
        _shuttingDown = true;
        foreach (var service in services)
        {
            try { await StopAsync(service, cancellationToken); }
            catch { /* continue cleaning remaining owned jobs */ }
        }
    }

    private ManagedRuntime GetRuntime(ServiceDefinition service) => _runtimes.GetOrAdd(service.Id, _ => new ManagedRuntime(service, _logRoot));

    private async Task HandleUnexpectedExitAsync(ManagedRuntime runtime)
    {
        await runtime.Gate.WaitAsync();
        CancellationTokenSource? restart = null;
        try
        {
            var exitCode = runtime.Process is { } p && p.HasExited ? p.ExitCode : -1;
            runtime.Log.Write("SYS", $"PID exited with code {exitCode}.");
            CleanupProcess(runtime);
            if (_shuttingDown || runtime.IntentionalStop)
            {
                SetState(runtime, ServiceState.Stopped, null);
                return;
            }

            if (!runtime.Definition.RestartOnUnexpectedExit)
            {
                SetState(runtime, ServiceState.Faulted, $"Exited with code {exitCode}.");
                Failure?.Invoke(this, (runtime.Definition, $"Exited with code {exitCode}."));
                return;
            }

            runtime.RestartAttempts++;
            if (runtime.RestartAttempts > 5)
            {
                const string message = "Restart limit reached after 5 consecutive failures.";
                SetState(runtime, ServiceState.Faulted, message);
                Failure?.Invoke(this, (runtime.Definition, message));
                return;
            }

            var delay = TimeSpan.FromSeconds(1 << (runtime.RestartAttempts - 1));
            restart = runtime.RestartCancellation = new CancellationTokenSource();
            SetState(runtime, ServiceState.Starting, $"Restarting in {delay.TotalSeconds:0}s");
            runtime.Log.Write("SYS", $"Unexpected exit; restart attempt {runtime.RestartAttempts}/5 in {delay.TotalSeconds:0}s.");
        }
        finally { runtime.Gate.Release(); }

        if (restart is null) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1 << (runtime.RestartAttempts - 1)), restart.Token);
            await StartAsync(runtime.Definition, restart.Token);
        }
        catch (OperationCanceledException) { }
        catch { /* StartAsync already reports the failure. */ }
    }

    private static async Task RunStopCommandAsync(ServiceDefinition service, int processId, CancellationToken cancellationToken)
    {
        var command = service.StopCommand.Replace("${PID}", processId.ToString(), StringComparison.Ordinal);
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            WorkingDirectory = string.IsNullOrWhiteSpace(service.WorkingDirectory) ? Environment.CurrentDirectory : service.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Arguments = BuildCmdArguments(command);
        using var stop = Process.Start(start) ?? throw new InvalidOperationException("Stop command did not start.");
        await stop.WaitForExitAsync(cancellationToken);
    }

    private static ProcessStartInfo CreateHostStartInfo(string gateName, string command)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "CommandTray.dll");
        var currentPath = Environment.ProcessPath;
        var runningAsCommandTray = currentPath is not null &&
            string.Equals(Path.GetFileNameWithoutExtension(currentPath), "CommandTray", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            FileName = runningAsCommandTray ? currentPath! : "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (!runningAsCommandTray) start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("--command-host");
        start.ArgumentList.Add(gateName);
        start.ArgumentList.Add(encoded);
        return start;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (process.HasExited) return true;
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCancellation.Token); return true; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return process.HasExited; }
    }

    // The command is intentionally passed verbatim after /C so CMD owns shell parsing
    // (pipes, redirects, variables, and the user's existing quotes).
    internal static string BuildCmdArguments(string command) => $"/D /S /C {command}";

    private void StartStableTimer(ManagedRuntime runtime)
    {
        runtime.StableCancellation?.Cancel();
        var cancellation = runtime.StableCancellation = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellation.Token);
                if (runtime.Process is { HasExited: false }) runtime.RestartAttempts = 0;
            }
            catch (OperationCanceledException) { }
        });
    }

    private static void CleanupProcess(ManagedRuntime runtime)
    {
        runtime.StableCancellation?.Cancel();
        runtime.Process?.Dispose();
        runtime.Process = null;
        runtime.StartGate?.Dispose();
        runtime.StartGate = null;
        runtime.ChildProcessId = null;
        runtime.Job?.Dispose();
        runtime.Job = null;
    }

    private void SetState(ManagedRuntime runtime, ServiceState state, string? error)
    {
        runtime.State = state;
        runtime.Error = error;
        StateChanged?.Invoke(this, runtime.Snapshot());
    }

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;
        foreach (var runtime in _runtimes.Values)
        {
            runtime.RestartCancellation?.Cancel();
            try { runtime.Job?.Terminate(); } catch { }
            CleanupProcess(runtime);
            runtime.Log.Dispose();
            runtime.Gate.Dispose();
        }
        await Task.CompletedTask;
    }

    private sealed class ManagedRuntime
    {
        public ManagedRuntime(ServiceDefinition definition, string? logRoot)
        {
            Definition = definition;
            Log = new RollingLog(definition.Id, logRoot);
        }
        public ServiceDefinition Definition { get; set; }
        public ServiceState State { get; set; }
        public string? Error { get; set; }
        public Process? Process { get; set; }
        public int? ChildProcessId { get; set; }
        public EventWaitHandle? StartGate { get; set; }
        public WindowsJob? Job { get; set; }
        public RollingLog Log { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool IntentionalStop { get; set; }
        public int RestartAttempts { get; set; }
        public CancellationTokenSource? RestartCancellation { get; set; }
        public CancellationTokenSource? StableCancellation { get; set; }
        public ServiceSnapshot Snapshot() => new(Definition.Id, Definition.Name, State,
            Process is { HasExited: false } p ? ChildProcessId ?? p.Id : null, Error);
    }
}
