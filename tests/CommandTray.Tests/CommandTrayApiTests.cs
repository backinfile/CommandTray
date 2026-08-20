using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray.Tests;

public sealed class CommandTrayApiTests
{
    [Fact]
    public async Task CrudPersistsChangesAndNeverReturnsSecretValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CommandTray-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ConfigurationStore(Path.Combine(directory, "config.json"));
            var config = new AppConfiguration
            {
                Services =
                [
                    new ServiceDefinition
                    {
                        Name = "Original",
                        Command = "echo original",
                        WorkingDirectory = directory,
                        Environment = [new EnvironmentEntry { Name = "TOKEN", Value = "secret", IsSecret = true }]
                    }
                ]
            };
            store.Save(config);
            await using var supervisor = new ProcessSupervisor(Path.Combine(directory, "logs"));
            var api = new CommandTrayApi(config, store, supervisor);
            var changes = 0;
            api.ConfigurationChanged += (_, _) => changes++;

            var listed = Assert.Single(api.ListServices());
            var secret = Assert.Single(listed.Environment);
            Assert.Null(secret.Value);
            Assert.True(secret.HasValue);

            var updated = await api.UpdateServiceAsync(listed.Id.ToString(), new ServiceInput
            {
                Name = "Updated",
                Command = "echo updated",
                WorkingDirectory = directory,
                Environment = [new EnvironmentVariableInput { Name = "TOKEN", IsSecret = true }]
            });
            Assert.Equal("Updated", updated.Name);
            Assert.Equal("secret", config.Services[0].Environment[0].Value);

            var created = await api.CreateServiceAsync(new ServiceInput
            {
                Name = "Created",
                Command = "echo created",
                WorkingDirectory = directory
            });
            Assert.Equal(2, store.Load().Services.Count);

            var deleted = await api.DeleteServiceAsync(created.Id.ToString());
            Assert.True(deleted.Success);
            Assert.Single(store.Load().Services);
            Assert.Equal(3, changes);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
