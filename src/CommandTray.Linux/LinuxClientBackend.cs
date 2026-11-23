using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommandTray.Core;
using CommandTray.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CommandTray.Linux;

public sealed class LinuxClientBackend(
    Func<CancellationToken, ValueTask<string?>>? clipboardRead = null,
    Func<string, CancellationToken, ValueTask>? clipboardWrite = null,
    bool startLocalDaemon = false) : IClientBackend, IAsyncDisposable
{
    private readonly LinuxCommands _commands = new();
    private readonly SemaphoreSlim _daemonGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly AsyncLocal<HttpResponse?> _mcpResponse = new();
    private WebApplication? _mcp;
    private string? _sensitiveClipboardText;
    private CancellationTokenSource? _clipboardLifetime;
    private Task? _clipboardTask;
    private SuiteUpdates? _updates;
    private CancellationTokenSource? _updateLifetime;
    private Task? _updateTask;
    private Func<string, CancellationToken, Task>? _installUpdate;

    public ClientFeatures Features => ClientFeatures.LocalFiles | ClientFeatures.Terminals | ClientFeatures.Commands |
        ClientFeatures.Layout | ClientFeatures.Ssh | ClientFeatures.ClipboardHistory |
        (_updates is null ? ClientFeatures.None : ClientFeatures.Updates);

    internal bool HasDesktopClipboard => clipboardRead is not null && clipboardWrite is not null;
    public SuiteUpdates? Updates => _updates;
    public static string? DesktopUpdatePlatform => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "linux-x64-desktop",
        Architecture.Arm64 => "linux-arm64-desktop",
        _ => null
    };

    public void StartDesktopUpdates(Func<string, CancellationToken, Task> install)
    {
        if (_updates is not null || DesktopUpdatePlatform is not { } platform ||
            Assembly.GetEntryAssembly() is not { Location.Length: 0 } entry) return;
        _updates = new SuiteUpdates(LocalState.Root, platform, SuiteUpdates.AssemblyVersion(entry));
        _installUpdate = install;
        _updateLifetime = new CancellationTokenSource();
        _updateTask = _updates.RunAsync(_updateLifetime.Token);
    }

    public void StartClipboardSync()
    {
        if (clipboardRead is null || clipboardWrite is null || _clipboardLifetime is not null) return;
        _clipboardLifetime = new CancellationTokenSource();
        var sync = new ClipboardAutoSync(() =>
        {
            var settings = LocalSettings.Load();
            return settings is { ClipboardAutoSync: true, DeviceId: { Length: > 0 } id, Server: not null, Token: not null }
                ? new(id, LinuxCommands.Relay(settings)) : null;
        }, Read, async (expected, text, cancellation) =>
        {
            if (await Read(cancellation) != expected || !LocalSettings.Load().ClipboardAutoSync) return false;
            await clipboardWrite(text, cancellation).AsTask().WaitAsync(cancellation);
            RememberClipboard(text);
            return true;
        });
        _clipboardTask = sync.RunAsync(_clipboardLifetime.Token);

        async Task<string?> Read(CancellationToken cancellation)
        {
            var text = await clipboardRead(cancellation).AsTask().WaitAsync(cancellation) ?? "";
            if (text == _sensitiveClipboardText) return null;
            _sensitiveClipboardText = null;
            return text;
        }
    }

    public async ValueTask<JsonNode?> ExecuteAsync(string operation, JsonObject args, CancellationToken cancellationToken = default)
    {
        string Required(string name) => Text(args, name) is { Length: > 0 } text
            ? text : throw new ArgumentException($"Missing {name}.");
        string Item() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Required("itemId"))));
        string HostId() => Text(args, "id") is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N");

        string[]? cli = operation switch
        {
            "workspace_read" => ["workspace", "get"],
            "space_create" => ["workspace", "create", Required("name")],
            "space_activate" => ["workspace", "use", Required("spaceId")],
            "space_rename" => ["workspace", "rename", Required("spaceId"), Required("name")],
            "space_delete" => ["workspace", "remove", Required("spaceId")],
            "item_save" => ["workspace", "put-item", Required("spaceId"), PrepareItem(args)],
            "item_remove" => ["workspace", "remove-item", Required("spaceId"), Required("itemId")],
            "item_move" => ["workspace", "move-item", Required("spaceId"), Required("itemId"), args.ToJsonString()],
            "relay_devices" => ["sync", "devices"],
            "sync_push" => ["sync", "push"],
            "sync_pull" => ["sync", "pull"],
            "sync_resolve" => ["sync", "resolve", Required("choice")],
            "files_list" => ["share", "list"],
            "file_download" => ["share", "get", Required("id"), Required("path")],
            "file_delete" => ["share", "remove", Required("id")],
            "clipboard_read" or "clipboard_receive" => ["clip", "status"],
            "clipboard_relay_history" => ["clip", "history"],
            "clipboard_send" => ["clip", "put", Text(args, "text") ?? ""],
            "terminal_write" => ["terminal", "write", Item(), Text(args, "text") ?? ""],
            "terminal_read" => ["terminal", "read", Item(), "300"],
            "terminal_resize" => ["terminal", "resize", Item(), (args["columns"]?.GetValue<int>() ?? 80).ToString(),
                (args["rows"]?.GetValue<int>() ?? 24).ToString()],
            "terminal_close" => ["terminal", "close", Item()],
            "ssh_save" => ["host", "put", HostId(), Required("name"), Required("address"), Required("username"),
                (args["port"]?.GetValue<int>() ?? 22).ToString(), Text(args, "keyId") ?? ""],
            "ssh_delete" => ["host", "remove", Required("id")],
            "ssh_keys" => ["identity", "list"],
            "ssh_key_delete" => ["identity", "remove", Required("id")],
            "commands_list" => ["service", "list"],
            "command_start" or "command_stop" or "command_restart" or "command_delete" => ["service", operation[8..], Required("id")],
            "command_log" => ["service", "logs", Required("id"), "250"],
            _ => null
        };

        if (cli is not null)
        {
            if (operation is "command_start" or "command_restart")
                await MaterializeCommandAsync(Required("id"), cancellationToken);
            if (operation == "command_delete" && !await HasLocalCommandAsync(Required("id"), cancellationToken))
            {
                LinuxCommands.DeleteCommandTemplate(Required("id"));
                return null;
            }
            var result = await CallAsync(cancellationToken, cli);
            if (operation == "commands_list" && result is JsonArray commands)
                return MergeCommands(commands);
            if (operation is "terminal_read" or "command_log") return JsonValue.Create(Text(result, "content") ?? "");
            if (operation is "clipboard_send" or "clipboard_receive")
            {
                var value = Text(result, "text") ?? Text(args, "text") ?? "";
                if (operation == "clipboard_receive" && clipboardWrite is not null)
                {
                    await clipboardWrite(value, cancellationToken);
                    _sensitiveClipboardText = null;
                }
                RememberClipboard(value);
            }
            return result;
        }

        switch (operation)
        {
            case "update_status":
            case "update_check":
            case "update_set":
            case "update_install":
                return await (_updates ?? throw new InvalidOperationException("Suite updates require a release build."))
                    .ExecuteAsync(operation, args, _installUpdate!, cancellationToken);
            case "relay_status":
            {
                var settings = LocalSettings.Load();
                var local = WorkspaceState.Load();
                return new JsonObject
                {
                    ["server"] = settings.Server, ["deviceName"] = settings.DeviceName ?? Environment.MachineName,
                    ["paired"] = settings.Token is not null, ["revision"] = local.BaseRevision,
                    ["conflict"] = LinuxCommands.ReadConflict(settings) is not null
                };
            }
            case "relay_pair":
                return AsNode(await _commands.PairAsync(Required("server"), Required("deviceName"), Required("pairingCode"), cancellationToken));
            case "relay_unpair":
                return AsNode(_commands.Unpair());
            case "file_upload":
                return AsNode(await _commands.UploadSharedFileAsync(Required("path"), Text(args, "name"), Text(args, "folder"), cancellationToken));
            case "settings_read":
                return SettingsView();
            case "settings_save":
                return await SaveSettingsAsync(args, cancellationToken);
            case "mcp_config":
                return new JsonObject
                {
                    ["url"] = $"http://127.0.0.1:{LocalSettings.Load().McpPort}/mcp",
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + McpHost.GetToken(rotate: false) }
                };
            case "local_list":
                return ListDirectory(Text(args, "path"));
            case "local_preview":
                return JsonValue.Create(await PreviewFileAsync(Required("path"), cancellationToken));
            case "local_copy":
                File.Copy(AbsolutePath(Required("source")), AbsolutePath(Required("destination")), overwrite: false);
                return null;
            case "local_mkdir":
                Directory.CreateDirectory(AbsolutePath(Required("path")));
                return null;
            case "clipboard_local_read":
            {
                var value = await (clipboardRead ?? throw NoClipboard())(cancellationToken) ?? "";
                if (value != _sensitiveClipboardText)
                {
                    _sensitiveClipboardText = null;
                    RememberClipboard(value);
                }
                return new JsonObject { ["text"] = value };
            }
            case "clipboard_local_write":
            {
                var value = Text(args, "text") ?? "";
                await (clipboardWrite ?? throw NoClipboard())(value, cancellationToken);
                _sensitiveClipboardText = args["sensitive"]?.GetValue<bool>() == true ? value : null;
                if (_sensitiveClipboardText is null) RememberClipboard(value);
                return null;
            }
            case "clipboard_history":
                return AsNode(LocalState.Read(ClipboardHistoryFile, new List<string>()));
            case "command_save":
            {
                var existing = Text(args, "id");
                var name = Required("name");
                var cwd = DirectoryOrHome(Text(args, "workingDirectory"));
                var content = Required("command");
                string[] words = existing is null or ""
                    ? ["service", "create", name, "--cwd", cwd, "--", "/bin/sh", "-c", content]
                    : await HasLocalCommandAsync(existing, cancellationToken)
                        ? ["service", "update", existing, "--name", name, "--cwd", cwd, "--", "/bin/sh", "-c", content]
                        : ["service", "create", name, "--id", existing, "--cwd", cwd, "--", "/bin/sh", "-c", content];
                var saved = await CallAsync(cancellationToken, words);
                return saved is JsonObject profile ? CommandView(profile) : saved;
            }
            case "terminal_open":
            {
                var path = Text(args, "workingDirectory");
                var cwd = DirectoryOrHome(path is { Length: > 2 } && char.IsAsciiLetter(path[0]) && path[1] == ':' ? null : path);
                if (Text(args, "hostId") is { Length: > 0 } host)
                {
                    var profile = LinuxCommands.SshProfile(host, cwd, Text(args, "passphrase"));
                    return await CallAsync(cancellationToken, ["terminal", "open", "--id", Item(), cwd, profile.Executable, .. profile.Arguments]);
                }
                // Shell names from a different device can still be present in a synced terminal card.
                var executable = Text(args, "shell");
                if (string.IsNullOrWhiteSpace(executable) || executable is "CMD" or "cmd.exe" or "PowerShell" or "powershell.exe")
                    executable = ServiceRuntime.DefaultShell();
                return await CallAsync(cancellationToken, ["terminal", "open", "--id", Item(), cwd, executable]);
            }
            case "ssh_import":
                return AsNode(_commands.ImportAndInstallIdentity(Required("path"), Required("name"), Required("passphrase")));
            default:
                throw new NotSupportedException($"The Linux client does not implement {operation}.");
        }
    }

    private static JsonObject SettingsView()
    {
        var settings = LocalSettings.Load();
        return new JsonObject
        {
            ["language"] = settings.Language, ["runAtLogin"] = settings.RunAtLogin,
            ["mcpEnabled"] = settings.McpEnabled, ["mcpEndpoint"] = $"http://127.0.0.1:{settings.McpPort}/mcp",
            ["clipboardAutoSync"] = settings.ClipboardAutoSync,
            ["sidebarWidth"] = settings.SidebarWidth, ["sidebarCollapsed"] = settings.SidebarCollapsed
        };
    }

    public async Task StartConfiguredMcpAsync(CancellationToken cancellationToken = default)
    {
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            if (LocalSettings.Load().McpEnabled && _mcp is null)
                _mcp = await McpHost.StartHttpAsync(this, cancellationToken);
        }
        finally { _settingsGate.Release(); }
    }

    private async Task<JsonObject> SaveSettingsAsync(JsonObject args, CancellationToken cancellationToken)
    {
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            var previous = LocalSettings.Load();
            var language = Text(args, "language") ?? previous.Language;
            var login = args["runAtLogin"]?.GetValue<bool>() ?? previous.RunAtLogin;
            var mcpEnabled = args["mcpEnabled"]?.GetValue<bool>() ?? previous.McpEnabled;
            if (login != previous.RunAtLogin) await SetRunAtLoginAsync(login, cancellationToken);
            if (mcpEnabled && _mcp is null && HasDesktopClipboard)
                _mcp = await McpHost.StartHttpAsync(this, cancellationToken);
            if (!mcpEnabled && _mcp is not null)
            {
                if (_mcpResponse.Value is { } response)
                {
                    // Let the MCP request deliver its confirmation before stopping its transport.
                    response.OnCompleted(() => { _ = StopDisabledMcpAsync(); return Task.CompletedTask; });
                }
                else await StopMcpAsync();
            }

            using var mutex = LinuxCommands.SettingsLock();
            var current = LocalSettings.Load();
            LocalState.Write(LocalState.Settings, current with
            {
                Language = language, RunAtLogin = login, McpEnabled = mcpEnabled,
                ClipboardAutoSync = args["clipboardAutoSync"]?.GetValue<bool>() ?? current.ClipboardAutoSync,
                SidebarWidth = args["sidebarWidth"]?.GetValue<int>() ?? current.SidebarWidth,
                SidebarCollapsed = args["sidebarCollapsed"]?.GetValue<bool>() ?? current.SidebarCollapsed
            });
            return SettingsView();
        }
        finally { _settingsGate.Release(); }
    }

    internal async Task HandleMcpRequestAsync(HttpContext context, RequestDelegate next)
    {
        _mcpResponse.Value = context.Response;
        try { await next(context); }
        finally { _mcpResponse.Value = null; }
    }

    private async Task StopDisabledMcpAsync()
    {
        await _settingsGate.WaitAsync();
        try
        {
            if (!LocalSettings.Load().McpEnabled) await StopMcpAsync();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine("CommandTray MCP: " + error.Message);
        }
        finally { _settingsGate.Release(); }
    }

    private static async Task SetRunAtLoginAsync(bool enabled, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("systemctl")
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--user");
        start.ArgumentList.Add(enabled ? "enable" : "disable");
        start.ArgumentList.Add("commandtray.service");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start systemctl.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The CommandTray user service could not be configured. " + error.Trim());
    }

    private async Task StopMcpAsync()
    {
        if (_mcp is not { } app) return;
        _mcp = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        finally { await app.DisposeAsync(); }
    }

    private async Task<JsonNode?> CallAsync(CancellationToken cancellationToken, string[] arguments)
    {
        try { return AsNode(await _commands.ExecuteAsync(arguments, cancellationToken)); }
        catch (InvalidOperationException ex) when (startLocalDaemon && ex.InnerException is SocketException)
        {
            await EnsureDaemonAsync(cancellationToken);
            return AsNode(await _commands.ExecuteAsync(arguments, cancellationToken));
        }
    }

    private async Task EnsureDaemonAsync(CancellationToken cancellationToken)
    {
        await _daemonGate.WaitAsync(cancellationToken);
        try
        {
            if (await DaemonAvailableAsync(cancellationToken)) return;
            var userBinary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "commandtray");
            var bundled = Path.Combine(AppContext.BaseDirectory, "commandtray");
            var executable = File.Exists(bundled) ? bundled : File.Exists(userBinary) ? userBinary : "commandtray";
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add("daemon");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the CommandTray daemon.");
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (await DaemonAvailableAsync(cancellationToken)) return;
                await Task.Delay(100, cancellationToken);
            }
            throw new InvalidOperationException("The CommandTray daemon did not open its Unix control socket. Run commandtray daemon for details.");
        }
        finally { _daemonGate.Release(); }
    }

    private static async Task<bool> DaemonAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(LocalState.Socket), cancellationToken);
            return true;
        }
        catch (SocketException) { return false; }
    }

    private async Task<bool> HasLocalCommandAsync(string id, CancellationToken cancellationToken)
    {
        return await FindLocalCommandAsync(id, cancellationToken) is not null;
    }

    private async Task<JsonObject?> FindLocalCommandAsync(string id, CancellationToken cancellationToken)
    {
        var list = await CallAsync(cancellationToken, ["service", "list"]) as JsonArray ?? [];
        return list.OfType<JsonObject>().FirstOrDefault(command => Text(command, "id") == id);
    }

    private async Task MaterializeCommandAsync(string id, CancellationToken cancellationToken)
    {
        var installed = await FindLocalCommandAsync(id, cancellationToken);
        var portable = WorkspaceState.Load().Document["commands"] as JsonArray;
        var template = portable?.OfType<JsonObject>().FirstOrDefault(command => Text(command, "id") == id);
        if (template is null)
        {
            if (installed is null) throw new InvalidOperationException("Unknown command.");
            return;
        }
        var name = Text(template, "name") ?? throw new InvalidOperationException("The command template is missing its name.");
        var source = Text(template, "command") ?? throw new InvalidOperationException("The command template is missing its command.");
        var path = Text(template, "workingDirectory");
        var cwd = !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Directory.Exists(path)
            ? path : DirectoryOrHome(null);
        if (installed is not null)
        {
            var current = CommandView(installed);
            if (Text(current, "name") == name && Text(current, "command") == source && Text(current, "workingDirectory") == cwd) return;
        }

        var change = installed is null
            ? new[] { "service", "create", name, "--id", id }
            : new[] { "service", "update", id, "--name", name };
        await DaemonTransport.ExecuteAsync([.. change, "--cwd", cwd, "--", "/bin/sh", "-c", source], cancellationToken);
    }

    private static JsonArray MergeCommands(JsonArray profiles)
    {
        var local = profiles.OfType<JsonObject>().Select(CommandView).ToDictionary(command => Text(command, "id")!, StringComparer.Ordinal);
        var values = new JsonArray();
        if (WorkspaceState.Load().Document["commands"] is JsonArray portable)
        {
            foreach (var command in portable.OfType<JsonObject>())
            {
                if (Text(command, "id") is not { Length: > 0 } id) continue;
                var record = (JsonObject)command.DeepClone();
                record["state"] = local.Remove(id, out var profile) ? Text(profile, "state") : "Template";
                values.Add(record);
            }
        }
        foreach (var profile in local.Values) values.Add(profile);
        return values;
    }

    internal static JsonObject CommandView(JsonObject profile)
    {
        var executable = Text(profile, "executable") ?? "";
        var args = profile["arguments"] is JsonArray array ? array.Select(item => item?.GetValue<string>() ?? "").ToArray() : [];
        var command = executable == "/bin/sh" && args is ["-c", var source] ? source :
            string.Join(' ', new[] { executable }.Concat(args).Select(ShellDisplay));
        var state = profile["running"]?.GetValue<bool>() == true ? "Running" :
            profile["exitCode"] is JsonValue code && code.TryGetValue<int>(out var exit) && exit != 0 ? "Failed" : "Stopped";
        return new JsonObject
        {
            ["id"] = Text(profile, "id"), ["name"] = Text(profile, "name"), ["command"] = command,
            ["workingDirectory"] = Text(profile, "workingDirectory"), ["state"] = state
        };
    }

    private static string ShellDisplay(string value) => value.Length > 0 && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '.' or '-' or '=') ? value :
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string PrepareItem(JsonObject args)
    {
        var item = args["item"]?.DeepClone() as JsonObject ?? throw new ArgumentException("Missing item.");
        if (Text(item, "id") is not { Length: > 0 }) item["id"] = Guid.NewGuid().ToString("N");
        return item.ToJsonString();
    }

    private static JsonObject ListDirectory(string? path)
    {
        var directory = DirectoryOrHome(path);
        var entries = Directory.EnumerateFileSystemEntries(directory).Select(item =>
                new { name = Path.GetFileName(item), path = item, isDirectory = Directory.Exists(item) })
            .OrderByDescending(item => item.isDirectory).ThenBy(item => item.name, StringComparer.OrdinalIgnoreCase);
        return new JsonObject { ["path"] = directory, ["entries"] = AsNode(entries) };
    }

    private static async Task<string> PreviewFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(AbsolutePath(path));
        var bytes = new byte[128 * 1024];
        var count = await input.ReadAtLeastAsync(bytes.AsMemory(), bytes.Length, false, cancellationToken);
        if (bytes.AsSpan(0, count).Contains((byte)0)) throw new InvalidDataException("Only text files can be previewed.");
        return Encoding.UTF8.GetString(bytes, 0, count);
    }

    private static string DirectoryOrHome(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return AbsolutePath(path);
    }

    private static string AbsolutePath(string path) => Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) :
        throw new ArgumentException("Use an absolute Linux path.");

    private static Exception NoClipboard() => new InvalidOperationException("The Linux desktop clipboard is unavailable in this session.");
    private static string ClipboardHistoryFile => Path.Combine(LocalState.Root, "clipboard-history.json");

    private static void RememberClipboard(string text)
    {
        if (text.Length is 0 or > 256 * 1024) return;
        using var mutex = LocalState.Lock(Path.Combine(LocalState.Root, ".clipboard.lock"));
        var history = LocalState.Read(ClipboardHistoryFile, new List<string>());
        history.RemoveAll(item => item == text);
        history.Insert(0, text);
        if (history.Count > 30) history.RemoveRange(30, history.Count - 30);
        LocalState.Write(ClipboardHistoryFile, history);
    }

    private static string? Text(JsonNode? value, string name) =>
        value is JsonObject obj && obj[name] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? AsNode(object? value) => value is JsonNode node ? node.DeepClone() :
        JsonSerializer.SerializeToNode(value, LocalState.Json);

    public async ValueTask DisposeAsync()
    {
        if (_updateLifetime is { } updateLifetime)
        {
            updateLifetime.Cancel();
            if (_updateTask is not null) await _updateTask;
            updateLifetime.Dispose();
        }
        if (_clipboardLifetime is { } clipboardLifetime)
        {
            clipboardLifetime.Cancel();
            if (_clipboardTask is not null) await _clipboardTask;
            clipboardLifetime.Dispose();
        }
        await _settingsGate.WaitAsync();
        try { await StopMcpAsync(); }
        finally { _settingsGate.Release(); }
    }
}
