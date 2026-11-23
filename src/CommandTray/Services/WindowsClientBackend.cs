using System.Text.Json;
using System.Text.Json.Nodes;
using CommandTray.Core;
using CommandTray.Models;

namespace CommandTray.Services;

internal sealed class WindowsClientBackend(
    DesktopRuntime desktop, CommandTrayApi commands, ConfigurationStore configurationStore, AppConfiguration configuration)
    : IClientBackend
{
    private readonly DesktopMcpTools _actions = new(desktop);
    private readonly object _templateGate = new();
    private Dictionary<string, JsonObject>? _publishedCommands;
    private WorkspaceStore Workspace => desktop.Workspace;
    private DesktopRelay Relay => desktop.Relay;

    public ClientFeatures Features => ClientFeatures.LocalFiles | ClientFeatures.Terminals | ClientFeatures.Commands |
        ClientFeatures.Layout | ClientFeatures.Capture | ClientFeatures.Pet | ClientFeatures.Ssh | ClientFeatures.ClipboardHistory;

    public event EventHandler? SettingsChanged;

    public async ValueTask<JsonNode?> ExecuteAsync(string operation, JsonObject arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? Text(string name) => WorkspaceStore.Text(arguments[name]);
        string Required(string name) => Text(name) ?? throw new ArgumentException($"Specify {name}.");
        string Id() => Required("id");
        string Space() => Required("spaceId");
        string Item() => Required("itemId");
        string PathArgument() => Text("path") ?? LocalUri(Required("localUri"));
        int Number(string name, int fallback) => WorkspaceStore.Number(arguments[name], fallback);
        bool? Flag(string name) => arguments[name]?.GetValue<bool>();

        switch (operation)
        {
            case "workspace_read": return Workspace.ReadDocument().Document;
            case "space_create": return Json(new { id = Workspace.AddSpace(Required("name")) });
            case "space_rename": return Json(_actions.RenameSpace(Space(), Required("name")));
            case "space_delete": return Json(_actions.DeleteSpace(Space()));
            case "space_activate": return Json(_actions.ActivateSpace(Space()));
            case "item_save":
                var item = arguments["item"] as JsonObject ?? throw new ArgumentException("Specify item.");
                var itemId = Workspace.SaveItem(Space(), item);
                desktop.ReapRemovedTerminals();
                return Json(new { id = itemId });
            case "item_move": return Json(_actions.MoveItem(Space(), Item(),
                Number("x", 16), Number("y", 16), Number("width", 392), Number("height", 278)));
            case "item_remove": return Json(_actions.RemoveItem(Space(), Item()));
            case "relay_status": return Json(Relay.Status());
            case "relay_devices": return await Relay.ListDevicesAsync(cancellationToken);
            case "relay_pair": return Json(await _actions.Pair(Required("server"), Required("deviceName"), Required("pairingCode"), cancellationToken));
            case "relay_unpair": return Json(await _actions.Disconnect(cancellationToken));
            case "sync_push": return Json(await _actions.Push(cancellationToken));
            case "sync_pull": return Json(await _actions.Pull(cancellationToken));
            case "sync_resolve": return Json(await _actions.Resolve(Required("choice"), cancellationToken));
            case "files_list": return await Relay.ListFilesAsync(cancellationToken);
            case "file_upload": return await Relay.UploadAsync(PathArgument(), Text("folder"), cancellationToken, Text("name"));
            case "file_download": return Json(await _actions.Download(Id(), PathArgument(), cancellationToken, Flag("overwrite") ?? false));
            case "file_delete": return Json(await _actions.DeleteShared(Id(), cancellationToken));
            case "clipboard_read": return await Relay.GetClipboardAsync(cancellationToken);
            case "clipboard_relay_history": return await Relay.GetClipboardHistoryAsync(cancellationToken);
            case "clipboard_send": return await Relay.PutClipboardAsync(Required("text"), cancellationToken);
            case "clipboard_receive": return await _actions.ReceiveToLocalClipboard(cancellationToken);
            case "clipboard_history": return Json(desktop.ClipboardHistory());
            case "clipboard_local_read": return Json(new { text = await _actions.ReadClipboard(cancellationToken) });
            case "clipboard_local_write":
                await desktop.OnUiAsync(() =>
                {
                    desktop.WriteClipboard(Required("text"), Flag("sensitive") ?? false);
                    return true;
                }, cancellationToken);
                return Json(new { success = true });
            case "settings_read": return ReadSettings();
            case "settings_save": return await desktop.OnUiAsync(() => SaveSettings(Text("language"), Flag("runAtLogin"),
                Flag("mcpEnabled"), arguments["sidebarWidth"]?.GetValue<int>(), Flag("sidebarCollapsed"),
                Flag("clipboardAutoSync")), cancellationToken);
            case "mcp_config": return new JsonObject
            {
                ["url"] = McpServerHost.Endpoint,
                ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {desktop.McpCredential}" }
            };
            case "commands_list": return Json(commands.ListServices());
            case "command_save": return Json(await SaveCommandAsync(arguments, cancellationToken));
            case "command_start": return Json(await commands.StartServiceAsync(Id(), cancellationToken));
            case "command_stop": return Json(await commands.StopServiceAsync(Id(), cancellationToken));
            case "command_restart": return Json(await commands.RestartServiceAsync(Id(), cancellationToken));
            case "command_delete": return Json(await commands.DeleteServiceAsync(Id(), cancellationToken));
            case "command_log": return Json(new { text = commands.GetServiceLog(Id(), Number("lines", 300)).Content });
            case "local_list":
                var path = Path.GetFullPath(Text("path") is { Length: > 0 } directory ? directory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                return Json(new { path, entries = LocalFiles.List(path).Select(entry =>
                    new { entry.Name, entry.Path, isDirectory = entry.Directory, entry.Size }) });
            case "local_preview": return Json(await LocalFiles.PreviewAsync(Required("path"), cancellationToken));
            case "local_copy":
                LocalFiles.Copy(Required("source"), Required("destination"), Flag("overwrite") ?? false);
                return Json(new { success = true });
            case "local_mkdir": return Json(_actions.MakeFolder(Required("path")));
            case "terminal_open": return await OpenTerminalAsync(Item(), Text("shell"), Text("workingDirectory"), Text("hostId"),
                Text("passphrase"), cancellationToken);
            case "terminal_write": return Json(_actions.SendTerminal(Item(), Required("text")));
            case "terminal_read": return ReadTerminal(Item());
            case "terminal_resize": return Json(_actions.ResizeTerminal(Item(), Number("columns", 80), Number("rows", 24)));
            case "terminal_close": return Json(_actions.StopTerminal(TerminalSpace(Item()), Item()));
            case "capture_list": return Json(desktop.Media.Captures());
            case "capture_take": return Json(await _actions.Screenshot(cancellationToken, Integer(arguments["x"]), Integer(arguments["y"]),
                Integer(arguments["width"]), Integer(arguments["height"])));
            case "capture_select": return Json(await _actions.SelectScreenshot(cancellationToken));
            case "capture_pin": return Json(await _actions.Pin(Required("path"), cancellationToken));
            case "capture_annotate": return Json(await _actions.AnnotateScreenshot(Required("path"), cancellationToken));
            case "capture_open": return Json(_actions.OpenCapture(Required("path")));
            case "capture_record_start": return Json(await _actions.StartRecording(cancellationToken, Number("frameRate", 24)));
            case "capture_record_stop": return Json(await _actions.StopRecording(cancellationToken));
            case "capture_ocr": return Json(await _actions.Ocr(Required("path"), cancellationToken, Text("language") ?? "eng"));
            case "pet_set": return Json(await _actions.Pet(Flag("visible") ?? throw new ArgumentException("Specify visible."), cancellationToken));
            case "notify": return Json(await _actions.ShowNotice(Required("message"), cancellationToken, Text("spaceId"), Text("itemId")));
            case "ssh_save": return Json(_actions.SaveHost(Required("name"), Required("address"), Required("username"),
                Number("port", 22), Text("id"), Text("keyId")));
            case "ssh_delete": return Json(_actions.DeleteHost(Id()));
            case "ssh_import": return Json(new { id = desktop.ImportSshKey(PathArgument(), Required("name"), Required("passphrase")) });
            case "ssh_keys": return Json(desktop.Workspace.ListSshKeys());
            case "ssh_key_delete": return Json(_actions.DeleteKey(Id()));
            default: throw new NotSupportedException($"Unknown operation '{operation}'.");
        }
    }

    public void PublishCommandTemplates()
    {
        lock (_templateGate)
        {
            var current = commands.ListServices().Select(command => (Id: command.Id.ToString(), Template: new JsonObject
            {
                ["id"] = command.Id.ToString(),
                ["name"] = command.Name,
                ["command"] = command.Command,
                // Paths and environment values are local bindings and credentials.
                ["stopCommand"] = command.StopCommand,
                ["stopTimeoutSeconds"] = command.StopTimeoutSeconds
            })).ToDictionary(entry => entry.Id, entry => entry.Template, StringComparer.Ordinal);
            if (_publishedCommands is null)
            {
                _publishedCommands = Workspace.ReadDocument().Document["commands"] is JsonArray ? current : new(StringComparer.Ordinal);
            }
            var changed = current.Where(entry => !_publishedCommands.TryGetValue(entry.Key, out var prior) ||
                !JsonNode.DeepEquals(prior, entry.Value)).ToDictionary(StringComparer.Ordinal);
            var removed = _publishedCommands.Keys.Where(id => !current.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
            Workspace.UpdateCommandTemplates(changed, removed);
            _publishedCommands = current;
        }
    }

    public JsonObject ReadSettings()
    {
        lock (configuration)
            return Json(new
            {
                language = configuration.Language,
                runAtLogin = configuration.RunAtLogin,
                mcpEnabled = configuration.McpEnabled,
                clipboardAutoSync = configuration.ClipboardAutoSync,
                sidebarWidth = configuration.SidebarWidth,
                sidebarCollapsed = configuration.SidebarCollapsed,
                mcpEndpoint = McpServerHost.Endpoint
            })!.AsObject();
    }

    public JsonObject SaveSettings(string? language, bool? runAtLogin, bool? mcpEnabled,
        int? sidebarWidth = null, bool? sidebarCollapsed = null, bool? clipboardAutoSync = null)
    {
        if (language is not null and not ("auto" or "en" or "zh"))
            throw new ArgumentException("Language must be auto, en or zh.");
        lock (configuration)
        {
            var priorLanguage = configuration.Language;
            var priorLogin = configuration.RunAtLogin;
            var priorMcp = configuration.McpEnabled;
            var priorClipboard = configuration.ClipboardAutoSync;
            var priorSidebar = (configuration.SidebarWidth, configuration.SidebarCollapsed);
            var priorRegistryValue = runAtLogin.HasValue && AutoStartService.IsEnabled();
            try
            {
                if (language is not null) configuration.Language = language;
                if (runAtLogin is { } login)
                {
                    AutoStartService.SetEnabled(login);
                    configuration.RunAtLogin = login;
                }
                if (mcpEnabled is { } enabled) configuration.McpEnabled = enabled;
                if (clipboardAutoSync is { } syncClipboard) configuration.ClipboardAutoSync = syncClipboard;
                if (sidebarWidth.HasValue) configuration.SidebarWidth = sidebarWidth;
                if (sidebarCollapsed.HasValue) configuration.SidebarCollapsed = sidebarCollapsed.Value;
                configurationStore.Save(configuration);
            }
            catch
            {
                configuration.Language = priorLanguage;
                configuration.RunAtLogin = priorLogin;
                configuration.McpEnabled = priorMcp;
                configuration.ClipboardAutoSync = priorClipboard;
                (configuration.SidebarWidth, configuration.SidebarCollapsed) = priorSidebar;
                if (runAtLogin.HasValue) AutoStartService.SetEnabled(priorRegistryValue);
                throw;
            }
        }
        if (language is not null) Localizer.Configure(language);
        if (language is not null || runAtLogin.HasValue || mcpEnabled.HasValue)
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        return ReadSettings();
    }

    private async Task<ServiceView> SaveCommandAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (WorkspaceStore.Text(arguments["id"]) is { Length: > 0 } id)
            return await commands.UpdateServiceAsync(id, current => ServicePatch(arguments, current), cancellationToken);
        return await commands.CreateServiceAsync(ServicePatch(arguments, null), cancellationToken);
    }

    private static ServiceInput ServicePatch(JsonObject args, ServiceView? current)
    {
        string? Text(string name) => WorkspaceStore.Text(args[name]);
        bool? Flag(string name) => args[name]?.GetValue<bool>();
        return new ServiceInput
        {
            Name = Text("name") ?? current?.Name ?? "",
            Command = Text("command") ?? current?.Command ?? "",
            WorkingDirectory = Text("workingDirectory") ?? current?.WorkingDirectory,
            OpenUrl = Text("openUrl") ?? current?.OpenUrl,
            StartWithManager = Flag("startWithManager") ?? current?.StartWithManager ?? false,
            RestartOnUnexpectedExit = Flag("restartOnUnexpectedExit") ?? current?.RestartOnUnexpectedExit ?? false,
            StopCommand = Text("stopCommand") ?? current?.StopCommand,
            StopTimeoutSeconds = WorkspaceStore.Number(args["stopTimeoutSeconds"], current?.StopTimeoutSeconds ?? 5),
            Environment = args["environment"] is JsonArray env
                ? env.Deserialize<List<EnvironmentVariableInput>>(JsonSerializerOptions.Web)
                : current?.Environment.Select(x => new EnvironmentVariableInput { Name = x.Name, Value = x.Value, IsSecret = x.IsSecret }).ToList()
        };
    }

    private async Task<JsonNode?> OpenTerminalAsync(string itemId, string? shell, string? directory, string? hostId,
        string? passphrase, CancellationToken cancellationToken)
    {
        var spaceId = TerminalSpace(itemId);
        if (hostId is { Length: > 0 })
        {
            if (!await desktop.OnUiAsync(() => desktop.OpenSsh(itemId, hostId, passphrase), cancellationToken))
                return Json(new { status = "cancelled" });
            try
            {
                Workspace.EditData(spaceId, itemId, data =>
                {
                    data["hostId"] = hostId;
                    data["shell"] = "ssh";
                });
            }
            catch
            {
                desktop.CloseTerminal(itemId);
                throw;
            }
            return Json(new { status = "started" });
        }
        return Json(_actions.StartTerminal(spaceId, itemId, string.IsNullOrWhiteSpace(shell) ? "cmd" : shell, directory));
    }

    private JsonNode? ReadTerminal(string itemId)
    {
        var session = desktop.Terminal(itemId);
        var text = session?.Output ?? Workspace.Snapshot().Spaces.SelectMany(space => space.Items)
            .Where(item => item.Id == itemId && item.Kind == "terminal")
            .Select(item => WorkspaceStore.Text(item.Data["history"])).FirstOrDefault() ?? "";
        return Json(new { active = session?.Active ?? false, text });
    }

    private string TerminalSpace(string itemId) => Workspace.Snapshot().Spaces.FirstOrDefault(
        space => space.Items.Any(item => item.Id == itemId && item.Kind == "terminal"))?.Id
        ?? throw new KeyNotFoundException("Unknown terminal component.");

    private static string LocalUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath
        : throw new ArgumentException("Choose a local file.");

    private static int? Integer(JsonNode? node) => node is null ? null : WorkspaceStore.Number(node, 0);
    private static JsonNode? Json<T>(T value) => JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web);
}
