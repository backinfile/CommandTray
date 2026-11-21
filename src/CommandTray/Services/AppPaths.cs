namespace CommandTray.Services;

public static class AppPaths
{
    private static readonly string? OverrideDirectory = Environment.GetEnvironmentVariable("COMMANDTRAY_DATA_DIR");
    public static string DataDirectory { get; } = string.IsNullOrWhiteSpace(OverrideDirectory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommandTray")
        : Path.GetFullPath(OverrideDirectory);
    public static int McpPort { get; } = int.TryParse(Environment.GetEnvironmentVariable("COMMANDTRAY_MCP_PORT"), out var port)
        && port is > 0 and <= 65535 ? port : McpServerHost.Port;
    internal static string InstanceSuffix { get; } = string.IsNullOrWhiteSpace(OverrideDirectory) ? "" :
        "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..16];
    public static string ConfigurationFile => Path.Combine(DataDirectory, "config.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
