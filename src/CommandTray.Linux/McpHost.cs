using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CommandTray.Linux;

internal static class McpHost
{
    public static string GetToken(bool rotate)
    {
        using var mutex = LinuxCommands.SettingsLock();
        var settings = LocalSettings.Load();
        if (!rotate && settings.McpToken is { Length: > 0 } existing) return existing;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        LocalState.Write(LocalState.Settings, settings with { McpToken = token });
        return token;
    }

    public static async Task RunAsync(bool http, CancellationToken cancellationToken)
    {
        var tools = new LinuxMcpTools(new LinuxCommands());
        if (!http)
        {
            var stdio = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
            stdio.Logging.ClearProviders();
            await using var backend = new LinuxClientBackend();
            stdio.Services.AddMcpServer().WithStdioServerTransport().WithTools(tools)
                .WithTools(new LinuxFilesMcpTools(backend)).WithTools(LinuxSharedMcpTool.CreateAliases(backend));
            await stdio.Build().RunAsync(cancellationToken);
            return;
        }

        await ((IHost)BuildHttp(null)).RunAsync(cancellationToken);
    }

    public static async Task<WebApplication> StartHttpAsync(LinuxClientBackend backend, CancellationToken cancellationToken)
    {
        var app = BuildHttp(backend);
        try
        {
            await app.StartAsync(cancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static WebApplication BuildHttp(LinuxClientBackend? desktop)
    {
        GetToken(rotate: false);
        var settings = LocalSettings.Load();
        var address = $"http://127.0.0.1:{settings.McpPort}";
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ContentRootPath = LocalState.Root });
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, settings.McpPort));
        var backend = desktop ?? new LinuxClientBackend();
        var mcp = builder.Services.AddMcpServer().WithHttpTransport(transport => transport.Stateless = true)
            .WithTools(new LinuxMcpTools(new LinuxCommands())).WithTools(new LinuxFilesMcpTools(backend))
            .WithTools(LinuxSharedMcpTool.CreateAliases(backend));
        if (desktop is not null) mcp.WithTools(new LinuxClipboardMcpTools(desktop));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin;
            if (context.Request.Host.Host != "127.0.0.1" ||
                origin.Count > 0 && (origin.Count != 1 || origin[0] != address))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var bearer = context.Request.Headers.Authorization.ToString();
            var token = LocalSettings.Load().McpToken;
            if (token is null || !bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(bearer[7..]), Encoding.UTF8.GetBytes(token)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (desktop is not null) await desktop.HandleMcpRequestAsync(context, next);
            else await next(context);
        });
        app.MapMcp("/mcp");
        return app;
    }
}

internal sealed record LinuxToolResult(object? Data);

internal enum ProfileAction { Create, Update }
internal enum ServiceStateChange { Start, Stop, Restart }

internal sealed class LinuxMcpTools(LinuxCommands commands)
{
    private async Task<LinuxToolResult> RunAsync(CancellationToken cancellationToken, params string[] arguments)
        => new(await commands.ExecuteAsync(arguments, cancellationToken));

    [McpServerTool(Name = "list_services", ReadOnly = true, UseStructuredContent = true)]
    [Description("List command profiles and their states from the local Linux daemon.")]
    public Task<LinuxToolResult> ListServices(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "service", "list");

    [McpServerTool(Name = "get_service", ReadOnly = true, UseStructuredContent = true)]
    [Description("Look up a local Linux command profile by name or ID.")]
    public Task<LinuxToolResult> GetService(string name, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "service", "get", name);

    [McpServerTool(Name = "put_service", UseStructuredContent = true)]
    [Description("Create or update a local command profile. This does not start it.")]
    public Task<LinuxToolResult> PutService(ProfileAction action, string name, string executable,
        CancellationToken cancellationToken, string[]? arguments = null, string? workingDirectory = null,
        int? stopTimeoutSeconds = null, bool? autoStart = null)
    {
        var args = new List<string> { "service", action == ProfileAction.Create ? "create" : "update", name };
        if (workingDirectory is not null) args.AddRange(["--cwd", workingDirectory]);
        if (stopTimeoutSeconds is not null) args.AddRange(["--stop-timeout", stopTimeoutSeconds.Value.ToString()]);
        if (autoStart is not null) args.Add(autoStart.Value ? "--auto-start" : "--no-auto-start");
        args.AddRange(["--", executable]);
        if (arguments is not null) args.AddRange(arguments);
        return RunAsync(cancellationToken, args.ToArray());
    }

