using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using CommandTray.Sync;

namespace CommandTray.Relay;

internal sealed class RelayStore : IDisposable
{
    private readonly RelayDirectory _directory;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private RelayState _state;

    public RelayStore(RelayDirectory directory)
    {
        _directory = directory;
        var lease = directory.Acquire();
        try
        {
            Directory.CreateDirectory(directory.Blobs);
            _state = directory.Read();
            _lease = lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal RelayState State => Volatile.Read(ref _state);

    public DeviceRecord? Authenticate(string? bearer)
    {
        if (bearer is not { Length: 64 } || !bearer.All(Uri.IsHexDigit)) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(bearer));
        return State.Devices.FirstOrDefault(device =>
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(device.TokenHash), hash));
    }

    public async Task<(DeviceRecord Device, string Token)> PairAsync(string name, CancellationToken cancellation)
    {
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var entry = new DeviceRecord(Guid.NewGuid().ToString("N"), name, HashToken(token), DateTimeOffset.UtcNow);

        await _writes.WaitAsync(cancellation);
        try
        {
            var state = State;
            if (state.Devices.Length >= 200) throw new InvalidDataException("The relay already has 200 paired devices.");
            await CommitAsync(state with { Devices = [.. state.Devices, entry] }, cancellation);
            return (entry, token);
        }
        finally { _writes.Release(); }
    }

    public async Task<bool> DeleteDeviceAsync(string id, CancellationToken cancellation)
    {
        await _writes.WaitAsync(cancellation);
        try
        {
            var state = State;
            var devices = state.Devices.Where(device => device.Id != id).ToArray();
            if (devices.Length == state.Devices.Length) return false;
            await CommitAsync(state with { Devices = devices }, cancellation);
            return true;
        }
        finally { _writes.Release(); }
    }

    public async Task<WorkspaceWrite> PutWorkspaceAsync(WorkspaceInput input, CancellationToken cancellation)
    {
        await _writes.WaitAsync(cancellation);
        try
        {
            var state = State;
            if (state.Workspace.Revision != input.BaseRevision)
                return new(JsonElement.DeepEquals(state.Workspace.Document, input.Document), state.Workspace);

            var record = new WorkspaceRecord(state.Workspace.Revision + 1, input.Document.Clone());
            await CommitAsync(AdvanceWorkspace(state, record), cancellation);
            return new(true, record);
        }
        finally { _writes.Release(); }
    }

    public object WorkspaceChanges(long after)
    {
        var state = State;
        var history = state.WorkspaceHistory.Where(entry => entry.Revision > after).ToArray();
        if (after == state.Workspace.Revision || after < state.Workspace.Revision &&
            history.LongLength == state.Workspace.Revision - after &&
            history.Select((entry, index) => entry.Revision == after + index + 1).All(value => value))
            return new { revision = state.Workspace.Revision, changes = history };
        return state.Workspace;
    }

    public async Task<WorkspaceWrite> ApplyWorkspaceAsync(WorkspaceDeltaInput input, string deviceId, CancellationToken cancellation)
    {
        await _writes.WaitAsync(cancellation);
        try
        {
            var state = State;
            var receipt = new SyncReceipt(deviceId, input.OperationId!);
            if (state.SyncReceipts.Contains(receipt)) return new(true, state.Workspace);
            var snapshot = ReadDocument(state.Workspace.Document);
            if (input.BaseRevision > state.Workspace.Revision ||
                !WorkspaceDelta.TryApply(snapshot, input.Changes!, out var merged)) return new(false, state.Workspace);
            var document = JsonSerializer.SerializeToElement(merged, RelayData.Json);
            if (!RelayData.IsWorkspace(document) || Encoding.UTF8.GetByteCount(document.GetRawText()) > RelayData.WorkspaceLimit)
                throw new InvalidDataException("The workspace changes do not produce a valid workspace.");
            var workspace = state.Workspace;
            if (!JsonNode.DeepEquals(snapshot, merged))
            {
                workspace = new(workspace.Revision + 1, document);
                state = AdvanceWorkspace(state, workspace);
            }
            state = state with { SyncReceipts = [.. state.SyncReceipts.TakeLast(511), receipt] };
            await CommitAsync(state, cancellation);
            return new(true, workspace);
        }
        finally { _writes.Release(); }
    }

