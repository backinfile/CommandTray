using CommandTray.Models;

namespace CommandTray.Services;

internal static class ServiceConfigurationEditor
{
    public static List<EnvironmentEntry> ParseEnvironment(IEnumerable<string> lines)
    {
        var values = new List<EnvironmentEntry>();
        foreach (var raw in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var line = raw.Trim();
            var secret = line.StartsWith('*');
            if (secret) line = line[1..];
            var separator = line.IndexOf('=');
            if (separator < 1 || string.IsNullOrWhiteSpace(line[..separator]))
                throw new InvalidDataException(Localizer.T(
                    $"Use NAME=value for each environment variable: {raw}",
                    $"环境变量需使用 NAME=value 格式：{raw}"));
            values.Add(new EnvironmentEntry
            {
                Name = line[..separator].Trim(), Value = line[(separator + 1)..], IsSecret = secret
            });
        }
        return values;
    }

    public static void Save(AppConfiguration configuration, ConfigurationStore store,
        ServiceDefinition original, ServiceDefinition edited)
    {
        lock (configuration)
        {
            var services = configuration.Services.Select(service => service.Id == original.Id ? edited : service).ToList();
            var isNew = services.All(service => service.Id != original.Id);
            if (isNew) services.Add(edited);
            var proposed = new AppConfiguration
            {
                Language = configuration.Language, RunAtLogin = configuration.RunAtLogin,
                McpEnabled = configuration.McpEnabled, Services = services
            };
            // Previously live definitions changed before validation or disk
            // writes. Commit after persistence succeeds and keep the reference
            // already held by the process supervisor.
            store.Save(proposed);
            original.Name = edited.Name;
            original.Command = edited.Command;
            original.WorkingDirectory = edited.WorkingDirectory;
            original.OpenUrl = edited.OpenUrl;
            original.StopCommand = edited.StopCommand;
            original.StopTimeoutSeconds = edited.StopTimeoutSeconds;
            original.StartWithManager = edited.StartWithManager;
            original.RestartOnUnexpectedExit = edited.RestartOnUnexpectedExit;
            original.Environment = edited.Environment;
            if (isNew) configuration.Services.Add(original);
        }
    }
}