    [McpServerTool(Name = "change_service_state", Destructive = true, UseStructuredContent = true)]
    [Description("Start, stop or restart a configured service on this Linux device.")]
    public Task<LinuxToolResult> ChangeService(string name, ServiceStateChange state, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "service", state.ToString().ToLowerInvariant(), name);

    [McpServerTool(Name = "get_service_log", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read recent lines from the named local service log.")]
    public Task<LinuxToolResult> GetServiceLog(string name, CancellationToken cancellationToken, int lines = 100)
        => RunAsync(cancellationToken, "service", "logs", name, lines.ToString());

    [McpServerTool(Name = "delete_service", Destructive = true, UseStructuredContent = true)]
    [Description("Delete the named stopped service profile.")]
    public Task<LinuxToolResult> DeleteService(string name, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "service", "delete", name);

    [McpServerTool(Name = "list_terminals", ReadOnly = true, UseStructuredContent = true)]
    [Description("List Linux terminal sessions owned by the daemon.")]
    public Task<LinuxToolResult> ListTerminals(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "terminal", "list");

    [McpServerTool(Name = "open_terminal", OpenWorld = true, UseStructuredContent = true)]
    [Description("Open a terminal session on this Linux device in an absolute working directory.")]
    public Task<LinuxToolResult> OpenTerminal(string workingDirectory, CancellationToken cancellationToken,
        string? executable = null, string[]? arguments = null)
        => RunAsync(cancellationToken, ["terminal", "open", workingDirectory, executable ?? ServiceRuntime.DefaultShell(), .. (arguments ?? [])]);

    [McpServerTool(Name = "write_terminal", OpenWorld = true, UseStructuredContent = true)]
    [Description("Write input to an owned terminal. Include a newline to submit a command.")]
    public Task<LinuxToolResult> WriteTerminal(string terminalId, string input, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "terminal", "write", terminalId, input);

    [McpServerTool(Name = "read_terminal", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read recent output from an owned terminal.")]
    public Task<LinuxToolResult> ReadTerminal(string terminalId, CancellationToken cancellationToken, int lines = 200)
        => RunAsync(cancellationToken, "terminal", "read", terminalId, lines.ToString());

    [McpServerTool(Name = "resize_terminal", UseStructuredContent = true)]
    [Description("Set the character dimensions of an existing Linux PTY.")]
    public Task<LinuxToolResult> ResizeTerminal(string terminalId, int columns, int rows, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "terminal", "resize", terminalId, columns.ToString(), rows.ToString());

    [McpServerTool(Name = "close_terminal", Destructive = true, UseStructuredContent = true)]
    [Description("Close a terminal session owned by the daemon.")]
    public Task<LinuxToolResult> CloseTerminal(string terminalId, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "terminal", "close", terminalId);

    [McpServerTool(Name = "get_settings", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read Linux settings and credential configuration status.")]
    public Task<LinuxToolResult> GetSettings(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "config", "get");

    [McpServerTool(Name = "set_relay_address", Destructive = true, UseStructuredContent = true)]
    [Description("Set this device's relay URL. Changing it clears the prior device pairing.")]
    public Task<LinuxToolResult> SetRelay(string address, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "config", "set", "server", address);

    [McpServerTool(Name = "set_mcp_http_port", UseStructuredContent = true)]
    [Description("Set the loopback MCP HTTP port for the next server startup.")]
    public Task<LinuxToolResult> SetMcpPort(int port, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "config", "set", "mcp-port", port.ToString());

    [McpServerTool(Name = "list_spaces", ReadOnly = true, UseStructuredContent = true)]
    [Description("List spaces staged on this Linux device.")]
    public Task<LinuxToolResult> ListSpaces(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "list");

    [McpServerTool(Name = "read_workspace", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read the full local workspace document. SSH private keys are represented by encrypted envelopes.")]
    public Task<LinuxToolResult> GetWorkspace(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "get");

    [McpServerTool(Name = "read_space", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read one space, including its components and layout.")]
    public Task<LinuxToolResult> ReadSpace(string space, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "show", space);

    [McpServerTool(Name = "create_space", UseStructuredContent = true)]
    [Description("Create a local space and stage it for synchronization.")]
    public Task<LinuxToolResult> CreateSpace(string name, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "create", name);

    [McpServerTool(Name = "activate_space", UseStructuredContent = true)]
    [Description("Set the active space in the local workspace document.")]
    public Task<LinuxToolResult> ActivateSpace(string space, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "use", space);

    [McpServerTool(Name = "rename_space", UseStructuredContent = true)]
    [Description("Rename a space.")]
    public Task<LinuxToolResult> RenameSpace(string space, string name, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "rename", space, name);

    [McpServerTool(Name = "delete_space", Destructive = true, UseStructuredContent = true)]
    [Description("Remove a space and its components from the local workspace.")]
    public Task<LinuxToolResult> DeleteSpace(string space, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "remove", space);

    [McpServerTool(Name = "put_component", UseStructuredContent = true)]
    [Description("Set a component by ID using the protocol component object with kind, layout and data.")]
    public Task<LinuxToolResult> PutComponent(string space, JsonObject component, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "put-item", space, component.ToJsonString());

    [McpServerTool(Name = "move_component", UseStructuredContent = true)]
    [Description("Set the position and dimensions of a workspace component.")]
    public Task<LinuxToolResult> MoveComponent(string space, string componentId, double x, double y, double width, double height,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "move-item", space, componentId,
            new JsonObject { ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height }.ToJsonString());

    [McpServerTool(Name = "delete_component", Destructive = true, UseStructuredContent = true)]
    [Description("Remove one component from a local space.")]
    public Task<LinuxToolResult> DeleteComponent(string space, string componentId, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "remove-item", space, componentId);

    [McpServerTool(Name = "replace_workspace", Destructive = true, UseStructuredContent = true)]
    [Description("Stage a complete workspace document with schemaVersion 1.")]
    public Task<LinuxToolResult> ReplaceWorkspace(JsonObject document, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "workspace", "replace", document.ToJsonString());

    [McpServerTool(Name = "pair_device", OpenWorld = true, UseStructuredContent = true)]
    [Description("Pair the local Linux client with its relay using the locally configured pairing code.")]
    public Task<LinuxToolResult> Pair(string server, string name, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "sync", "pair", server, name);

    [McpServerTool(Name = "sync_status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read local pairing, revision and pending-work status.")]
    public Task<LinuxToolResult> SyncStatus(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "sync", "status");

    [McpServerTool(Name = "sync_push", OpenWorld = true, UseStructuredContent = true)]
    [Description("Push local edits to the relay without overwriting a conflicting remote revision.")]
    public Task<LinuxToolResult> Push(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "sync", "push");

    [McpServerTool(Name = "sync_pull", UseStructuredContent = true)]
    [Description("Fetch a relay workspace. Set replaceLocal to archive and replace unsynchronized edits.")]
    public Task<LinuxToolResult> Pull(CancellationToken cancellationToken, bool replaceLocal = false)
        => RunAsync(cancellationToken, replaceLocal ? ["sync", "pull", "--replace"] : ["sync", "pull"]);

    [McpServerTool(Name = "sync_resolve", OpenWorld = true, UseStructuredContent = true)]
    [Description("Resolve a retained workspace conflict in favor of local or remote.")]
    public Task<LinuxToolResult> Resolve(string choice, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "sync", "resolve", choice);

    [McpServerTool(Name = "list_devices", ReadOnly = true, UseStructuredContent = true)]
    [Description("List paired devices on the configured relay.")]
    public Task<LinuxToolResult> ListDevices(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "sync", "devices");

    [McpServerTool(Name = "revoke_device", Destructive = true, UseStructuredContent = true)]
    [Description("Remove a paired device from the relay.")]
    public Task<LinuxToolResult> RevokeDevice(string id, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "sync", "remove-device", id);

    [McpServerTool(Name = "read_clipboard", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read the synchronized text clipboard.")]
    public Task<LinuxToolResult> ReadClipboard(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "clip", "status");

    [McpServerTool(Name = "write_clipboard", OpenWorld = true, UseStructuredContent = true)]
    [Description("Update the paired devices' synchronized text clipboard.")]
    public Task<LinuxToolResult> WriteClipboard(string text, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "clip", "put", text);

    [McpServerTool(Name = "list_shared_files", ReadOnly = true, UseStructuredContent = true)]
    [Description("List files shared through the paired relay.")]
    public Task<LinuxToolResult> ListFiles(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "share", "list");

    [McpServerTool(Name = "upload_shared_file", OpenWorld = true, UseStructuredContent = true)]
    [Description("Upload an explicitly selected local file to the paired relay.")]
    public Task<LinuxToolResult> Upload(string path, CancellationToken cancellationToken, string? folder = null)
        => RunAsync(cancellationToken, "share", "put", path, folder ?? "");

    [McpServerTool(Name = "download_shared_file", UseStructuredContent = true)]
    [Description("Download a relay file to a new local output file.")]
    public Task<LinuxToolResult> Download(string id, string path, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "share", "get", id, path);

    [McpServerTool(Name = "delete_shared_file", Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Delete a file from the shared relay.")]
    public Task<LinuxToolResult> DeleteFile(string id, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "share", "remove", id);

    [McpServerTool(Name = "list_ssh_hosts", ReadOnly = true, UseStructuredContent = true)]
    [Description("List SSH host profiles in the local workspace document.")]
    public Task<LinuxToolResult> ListHosts(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "host", "list");

    [McpServerTool(Name = "get_ssh_host", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read an SSH host profile by ID.")]
    public Task<LinuxToolResult> GetHost(string id, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "host", "get", id);

    [McpServerTool(Name = "put_ssh_host", UseStructuredContent = true)]
    [Description("Store SSH connection metadata using an optional protected identity ID.")]
    public Task<LinuxToolResult> PutHost(string id, string name, string address, string username,
        CancellationToken cancellationToken, int port = 22, string? identityId = null)
        => RunAsync(cancellationToken, identityId is null
            ? ["host", "put", id, name, address, username, port.ToString()]
            : ["host", "put", id, name, address, username, port.ToString(), identityId]);

    [McpServerTool(Name = "delete_ssh_host", Destructive = true, UseStructuredContent = true)]
    [Description("Remove an SSH host profile by ID.")]
    public Task<LinuxToolResult> DeleteHost(string id, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "host", "remove", id);

    [McpServerTool(Name = "connect_ssh_host", OpenWorld = true, UseStructuredContent = true)]
    [Description("Open a terminal for an explicitly configured SSH host. Use terminal read/write to handle host verification prompts.")]
    public Task<LinuxToolResult> ConnectHost(string id, string workingDirectory, CancellationToken cancellationToken)
    {
        var profile = LinuxCommands.SshProfile(id, workingDirectory);
        return RunAsync(cancellationToken, ["terminal", "open", profile.WorkingDirectory, profile.Executable, .. profile.Arguments]);
    }

    [McpServerTool(Name = "list_ssh_identities", ReadOnly = true, UseStructuredContent = true)]
    [Description("List identity IDs, names and local availability without returning key bytes.")]
    public Task<LinuxToolResult> ListIdentities(CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "identity", "list");

    [McpServerTool(Name = "import_ssh_identity", UseStructuredContent = true)]
    [Description("Encrypt an explicitly selected private key with the locally unlocked vault and stage it for sync.")]
    public Task<LinuxToolResult> ImportIdentity(string id, string path, CancellationToken cancellationToken, string? name = null)
        => RunAsync(cancellationToken, "identity", "import", id, path, name ?? id);

    [McpServerTool(Name = "install_ssh_identity", UseStructuredContent = true)]
    [Description("Decrypt a synchronized identity into the private local key directory using the unlocked vault.")]
    public Task<LinuxToolResult> InstallIdentity(string id, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "identity", "install", id);

    [McpServerTool(Name = "remove_ssh_identity", Destructive = true, UseStructuredContent = true)]
    [Description("Remove a synchronized key envelope and the installed copy on this device.")]
    public Task<LinuxToolResult> RemoveIdentity(string id, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "identity", "remove", id);
}
