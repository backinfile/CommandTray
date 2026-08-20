using CommandTray.Models;

namespace CommandTray.Services;

/// <summary>
/// Thread-safe application boundary shared by the tray UI and the MCP tools.
/// Configuration mutations are serialized and persisted before they are announced.
/// </summary>
public sealed class CommandTrayApi
{
    private readonly AppConfiguration _configuration;
    private readonly ConfigurationStore _store;
    private readonly ProcessSupervisor _supervisor;
    private readonly SemaphoreSlim _operations = new(1, 1);

    public CommandTrayApi(AppConfiguration configuration, ConfigurationStore store, ProcessSupervisor supervisor)
    {
        _configuration = configuration;
        _store = store;
        _supervisor = supervisor;
    }

    public event EventHandler? ConfigurationChanged;

    public ManagerView GetManager()
    {
        lock (_configuration) return new ManagerView(McpServerHost.Endpoint, _configuration.McpEnabled);
    }

    public IReadOnlyList<ServiceView> ListServices()
    {
        lock (_configuration)
            return _configuration.Services.Select(ToView).ToArray();
    }

    public ServiceView GetService(string serviceId)
    {
        lock (_configuration)
            return ToView(FindService(serviceId));
    }

    public async Task<ServiceView> CreateServiceAsync(ServiceInput input, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            ServiceDefinition created;
            lock (_configuration)
            {
                created = BuildService(Guid.NewGuid(), input, null);
                ValidateReplacement(null, created);
                _configuration.Services.Add(created);
                try { _store.Save(_configuration); }
                catch
                {
                    _configuration.Services.Remove(created);
                    throw;
                }
            }
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            return ToView(created);
        }
        finally { _operations.Release(); }
    }

    public async Task<ServiceView> UpdateServiceAsync(string serviceId, ServiceInput input, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            ServiceDefinition existing;
            lock (_configuration)
            {
                existing = FindService(serviceId);
                var updated = BuildService(existing.Id, input, existing);
                ValidateReplacement(existing, updated);
                var previous = CloneService(existing);
                CopyService(updated, existing);
                try { _store.Save(_configuration); }
                catch
                {
                    CopyService(previous, existing);
                    throw;
                }
            }
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            return ToView(existing);
        }
        finally { _operations.Release(); }
    }

    public async Task<OperationResult> DeleteServiceAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            ServiceDefinition service;
            lock (_configuration) service = FindService(serviceId);
            var state = _supervisor.GetSnapshot(service).State;
            if (state is not (ServiceState.Stopped or ServiceState.Faulted))
                throw new InvalidOperationException("Stop the service before deleting it.");

            lock (_configuration)
            {
                if (!_configuration.Services.Remove(service))
                    throw new KeyNotFoundException($"Service '{serviceId}' no longer exists.");
                try { _store.Save(_configuration); }
                catch
                {
                    _configuration.Services.Add(service);
                    throw;
                }
            }
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            return new OperationResult(true, $"Deleted service '{service.Name}'.");
        }
        finally { _operations.Release(); }
    }

    public Task<ServiceView> StartServiceAsync(string serviceId, CancellationToken cancellationToken = default) =>
        RunLifecycleAsync(serviceId, _supervisor.StartAsync, cancellationToken);

    public Task<ServiceView> StopServiceAsync(string serviceId, CancellationToken cancellationToken = default) =>
        RunLifecycleAsync(serviceId, _supervisor.StopAsync, cancellationToken);

    public Task<ServiceView> RestartServiceAsync(string serviceId, CancellationToken cancellationToken = default) =>
        RunLifecycleAsync(serviceId, _supervisor.RestartAsync, cancellationToken);

    public LogView GetServiceLog(string serviceId, int lines)
    {
        ServiceDefinition service;
        lock (_configuration) service = FindService(serviceId);
        var path = _supervisor.GetLogPath(service);
        var requestedLines = Math.Clamp(lines, 1, 1000);
        if (!File.Exists(path)) return new LogView(service.Id, service.Name, path, "");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tail = new Queue<string>(requestedLines);
        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == requestedLines) tail.Dequeue();
            tail.Enqueue(line);
        }
        return new LogView(service.Id, service.Name, path, string.Join(Environment.NewLine, tail));
    }

    private async Task<ServiceView> RunLifecycleAsync(
        string serviceId,
        Func<ServiceDefinition, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            ServiceDefinition service;
            lock (_configuration) service = FindService(serviceId);
            await operation(service, cancellationToken);
            return ToView(service);
        }
        finally { _operations.Release(); }
    }

    private ServiceDefinition FindService(string serviceId)
    {
        if (!Guid.TryParse(serviceId, out var id))
            throw new ArgumentException("serviceId must be a valid GUID.", nameof(serviceId));
        return _configuration.Services.FirstOrDefault(service => service.Id == id)
            ?? throw new KeyNotFoundException($"Service '{serviceId}' was not found.");
    }

    private void ValidateReplacement(ServiceDefinition? existing, ServiceDefinition replacement)
    {
        var candidate = new AppConfiguration
        {
            Language = _configuration.Language,
            RunAtLogin = _configuration.RunAtLogin,
            McpEnabled = _configuration.McpEnabled,
            Services = _configuration.Services
                .Where(service => !ReferenceEquals(service, existing))
                .Append(replacement)
                .ToList()
        };
        ConfigurationStore.Validate(candidate);
    }

    private ServiceView ToView(ServiceDefinition service)
    {
        var snapshot = _supervisor.GetSnapshot(service);
        return new ServiceView(
            service.Id,
            service.Name,
            service.Command,
            service.WorkingDirectory,
            service.OpenUrl,
            service.StartWithManager,
            service.RestartOnUnexpectedExit,
            service.StopCommand,
            service.StopTimeoutSeconds,
            service.Environment.Select(variable => new EnvironmentVariableView(
                variable.Name,
                variable.IsSecret ? null : variable.Value,
                variable.IsSecret,
                !string.IsNullOrEmpty(variable.Value))).ToArray(),
            snapshot.State.ToString(),
            snapshot.ProcessId,
            snapshot.Error);
    }

    private static ServiceDefinition BuildService(Guid id, ServiceInput input, ServiceDefinition? existing)
    {
        var previousSecrets = existing?.Environment
            .Where(variable => variable.IsSecret)
            .ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new ServiceDefinition
        {
            Id = id,
            Name = input.Name?.Trim() ?? "",
            Command = input.Command?.Trim() ?? "",
            WorkingDirectory = input.WorkingDirectory?.Trim() ?? "",
            OpenUrl = input.OpenUrl?.Trim() ?? "",
            StartWithManager = input.StartWithManager,
            RestartOnUnexpectedExit = input.RestartOnUnexpectedExit,
            StopCommand = input.StopCommand?.Trim() ?? "",
            StopTimeoutSeconds = input.StopTimeoutSeconds,
            Environment = (input.Environment ?? [])
                .Select(variable => new EnvironmentEntry
                {
                    Name = variable.Name?.Trim() ?? "",
                    IsSecret = variable.IsSecret,
                    Value = variable.IsSecret && variable.Value is null && previousSecrets.TryGetValue(variable.Name ?? "", out var value)
                        ? value
                        : variable.Value ?? ""
                })
                .ToList()
        };
    }

    private static ServiceDefinition CloneService(ServiceDefinition service) => new()
    {
        Id = service.Id,
        Name = service.Name,
        Command = service.Command,
        WorkingDirectory = service.WorkingDirectory,
        OpenUrl = service.OpenUrl,
        StartWithManager = service.StartWithManager,
        RestartOnUnexpectedExit = service.RestartOnUnexpectedExit,
        StopCommand = service.StopCommand,
        StopTimeoutSeconds = service.StopTimeoutSeconds,
        Environment = service.Environment.Select(variable => new EnvironmentEntry
        {
            Name = variable.Name,
            Value = variable.Value,
            IsSecret = variable.IsSecret,
            ProtectedValue = variable.ProtectedValue
        }).ToList()
    };

    private static void CopyService(ServiceDefinition source, ServiceDefinition destination)
    {
        destination.Name = source.Name;
        destination.Command = source.Command;
        destination.WorkingDirectory = source.WorkingDirectory;
        destination.OpenUrl = source.OpenUrl;
        destination.StartWithManager = source.StartWithManager;
        destination.RestartOnUnexpectedExit = source.RestartOnUnexpectedExit;
        destination.StopCommand = source.StopCommand;
        destination.StopTimeoutSeconds = source.StopTimeoutSeconds;
        destination.Environment = source.Environment;
    }
}

public sealed record ManagerView(string Endpoint, bool Enabled);

public sealed record ServiceView(
    Guid Id,
    string Name,
    string Command,
    string WorkingDirectory,
    string OpenUrl,
    bool StartWithManager,
    bool RestartOnUnexpectedExit,
    string StopCommand,
    int StopTimeoutSeconds,
    IReadOnlyList<EnvironmentVariableView> Environment,
    string State,
    int? ProcessId,
    string? Error);

public sealed record EnvironmentVariableView(string Name, string? Value, bool IsSecret, bool HasValue);

public sealed record LogView(Guid ServiceId, string ServiceName, string Path, string Content);

public sealed record OperationResult(bool Success, string Message);

public sealed class ServiceInput
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public string? OpenUrl { get; set; }
    public bool StartWithManager { get; set; }
    public bool RestartOnUnexpectedExit { get; set; }
    public string? StopCommand { get; set; }
    public int StopTimeoutSeconds { get; set; } = 5;
    public List<EnvironmentVariableInput>? Environment { get; set; }
}

public sealed class EnvironmentVariableInput
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
}
