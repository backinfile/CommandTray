using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommandTray.Models;

namespace CommandTray.Services;

public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string PathName { get; }

    public ConfigurationStore(string? path = null) => PathName = path ?? AppPaths.ConfigurationFile;

    public AppConfiguration Load()
    {
        if (!File.Exists(PathName))
        {
            var initial = CreateInitial();
            Save(initial);
            return initial;
        }

        var config = JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(PathName), JsonOptions)
            ?? throw new InvalidDataException("Configuration file is empty.");
        NormalizeAndDecrypt(config);
        return config;
    }

    public void Save(AppConfiguration configuration)
    {
        Validate(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var persisted = Clone(configuration);
        foreach (var item in persisted.Services.SelectMany(x => x.Environment).Where(x => x.IsSecret))
        {
            item.ProtectedValue = Protect(item.Value);
            item.Value = "";
        }

        var temp = PathName + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(persisted, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, PathName, true);
    }

    public void ExportWithoutSecrets(AppConfiguration configuration, string destination)
    {
        var exported = Clone(configuration);
        foreach (var item in exported.Services.SelectMany(x => x.Environment).Where(x => x.IsSecret))
        {
            item.Value = "";
            item.ProtectedValue = "";
        }
        File.WriteAllText(destination, JsonSerializer.Serialize(exported, JsonOptions), new UTF8Encoding(false));
    }

    public AppConfiguration Import(string source)
    {
        var config = JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(source), JsonOptions)
            ?? throw new InvalidDataException("Imported configuration is empty.");
        foreach (var item in config.Services.SelectMany(x => x.Environment).Where(x => x.IsSecret))
        {
            item.Value = "";
            item.ProtectedValue = "";
        }
        Validate(config);
        return config;
    }

    public static void Validate(AppConfiguration configuration)
    {
        var ids = new HashSet<Guid>();
        foreach (var service in configuration.Services)
        {
            if (service.Id == Guid.Empty || !ids.Add(service.Id))
                throw new InvalidDataException("Every service must have a unique non-empty ID.");
            if (string.IsNullOrWhiteSpace(service.Name))
                throw new InvalidDataException("Service name is required.");
            if (string.IsNullOrWhiteSpace(service.Command))
                throw new InvalidDataException($"Command is required for '{service.Name}'.");
            if (service.StopTimeoutSeconds is < 1 or > 300)
                throw new InvalidDataException($"Stop timeout for '{service.Name}' must be between 1 and 300 seconds.");
            if (!string.IsNullOrWhiteSpace(service.OpenUrl) &&
                (!Uri.TryCreate(service.OpenUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                throw new InvalidDataException($"Open URL for '{service.Name}' must be HTTP or HTTPS.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in service.Environment)
            {
                if (string.IsNullOrWhiteSpace(variable.Name) || !names.Add(variable.Name))
                    throw new InvalidDataException($"Environment variable names for '{service.Name}' must be non-empty and unique.");
            }
        }
    }

    private static AppConfiguration CreateInitial() => new()
    {
        Services =
        [
            new ServiceDefinition
            {
                Name = "Python test server",
                Command = "python -c \"from http.server import ThreadingHTTPServer,SimpleHTTPRequestHandler; ThreadingHTTPServer(('127.0.0.1',58628),SimpleHTTPRequestHandler).serve_forever()\"",
                WorkingDirectory = Path.GetTempPath(),
                OpenUrl = "http://127.0.0.1:58628/"
            }
        ]
    };

    private static AppConfiguration Clone(AppConfiguration value) =>
        JsonSerializer.Deserialize<AppConfiguration>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void NormalizeAndDecrypt(AppConfiguration config)
    {
        foreach (var service in config.Services)
        {
            service.Environment ??= [];
            foreach (var item in service.Environment.Where(x => x.IsSecret))
                item.Value = Unprotect(item.ProtectedValue);
        }
        Validate(config);
    }
}
