using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CommandTray.Sync;
using static CommandTray.Linux.ServiceRuntime;

namespace CommandTray.Linux;

internal sealed partial class LinuxCommands
{
    private static string JournalPath(LocalSettings settings)
    {
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Server + "\n" + settings.DeviceId)));
        return Path.Combine(LocalState.Root, "workspace-journal-" + scope + ".json");
    }

    public static WorkspaceSyncConflict? ReadConflict(LocalSettings settings)
    {
        return settings.Token is null ? null : new WorkspaceSyncJournal(JournalPath(settings)).ReadConflict();
    }

    private static async Task<object> SynchronizeWorkspaceAsync(string action, string[] arguments, CancellationToken cancellationToken)
    {
        using var synchronization = await LockSynchronizationAsync(cancellationToken);
        var settings = LocalSettings.Load();
        if (settings.Server is null || settings.Token is null)
            throw new InvalidOperationException("Pair this device with commandtray sync pair before using the relay.");

        var local = WorkspaceState.Load();
        var path = JournalPath(settings);
        var journal = new WorkspaceSyncJournal(path);
        var client = new RelayClient(ParseServer(settings.Server), settings.Token);

        var replacement = action == "pull" && arguments.Contains("--replace", StringComparer.Ordinal);
        var applied = false;
        void Apply(JsonObject document, long revision)
        {
            using var workspace = WorkspaceLock();
            var current = WorkspaceState.Load();
            var pairing = LocalSettings.Load();
            if (pairing.Server != settings.Server || pairing.Token != settings.Token ||
                current.BaseRevision != local.BaseRevision || current.Dirty != local.Dirty ||
                !JsonNode.DeepEquals(current.Document, local.Document))
                throw new InvalidOperationException("The local workspace or pairing changed while synchronizing. Sync again to include the latest changes.");
            LocalState.Write(LocalState.Workspace, new WorkspaceState(revision, action == "pull" && !replacement && local.Dirty, document));
            applied = true;
        }

        string? archived = null;
        WorkspaceSyncResult result;
        switch (action)
        {
            case "resolve":
            {
                var choice = Required(arguments, 0, "conflict choice (local or remote)");
                if (choice is not ("local" or "remote")) throw new ArgumentException("Choose local or remote.");
                result = await journal.ResolveAsync(client, local.Document, choice, Apply, cancellationToken);
                break;
            }
            case "pull" when replacement:
            {
                if (local.Dirty)
                {
                    archived = Path.Combine(LocalState.Root, "workspace-local-" + Guid.NewGuid().ToString("N") + ".json");
                    LocalState.Write(archived, local);
                }
                if (journal.ReadConflict() is not null)
                    result = await journal.ResolveAsync(client, local.Document, "remote", Apply, cancellationToken);
                else
                {
                    var remote = await client.GetWorkspaceAsync(cancellationToken);
                    var document = remote["document"] as JsonObject
                        ?? throw new InvalidDataException("The relay response omitted the workspace document.");
                    var revision = Revision(remote);
                    Apply(document, revision);
                    File.Delete(path);
                    result = new WorkspaceSyncResult(revision, document, null);
                }
                break;
            }
            default:
                result = await journal.SynchronizeAsync(client, local.Document, local.BaseRevision, action == "push", Apply, cancellationToken);
                break;
        }

        var conflictFile = Path.Combine(LocalState.Root, "workspace-remote-conflict.json");
        if (result.Conflict is { } conflict)
        {
            LocalState.Write(conflictFile, new { revision = conflict.RemoteRevision, document = conflict.Remote });
            throw new InvalidOperationException($"The remote workspace changed. Local edits are preserved. Remote conflict saved to {conflictFile}.");
        }
        File.Delete(conflictFile);
        if (!applied && (action is "push" or "resolve") && local.Dirty)
        {
            using var workspace = WorkspaceLock();
            var current = WorkspaceState.Load();
            var pairing = LocalSettings.Load();
            if (current.BaseRevision == local.BaseRevision && JsonNode.DeepEquals(current.Document, local.Document) &&
                pairing.Server == settings.Server && pairing.Token == settings.Token)
                LocalState.Write(LocalState.Workspace, current with { Dirty = false });
        }
        return new { revision = result.Revision, dirty = WorkspaceState.Load().Dirty, saved = archived };
    }

    private static async Task<FileStream> LockSynchronizationAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(LocalState.Root, ".sync.lock");
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return LocalState.Lock(lockPath); }
            catch (InvalidOperationException) when (attempt < 40)
            {
                await Task.Delay(125, cancellationToken);
            }
        }
    }

    public static async Task SyncPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var interval = new PeriodicTimer(TimeSpan.FromSeconds(12));
        try
        {
            while (await interval.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var settings = LocalSettings.Load();
                    if (settings.Token is null || ReadConflict(settings) is not null) continue;
                    var workspace = WorkspaceState.Load();
                    var action = !workspace.Dirty && workspace.BaseRevision == 0 && workspace.Document["spaces"] is JsonArray { Count: 0 }
                        ? "pull" : "push";
                    await SynchronizeWorkspaceAsync(action, [], cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TimeoutException or
                    System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    // The journal and local workspace remain available for the next reconnection.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
