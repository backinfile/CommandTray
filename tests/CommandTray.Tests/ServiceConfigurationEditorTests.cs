using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray.Tests;

public sealed class ServiceConfigurationEditorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"CommandTray-editor-{Guid.NewGuid():N}");

    [Fact]
    public void ValidationFailurePreservesTheLiveDefinitionAndSavedConfiguration()
    {
        var original = new ServiceDefinition { Name = "Original", Command = "echo original" };
        var configuration = new AppConfiguration { Services = [original] };
        var store = new ConfigurationStore(Path.Combine(_directory, "config.json"));
        store.Save(configuration);
        var invalid = new ServiceDefinition { Id = original.Id, Name = "Edited", Command = "" };

        Assert.Throws<InvalidDataException>(() => ServiceConfigurationEditor.Save(configuration, store, original, invalid));

        Assert.Equal("Original", original.Name);
        Assert.Equal("echo original", original.Command);
        Assert.Equal("Original", store.Load().Services.Single().Name);
    }

    [Fact]
    public void PersistenceFailurePreservesTheLiveDefinition()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "This file prevents creating a directory.");
        var store = new ConfigurationStore(Path.Combine(blocker, "config.json"));
        var original = new ServiceDefinition { Name = "Original", Command = "echo original" };
        var configuration = new AppConfiguration { Services = [original] };
        var edited = new ServiceDefinition { Id = original.Id, Name = "Edited", Command = "echo edited" };

        Assert.Throws<IOException>(() => ServiceConfigurationEditor.Save(configuration, store, original, edited));

        Assert.Same(original, configuration.Services.Single());
        Assert.Equal("Original", original.Name);
    }

    [Fact]
    public void SavePreservesSupervisorReferencesAndEncryptsNewSecrets()
    {
        var original = new ServiceDefinition { Name = "Original", Command = "echo original" };
        var configuration = new AppConfiguration { Language = "zh", McpEnabled = false, Services = [original] };
        var store = new ConfigurationStore(Path.Combine(_directory, "config.json"));
        var edited = new ServiceDefinition
        {
            Id = original.Id, Name = "Edited", Command = "echo edited",
            Environment = ServiceConfigurationEditor.ParseEnvironment(["PORT=3000", "*TOKEN=private=token"])
        };

        ServiceConfigurationEditor.Save(configuration, store, original, edited);

        Assert.Same(original, configuration.Services.Single());
        Assert.Equal("Edited", original.Name);
        Assert.Equal("private=token", store.Load().Services.Single().Environment[1].Value);
        Assert.DoesNotContain("private=token", File.ReadAllText(store.PathName));
        Assert.Equal("zh", store.Load().Language);
        Assert.False(store.Load().McpEnabled);
    }

    [Fact]
    public void NewServiceIsAddedOnlyAfterItCanBeSaved()
    {
        var configuration = new AppConfiguration();
        var draft = new ServiceDefinition { Name = "Draft" };
        var store = new ConfigurationStore(Path.Combine(_directory, "config.json"));
        Assert.Throws<InvalidDataException>(() => ServiceConfigurationEditor.Save(configuration, store, draft, draft));
        Assert.Empty(configuration.Services);

        var edited = new ServiceDefinition { Id = draft.Id, Name = "New API", Command = "echo API" };
        ServiceConfigurationEditor.Save(configuration, store, draft, edited);
        Assert.Same(draft, configuration.Services.Single());
        Assert.Equal("New API", store.Load().Services.Single().Name);
    }

    [Theory]
    [InlineData("*=value")]
    [InlineData(" =value")]
    [InlineData("TOKEN")]
    public void MalformedEnvironmentEntriesAreRejected(string entry) =>
        Assert.Throws<InvalidDataException>(() => ServiceConfigurationEditor.ParseEnvironment([entry]));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
