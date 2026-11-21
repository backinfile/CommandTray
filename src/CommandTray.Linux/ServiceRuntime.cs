using System.Diagnostics;
using System.Text;

namespace CommandTray.Linux;

internal sealed class ServiceRuntime : IAsyncDisposable
{
    private readonly List<CommandProfile> _profiles = LocalState.Read(LocalState.Profiles, new List<CommandProfile>());
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Job> _terminals = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task StartConfiguredAsync()
    {
        foreach (var profile in _profiles.Where(profile => profile.AutoStart))
        {
            try { _jobs[profile.Id] = await StartAsync(profile, interactive: false); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CommandTray failed to start {profile.Name}: {ex.Message}");
            }
        }
    }

    public async Task<object> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2) throw new ArgumentException("Use service or terminal followed by an action.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return args[0] switch
            {
                "service" => await ExecuteServiceAsync(args[1], args[2..]),
                "terminal" => await ExecuteTerminalAsync(args[1], args[2..]),
                _ => throw new ArgumentException("Unknown daemon command.")
            };
        }
        finally { _gate.Release(); }
    }

    private async Task<object> ExecuteServiceAsync(string action, string[] args)
    {
        if (action == "list") return _profiles.Select(View).ToArray();
        if (action is "create" or "update")
        {
            var name = Required(args, 0, "service name").Trim();
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a service name.");
            var existing = Find(name);
            if (action == "create" && existing is not null)
                throw new InvalidOperationException("That service already exists.");
            if (action == "update" && existing is null)
                throw new InvalidOperationException("No service matches that name.");
            if (existing is not null && _jobs.TryGetValue(existing.Id, out var running) && running.Running)
                throw new InvalidOperationException("Stop the service before editing it.");

            var separator = Array.IndexOf(args, "--");
            if (separator < 1 || separator == args.Length - 1)
                throw new ArgumentException("Provide the executable and arguments after --.");
            var cwd = existing?.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var timeout = existing?.StopTimeoutSeconds ?? 3;
            var auto = existing?.AutoStart ?? false;
            for (var i = 1; i < separator; i++)
            {
                switch (args[i])
                {
                    case "--cwd": cwd = Required(args[..separator], ++i, "working directory"); break;
                    case "--stop-timeout": timeout = Positive(Required(args[..separator], ++i, "stop timeout"), 120); break;
                    case "--auto-start": auto = true; break;
                    case "--no-auto-start": auto = false; break;
                    default: throw new ArgumentException($"Unknown profile setting: {args[i]}");
                }
            }
            var profile = new CommandProfile(existing?.Id ?? Guid.NewGuid().ToString("N"), existing?.Name ?? name,
                args[separator + 1], args[(separator + 2)..], ValidateDirectory(cwd), timeout, auto);
            MutateProfiles(profile, existing);
            return View(profile);
        }

        var selected = Find(Required(args, 0, "service name or ID")) ??
            throw new InvalidOperationException("No service matches that name or ID.");
        switch (action)
        {
            case "get": return View(selected);
            case "start":
                if (!_jobs.TryGetValue(selected.Id, out var current) || !current.Running)
                    _jobs[selected.Id] = await StartAsync(selected, interactive: false);
                return View(selected);
            case "stop":
                if (_jobs.TryGetValue(selected.Id, out var stopping)) await stopping.StopAsync();
                return View(selected);
            case "restart":
                if (_jobs.TryGetValue(selected.Id, out var restarting)) await restarting.StopAsync();
                _jobs[selected.Id] = await StartAsync(selected, interactive: false);
                return View(selected);
            case "logs":
                return new { selected.Id, selected.Name, content = ReadTail(LogPath(selected.Id), args.Length >= 2 ? Positive(args[1], 1000) : 100) };
            case "delete":
                if (_jobs.TryGetValue(selected.Id, out var job) && job.Running)
                    throw new InvalidOperationException("Stop the service before deleting it.");
                MutateProfiles(null, selected);
                _jobs.Remove(selected.Id);
                return new { deleted = selected.Id };
            default: throw new ArgumentException("Unknown service action.");
        }
    }

    private async Task<object> ExecuteTerminalAsync(string action, string[] args)
    {
        if (action == "list") return _terminals.Values.Select(job => job.View()).ToArray();
        if (action == "open")
        {
            var cwd = ValidateDirectory(Required(args, 0, "working directory"));
            var executable = args.Length > 1 ? args[1] : DefaultShell();
            var arguments = args.Length > 2 ? args[2..] : [];
            var id = Guid.NewGuid().ToString("N");
            // util-linux script provides a real PTY to shells and SSH, and forwards termination to its child.
            var profile = new CommandProfile(id, Path.GetFileName(executable), "script",
                ["--quiet", "--flush", "--return", "--command", string.Join(" ", new[] { executable }.Concat(arguments).Select(ShellQuote)), "/dev/null"],
                cwd, 3);
            var job = await StartAsync(profile, interactive: true);
            _terminals.Add(id, job);
            return job.View();
        }

        var idToFind = Required(args, 0, "terminal ID");
        if (!_terminals.TryGetValue(idToFind, out var selected)) throw new InvalidOperationException("Unknown terminal.");
        switch (action)
        {
            case "write":
                await selected.WriteAsync(Required(args, 1, "terminal input", allowEmpty: true));
                return selected.View();
            case "read":
                return new { id = idToFind, content = ReadTail(LogPath(idToFind), args.Length >= 2 ? Positive(args[1], 1000) : 200) };
            case "close":
                await selected.StopAsync();
                _terminals.Remove(idToFind);
                return new { closed = idToFind };
            default: throw new ArgumentException("Unknown terminal action.");
        }
    }

    private CommandProfile? Find(string name) => _profiles.FirstOrDefault(profile => profile.Id == name ||
        string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    private object View(CommandProfile profile)
    {
        _jobs.TryGetValue(profile.Id, out var job);
        return new
        {
            profile.Id, profile.Name, profile.Executable, profile.Arguments, profile.WorkingDirectory, profile.StopTimeoutSeconds,
            profile.AutoStart, running = job?.Running ?? false, exitCode = job?.ExitCode
        };
    }

    private void MutateProfiles(CommandProfile? replacement, CommandProfile? existing)
    {
        var next = _profiles.Where(profile => profile != existing).ToList();
        if (replacement is not null) next.Add(replacement);
        LocalState.Write(LocalState.Profiles, next);
        _profiles.Clear();
        _profiles.AddRange(next);
    }

    private static async Task<Job> StartAsync(CommandProfile profile, bool interactive)
    {
        var group = Path.Combine(LocalState.Runtime, Guid.NewGuid().ToString("N"));
        LocalState.EnsureDirectory(group);
        var command = Path.Combine(group, "command.json");
        var status = Path.Combine(group, "status.json");
        LocalState.Write(command, profile);
        var start = ProcessGroup.EntryPoint("process-host", command, status, Environment.ProcessId.ToString());
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch the process host.");
        var job = new Job(profile, process, status, interactive);
        try
        {
            await job.AwaitStartAsync();
            job.Observe();
            return job;
        }
        catch
        {
            await job.AbortAsync();
            throw;
        }
    }

    private const int MaxReadBytes = 128 * 1024;
    private static string LogPath(string id) => Path.Combine(LocalState.Root, "logs", id + ".log");

    private static string ReadTail(string path, int lines)
    {
        var prefix = Path.GetFileNameWithoutExtension(path);
        try
        {
            var content = ReadEnd(path);
            if (content.Count(character => character == '\n') < lines)
                content = ReadEnd(Path.Combine(Path.GetDirectoryName(path)!, prefix + ".previous.log")) + content;
            return string.Join('\n', content.TrimEnd('\r', '\n').Split('\n').TakeLast(lines));
        }
        catch (IOException) { return string.Empty; }
    }

    private static string ReadEnd(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Max(0, file.Length - MaxReadBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return string.Empty; }
        catch (DirectoryNotFoundException) { return string.Empty; }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static string DefaultShell() => Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : "/bin/sh";
    public static string ValidateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute working directory.");
        var value = Path.GetFullPath(path);
        return Directory.Exists(value) ? value : throw new DirectoryNotFoundException($"Directory not found: {value}");
    }
    public static int Positive(string value, int maximum) =>
        int.TryParse(value, out var result) && result >= 1 && result <= maximum ?
            result : throw new ArgumentException($"Enter an integer from 1 to {maximum}.");
    public static string Required(string[] arguments, int index, string name, bool allowEmpty = false) =>
        index < arguments.Length && (allowEmpty || arguments[index].Length > 0) ? arguments[index] :
            throw new ArgumentException($"Missing {name}.");

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_jobs.Values.Concat(_terminals.Values).Select(job => job.StopAsync()));
        _gate.Dispose();
    }

    private sealed class Job
    {
        private const long MaxLogBytes = 2 * 1024 * 1024;
        private readonly CommandProfile _profile;
        private readonly Process _host;
        private readonly string _statusFile;
        private readonly bool _interactive;
        private readonly object _logLock = new();
        private readonly Task[] _pumps;
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _stopping;
        private volatile bool _running = true;
        private int? _exitCode;

        public Job(CommandProfile profile, Process host, string statusFile, bool interactive)
        {
            _profile = profile;
            _host = host;
            _statusFile = statusFile;
            _interactive = interactive;
            if (!interactive) _host.StandardInput.Close();
            _pumps = [PumpAsync(_host.StandardOutput), PumpAsync(_host.StandardError)];
        }

        public bool Running => _running;
        public int? ExitCode => _complete.Task.IsCompletedSuccessfully ? _exitCode : null;

        public object View() => new { _profile.Id, _profile.Name, _profile.WorkingDirectory, running = Running, exitCode = ExitCode };

        public async Task AwaitStartAsync()
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                var status = LocalState.Read<RunStatus?>(_statusFile, null);
                if (status?.Started is true) return;
                if (status?.Error is { } error) throw new InvalidOperationException(error);
                if (_host.HasExited) throw new InvalidOperationException("The Linux process host exited before the command started.");
                await Task.Delay(35);
            }
            throw new TimeoutException("Timed out waiting for the process host.");
        }

        public void Observe() => _ = ObserveAsync();

        private async Task ObserveAsync()
        {
            try
            {
                while (!_host.HasExited)
                {
                    var status = LocalState.Read<RunStatus?>(_statusFile, null);
                    if (status?.ExitCode is { } code)
                    {
                        _exitCode = code;
                        if (!_stopping) ProcessGroup.Signal(_host, force: true);
                    }
                    await Task.Delay(65);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"CommandTray monitor failed for {_profile.Name}: {ex.Message}");
                ProcessGroup.Signal(_host, force: true);
            }
            finally { await FinishAsync(); }
        }

        public async Task AbortAsync()
        {
            ProcessGroup.Signal(_host, force: true);
            await FinishAsync();
        }

        private async Task FinishAsync()
        {
            try
            {
                await _host.WaitForExitAsync();
                _exitCode ??= LocalState.Read<RunStatus?>(_statusFile, null)?.ExitCode ?? _host.ExitCode;
                await Task.WhenAll(_pumps).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"CommandTray cleanup for {_profile.Name}: {ex.Message}");
            }
            finally
            {
                _running = false;
                _host.Dispose();
                try
                {
                    // The directory is a unique direct child created by StartAsync, and only contains these files.
                    var folder = Path.GetDirectoryName(_statusFile)!;
                    File.Delete(_statusFile);
                    File.Delete(Path.Combine(folder, "command.json"));
                    Directory.Delete(folder);
                }
                catch (IOException) { }
                _complete.TrySetResult();
            }
        }

        public async Task StopAsync()
        {
            if (_complete.Task.IsCompleted) return;
            _stopping = true;
            ProcessGroup.Signal(_host, force: false);
            await Task.WhenAny(_complete.Task, Task.Delay(TimeSpan.FromSeconds(_profile.StopTimeoutSeconds)));
            if (!_complete.Task.IsCompleted) ProcessGroup.Signal(_host, force: true);
            await _complete.Task;
        }

        public async Task WriteAsync(string input)
        {
            if (!_interactive || !_running) throw new InvalidOperationException("The terminal session is not running.");
            await _host.StandardInput.WriteAsync(input);
            await _host.StandardInput.FlushAsync();
        }

        private async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer)) > 0)
                    Append(buffer.AsSpan(0, count));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"CommandTray log writer for {_profile.Name}: {ex.Message}");
            }
        }

        private void Append(ReadOnlySpan<char> text)
        {
            lock (_logLock)
            {
                var path = LogPath(_profile.Id);
                var directory = Path.GetDirectoryName(path)!;
                LocalState.EnsureDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes)
                    File.Move(path, Path.Combine(directory, _profile.Id + ".previous.log"), overwrite: true);

                using var file = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete,
                    UnixCreateMode = LocalState.PrivateFile
                });
                using var writer = new StreamWriter(file, new UTF8Encoding(false));
                writer.Write(text);
            }
        }
    }
}
