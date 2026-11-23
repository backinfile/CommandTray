using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandTray.Services;

public sealed record WorkspaceItem(string Id, string Kind, string Title, Rectangle Bounds, JsonObject Data);
public sealed record WorkspaceSpace(string Id, string Name, IReadOnlyList<WorkspaceItem> Items);
public sealed record WorkspaceSnapshot(string? ActiveSpaceId, IReadOnlyList<WorkspaceSpace> Spaces);

public sealed record SshKeyView(string Id, string Name);

internal sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "files", "terminal", "note", "todo", "board", "preview", "service", "log",
        "commands", "clipboard", "captures", "ssh", "sharedFiles"
    };
    private readonly object _gate = new();
    private readonly string _path;
    private JsonObject _document;
    private long _generation;

    public WorkspaceStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "workspace.json");
        _document = File.Exists(_path)
            ? (JsonNode.Parse(File.ReadAllText(_path)) as JsonObject
               ?? throw new InvalidDataException("Workspace must be a JSON object."))
            : StartDocument();
        Validate(_document);
    }

    public event EventHandler? Changed;

    public (JsonObject Document, long Generation) ReadDocument()
    {
        lock (_gate) return ((JsonObject)_document.DeepClone(), _generation);
    }

    public (JsonObject Document, long Generation) ReadForSync()
    {
        var (document, generation) = ReadDocument();
        foreach (var (_, _, key, data) in LocalPathFields(document)) data.Remove(key);
        return (document, generation);
    }

    public WorkspaceSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new WorkspaceSnapshot(Text(_document["activeSpaceId"]),
                Spaces(_document).OfType<JsonObject>().Select(space => new WorkspaceSpace(
                    Text(space["id"])!, Text(space["name"])!, Items(space).OfType<JsonObject>().Select(ToItem).ToArray()
                )).ToArray());
        }
    }

    public string AddSpace(string name)
    {
        name = RequireText(name, "Space name", 128);
        var id = Guid.NewGuid().ToString("N");
        Edit(doc =>
        {
            Spaces(doc).Add(new JsonObject { ["id"] = id, ["name"] = name, ["items"] = new JsonArray() });
            doc["activeSpaceId"] = id;
        });
        return id;
    }

    public void RenameSpace(string id, string name) =>
        Edit(doc => Find(Spaces(doc), id, "space")["name"] = RequireText(name, "Space name", 128));

    public void DeleteSpace(string id) =>
        Edit(doc =>
        {
            var spaces = Spaces(doc);
            if (spaces.Count == 1) throw new InvalidOperationException("Keep at least one workspace.");
            spaces.Remove(Find(spaces, id, "space"));
            if (Text(doc["activeSpaceId"]) == id) doc["activeSpaceId"] = Text(spaces[0]?["id"]);
        });

    public void ActivateSpace(string id) =>
        Edit(doc =>
        {
            _ = Find(Spaces(doc), id, "space");
            doc["activeSpaceId"] = id;
        });

    public string AddItem(string spaceId, string kind, string title, JsonObject? data = null, Rectangle? bounds = null)
    {
        if (!Kinds.Contains(kind)) throw new ArgumentException("Unknown workspace item kind.", nameof(kind));
        var id = Guid.NewGuid().ToString("N");
        title = RequireText(title, "Item title", 128);
        Edit(doc =>
        {
            var items = Items(Find(Spaces(doc), spaceId, "space"));
            var n = items.Count;
            var geometry = bounds ?? new Rectangle(16 + (n % 2) * 408, 16 + (n / 2) * 294, 392, 278);
            var item = new JsonObject
            {
                ["id"] = id, ["kind"] = kind, ["title"] = title,
                ["data"] = data?.DeepClone() ?? new JsonObject()
            };
            Position(item, geometry);
            items.Add(item);
        });
        return id;
    }

    public string SaveItem(string spaceId, JsonObject update)
    {
        var id = Text(update["id"]) ?? Guid.NewGuid().ToString("N");
        Edit(doc =>
        {
            var items = Items(Find(Spaces(doc), spaceId, "space"));
            var item = items.OfType<JsonObject>().FirstOrDefault(x => Text(x["id"]) == id);
            var n = items.Count;
            if (item is null)
            {
                item = new JsonObject { ["id"] = id, ["data"] = new JsonObject() };
                Position(item, new Rectangle(16 + (n % 2) * 408, 16 + (n / 2) * 294, 392, 278));
                items.Add(item);
            }

            var kind = Text(update["kind"]) ?? Text(item["kind"]);
            if (kind is null || (!Kinds.Contains(kind) && kind != Text(item["kind"])))
                throw new ArgumentException("Unknown workspace item kind.");
            item["kind"] = kind;
            item["title"] = RequireText(Text(update["title"]) ?? Text(item["title"]) ?? kind, "Item title", 128);
            Position(item, new Rectangle(
                Number(update["x"], Number(item["x"], 16)),
                Number(update["y"], Number(item["y"], 16)),
                Number(update["width"], Number(item["width"], 392)),
                Number(update["height"], Number(item["height"], 278))));
            if (update["data"] is JsonObject changes)
            {
                var data = item["data"] as JsonObject ?? new JsonObject();
                foreach (var entry in changes) data[entry.Key] = entry.Value?.DeepClone();
                if (data.Parent is null) item["data"] = data;
            }
        });
        return id;
    }

    public void RenameItem(string spaceId, string itemId, string title) =>
        Edit(doc => Item(doc, spaceId, itemId)["title"] = RequireText(title, "Item title", 128));

    public void MoveItem(string spaceId, string itemId, Rectangle bounds) =>
        Edit(doc => Position(Item(doc, spaceId, itemId), bounds));

    public void SetData(string spaceId, string itemId, JsonObject data) =>
        Edit(doc => Item(doc, spaceId, itemId)["data"] = data.DeepClone());

    public void EditData(string spaceId, string itemId, Action<JsonObject> change) =>
        Edit(doc =>
        {
            var item = Item(doc, spaceId, itemId);
            if (item["data"] is not JsonObject) item["data"] = new JsonObject();
            change((JsonObject)item["data"]!);
        });

    public void RemoveItem(string spaceId, string itemId) =>
        Edit(doc =>
        {
            var items = Items(Find(Spaces(doc), spaceId, "space"));
            items.Remove(Find(items, itemId, "item"));
        });

    public void Arrange(string spaceId, int availableWidth) =>
        Edit(doc =>
        {
            var columns = Math.Max(1, (availableWidth - 16) / 408);
            var items = Items(Find(Spaces(doc), spaceId, "space"));
            for (var n = 0; n < items.Count; n++)
                Position((JsonObject)items[n]!, new Rectangle(16 + (n % columns) * 408, 16 + (n / columns) * 294, 392, 278));
        });

    public void RecoverFromRelay(JsonObject document, long expectedGeneration, bool backup = true, bool restoreLocalPaths = false)
    {
        var copy = (JsonObject)document.DeepClone();
        Validate(copy);
        lock (_gate)
        {
            if (_generation != expectedGeneration)
                throw new InvalidOperationException("The workspace changed during the download. Retry the pull.");
            if (restoreLocalPaths)
            {
                var originals = LocalPathFields(_document).Where(path => path.Data.ContainsKey(path.Key))
                    .ToDictionary(path => (path.SpaceId, path.ItemId, path.Key), path => path.Data[path.Key]);
                foreach (var (spaceId, itemId, key, data) in LocalPathFields(copy, createMissing: true))
                {
                    if (originals.TryGetValue((spaceId, itemId, key), out var original))
                        data[key] = original?.DeepClone();
                    else data.Remove(key);
                }
            }
            if (JsonNode.DeepEquals(copy, _document)) return;
            if (backup) WriteAtomic(Path.Combine(Path.GetDirectoryName(_path)!, "workspace-before-pull.json"), _document);
            Persist(copy);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateCommandTemplates(IReadOnlyDictionary<string, JsonObject> changed, IReadOnlySet<string> removed)
    {
        if (changed.Count == 0 && removed.Count == 0) return;
        Edit(doc =>
        {
            var records = doc["commands"] as JsonArray;
            if (records is null) doc["commands"] = records = new JsonArray();
            for (var i = records.Count - 1; i >= 0; i--)
                if (Text((records[i] as JsonObject)?["id"]) is { } id && removed.Contains(id)) records.RemoveAt(i);
            foreach (var (id, patch) in changed)
            {
                var existing = records.OfType<JsonObject>().FirstOrDefault(record => Text(record["id"]) == id);
                if (existing is null)
                    records.Add(patch.DeepClone());
                else
                    foreach (var entry in patch) existing[entry.Key] = entry.Value?.DeepClone();
            }
        });
    }

    public JsonArray ReadHosts()
    {
        lock (_gate) return (JsonArray)_document["sshHosts"]!.DeepClone();
    }

    public string SetHost(string? id, string name, string address, string username, int port, string? keyId)
    {
        name = RequireText(name, "Host name", 128);
        if (!SshArguments.ValidAddress(address) || !SshArguments.ValidUsername(username) || port is < 1 or > 65535)
            throw new ArgumentException("Enter a valid SSH address, username and port.");
        var hostId = id ?? Guid.NewGuid().ToString("N");
        Edit(doc =>
        {
            var hosts = (JsonArray)doc["sshHosts"]!;
            var host = id is null ? new JsonObject { ["id"] = hostId } : Find(hosts, hostId, "host");
            host["name"] = name;
            host["address"] = address;
            host["username"] = username;
            host["port"] = port;
            host["keyId"] = keyId;
            if (id is null) hosts.Add(host);
        });
        return hostId;
    }

    public void DeleteHost(string id) =>
        Edit(doc =>
        {
            var hosts = (JsonArray)doc["sshHosts"]!;
            hosts.Remove(Find(hosts, id, "host"));
        });

    public IReadOnlyList<SshKeyView> ListSshKeys()
    {
        lock (_gate)
        {
            if (_document["sshKeyEnvelopes"] is not JsonArray keys) return [];
            return keys.OfType<JsonObject>().Select(key => new SshKeyView(Text(key["id"])!, Text(key["name"])!)).ToArray();
        }
    }

    public void StoreSshKey(string id, string name, JsonObject envelope) =>
        Edit(doc =>
        {
            if (doc["sshKeyEnvelopes"] is not JsonArray) doc["sshKeyEnvelopes"] = new JsonArray();
            ((JsonArray)doc["sshKeyEnvelopes"]!).Add(new JsonObject { ["id"] = id, ["name"] = name, ["envelope"] = envelope.DeepClone() });
        });

    public JsonObject ReadSshKey(string id)
    {
        lock (_gate)
        {
            var keys = _document["sshKeyEnvelopes"] as JsonArray ?? throw new KeyNotFoundException("SSH key not found.");
            return (JsonObject)Find(keys, id, "SSH key")["envelope"]!.DeepClone();
        }
    }

    public void RemoveSshKey(string id) =>
        Edit(doc =>
        {
            var keys = doc["sshKeyEnvelopes"] as JsonArray ?? throw new KeyNotFoundException("SSH key not found.");
            keys.Remove(Find(keys, id, "SSH key"));
            foreach (var host in ((JsonArray)doc["sshHosts"]!).OfType<JsonObject>())
                if (Text(host["keyId"]) == id) host["keyId"] = null;
        });

    private void Edit(Action<JsonObject> change)
    {
        lock (_gate)
        {
            var draft = (JsonObject)_document.DeepClone();
            change(draft);
            Validate(draft);
            Persist(draft);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Persist(JsonObject draft)
    {
        WriteAtomic(_path, draft);
        _document = draft;
        _generation++;
    }

    internal static void WriteAtomic(string path, JsonNode document)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, document.ToJsonString(Format));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static int Number(JsonNode? node, int fallback) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

    private static string RequireText(string value, string label, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength
            ? value.Trim() : throw new ArgumentException($"{label} must have 1–{maxLength} characters.");

    private static JsonArray Spaces(JsonObject document) => (JsonArray)document["spaces"]!;
    private static JsonArray Items(JsonObject space) => (JsonArray)space["items"]!;
    private static JsonObject Item(JsonObject doc, string spaceId, string itemId) =>
        Find(Items(Find(Spaces(doc), spaceId, "space")), itemId, "item");

    private static JsonObject Find(JsonArray list, string id, string label) =>
        list.OfType<JsonObject>().FirstOrDefault(node => Text(node["id"]) == id)
        ?? throw new KeyNotFoundException($"Unknown {label}: {id}");

    private static void Position(JsonObject item, Rectangle bounds)
    {
        if (bounds.X is < 0 or > 24000 || bounds.Y is < 0 or > 24000 ||
            bounds.Width is < 220 or > 4000 || bounds.Height is < 150 or > 4000)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Item is outside the canvas limits.");
        item["x"] = bounds.X;
        item["y"] = bounds.Y;
        item["width"] = bounds.Width;
        item["height"] = bounds.Height;
    }

    private static WorkspaceItem ToItem(JsonObject item) => new(
        Text(item["id"])!, Text(item["kind"])!, Text(item["title"]) ?? Text(item["kind"])!,
        new Rectangle(Math.Clamp(Number(item["x"], 16), 0, 24000),
            Math.Clamp(Number(item["y"], 16), 0, 24000),
            Math.Clamp(Number(item["width"], 392), 220, 4000),
            Math.Clamp(Number(item["height"], 278), 150, 4000)),
        item["data"] is JsonObject data ? (JsonObject)data.DeepClone() : new JsonObject());

    private static JsonObject StartDocument()
    {
        const string id = "default";
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["activeSpaceId"] = id,
            ["spaces"] = new JsonArray(new JsonObject
            {
                ["id"] = id, ["name"] = Localizer.T("Workspace", "工作空间"), ["items"] = new JsonArray()
            }),
            ["sshHosts"] = new JsonArray()
        };
    }

    private static void Validate(JsonObject doc)
    {
        if (Number(doc["schemaVersion"], 1) != 1 ||
            doc["spaces"] is not JsonArray { Count: > 0 and <= 64 } spaces)
            throw new InvalidDataException("Workspace needs 1–64 spaces and schema version 1.");
        if (doc["sshHosts"] is null) doc["sshHosts"] = new JsonArray();
        if (doc["sshHosts"] is not JsonArray { Count: <= 256 } hosts)
            throw new InvalidDataException("Invalid SSH hosts.");
        CheckIds(hosts);
        if (doc["sshKeyEnvelopes"] is JsonArray { Count: <= 128 } keys)
        {
            CheckIds(keys);
            foreach (var key in keys.OfType<JsonObject>())
                if (Text(key["name"]) is not { Length: > 0 and <= 128 } || key["envelope"] is not JsonObject)
                    throw new InvalidDataException("Invalid encrypted SSH key.");
        }
        else if (doc["sshKeyEnvelopes"] is not null)
            throw new InvalidDataException("Invalid encrypted SSH key list.");
        CheckIds(spaces);
        foreach (var space in spaces.OfType<JsonObject>())
        {
            _ = RequireText(Text(space["name"]) ?? "", "Space name", 128);
            if (space["items"] is null) space["items"] = new JsonArray();
            if (space["items"] is not JsonArray { Count: <= 256 } items)
                throw new InvalidDataException("A space supports up to 256 items.");
            CheckIds(items);
            foreach (var item in items.OfType<JsonObject>())
                _ = RequireText(Text(item["kind"]) ?? "", "Item kind", 64);
        }
        if (Text(doc["activeSpaceId"]) is not string active || !spaces.Any(space => Text(space?["id"]) == active))
            doc["activeSpaceId"] = Text(spaces[0]?["id"]);
        doc["schemaVersion"] = 1;
    }

    private static IEnumerable<(string SpaceId, string ItemId, string Key, JsonObject Data)> LocalPathFields(
        JsonObject document, bool createMissing = false)
    {
        foreach (var space in Spaces(document).OfType<JsonObject>())
        foreach (var item in Items(space).OfType<JsonObject>())
        {
            var kind = Text(item["kind"]);
            if (kind is not ("files" or "preview" or "terminal")) continue;
            if (item["data"] is not JsonObject data)
            {
                if (!createMissing) continue;
                item["data"] = data = new JsonObject();
            }
            var spaceId = Text(space["id"])!;
            var itemId = Text(item["id"])!;
            if (kind == "terminal")
            {
                yield return (spaceId, itemId, "workingDirectory", data);
                yield return (spaceId, itemId, "directory", data);
            }
            else yield return (spaceId, itemId, "path", data);
        }
    }

    private static void CheckIds(JsonArray list)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in list)
            if (node is not JsonObject item || Text(item["id"]) is not { Length: > 0 and <= 128 } id || !ids.Add(id))
                throw new InvalidDataException("Every workspace entity must have a unique ID.");
    }
}

internal static class SshArguments
{
    public static bool ValidAddress(string? address) => address is { Length: > 0 and <= 253 }
        && !address.StartsWith('-') && address.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '-' or '[' or ']' or '_');

    public static bool ValidUsername(string? username) => username is { Length: > 0 and <= 128 }
        && username.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    public static IReadOnlyList<string> ForHost(JsonObject host, string? identityFile = null)
    {
        var address = WorkspaceStore.Text(host["address"]);
        var username = WorkspaceStore.Text(host["username"]);
        var port = WorkspaceStore.Number(host["port"], 22);
        if (!ValidAddress(address) || !ValidUsername(username) || port is < 1 or > 65535)
            throw new InvalidDataException("Invalid SSH connection details.");
        var arguments = new List<string> { "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (identityFile is not null) arguments.AddRange(["-i", identityFile]);
        arguments.Add("--");
        arguments.Add($"{username}@{address}");
        return arguments;
    }
}
