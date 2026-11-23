using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Avalonia.Threading;
using CommandTray.Core;
using CommandTray.Sync;

namespace CommandTray.Android;

internal sealed class AndroidBackend : IClientBackend
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Context _context;
    private readonly string _privateCache;
    private readonly AndroidSettings _settings;
    private readonly AndroidWorkspace _workspace;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35) };
    private WeakReference<Activity>? _focused;
    private AndroidMcpHost? _mcp;

    public AndroidBackend(Context context)
    {
        _context = context;
        _settings = new AndroidSettings(context);
        var files = context.FilesDir?.AbsolutePath ?? throw new IOException("Android files directory is unavailable.");
        _privateCache = context.CacheDir?.AbsolutePath ?? throw new IOException("Android cache directory is unavailable.");
        _workspace = new AndroidWorkspace(files, _settings);
    }

    public ClientFeatures Features => default;
    internal string CacheDirectory => _privateCache;

    public ValueTask<JsonNode?> ExecuteAsync(string operation, JsonObject arguments, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => ExecuteLockedAsync(operation, arguments, cancellationToken), cancellationToken));

    private async Task<JsonNode?> ExecuteLockedAsync(string operation, JsonObject args, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            switch (operation)
            {
                case "workspace_read": return _workspace.Document();
                case "space_create": return new JsonObject { ["id"] = _workspace.CreateSpace(Text(args, "name")) };
                case "space_rename":
                    _workspace.RenameSpace(Text(args, "spaceId"), Text(args, "name"));
                    return null;
                case "space_delete":
                    _workspace.DeleteSpace(Text(args, "spaceId"));
                    return null;
                case "space_activate":
                    _workspace.ActivateSpace(Text(args, "spaceId"));
                    return null;
                case "item_save":
                    return new JsonObject { ["id"] = _workspace.SaveItem(Text(args, "spaceId"),
                        args["item"] as JsonObject ?? throw new ArgumentException("Component is required.")) };
                case "item_remove":
                    _workspace.RemoveItem(Text(args, "spaceId"), Text(args, "itemId"));
                    return null;
                case "relay_status": return Status();
                case "relay_pair":
                {
                    var server = AndroidSettings.NormalizeServer(Text(args, "server"));
                    var device = Text(args, "deviceName").Trim();
                    if (device.Length is 0 or > 120 || device.Any(char.IsControl))
                        throw new ArgumentException("Use a device name between 1 and 120 characters.");
                    var response = await new RelayClient(new Uri(server), http: _http).PairAsync(device, Text(args, "pairingCode"), cancellation);
                    _settings.SavePairing(server, device, response.DeviceId, response.Token);
                    return Status();
                }
                case "relay_unpair":
                    _settings.ForgetPairing();
                    return Status();
                case "relay_devices": return await Client().ListDevicesAsync(cancellation);
                case "sync_push":
                case "sync_pull":
                    return SyncResult(await _workspace.SynchronizeAsync(Client(), operation == "sync_push", cancellation));
                case "sync_resolve":
                    return SyncResult(await _workspace.ResolveAsync(Client(), Text(args, "choice"), cancellation));
                case "files_list": return await Client().ListFilesAsync(cancellation);
                case "file_upload":
                {
                    var input = TrustedFile(Text(args, "path"));
                    await using var stream = File.OpenRead(input);
                    return await Client().UploadAsync(Text(args, "name"), stream, Text(args, "folder"), cancellation);
                }
                case "file_download":
                {
                    var destination = TrustedFile(Text(args, "path"));
                    await using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 32768, useAsync: true);
                    await Client().DownloadAsync(Text(args, "id"), stream, cancellation);
                    return new JsonObject { ["size"] = stream.Position };
                }
                case "file_delete":
                    await Client().DeleteFileAsync(Text(args, "id"), cancellation);
                    return null;
                case "clipboard_read": return await Client().GetClipboardAsync(cancellation);
                case "clipboard_send": return await Client().PutClipboardAsync(Text(args, "text"), cancellation);
                case "clipboard_receive":
                {
                    var response = await Client().GetClipboardAsync(cancellation);
                    await LocalClipboardAsync(Text(response, "text"), cancellation);
                    return response;
                }
                case "clipboard_local_read": return JsonValue.Create(await LocalClipboardAsync(null, cancellation));
                case "clipboard_local_write":
                    await LocalClipboardAsync(Text(args, "text"), cancellation, args["sensitive"]?.GetValue<bool>() == true);
                    return null;
                case "settings_read": return ReadSettings();
                case "settings_save":
                    if (args["language"] is JsonValue language && language.TryGetValue<string>(out var selected))
                        _settings.SetLanguage(selected);
                    if (args["mcpEnabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on))
                        SetMcp(on);
                    return ReadSettings();
                case "mcp_config":
                    if (_mcp is null)
                        throw new InvalidOperationException("Enable local MCP first.");
                    return new JsonObject
                    {
                        ["url"] = AndroidSettings.McpEndpoint,
                        ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + _settings.McpToken() }
                    };
                default: throw new NotSupportedException("This operation is unavailable on Android.");
            }
        }
        finally { _gate.Release(); }
    }

    internal void UpdateClipboardFocus(Activity activity, bool hasFocus)
    {
        // The foreground lifecycle and all clipboard accesses run on Android's UI thread.
        if (hasFocus)
            _focused = new WeakReference<Activity>(activity);
        else if (_focused?.TryGetTarget(out var current) == true && ReferenceEquals(activity, current))
            _focused = null;
    }

    internal async Task StartConfiguredMcpAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_settings.McpEnabled)
                SetMcp(true);
        }
        catch (Exception error) { global::Android.Util.Log.Warn("CommandTray", "Local MCP startup failed: " + error.GetType().Name); }
        finally { _gate.Release(); }
    }

    internal async Task SyncWhileResumedAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (_settings.Paired)
                {
                    try { await ExecuteAsync("sync_push", new JsonObject(), cancellation); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                    catch (Exception error) { global::Android.Util.Log.Debug("CommandTray", "Workspace sync deferred: " + error.GetType().Name); }
                }
                await Task.Delay(TimeSpan.FromSeconds(15), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private void SetMcp(bool enabled)
    {
        if (!enabled)
        {
            _settings.SetMcpEnabled(false);
            _mcp?.Dispose();
            _mcp = null;
            return;
        }
        if (_mcp is not null)
            return;
        _settings.SetMcpEnabled(true);
        try { _mcp = new AndroidMcpHost(this, _settings.McpToken()); }
        catch
        {
            _settings.SetMcpEnabled(false);
            throw;
        }
    }

    private async Task<string?> LocalClipboardAsync(string? replacement, CancellationToken cancellation, bool sensitive = false)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_focused is null || !_focused.TryGetTarget(out var activity) || !activity.HasWindowFocus)
                throw new InvalidOperationException("Bring CommandTray to the foreground before using the Android clipboard.");
            var clipboard = (ClipboardManager?)_context.GetSystemService(Context.ClipboardService)
                ?? throw new InvalidOperationException("Android clipboard is unavailable.");
            if (replacement is not null)
            {
                using var clipData = ClipData.NewPlainText("CommandTray", replacement)
                    ?? throw new InvalidOperationException("Android clipboard is unavailable.");
                if (sensitive && clipData.Description is { } description)
                {
                    using var extras = new global::Android.OS.PersistableBundle();
                    extras.PutBoolean("android.content.extra.IS_SENSITIVE", true);
                    description.Extras = extras;
                }
                clipboard.PrimaryClip = clipData;
                return null;
            }
            var clip = clipboard.PrimaryClip;
            return clip is { ItemCount: > 0 } ? clip.GetItemAt(0)?.Text ?? "" : "";
        }, DispatcherPriority.Normal, cancellation);
    }

    private RelayClient Client() => !_settings.Paired
        ? throw new InvalidOperationException("Pair this device before opening the relay.")
        : new RelayClient(new Uri(_settings.Server), _settings.RelayToken(), _http);

    private string TrustedFile(string path)
    {
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A selected Android document is required.");
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(_privateCache) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Select the file through the Android document picker.");
        return full;
    }

    private JsonObject Status() => new()
    {
        ["server"] = _settings.Server, ["deviceName"] = _settings.DeviceName, ["deviceId"] = _settings.DeviceId,
        ["paired"] = _settings.Paired, ["revision"] = AndroidWorkspace.Revision(_workspace.Read()), ["conflict"] = _workspace.Conflict()
    };

    private JsonObject ReadSettings() => new()
    {
        ["language"] = _settings.Language, ["runAtLogin"] = false, ["mcpEnabled"] = _mcp is not null,
        ["mcpEndpoint"] = AndroidSettings.McpEndpoint
    };

    private JsonObject SyncResult(WorkspaceSyncResult result) => new()
    {
        ["revision"] = result.Revision, ["document"] = result.Document.DeepClone(), ["conflict"] = _workspace.Conflict()
    };

    internal static string Text(JsonObject args, string key) =>
        args[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
