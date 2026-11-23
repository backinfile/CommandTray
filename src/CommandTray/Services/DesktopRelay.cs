using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CommandTray.Sync;

namespace CommandTray.Services;

internal sealed class DesktopRelay
{
    private readonly string _path;
    private readonly string _directory;
    private readonly WorkspaceStore _workspace;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _settingsGate = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private Connection? _connection;
    private WorkspaceSyncJournal? _journal;
    private string? _journalPath;

    private sealed record Connection(Uri Server, string DeviceId, string DeviceName, string Token, long Revision);

    public DesktopRelay(string directory, WorkspaceStore workspace)
    {
        _directory = directory;
        _path = Path.Combine(directory, "relay.json");
        _workspace = workspace;
        if (!File.Exists(_path)) return;
        var file = JsonNode.Parse(File.ReadAllText(_path))!;
        var server = ParseServer(file["server"]!.GetValue<string>());
        var token = Unprotect(file["credential"]!.GetValue<string>());
        _connection = new Connection(server, file["deviceId"]!.GetValue<string>(),
            WorkspaceStore.Text(file["deviceName"]) ?? Environment.MachineName, token, file["revision"]!.GetValue<long>());
    }

    public object Status()
    {
        lock (_settingsGate)
        {
            var conflict = _connection is null ? null : Journal(_connection).ReadConflict();
            return new
            {
                server = _connection?.Server.AbsoluteUri, deviceId = _connection?.DeviceId,
                deviceName = _connection?.DeviceName ?? Environment.MachineName,
                revision = _connection?.Revision ?? 0, paired = _connection is not null,
                conflict = conflict is null ? null : new { conflict.RemoteRevision }
            };
        }
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
            Save(new Connection(server, result.DeviceId, name, result.Token, 0));
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

    public Task<long> PushWorkspaceAsync(CancellationToken cancellationToken = default) =>
        SynchronizeAsync(push: true, cancellationToken);

    public Task<long> PullWorkspaceAsync(CancellationToken cancellationToken = default) =>
        SynchronizeAsync(push: false, cancellationToken);

    public async Task<long> ResolveWorkspaceAsync(string choice, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var connection = GetConnection();
            var (document, generation) = _workspace.ReadForSync();
            var result = await Journal(connection).ResolveAsync(Client(connection), document, choice,
                (incoming, revision) => Apply(connection, incoming, generation, revision, backup: true), cancellationToken);
            return RequireResolved(result);
        }
        finally { _operations.Release(); }
    }

    public async Task RunBackgroundSyncAsync(Action onCompleted, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(12));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Server is null) continue;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await SynchronizeAsync(push: true, request.Token, backup: false).ConfigureAwait(false);
                    onCompleted();
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException
                    or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    // The next timer tick retries transport errors and an explicit resolution clears conflicts.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<long> SynchronizeAsync(bool push, CancellationToken cancellationToken, bool backup = true)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = GetConnection();
            var (document, generation) = _workspace.ReadForSync();
            var result = await Journal(connection).SynchronizeAsync(Client(connection), document, connection.Revision,
                push, (incoming, revision) => Apply(connection, incoming, generation, revision, backup), cancellationToken)
                .ConfigureAwait(false);
            return RequireResolved(result);
        }
        finally { _operations.Release(); }
    }

    private void Apply(Connection connection, JsonObject document, long generation, long revision, bool backup)
    {
        _workspace.RecoverFromRelay(document, generation, backup, restoreLocalPaths: true);
        if (connection.Revision != revision) Save(connection with { Revision = revision });
    }

    private WorkspaceSyncJournal Journal(Connection connection)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.Server.AbsoluteUri + "\n" + connection.DeviceId)));
        var path = Path.Combine(_directory, $"workspace-sync-{hash}.json");
        lock (_settingsGate)
        {
            if (_journalPath != path)
            {
                _journal = new WorkspaceSyncJournal(path);
                _journalPath = path;
            }
            return _journal!;
        }
    }

    private static long RequireResolved(WorkspaceSyncResult result) => result.Conflict is null ? result.Revision :
        throw new InvalidOperationException("Workspace sync conflict. Choose the local or relay version in Settings.");

    public Task<JsonObject> GetClipboardAsync(CancellationToken cancellationToken = default) =>
        Client(GetConnection()).GetClipboardAsync(cancellationToken);

    public Task<JsonObject> PutClipboardAsync(string text, CancellationToken cancellationToken = default) =>
        Client(GetConnection()).PutClipboardAsync(text, cancellationToken);

    public Task<JsonArray> ListFilesAsync(CancellationToken cancellationToken = default) =>
        Client(GetConnection()).ListFilesAsync(cancellationToken);

    public Task<JsonArray> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        Client(GetConnection()).ListDevicesAsync(cancellationToken);

    public async Task<JsonObject> UploadAsync(string path, string? folder, CancellationToken cancellationToken = default, string? name = null)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        return await Client(GetConnection()).UploadAsync(name ?? Path.GetFileName(path), stream, folder, cancellationToken);
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
                ["deviceName"] = connection.DeviceName,
                ["credential"] = Protect(connection.Token), ["revision"] = connection.Revision
            });
            _connection = connection;
        }
    }

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
