using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
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
            var backend = new WindowsClientBackend(desktop, api, store, configuration);
            desktop.AttachShell(new WindowsDesktopShell(backend, System.Drawing.SystemIcons.Application), backend);
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
            Assert.Contains("item_move", names);
            Assert.Contains("workspace_read", names);
            Assert.DoesNotContain("mcp_config", names);

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
            var itemId = Assert.Single(Assert.Single(desktop.Workspace.Snapshot().Spaces, entry => entry.Id == space.Id).Items).Id;
            var moved = await client.CallToolAsync("item_move", new Dictionary<string, object?>
            {
                ["spaceId"] = space.Id, ["itemId"] = itemId, ["x"] = 36, ["y"] = 42, ["width"] = 420, ["height"] = 320
            }, cancellationToken: timeout.Token);
            Assert.NotEqual(true, moved.IsError);

            var reloaded = new WorkspaceStore(directory.FullName).Snapshot();
            var item = Assert.Single(Assert.Single(reloaded.Spaces, item => item.Id == space.Id).Items);
            Assert.Equal("Verify the installer", item.Data["text"]?.GetValue<string>());
            Assert.Equal(new System.Drawing.Rectangle(36, 42, 420, 320), item.Bounds);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void RelayWorkspacePreservesDevicePathWhileApplyingRemoteChanges()
    {
        var directory = Directory.CreateTempSubdirectory("commandtray-desktop-");
        try
        {
            var workspace = new WorkspaceStore(directory.FullName);
            var space = workspace.Snapshot().ActiveSpaceId!;
            var id = workspace.AddItem(space, "files", "Files", new JsonObject { ["path"] = @"C:\Work" });
            var (portable, generation) = workspace.ReadForSync();
            var remoteItem = portable["spaces"]![0]!["items"]![0]!;
            Assert.Null(remoteItem["data"]!["path"]);
            remoteItem["data"]!["path"] = "/home/other";
            remoteItem["title"] = "Remote title";
            workspace.RecoverFromRelay(portable, generation, restoreLocalPaths: true);

            var restored = Assert.Single(Assert.Single(new WorkspaceStore(directory.FullName).Snapshot().Spaces).Items);
            Assert.Equal(id, restored.Id);
            Assert.Equal("Remote title", restored.Title);
            Assert.Equal(@"C:\Work", restored.Data["path"]?.GetValue<string>());
        }
        finally { directory.Delete(recursive: true); }
    }
}
