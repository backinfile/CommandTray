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
    private readonly SemaphoreSlim _workspaceSync = new(1);
    private readonly Context _context;
    private readonly string _privateCache;
    private readonly AndroidSettings _settings;
    private readonly AndroidWorkspace _workspace;
    private readonly ClipboardAutoSync _clipboardSync;
    private readonly ClipboardPreferences _clipboardPreferences;
    private readonly SuiteUpdates _updates;
    private readonly object _updateSchedule = new();
    private DateTimeOffset _nextUpdateCheck;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35) };
    private WeakReference<Activity>? _focused;
    private AndroidMcpHost? _mcp;
    private long? _observedClipboard;
    private string? _observedClipboardText;
    private const string SensitiveClipboard = "android.content.extra.IS_SENSITIVE";

    public AndroidBackend(Context context)
    {
        _context = context;
        _settings = new AndroidSettings(context);
        var files = context.FilesDir?.AbsolutePath ?? throw new IOException("Android files directory is unavailable.");
        _privateCache = context.CacheDir?.AbsolutePath ?? throw new IOException("Android cache directory is unavailable.");
        _workspace = new AndroidWorkspace(files, _settings);
        _clipboardPreferences = new ClipboardPreferences(files);
        var packageName = context.PackageName ?? throw new InvalidOperationException("Android package name is unavailable.");
        var packageVersion = context.PackageManager?.GetPackageInfo(packageName, 0)?.VersionName;
        _updates = new SuiteUpdates(files, "android", Version.TryParse(packageVersion, out var version) && version.Build >= 0
            ? new Version(version.Major, version.Minor, version.Build) : new Version(0, 0, 0));
        _clipboardSync = new ClipboardAutoSync(ClipboardConnection,
            cancellation => Dispatcher.UIThread.InvokeAsync(ReadClipboardForSync, DispatcherPriority.Normal, cancellation).GetTask(),
            (expected, text, cancellation) => Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_settings.ClipboardAutoSync || _clipboardPreferences.Direction == "send" ||
                    ReadClipboardForSync() != expected || FocusedClipboard() is not { } clipboard)
                    return false;
                using var clip = ClipData.NewPlainText("CommandTray", text);
                clipboard.PrimaryClip = clip ?? throw new InvalidOperationException("Android clipboard is unavailable.");
                return true;
            }, DispatcherPriority.Normal, cancellation).GetTask());
    }

    public ClientFeatures Features => ClientFeatures.Updates;
    internal string CacheDirectory => _privateCache;

    public ValueTask<JsonNode?> ExecuteAsync(string operation, JsonObject arguments, CancellationToken cancellationToken = default) =>
        ExecuteAsync(operation, arguments, null, cancellationToken);

    public ValueTask<JsonNode?> ExecuteAsync(string operation, JsonObject arguments,
        IProgress<long>? transferProgress, CancellationToken cancellationToken) =>
        new(Task.Run(() => operation is "relay_pair" or "relay_unpair" or "sync_push" or "sync_pull" or "sync_resolve"
            ? ExecuteWorkspaceSyncAsync(operation, arguments, cancellationToken)
            : operation.StartsWith("update_", StringComparison.Ordinal)
            ? _updates.ExecuteAsync(operation, arguments, InstallUpdateAsync, cancellationToken).AsTask()
            : ExecuteLockedAsync(operation, arguments, cancellationToken, transferProgress), cancellationToken));

    private async Task<JsonNode?> ExecuteWorkspaceSyncAsync(string operation, JsonObject args, CancellationToken cancellation)
    {
        // Keep the active relay partition stable throughout sync. Local document operations use the disk lock.
        await _workspaceSync.WaitAsync(cancellation);
        try
        {
            if (operation is "relay_pair" or "relay_unpair")
                return await ExecuteLockedAsync(operation, args, cancellation);

            var client = Client();
            var result = operation == "sync_resolve"
                ? await _workspace.ResolveAsync(client, Text(args, "choice"), cancellation)
                : await _workspace.SynchronizeAsync(client, operation == "sync_push", cancellation);
            lock (_workspace.DiskGate) return SyncResult(result);
        }
        finally { _workspaceSync.Release(); }
    }

    private async Task<JsonNode?> ExecuteLockedAsync(string operation, JsonObject args, CancellationToken cancellation,
        IProgress<long>? transferProgress = null)
    {
        if (operation.StartsWith("file_", StringComparison.Ordinal) || operation == "files_list")
            return await ExecuteFileAsync(operation, args, cancellation, transferProgress);

        await _gate.WaitAsync(cancellation);
        try
        {
            lock (_workspace.DiskGate)
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
                    case "sync_conflict": return _workspace.Conflict();
                }
            }

            switch (operation)
            {
                case "relay_pair":
                {
                    var server = AndroidSettings.NormalizeServer(Text(args, "server"));
                    var device = Text(args, "deviceName").Trim();
                    if (device.Length is 0 or > 120 || device.Any(char.IsControl))
                        throw new ArgumentException("Use a device name between 1 and 120 characters.");
                    var response = await new RelayClient(new Uri(server), http: _http).PairAsync(device, Text(args, "pairingCode"), cancellation);
                    if (string.IsNullOrEmpty(response.DeviceId) || string.IsNullOrEmpty(response.Token))
                        throw new InvalidDataException("The relay returned incomplete pairing credentials.");
                    var client = new RelayClient(new Uri(server), response.Token, _http);
                    var activeSpace = await _workspace.StageOfflineSpacesAsync(server, client, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    _settings.SavePairing(server, device, response.DeviceId, response.Token, activeSpace);
                    return Status();
                }
                case "relay_unpair":
                    _settings.ForgetPairing();
                    return Status();
                case "relay_devices": return await Client().ListDevicesAsync(cancellation);
                case "clipboard_read": return await Client().GetClipboardAsync(cancellation);
                case "clipboard_relay_history": return await Client().GetClipboardHistoryAsync(cancellation);
                case "clipboard_preferences": return _clipboardPreferences.Execute(args);
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
                    if (args["clipboardAutoSync"] is JsonValue clipboardSync && clipboardSync.TryGetValue<bool>(out var autoSync))
                        _settings.SetClipboardAutoSync(autoSync);
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

    private async Task<JsonNode?> ExecuteFileAsync(string operation, JsonObject args, CancellationToken cancellation,
        IProgress<long>? progress)
    {
        RelayClient client;
        await _gate.WaitAsync(cancellation);
        try
        {
            if (args["deviceId"]?.GetValue<string>() is { } expected && _settings.DeviceId != expected)
                throw new InvalidOperationException("The relay connection changed. Select the file again.");
            client = Client();
        }
        finally { _gate.Release(); }
        switch (operation)
        {
            case "files_list": return await client.ListFilesAsync(cancellation);
            case "file_upload":
            {
                var input = TrustedFile(Text(args, "path"));
                await using var stream = File.OpenRead(input);
                return await client.UploadAsync(Text(args, "name"), stream, Text(args, "folder"), cancellation, progress);
            }
            case "file_download":
            {
                var destination = TrustedFile(Text(args, "path"));
                await using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 32768, useAsync: true);
                await client.DownloadAsync(Text(args, "id"), stream, cancellation, progress);
                return new JsonObject { ["size"] = stream.Position };
            }
            case "file_delete":
                await client.DeleteFileAsync(Text(args, "id"), cancellation);
                return null;
            default:
                throw new NotSupportedException("Unknown shared file operation.");
        }
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

    internal Task SyncWhileResumedAsync(CancellationToken cancellation) => Task.WhenAll(
        SyncWorkspaceWhileResumedAsync(cancellation), _clipboardSync.RunAsync(cancellation), CheckUpdatesWhileResumedAsync(cancellation));

    private async Task CheckUpdatesWhileResumedAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), cancellation);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                bool check;
                lock (_updateSchedule)
                {
                    check = _updates.Enabled && DateTimeOffset.UtcNow >= _nextUpdateCheck;
                    if (check) _nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(4);
                }
                if (!check) continue;
                try { await _updates.CheckAsync(cancellation); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    lock (_updateSchedule) _nextUpdateCheck = default;
                    return;
                }
            }
            while (await timer.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task InstallUpdateAsync(string directory, CancellationToken cancellation)
    {
        var apk = Path.Combine(directory, "CommandTray-Suite.apk");
        await using (var from = File.OpenRead(SuiteUpdates.PackagePath(directory)))
        await using (var to = File.Create(apk))
            await from.CopyToAsync(to, cancellation);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var package = _context.PackageName!;
            if (_context.PackageManager?.CanRequestPackageInstalls() != true)
            {
                using var settings = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                    global::Android.Net.Uri.Parse("package:" + package));
                _context.StartActivity(settings.AddFlags(ActivityFlags.NewTask));
                return;
            }
            using var file = new Java.IO.File(apk);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(_context, package + ".updates", file);
            using var install = new Intent(Intent.ActionView);
            install.SetDataAndType(uri, "application/vnd.android.package-archive");
            _context.StartActivity(install.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission));
        }, DispatcherPriority.Normal, cancellation);
    }

    private async Task SyncWorkspaceWhileResumedAsync(CancellationToken cancellation)
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

    private ClipboardSyncConnection? ClipboardConnection()
    {
        if (!_settings.ClipboardAutoSync || !_settings.Paired) return null;
        var device = _settings.DeviceId;
        var relay = Client();
        return _settings.DeviceId == device ? new(device, relay, _clipboardPreferences.Direction) : null;
    }

    private ClipboardManager? FocusedClipboard() =>
        _focused is not null && _focused.TryGetTarget(out var activity) && activity.HasWindowFocus
            ? _context.GetSystemService(Context.ClipboardService) as ClipboardManager : null;

    private string? ReadClipboardForSync()
    {
        if (FocusedClipboard() is not { } clipboard) return null;
        using var description = clipboard.PrimaryClipDescription;
        if (description is null) return "";
        if (description.Extras?.GetBoolean(SensitiveClipboard) == true) return null;
        if (_observedClipboard != description.Timestamp)
        {
            using var clip = clipboard.PrimaryClip;
            _observedClipboardText = clip?.GetItemAt(0)?.Text;
            _observedClipboard = description.Timestamp;
        }
        return _observedClipboardText;
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
                    extras.PutBoolean(SensitiveClipboard, true);
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
        ["clipboardAutoSync"] = _settings.ClipboardAutoSync,
        ["mcpEndpoint"] = AndroidSettings.McpEndpoint
    };

    private JsonObject SyncResult(WorkspaceSyncResult result) => new()
    {
        ["revision"] = result.Revision, ["document"] = result.Document.DeepClone(), ["conflict"] = _workspace.Conflict()
    };

    internal static string Text(JsonObject args, string key) =>
        args[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
