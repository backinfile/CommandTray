using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CommandTray.Sync;

namespace CommandTray.Services;

internal sealed class DesktopRelay
{
    private readonly string _path;
    private readonly WorkspaceStore _workspace;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _settingsGate = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private Connection? _connection;

    private sealed record Connection(Uri Server, string DeviceId, string Token, long Revision);

    public DesktopRelay(string directory, WorkspaceStore workspace)
    {
        _path = Path.Combine(directory, "relay.json");
        _workspace = workspace;
        if (!File.Exists(_path)) return;
        var file = JsonNode.Parse(File.ReadAllText(_path))!;
        var server = ParseServer(file["server"]!.GetValue<string>());
        var token = Unprotect(file["credential"]!.GetValue<string>());
        _connection = new Connection(server, file["deviceId"]!.GetValue<string>(), token, file["revision"]!.GetValue<long>());
    }

    public object Status()
    {
        lock (_settingsGate)
            return new { server = _connection?.Server.AbsoluteUri, deviceId = _connection?.DeviceId,
                revision = _connection?.Revision ?? 0, paired = _connection is not null };
    }

    public string? Server
    {
        get { lock (_settingsGate) return _connection?.Server.AbsoluteUri; }
    }

    public async Task PairAsync(string url, string name, string pairingCode, CancellationToken cancellationToken = default)
    {
        var server = ParseServer(url);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var result = await new RelayClient(server, http: Http).PairAsync(name, pairingCode, cancellationToken);
            Save(new Connection(server, result.DeviceId, result.Token, 0));
        }
        finally { _operations.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            lock (_settingsGate)
            {
                File.Delete(_path);
                _connection = null;
            }
        }
        finally { _operations.Release(); }
    }

    public async Task<long> PushWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var connection = GetConnection();
            var (document, _) = _workspace.ReadDocument();
            var response = await Client(connection).PutWorkspaceAsync(connection.Revision, document, cancellationToken);
            var revision = Revision(response);
            Save(connection with { Revision = revision });
            return revision;
        }
        finally { _operations.Release(); }
    }

    public async Task<long> PullWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var connection = GetConnection();
            var (_, generation) = _workspace.ReadDocument();
            var response = await Client(connection).GetWorkspaceAsync(cancellationToken);
            var revision = Revision(response);
            if (revision == 0 || response["document"] is not JsonObject document)
                throw new InvalidOperationException("No workspace has been pushed to this relay.");
            _workspace.RecoverFromRelay(document, generation);
            Save(connection with { Revision = revision });
            return revision;
        }
        finally { _operations.Release(); }
    }

    public Task<JsonObject> GetClipboardAsync(CancellationToken cancellationToken = default) =>
        Client(GetConnection()).GetClipboardAsync(cancellationToken);

    public Task<JsonObject> PutClipboardAsync(string text, CancellationToken cancellationToken = default) =>
        Client(GetConnection()).PutClipboardAsync(text, cancellationToken);

    public Task<JsonArray> ListFilesAsync(CancellationToken cancellationToken = default) =>
        Client(GetConnection()).ListFilesAsync(cancellationToken);

    public Task<JsonArray> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        Client(GetConnection()).ListDevicesAsync(cancellationToken);

    public async Task<JsonObject> UploadAsync(string path, string? folder, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        return await Client(GetConnection()).UploadAsync(Path.GetFileName(path), stream, folder, cancellationToken);
    }

    public async Task DownloadAsync(string id, string destination, bool overwrite, CancellationToken cancellationToken = default)
    {
        destination = Path.GetFullPath(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await Client(GetConnection()).DownloadAsync(id, stream, cancellationToken);
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task DeleteFileAsync(string id, CancellationToken cancellationToken = default) =>
        await Client(GetConnection()).DeleteFileAsync(id, cancellationToken);

    private Connection GetConnection()
    {
        lock (_settingsGate) return _connection ?? throw new InvalidOperationException("Pair this device in settings first.");
    }

    private static RelayClient Client(Connection connection) => new(connection.Server, connection.Token, Http);

    private void Save(Connection connection)
    {
        lock (_settingsGate)
        {
            WorkspaceStore.WriteAtomic(_path, new JsonObject
            {
                ["server"] = connection.Server.AbsoluteUri, ["deviceId"] = connection.DeviceId,
                ["credential"] = Protect(connection.Token), ["revision"] = connection.Revision
            });
            _connection = connection;
        }
    }

    private static long Revision(JsonObject response) => response["revision"]?.GetValue<long>()
        ?? throw new InvalidDataException("The relay response has no workspace revision.");

    private static Uri ParseServer(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var result) ||
            (result.Scheme != "https" && !(result.Scheme == "http" && result.IsLoopback)) ||
            result.UserInfo.Length > 0 || result.Query.Length > 0 || result.Fragment.Length > 0)
            throw new ArgumentException("Use an HTTPS relay address or HTTP on this computer.");
        return result;
    }

    internal static string Protect(string value) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    internal static string Unprotect(string value) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));

    public static string ReadOrCreateLocalCredential(string directory)
    {
        var path = Path.Combine(directory, "mcp-credential.json");
        if (File.Exists(path))
            return Unprotect(JsonNode.Parse(File.ReadAllText(path))!.GetValue<string>());
        var credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower(CultureInfo.InvariantCulture);
        WorkspaceStore.WriteAtomic(path, JsonValue.Create(Protect(credential))!);
        return credential;
    }
}
