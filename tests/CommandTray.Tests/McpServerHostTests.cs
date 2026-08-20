using System.Net;
using System.Net.Sockets;
using CommandTray.Models;
using CommandTray.Services;
using ModelContextProtocol.Client;

namespace CommandTray.Tests;

public sealed class McpServerHostTests
{
    [Fact]
    public async Task StreamableHttpEndpointPublishesAllCommandTrayTools()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CommandTray-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var config = new AppConfiguration
            {
                Services = [new ServiceDefinition { Name = "Test", Command = "echo ok", WorkingDirectory = directory }]
            };
            var store = new ConfigurationStore(Path.Combine(directory, "config.json"));
            store.Save(config);
            await using var supervisor = new ProcessSupervisor(Path.Combine(directory, "logs"));
            var api = new CommandTrayApi(config, store, supervisor);
            await using var host = new McpServerHost(GetAvailablePort());
            await host.StartAsync(api);

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.CurrentEndpoint),
                TransportMode = HttpTransportMode.StreamableHttp
            });
            await using var client = await McpClient.CreateAsync(transport);
            var names = (await client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet();

            Assert.Equal(
                new HashSet<string>
                {
                    "get_manager", "list_services", "get_service", "create_service", "update_service",
                    "delete_service", "start_service", "stop_service", "restart_service", "get_service_log"
                },
                names);

            var result = await client.CallToolAsync(
                "list_services",
                new Dictionary<string, object?>(),
                cancellationToken: CancellationToken.None);
            Assert.NotEmpty(result.Content);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