    private static RelayState AdvanceWorkspace(RelayState state, WorkspaceRecord workspace)
    {
        var changes = WorkspaceDelta.Between(ReadDocument(state.Workspace.Document), ReadDocument(workspace.Document));
        var history = new List<WorkspaceRevision>();
        int bytes = 0;
        foreach (var revision in state.WorkspaceHistory.Append(new(workspace.Revision, changes)).Reverse().Take(32))
        {
            int entryBytes = JsonSerializer.SerializeToUtf8Bytes(revision, RelayData.Json).Length;
            if (entryBytes > 4 * 1024 * 1024 - bytes || revision.Changes.Any(change => change.Path.Length > 32)) break;
            bytes += entryBytes;
            history.Add(revision);
        }
        history.Reverse();
        return state with { Workspace = workspace, WorkspaceHistory = [.. history] };
    }

    private static JsonObject ReadDocument(JsonElement element) => JsonNode.Parse(element.GetRawText())!.AsObject();

    public async Task<ClipboardRecord> PutClipboardAsync(string text, string deviceId, CancellationToken cancellation)
    {
        await _writes.WaitAsync(cancellation);
        try
        {
            var state = State;
            if (state.Clipboard.Text == text) return state.Clipboard;
            var record = new ClipboardRecord(state.Clipboard.Revision + 1, text, deviceId, DateTimeOffset.UtcNow);
            await CommitAsync(state with { Clipboard = record }, cancellation);
            return record;
        }
        finally { _writes.Release(); }
    }

    public async Task<StoredFile> UploadAsync(string name, string folder, Stream input, string deviceId, CancellationToken cancellation)
    {
        string id = Guid.NewGuid().ToString("N");
        string temp = _directory.Blob(id) + ".partial";
        try
        {
            var (size, sha256) = await StageAsync(temp, input, cancellation);
            await _writes.WaitAsync(cancellation);
            try
            {
                var state = State;
                var identical = state.Files.FirstOrDefault(file => file.Name == name && file.Folder == folder && file.Sha256 == sha256);
                if (identical is not null) return identical;
                var entry = new StoredFile(id, name, folder, size, sha256, DateTimeOffset.UtcNow, deviceId);
                var destination = _directory.Blob(id);
                File.Move(temp, destination);
                try
                {
                    await CommitAsync(state with { Files = [.. state.Files, entry] }, cancellation);
                }
                catch
                {
                    File.Delete(destination);
                    throw;
                }
                return entry;
            }
            finally { _writes.Release(); }
        }
        finally { File.Delete(temp); }
    }

    public (StoredFile Entry, Stream Data)? OpenFile(string id)
    {
        if (!RelayData.IsId(id)) return null;
        var entry = State.Files.FirstOrDefault(file => file.Id == id);
        if (entry is null) return null;
        try
        {
            return (entry, new FileStream(_directory.Blob(id), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 64 * 1024, true));
        }
        catch (FileNotFoundException) { return null; }
    }

    public async Task<bool> DeleteFileAsync(string id, CancellationToken cancellation)
    {
        if (!RelayData.IsId(id)) return false;
        await _writes.WaitAsync(cancellation);
        try
        {
            var state = State;
            var remaining = state.Files.Where(file => file.Id != id).ToArray();
            if (remaining.Length == state.Files.Length) return false;
            await CommitAsync(state with { Files = remaining }, cancellation);
            File.Delete(_directory.Blob(id));
            return true;
        }
        finally { _writes.Release(); }
    }

    private static async Task<(long Size, string Sha256)> StageAsync(string temp, Stream input, CancellationToken cancellation)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
            long size = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
            {
                size += count;
                if (size > RelayData.FileLimit) throw new FileLimitException();
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
            }
            await output.FlushAsync(cancellation);
            output.Flush(flushToDisk: true);
            return (size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private async Task CommitAsync(RelayState next, CancellationToken cancellation)
    {
        var destination = _directory.StatePath;
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, next, RelayData.Json, cancellation);
                await stream.FlushAsync(cancellation);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, destination, overwrite: true);
            Volatile.Write(ref _state, next);
        }
        finally { File.Delete(temp); }
    }

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public void Dispose()
    {
        _lease.Dispose();
        _writes.Dispose();
    }
}

internal sealed class FileLimitException : IOException;
