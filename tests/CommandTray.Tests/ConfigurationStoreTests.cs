using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray.Tests;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"CommandTray-tests-{Guid.NewGuid():N}");

    [Fact]
    public void SecretValuesAreEncryptedOnDiskAndRestoredForCurrentUser()
    {
        var path = Path.Combine(_directory, "config.json");
        var store = new ConfigurationStore(path);
        var config = CreateConfiguration();
        config.Services[0].Environment.Add(new EnvironmentEntry { Name = "API_KEY", Value = "top-secret", IsSecret = true });

        store.Save(config);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("top-secret", json);
        Assert.Equal("top-secret", store.Load().Services[0].Environment[0].Value);
    }

    [Fact]
    public void ExportStripsSecretValues()
    {
        Directory.CreateDirectory(_directory);
        var export = Path.Combine(_directory, "export.json");
        var store = new ConfigurationStore(Path.Combine(_directory, "config.json"));
        var config = CreateConfiguration();
        config.Services[0].Environment.Add(new EnvironmentEntry { Name = "TOKEN", Value = "secret-value", IsSecret = true });

        store.ExportWithoutSecrets(config, export);

        var json = File.ReadAllText(export);
        Assert.DoesNotContain("secret-value", json);
        Assert.Contains("\"isSecret\": true", json);
    }

    [Fact]
    public void DuplicateEnvironmentNamesAreRejectedCaseInsensitively()
    {
        var config = CreateConfiguration();
        config.Services[0].Environment =
        [
            new EnvironmentEntry { Name = "PORT", Value = "1" },
            new EnvironmentEntry { Name = "port", Value = "2" }
        ];
        Assert.Throws<InvalidDataException>(() => ConfigurationStore.Validate(config));
    }

    private static AppConfiguration CreateConfiguration() => new()
    {
        Services = [new ServiceDefinition { Name = "Test", Command = "echo ok", WorkingDirectory = Path.GetTempPath() }]
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
