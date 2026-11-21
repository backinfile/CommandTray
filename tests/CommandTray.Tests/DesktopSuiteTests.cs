using System.Net;
using System.Net.Sockets;
using CommandTray.Models;
using CommandTray.Services;
using ModelContextProtocol.Client;

namespace CommandTray.Tests;

public sealed class DesktopSuiteTests
{
    [Fact]
    public async Task AuthenticatedDesktopMcpPersistsComponentsWithoutChangingLegacyTools()
    {
        var directory = Directory.CreateTempSubdirectory("commandtray-desktop-");
        try
        {
            var store = new ConfigurationStore(Path.Combine(directory.FullName, "config.json"));
            var configuration = new AppConfiguration();
            await using var supervisor = new ProcessSupervisor(Path.Combine(directory.FullName, "logs"));
            var api = new CommandTrayApi(configuration, store, supervisor);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var desktop = new DesktopRuntime(directory.FullName);
            await using var host = new McpServerHost(port);
            await host.StartDesktopAsync(api, desktop);
            using var http = new HttpClient();
            using var unauthorized = await http.GetAsync(host.CurrentEndpoint);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var wrongOrigin = new HttpRequestMessage(HttpMethod.Get, host.CurrentEndpoint);
            wrongOrigin.Headers.Add("Origin", "https://example.invalid");
            wrongOrigin.Headers.Add("Authorization", "Bearer " + desktop.McpCredential);
            using var rejected = await http.SendAsync(wrongOrigin);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.CurrentEndpoint),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + desktop.McpCredential }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            var names = (await client.ListToolsAsync(cancellationToken: timeout.Token)).Select(tool => tool.Name).ToHashSet();
            Assert.Contains("start_service", names);
            Assert.Contains("add_workspace_component", names);
            Assert.Contains("start_terminal_session", names);
            Assert.Contains("list_shared_files", names);

            var create = await client.CallToolAsync("create_workspace", new Dictionary<string, object?> { ["name"] = "Ship" },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, create.IsError);
            var space = Assert.Single(desktop.Workspace.Snapshot().Spaces, item => item.Name == "Ship");
            var add = await client.CallToolAsync("add_workspace_component", new Dictionary<string, object?>
            {
                ["spaceId"] = space.Id, ["kind"] = "note", ["title"] = "Release",
                ["data"] = new Dictionary<string, object?> { ["text"] = "Verify the installer" }
            }, cancellationToken: timeout.Token);
            Assert.NotEqual(true, add.IsError);
            var reloaded = new WorkspaceStore(directory.FullName).Snapshot();
            var item = Assert.Single(Assert.Single(reloaded.Spaces, item => item.Id == space.Id).Items);
            Assert.Equal("Verify the installer", item.Data["text"]?.GetValue<string>());
        }
        finally { directory.Delete(recursive: true); }
    }
}
