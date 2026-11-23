using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CommandTray.Sync;

namespace CommandTray.Android;

internal sealed class AndroidWorkspace(string root, AndroidSettings settings)
{
    private string PartKey => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Server)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private string StatePath => Path.Combine(root, "workspace-" + PartKey + ".json");
    private string JournalPath => Path.Combine(root, "workspace-" + PartKey + ".journal.json");
    private WorkspaceSyncJournal Journal => new(JournalPath);

    public JsonObject Read()
    {
        var path = StatePath;
        if (!File.Exists(path) && File.Exists(path + ".bak"))
            path += ".bak";
        return File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("The workspace cache is invalid.")
            : new JsonObject { ["revision"] = 0, ["dirty"] = false, ["document"] = EmptyDocument() };
    }

    public JsonObject Document()
    {
        var state = Read();
        var document = RequireDocument(state);
        var spaces = Spaces(document);
        var localActive = settings.ActiveSpace;
        var selected = FindOptional(spaces, localActive) ?? FindOptional(spaces, Text(document["activeSpaceId"])) ??
            spaces.OfType<JsonObject>().FirstOrDefault();
        document["activeSpaceId"] = selected is null ? null : Text(selected["id"]);
        return document;
    }

    public string CreateSpace(string name)
    {
        name = RequiredName(name, "Space");
        var doc = Document();
        var id = NewId();
        Spaces(doc).Add(new JsonObject { ["id"] = id, ["name"] = name, ["items"] = new JsonArray() });
        doc["activeSpaceId"] = id;
        SaveEdit(doc);
        settings.SelectSpace(id);
        return id;
    }

    public void RenameSpace(string id, string name)
    {
        var doc = Document();
        Find(Spaces(doc), id)["name"] = RequiredName(name, "Space");
        SaveEdit(doc);
    }

    public void DeleteSpace(string id)
    {
        var doc = Document();
        var spaces = Spaces(doc);
        spaces.Remove(Find(spaces, id));
        if (id == Text(doc["activeSpaceId"]))
        {
            var next = spaces.OfType<JsonObject>().FirstOrDefault();
            doc["activeSpaceId"] = next is null ? null : Text(next["id"]);
            settings.SelectSpace(Text(doc["activeSpaceId"]));
        }
        SaveEdit(doc);
    }

    public void ActivateSpace(string id)
    {
        _ = Find(Spaces(Document()), id);
        // Older builds and mobile navigation use a per-device selection.
        settings.SelectSpace(id);
    }

    public string SaveItem(string spaceId, JsonObject requested)
    {
        var doc = Document();
        var space = Find(Spaces(doc), spaceId);
        var items = space["items"] as JsonArray ?? throw new InvalidDataException("The space has no components array.");
        var id = Text(requested["id"]);
        var kind = Text(requested["kind"]);
        if (id.Length == 0)
            id = NewId();

        var existing = FindOptional(items, id);
        if (kind.Length == 0)
            kind = Text(existing?["kind"]);
        if (kind is not ("note" or "todo") || (existing is not null && Text(existing["kind"]) != kind))
            throw new ArgumentException("Android can edit notes and to-dos.");
        var title = requested["title"] is null ? Text(existing?["title"]) : Text(requested["title"]);
        if (title.Length > 128)
            throw new ArgumentException("The component name is too long.");
        var replacement = existing is null ? (JsonObject)requested.DeepClone() : existing;
        replacement["id"] = id;
        replacement["kind"] = kind;
        replacement["title"] = title;
        if (requested["data"] is JsonObject requestedData)
        {
            if (replacement["data"] is not JsonObject currentData)
            {
                currentData = new JsonObject();
                replacement["data"] = currentData;
            }
            foreach (var (key, value) in requestedData)
                currentData[key] = value?.DeepClone();
        }

        if (existing is null)
        {
            replacement["x"] ??= 0;
            replacement["y"] ??= 0;
            replacement["width"] ??= 320;
            replacement["height"] ??= 240;
            replacement["data"] ??= kind == "note"
                ? new JsonObject { ["text"] = "" }
                : new JsonObject { ["tasks"] = new JsonArray() };
            items.Add(replacement);
        }
        SaveEdit(doc);
        return id;
    }

    public void RemoveItem(string spaceId, string itemId)
    {
        var doc = Document();
        var space = Find(Spaces(doc), spaceId);
        var items = space["items"] as JsonArray ?? throw new InvalidDataException("The space has no components array.");
        var item = Find(items, itemId);
        if (Text(item["kind"]) is not ("note" or "todo"))
            throw new ArgumentException("Android can remove notes and to-dos.");
        items.Remove(item);
        SaveEdit(doc);
    }

    public async Task<WorkspaceSyncResult> SynchronizeAsync(RelayClient client, bool push, CancellationToken cancellation)
    {
        var state = Read();
        if (state["remote"] is JsonObject)
            throw new InvalidOperationException("Resolve the saved workspace conflict first.");
        var local = RequireDocument(state);
        var revision = Revision(state);
        // A clean legacy snapshot has no pending edits to compare with the current relay revision.
        if (revision > 0 && state["dirty"]?.GetValue<bool>() == false && !File.Exists(JournalPath))
        {
            var incoming = await client.GetWorkspaceAsync(cancellation);
            if (Revision(incoming) >= revision)
            {
                local = RequireDocument(incoming);
                revision = Revision(incoming);
                CommitSync(local, revision);
            }
        }
        // Device navigation is stored separately. It does not dirty the network workspace.
        return await Journal.SynchronizeAsync(client, local, revision, push, CommitSync, cancellation);
    }

    public async Task<WorkspaceSyncResult> ResolveAsync(RelayClient client, string choice, CancellationToken cancellation)
    {
        if (choice is not ("local" or "remote"))
            throw new ArgumentException("Choose the local or remote version.");
        var state = Read();
        var journal = Journal;
        if (journal.ReadConflict() is not null)
            return await journal.ResolveAsync(client, RequireDocument(state), choice, CommitSync, cancellation);

        // Java's previous cache kept a conflicting relay envelope beside the local document.
        if (state["remote"] is not JsonObject legacy)
            throw new InvalidOperationException("There is no workspace conflict.");
        var incoming = await client.GetWorkspaceAsync(cancellation);
        var remoteVersion = Revision(incoming);
        if (remoteVersion < Revision(legacy))
            throw new InvalidOperationException("The relay workspace revision has moved backwards.");
        if (choice == "remote")
        {
            CommitSync(RequireDocument(incoming), remoteVersion);
            return await SynchronizeAsync(client, push: false, cancellation);
        }
        var envelope = await client.PutWorkspaceAsync(remoteVersion, RequireDocument(state), cancellation);
        CommitSync(RequireDocument(envelope), Revision(envelope));
        return await SynchronizeAsync(client, push: false, cancellation);
    }

    public JsonObject? Conflict()
    {
        if (Journal.ReadConflict() is { } conflict)
            return new JsonObject
            {
                ["remoteRevision"] = conflict.RemoteRevision,
                ["local"] = conflict.Local.DeepClone(),
                ["remote"] = conflict.Remote.DeepClone()
            };
        var state = Read();
        return state["remote"] is JsonObject legacy
            ? new JsonObject
            {
                ["remoteRevision"] = Revision(legacy),
                ["local"] = RequireDocument(state).DeepClone(),
                ["remote"] = RequireDocument(legacy).DeepClone()
            }
            : null;
    }

    public static long Revision(JsonObject envelope) =>
        envelope["revision"] is JsonValue number && number.TryGetValue<long>(out var revision) ? revision : 0;

    private void CommitSync(JsonObject document, long revision) =>
        Write(new JsonObject { ["revision"] = revision, ["document"] = document.DeepClone(), ["dirty"] = false });

    private void SaveEdit(JsonObject document)
    {
        var envelope = Read();
        envelope["document"] = document.DeepClone();
        envelope["dirty"] = true;
        Write(envelope);
    }

    private void Write(JsonObject value)
    {
        var path = StatePath;
        var temp = path + "." + NewId() + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
            {
                System.Text.Json.JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static JsonObject EmptyDocument() => new()
    {
        ["schemaVersion"] = 1, ["activeSpaceId"] = null, ["spaces"] = new JsonArray(), ["sshHosts"] = new JsonArray()
    };

    private static JsonObject RequireDocument(JsonObject envelope) =>
        envelope["document"] is JsonObject doc && doc["spaces"] is JsonArray
            ? doc
            : throw new InvalidDataException("The workspace document is invalid.");

    private static JsonArray Spaces(JsonObject document) => (JsonArray)document["spaces"]!;

    private static JsonObject Find(JsonArray entries, string id) => FindOptional(entries, id)
        ?? throw new KeyNotFoundException("The workspace item was not found.");

    private static JsonObject? FindOptional(JsonArray entries, string id) =>
        id.Length == 0 ? null : entries.OfType<JsonObject>().FirstOrDefault(entry => Text(entry["id"]) == id);

    private static string RequiredName(string input, string type)
    {
        var name = input.Trim();
        if (name.Length is 0 or > 128)
            throw new ArgumentException(type + " name must be between 1 and 128 characters.");
        return name;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
