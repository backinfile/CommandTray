namespace CommandTray.Models;

public sealed class AppConfiguration
{
    public string Language { get; set; } = "auto";
    public bool RunAtLogin { get; set; }
    public bool McpEnabled { get; set; } = true;
    public List<ServiceDefinition> Services { get; set; } = [];
}

public sealed class ServiceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New service";
    public string Command { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string OpenUrl { get; set; } = "";
    public bool StartWithManager { get; set; }
    public bool RestartOnUnexpectedExit { get; set; }
    public string StopCommand { get; set; } = "";
    public int StopTimeoutSeconds { get; set; } = 5;
    public List<EnvironmentEntry> Environment { get; set; } = [];
}

public sealed class EnvironmentEntry
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsSecret { get; set; }
    public string ProtectedValue { get; set; } = "";
}

public enum ServiceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

public sealed record ServiceSnapshot(Guid Id, string Name, ServiceState State, int? ProcessId, string? Error);
