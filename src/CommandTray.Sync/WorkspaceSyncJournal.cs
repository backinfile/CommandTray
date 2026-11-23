using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandTray.Sync;

public sealed record WorkspaceSyncConflict(long RemoteRevision, JsonObject Local, JsonObject Remote);
public sealed record WorkspaceSyncResult(long Revision, JsonObject Document, WorkspaceSyncConflict? Conflict);

public sealed class WorkspaceSyncJournal
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _disk = new();

    private sealed record Pending(long Revision, string Id, JsonObject Local, WorkspaceChange[] Changes);
    private sealed record Journal(long Revision, JsonObject Base, Pending? Outbox = null, WorkspaceSyncConflict? Conflict = null);

    public WorkspaceSyncJournal(string statePath) => _path = Path.GetFullPath(statePath);

    public WorkspaceSyncConflict? ReadConflict() => Read()?.Conflict;

    public async Task<WorkspaceSyncResult> SynchronizeAsync(
        RelayClient client, JsonObject local, long knownRevision, bool push,
        Action<JsonObject, long> apply, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            var state = Read();
            if (state?.Conflict is { } conflict)
                return new(knownRevision, local, conflict with { Local = Copy(local) });

            if (state is null)
            {
                var (revision, remote) = Snapshot(await client.GetWorkspaceAsync(cancellationToken));
                if (JsonNode.DeepEquals(local, remote) || !push && local["spaces"] is JsonArray { Count: 0 } && revision > 0)
                    return Complete(revision, remote, remote, local, knownRevision, apply);
                if (revision != knownRevision)
                    return KeepConflict(new Journal(knownRevision, Copy(local)), local, revision, remote);
                state = new Journal(revision, remote);
                Write(state);
            }

            if (state.Outbox is not null)
                return await DeliverAsync(client, state, local, knownRevision, apply, cancellationToken);

            var changes = WorkspaceDelta.Between(state.Base, local);
            if (push && changes.Length > 0)
            {
                state = state with { Outbox = new Pending(state.Revision, Guid.NewGuid().ToString("N"), Copy(local), changes) };
                Write(state);
                return await DeliverAsync(client, state, local, knownRevision, apply, cancellationToken);
            }

            var (remoteRevision, remoteDocument) = await FetchAsync(client, state, cancellationToken);
            if (remoteRevision < state.Revision || !WorkspaceDelta.TryApply(remoteDocument, changes, out var merged))
                return KeepConflict(state, local, remoteRevision, remoteDocument);
            return Complete(remoteRevision, merged, remoteDocument, local, knownRevision, apply);
        }
        finally { _operation.Release(); }
    }

    public async Task<WorkspaceSyncResult> ResolveAsync(
        RelayClient client, JsonObject local, string choice, Action<JsonObject, long> apply,
        CancellationToken cancellationToken = default)
    {
        if (choice is not ("local" or "remote")) throw new ArgumentException("Choose local or remote.", nameof(choice));
        await _operation.WaitAsync(cancellationToken);
        try
        {
            var state = Read();
            if (state?.Conflict is null) throw new InvalidOperationException("There is no workspace conflict to resolve.");
            var (revision, remote) = Snapshot(await client.GetWorkspaceAsync(cancellationToken));
            if (choice == "remote") return Complete(revision, remote, remote, local, state.Revision, apply);
            var changes = WorkspaceDelta.Between(remote, local);
            if (changes.Length == 0) return Complete(revision, remote, remote, local, state.Revision, apply);
            state = new Journal(revision, remote, new Pending(revision, Guid.NewGuid().ToString("N"), Copy(local), changes));
            Write(state);
            return await DeliverAsync(client, state, local, revision, apply, cancellationToken);
        }
        finally { _operation.Release(); }
    }

    private async Task<WorkspaceSyncResult> DeliverAsync(RelayClient client, Journal state, JsonObject local,
        long knownRevision, Action<JsonObject, long> apply, CancellationToken cancellationToken)
    {
        var pending = state.Outbox!;
        long revision;
        JsonObject remote;
        try
        {
            if (pending.Changes.Length > 4096)
                (revision, remote) = await SendSnapshotAsync(client, pending, cancellationToken);
            else
            {
                try
                {
                    (revision, remote) = Snapshot(await client.ApplyWorkspaceChangesAsync(
                        pending.Revision, pending.Id, pending.Changes, cancellationToken));
                }
                catch (RelayException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                    or HttpStatusCode.RequestEntityTooLarge)
                {
                    (revision, remote) = await SendSnapshotAsync(client, pending, cancellationToken);
                }
            }
        }
        catch (RelayConflictException conflict)
        {
            (revision, remote) = Snapshot(conflict.Current);
            return KeepConflict(state, local, revision, remote);
        }
        var newerLocalChanges = WorkspaceDelta.Between(pending.Local, local);
        if (!WorkspaceDelta.TryApply(remote, newerLocalChanges, out var merged))
            return KeepConflict(state, local, revision, remote);
        return Complete(revision, merged, remote, local, knownRevision, apply);
    }

    private static async Task<(long Revision, JsonObject Document)> SendSnapshotAsync(
        RelayClient client, Pending pending, CancellationToken cancellationToken)
    {
        var response = await client.GetWorkspaceAsync(cancellationToken);
        var (revision, document) = Snapshot(response);
        if (revision < pending.Revision || !WorkspaceDelta.TryApply(document, pending.Changes, out var merged))
            throw new RelayConflictException(response);
        if (JsonNode.DeepEquals(merged, document)) return (revision, document);
        return Snapshot(await client.PutWorkspaceAsync(revision, merged, cancellationToken));
    }

    private static async Task<(long Revision, JsonObject Document)> FetchAsync(
        RelayClient client, Journal state, CancellationToken cancellationToken)
    {
        JsonObject response;
        try { response = await client.GetWorkspaceChangesAsync(state.Revision, cancellationToken); }
        catch (RelayException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return Snapshot(await client.GetWorkspaceAsync(cancellationToken));
        }
        if (response["document"] is JsonObject) return Snapshot(response);
        var revision = state.Revision;
        var snapshot = state.Base;
        if (response["changes"] is JsonArray history)
        {
            foreach (var entry in history)
            {
                if (entry?.Deserialize<WorkspaceRevision>(Format) is not { } patch ||
                    patch.Revision != ++revision || !WorkspaceDelta.TryApply(snapshot, patch.Changes, out snapshot))
                    return Snapshot(await client.GetWorkspaceAsync(cancellationToken));
            }
        }
        if (Revision(response) != revision)
            return Snapshot(await client.GetWorkspaceAsync(cancellationToken));
        return (revision, snapshot);
    }

    private WorkspaceSyncResult Complete(long revision, JsonObject document, JsonObject baseline, JsonObject local,
        long knownRevision, Action<JsonObject, long> apply)
    {
        if (knownRevision != revision || !JsonNode.DeepEquals(document, local)) apply(Copy(document), revision);
        Write(new Journal(revision, Copy(baseline)));
        return new(revision, document, null);
    }

    private WorkspaceSyncResult KeepConflict(Journal state, JsonObject local, long revision, JsonObject remote)
    {
        var conflict = new WorkspaceSyncConflict(revision, Copy(local), Copy(remote));
        Write(state with { Outbox = null, Conflict = conflict });
        return new(state.Revision, local, conflict);
    }

    private Journal? Read()
    {
        lock (_disk)
        {
            if (!File.Exists(_path)) return null;
            using var input = File.OpenRead(_path);
            return JsonSerializer.Deserialize<Journal>(input, Format)
                ?? throw new InvalidDataException("The workspace sync journal is empty.");
        }
    }

    private void Write(Journal journal)
    {
        lock (_disk)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".new";
            try
            {
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, journal, Format);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static (long Revision, JsonObject Document) Snapshot(JsonObject response) =>
        (Revision(response), response["document"] as JsonObject ??
            throw new InvalidDataException("The relay did not return a workspace document."));

    private static long Revision(JsonObject response) => response["revision"]?.GetValue<long>()
        ?? throw new InvalidDataException("The relay did not return a revision.");

    private static JsonObject Copy(JsonObject source) => (JsonObject)source.DeepClone();
}
