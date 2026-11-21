using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommandTray.Sync;
using static CommandTray.Linux.ServiceRuntime;

namespace CommandTray.Linux;

internal sealed class LinuxCommands
{
    public async Task<object?> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length < 2) throw new ArgumentException("Provide a command and action. Use --help for usage.");
        var action = args[1];
        var tail = args[2..];
        switch (args[0])
        {
            case "service":
            case "terminal":
                return await DaemonTransport.ExecuteAsync(args, cancellationToken);
            case "config":
                return Config(action, tail);
            case "workspace":
                return Workspace(action, tail);
            case "sync":
                return await SyncAsync(action, tail, cancellationToken);
            case "clip":
                return await ClipboardAsync(action, tail, cancellationToken);
            case "share":
                return await ShareAsync(action, tail, cancellationToken);
            case "host":
                return Host(action, tail);
            case "identity":
                return Identity(action, tail);
            default: throw new ArgumentException("Unknown command. Use --help for usage.");
        }
    }

    private static object Config(string action, string[] args)
    {
        switch (action)
        {
            case "get":
                return SettingsView(LocalSettings.Load());
            case "set":
            {
                using var mutex = SettingsLock();
                var current = LocalSettings.Load();
                var key = Required(args, 0, "setting");
                var value = Required(args, 1, "value");
                LocalSettings next;
                switch (key)
                {
                    case "server":
                        var uri = ParseServer(value);
                        if (current.Server == uri.AbsoluteUri) return SettingsView(current);
                        using (WorkspaceLock()) ResetWorkspaceSync();
                        next = current with { Server = uri.AbsoluteUri, DeviceId = null, Token = null };
                        break;
                    case "mcp-port":
                        next = current with { McpPort = Positive(value, 65535) };
                        break;
                    default: throw new ArgumentException("Set server or mcp-port.");
                }
                LocalState.Write(LocalState.Settings, next);
                return SettingsView(next);
            }
            default: throw new ArgumentException("Use config get or config set.");
        }
    }

    private static object SettingsView(LocalSettings settings) => new
    {
        settings.Server, settings.DeviceId, paired = !string.IsNullOrWhiteSpace(settings.Token),
        mcpEndpoint = $"http://127.0.0.1:{settings.McpPort}/mcp",
        mcpTokenConfigured = !string.IsNullOrWhiteSpace(settings.McpToken),
        dataDirectory = LocalState.Root
    };

    public static FileStream SettingsLock() => LocalState.Lock(Path.Combine(LocalState.Root, ".settings.lock"));
    private static FileStream WorkspaceLock() => LocalState.Lock(Path.Combine(LocalState.Root, ".workspace.lock"));
    private static Uri ParseServer(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Enter an HTTP or HTTPS relay address without credentials, query, or fragment.");
        return uri;
    }

    private static RelayClient Relay()
    {
        var settings = LocalSettings.Load();
        if (settings.Server is null || settings.Token is null)
            throw new InvalidOperationException("Pair this device with commandtray sync pair before using the relay.");
        return new RelayClient(ParseServer(settings.Server), settings.Token);
    }

    private static void ResetWorkspaceSync()
    {
        if (!File.Exists(LocalState.Workspace)) return;
        var workspace = WorkspaceState.Load();
        LocalState.Write(LocalState.Workspace, workspace with { BaseRevision = 0, Dirty = true });
    }

    private static async Task<object?> SyncAsync(string action, string[] args, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "pair":
            {
                var server = ParseServer(Required(args, 0, "relay address"));
                var name = args.Length > 1 ? args[1] : Environment.MachineName;
                var code = Environment.GetEnvironmentVariable("COMMANDTRAY_PAIRING_CODE");
                if (string.IsNullOrWhiteSpace(code))
                    throw new InvalidOperationException("Set COMMANDTRAY_PAIRING_CODE locally before pairing.");
                using var config = SettingsLock();
                var current = LocalSettings.Load();
                var pairing = await new RelayClient(server).PairAsync(name, code, cancellationToken);
                if (current.Server != server.AbsoluteUri)
                {
                    using var workspace = WorkspaceLock();
                    ResetWorkspaceSync();
                }
                current = current with { Server = server.AbsoluteUri, DeviceId = pairing.DeviceId, Token = pairing.Token };
                LocalState.Write(LocalState.Settings, current);
                return SettingsView(current);
            }
            case "status":
            {
                var local = WorkspaceState.Load();
                return new { settings = SettingsView(LocalSettings.Load()), local.BaseRevision, local.Dirty };
            }
            case "devices":
                return await Relay().ListDevicesAsync(cancellationToken);
            case "remove-device":
            {
                var id = Required(args, 0, "device ID");
                var settings = LocalSettings.Load();
                await Relay().DeleteDeviceAsync(id, cancellationToken);
                if (id == settings.DeviceId)
                {
                    using var mutex = SettingsLock();
                    var current = LocalSettings.Load();
                    if (current.DeviceId == id)
                        LocalState.Write(LocalState.Settings, current with { DeviceId = null, Token = null });
                }
                return new { removed = id };
            }
            case "push":
            {
                using var workspace = WorkspaceLock();
                var local = WorkspaceState.Load();
                try
                {
                    var remote = await Relay().PutWorkspaceAsync(local.BaseRevision, local.Document, cancellationToken);
                    var revision = Revision(remote);
                    LocalState.Write(LocalState.Workspace, local with { BaseRevision = revision, Dirty = false });
                    return new { revision, dirty = false };
                }
                catch (RelayConflictException ex)
                {
                    var conflict = Path.Combine(LocalState.Root, "workspace-remote-conflict.json");
                    LocalState.Write(conflict, ex.Current);
                    throw new InvalidOperationException($"The remote workspace changed. Local edits are preserved. Remote conflict saved to {conflict}.", ex);
                }
            }
            case "pull":
            {
                using var workspace = WorkspaceLock();
                var local = WorkspaceState.Load();
                var remote = await Relay().GetWorkspaceAsync(cancellationToken);
                var revision = Revision(remote);
                string? saved = null;
                if (local.Dirty)
                {
                    if (!args.Contains("--replace", StringComparer.Ordinal))
                    {
                        if (local.BaseRevision == revision) return new { revision, dirty = true };
                        var conflict = Path.Combine(LocalState.Root, "workspace-remote-conflict.json");
                        LocalState.Write(conflict, remote);
                        throw new InvalidOperationException($"The remote workspace changed. Local edits are preserved. Remote conflict saved to {conflict}.");
                    }
                    saved = Path.Combine(LocalState.Root, "workspace-local-" + Guid.NewGuid().ToString("N") + ".json");
                    LocalState.Write(saved, local);
                }
                var document = remote["document"] as JsonObject ?? throw new InvalidDataException("The relay response omitted the workspace document.");
                LocalState.Write(LocalState.Workspace, new WorkspaceState(revision, false, document));
                return new { revision, dirty = false, saved };
            }
            default: throw new ArgumentException("Unknown sync action.");
        }
    }

    private static long Revision(JsonObject envelope) =>
        envelope["revision"]?.GetValue<long>() ?? throw new InvalidDataException("The relay response omitted the workspace revision.");

    private static async Task<object?> ClipboardAsync(string action, string[] args, CancellationToken cancellationToken)
    {
        return action switch
        {
            "put" => await Relay().PutClipboardAsync(Required(args, 0, "clipboard text", allowEmpty: true), cancellationToken),
            "get" => (await Relay().GetClipboardAsync(cancellationToken))["text"]?.GetValue<string>() ?? string.Empty,
            "status" => await Relay().GetClipboardAsync(cancellationToken),
            _ => throw new ArgumentException("Use clip put, clip get or clip status.")
        };
    }

    private static async Task<object> ShareAsync(string action, string[] args, CancellationToken cancellationToken)
    {
        var relay = Relay();
        switch (action)
        {
            case "list": return await relay.ListFilesAsync(cancellationToken);
            case "put":
            {
                var path = Path.GetFullPath(Required(args, 0, "local file"));
                await using var stream = File.OpenRead(path);
                return await relay.UploadAsync(Path.GetFileName(path), stream, args.ElementAtOrDefault(1), cancellationToken);
            }
            case "get":
            {
                var id = Required(args, 0, "shared file ID");
                var path = Path.GetFullPath(Required(args, 1, "output path"));
                var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".commandtray-" + Guid.NewGuid().ToString("N") + ".part");
                try
                {
                    await using (var output = new FileStream(temporary, new FileStreamOptions
                    {
                        Access = FileAccess.Write, Mode = FileMode.CreateNew,
                        Options = FileOptions.Asynchronous, UnixCreateMode = LocalState.PrivateFile
                    }))
                    {
                        await relay.DownloadAsync(id, output, cancellationToken);
                    }
                    File.Move(temporary, path, overwrite: false);
                }
                finally { File.Delete(temporary); }
                return new { path };
            }
            case "remove":
            {
                var id = Required(args, 0, "shared file ID");
                await relay.DeleteFileAsync(id, cancellationToken);
                return new { removed = id };
            }
            default: throw new ArgumentException("Use share list, share put, share get or share remove.");
        }
    }

    private static object Workspace(string action, string[] args)
    {
        using var mutex = WorkspaceLock();
        var workspace = WorkspaceState.Load();
        var document = workspace.Document;
        if (action == "get") return document;
        if (action is "load" or "replace")
        {
            var source = Required(args, 0, action == "load" ? "workspace JSON path" : "workspace JSON");
            var node = ParseObject(action == "load" ? File.ReadAllText(source) : source);
            ValidateWorkspace(node);
            return Save(workspace with { Document = node });
        }
        var spaces = ArrayAt(document, "spaces");
        if (action == "list") return spaces.Select(space =>
            new { id = Text(space, "id"), name = Text(space, "name"), active = Text(document, "activeSpaceId") == Text(space, "id") }).ToArray();
        if (action == "create")
        {
            var name = Required(args, 0, "space name").Trim();
            if (name.Length == 0) throw new ArgumentException("Enter a space name.");
            var id = Guid.NewGuid().ToString("N");
            var space = new JsonObject { ["id"] = id, ["name"] = name, ["items"] = new JsonArray() };
            spaces.Add(space);
            document["activeSpaceId"] ??= id;
            Save(workspace);
            return space;
        }
        var selector = Required(args, 0, "space ID or name");
        var match = ObjectBy(spaces, "id", selector) ?? ObjectBy(spaces, "name", selector) ??
            throw new InvalidOperationException("No space matches that ID or name.");
        switch (action)
        {
            case "show": return match;
            case "use":
                document["activeSpaceId"] = Text(match, "id");
                break;
            case "rename":
                var renamed = Required(args, 1, "new space name").Trim();
                if (renamed.Length == 0) throw new ArgumentException("Enter a space name.");
                match["name"] = renamed;
                break;
            case "remove":
                spaces.Remove(match);
                if (Text(document, "activeSpaceId") == Text(match, "id"))
                    document["activeSpaceId"] = Text(spaces.FirstOrDefault(), "id");
                break;
            case "put-item":
            {
                var items = ArrayAt(match, "items");
                var item = ParseObject(Required(args, 1, "component JSON"));
                var id = Text(item, "id");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(Text(item, "kind")))
                    throw new ArgumentException("Component JSON must supply id and kind.");
                if (ObjectBy(items, "id", id) is { } old) items[items.IndexOf(old)] = item;
                else items.Add(item);
                break;
            }
            case "remove-item":
            {
                var items = ArrayAt(match, "items");
                var id = Required(args, 1, "component ID");
                var item = ObjectBy(items, "id", id) ?? throw new InvalidOperationException("Unknown component.");
                items.Remove(item);
                break;
            }
            default: throw new ArgumentException("Unknown workspace action.");
        }
        Save(workspace);
        return document;
    }

    private static object Host(string action, string[] args)
    {
        using var mutex = WorkspaceLock();
        var workspace = WorkspaceState.Load();
        var hosts = ArrayAt(workspace.Document, "sshHosts");
        if (action == "list") return hosts;
        var id = Required(args, 0, "host ID");
        var existing = ObjectBy(hosts, "id", id);
        if (action == "get") return existing ?? throw new InvalidOperationException("Unknown SSH host.");
        if (action == "remove")
        {
            if (existing is null) throw new InvalidOperationException("Unknown SSH host.");
            hosts.Remove(existing);
            Save(workspace);
            return new { removed = id };
        }
        if (action != "put") throw new ArgumentException("Use host list, host get, host put, host remove or host connect.");

        var name = Required(args, 1, "display name");
        var address = Required(args, 2, "host address");
        var username = Required(args, 3, "SSH username");
        var port = args.Length > 4 ? Positive(args[4], 65535) : 22;
        ValidateSsh(address, username);
        var host = existing ?? new JsonObject { ["id"] = id };
        host["name"] = name;
        host["address"] = address;
        host["username"] = username;
        host["port"] = port;
        host["keyId"] = args.ElementAtOrDefault(5);
        if (existing is null) hosts.Add(host);
        Save(workspace);
        return host;
    }

    public static CommandProfile SshProfile(string id, string workingDirectory)
    {
        var host = ObjectBy(ArrayAt(WorkspaceState.Load().Document, "sshHosts"), "id", id)
            ?? throw new InvalidOperationException("Unknown SSH host.");
        var address = Text(host, "address") ?? string.Empty;
        var username = Text(host, "username") ?? string.Empty;
        ValidateSsh(address, username);
        var port = host["port"]?.GetValue<int>() ?? 22;
        if (port < 1 || port > 65535) throw new ArgumentException("Invalid SSH port.");
        var arguments = new List<string> { "-p", port.ToString(), "-l", username, "-o", "StrictHostKeyChecking=ask" };
        if (Text(host, "keyId") is { Length: > 0 } keyId)
        {
            var key = IdentityPath(keyId);
            if (!File.Exists(key)) throw new InvalidOperationException("Install the SSH identity on this device before connecting.");
            arguments.AddRange(["-i", key, "-o", "IdentitiesOnly=yes"]);
        }
        arguments.Add(address);
        return new CommandProfile(id, Text(host, "name") ?? id, "ssh", arguments.ToArray(), ValidateDirectory(workingDirectory));
    }

    private static void ValidateSsh(string address, string username)
    {
        // ssh treats leading dashes as options and connection strings as URIs unless the value is constrained.
        if (address.Length == 0 || address[0] == '-' ||
            address.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or ':' or '_' or '%')) ||
            username.Length == 0 || username.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new ArgumentException("Enter a hostname or IP address and a valid SSH username.");
    }

    private static object Identity(string action, string[] args)
    {
        using var mutex = WorkspaceLock();
        var workspace = WorkspaceState.Load();
        var keys = ArrayAt(workspace.Document, "sshKeyEnvelopes");
        if (action == "list")
            return keys.Select(key => new { id = Text(key, "id"), name = Text(key, "name"), installed = Text(key, "id") is { } id && File.Exists(IdentityPath(id)) }).ToArray();

        var idToFind = Required(args, 0, "identity ID");
        var existing = ObjectBy(keys, "id", idToFind);
        if (action == "remove")
        {
            var removed = existing is not null && keys.Remove(existing);
            if (removed) Save(workspace);
            File.Delete(IdentityPath(idToFind));
            return new { removed };
        }
        if (action is not ("import" or "install")) throw new ArgumentException("Unknown identity action.");
        var passphrase = Environment.GetEnvironmentVariable("COMMANDTRAY_VAULT_PASSPHRASE");
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new InvalidOperationException("Set COMMANDTRAY_VAULT_PASSPHRASE locally before using the SSH vault.");
        if (action == "import")
        {
            var keyFile = Required(args, 1, "private key path");
            if (new FileInfo(keyFile).Length > 256 * 1024) throw new ArgumentException("The SSH key file is too large.");
            var clear = File.ReadAllBytes(keyFile);
            try
            {
                if (!clear.AsSpan().StartsWith("-----BEGIN "u8) || clear.AsSpan().IndexOf("PRIVATE KEY-----"u8) < 0)
                    throw new ArgumentException("Select a PEM or OpenSSH private key.");
                var envelope = SecretEnvelope.Encrypt(clear, passphrase, idToFind);
                var record = existing ?? new JsonObject { ["id"] = idToFind };
                record["name"] = args.ElementAtOrDefault(2) ?? idToFind;
                record["envelope"] = JsonSerializer.SerializeToNode(envelope, LocalState.Json);
                if (existing is null) keys.Add(record);
                Save(workspace);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
            return new { id = idToFind, encrypted = true };
        }

        var secret = existing?["envelope"]?.Deserialize<SecretEnvelope>(LocalState.Json)
            ?? throw new InvalidOperationException("Unknown SSH identity.");
        var decoded = secret.Decrypt(passphrase, idToFind);
        try { WriteIdentity(idToFind, decoded); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
        return new { id = idToFind, installed = true };
    }

    private static void WriteIdentity(string id, ReadOnlySpan<byte> key)
    {
        var destination = IdentityPath(id);
        var folder = Path.GetDirectoryName(destination)!;
        LocalState.EnsureDirectory(folder);
        var temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, new FileStreamOptions
            {
                Access = FileAccess.Write, Mode = FileMode.CreateNew, UnixCreateMode = LocalState.PrivateFile
            })) file.Write(key);
            File.Move(temp, destination, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static string IdentityPath(string id) => Path.Combine(LocalState.Root, "identities",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))));

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Enter a JSON object.");
    private static string? Text(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
    private static JsonObject? ObjectBy(JsonArray array, string field, string value) =>
        array.OfType<JsonObject>().FirstOrDefault(obj => string.Equals(Text(obj, field), value, StringComparison.Ordinal));
    private static JsonArray ArrayAt(JsonObject root, string name)
    {
        if (root[name] is JsonArray array) return array;
        var result = new JsonArray();
        root[name] = result;
        return result;
    }

    private static void ValidateWorkspace(JsonObject document)
    {
        if (document["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1 ||
            document["spaces"] is not JsonArray spaces || spaces.Any(space => space is not JsonObject obj ||
                string.IsNullOrWhiteSpace(Text(obj, "id")) || string.IsNullOrWhiteSpace(Text(obj, "name")) || obj["items"] is not JsonArray))
            throw new ArgumentException("Use CommandTray workspace schemaVersion 1 with a spaces array.");
    }

    private static JsonObject Save(WorkspaceState workspace)
    {
        LocalState.Write(LocalState.Workspace, workspace with { Dirty = true });
        return workspace.Document;
    }
}
