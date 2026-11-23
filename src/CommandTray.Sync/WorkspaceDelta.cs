using System.Text.Json.Nodes;

namespace CommandTray.Sync;

public sealed record WorkspaceChange(
    string[] Path, bool HadBefore, JsonNode? Before, bool HasAfter, JsonNode? After, bool Order = false);

public sealed record WorkspaceRevision(long Revision, WorkspaceChange[] Changes);

public static class WorkspaceDelta
{
    public static WorkspaceChange[] Between(JsonObject previous, JsonObject desired)
    {
        var changes = new List<WorkspaceChange>();
        Compare([], true, previous, true, desired, changes);
        return [.. changes];
    }

    public static bool TryApply(JsonObject document, IReadOnlyList<WorkspaceChange> changes, out JsonObject merged)
    {
        merged = (JsonObject)document.DeepClone();
        foreach (var change in changes)
        {
            if (change is null || change.Path is not { Length: > 0 and <= 32 } ||
                change.Path.Any(segment => segment is null || segment.Length > 512) ||
                !Apply(merged, change))
                return false;
        }
        return true;
    }

    private static void Compare(string[] path, bool had, JsonNode? before, bool has, JsonNode? after,
        List<WorkspaceChange> changes)
    {
        if (had == has && JsonNode.DeepEquals(before, after)) return;
        if (before is JsonObject oldObject && after is JsonObject newObject)
        {
            foreach (var key in oldObject.Select(property => property.Key).Union(newObject.Select(property => property.Key)))
            {
                bool hadKey = oldObject.TryGetPropertyValue(key, out var oldValue);
                bool hasKey = newObject.TryGetPropertyValue(key, out var newValue);
                Compare([.. path, key], hadKey, oldValue, hasKey, newValue, changes);
            }
            return;
        }
        if (before is JsonArray oldArray && after is JsonArray newArray &&
            Entries(oldArray) is { } oldEntries && Entries(newArray) is { } newEntries)
        {
            foreach (var id in oldEntries.Keys.Union(newEntries.Keys))
            {
                bool hadId = oldEntries.TryGetValue(id, out var oldValue);
                bool hasId = newEntries.TryGetValue(id, out var newValue);
                Compare([.. path, id], hadId, oldValue, hasId, newValue, changes);
            }
            string[] intermediate = [.. oldEntries.Keys.Where(newEntries.ContainsKey), .. newEntries.Keys.Where(id => !oldEntries.ContainsKey(id))];
            if (!intermediate.SequenceEqual(newEntries.Keys))
                changes.Add(new(path, true, Ids(intermediate), true, Ids(newEntries.Keys), Order: true));
            return;
        }
        changes.Add(new(path, had, before?.DeepClone(), has, after?.DeepClone()));
    }

    private static bool Apply(JsonObject document, WorkspaceChange change)
    {
        JsonNode? container = document;
        int limit = change.Order ? change.Path.Length : change.Path.Length - 1;
        for (int i = 0; i < limit; i++)
        {
            if (!Lookup(container, change.Path[i], out container)) return false;
        }
        if (change.Order)
            return container is JsonArray list && Reorder(list, change);
        string key = change.Path[^1];
        bool found = Lookup(container, key, out var existing);
        if (found == change.HasAfter && JsonNode.DeepEquals(existing, change.After)) return true;
        if (found != change.HadBefore || !JsonNode.DeepEquals(existing, change.Before)) return false;
        if (container is JsonObject obj)
        {
            if (change.HasAfter) obj[key] = change.After?.DeepClone();
            else obj.Remove(key);
            return true;
        }
        if (container is not JsonArray array) return false;
        if (change.HasAfter && GetId(change.After) != key) return false;
        int index = existing is null ? -1 : array.IndexOf(existing);
        if (index >= 0)
        {
            if (change.HasAfter) array[index] = change.After?.DeepClone();
            else array.RemoveAt(index);
        }
        else if (change.HasAfter) array.Add(change.After?.DeepClone());
        return true;
    }

    private static bool Reorder(JsonArray array, WorkspaceChange change)
    {
        if (Entries(array) is not { } entries || ReadIds(change.Before) is not { } before ||
            ReadIds(change.After) is not { } after || !before.ToHashSet().SetEquals(after)) return false;
        var orderedIds = after.Where(entries.ContainsKey).ToArray();
        var targets = orderedIds.ToHashSet();
        var current = entries.Keys.Where(targets.Contains).ToArray();
        if (current.SequenceEqual(orderedIds)) return true;
        if (!current.SequenceEqual(before.Where(entries.ContainsKey))) return false;
        var reordered = new Queue<JsonNode>(orderedIds.Select(id => entries[id].DeepClone()));
        var replacement = entries.Select(entry => targets.Contains(entry.Key) ? reordered.Dequeue() : entry.Value.DeepClone()).ToArray();
        array.Clear();
        foreach (var item in replacement) array.Add(item);
        return true;
    }

    private static bool Lookup(JsonNode? node, string key, out JsonNode? value)
    {
        if (node is JsonObject obj) return obj.TryGetPropertyValue(key, out value);
        value = node is JsonArray array ? array.FirstOrDefault(item => GetId(item) == key) : null;
        return value is not null;
    }

    private static Dictionary<string, JsonObject>? Entries(JsonArray array)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonObject entry || GetId(item) is not { Length: > 0 } id || !result.TryAdd(id, entry))
                return null;
        }
        return result;
    }

    private static string? GetId(JsonNode? value) => value is JsonObject entry && entry["id"] is JsonValue id &&
        id.TryGetValue<string>(out var text) ? text : null;

    private static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());

    private static string[]? ReadIds(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id) ||
                string.IsNullOrEmpty(id) || !seen.Add(id)) return null;
            result.Add(id);
        }
        return [.. result];
    }
}
