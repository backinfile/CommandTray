namespace CommandTray.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommandTray");
    public static string ConfigurationFile => Path.Combine(DataDirectory, "config.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
