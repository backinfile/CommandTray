using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandTray.Sync;

public sealed class ClipboardPreferences(string directory, Func<string, string>? protect = null, Func<string, string>? unprotect = null)
{
    private const int RecordLimit = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(directory, "clipboard-preferences.json");
    private readonly object _gate = new();
    private Preferences? _cached;

    private sealed record Preferences(string Direction, string[] Pinned, string[] Hidden, Dictionary<string, string>? Archived = null);

    public string Direction
    {
        get { lock (_gate) return Read().Direction; }
    }

    public JsonObject Execute(JsonObject args)
    {
        lock (_gate)
        {
            var current = Read();
            var next = current;
            if (args["direction"]?.GetValue<string>() is { } direction)
            {
                if (direction is not ("both" or "send" or "receive")) throw new ArgumentException("Invalid clipboard direction.");
                next = next with { Direction = direction };
            }
            if (args["id"]?.GetValue<string>() is { } id)
            {
                if (id.Length != 64 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid clipboard entry.");
                if (args["pin"]?.GetValue<bool>() is { } pin)
                {
                    if (pin && next.Pinned.Length >= RecordLimit && !next.Pinned.Contains(id))
                        throw new InvalidOperationException("Unpin an entry before pinning another.");
                    next = next with { Pinned = Set(next.Pinned, id, pin) };
                }
                if (args["hide"]?.GetValue<bool>() == true)
                    next = next with { Pinned = Set(next.Pinned, id, false), Hidden = Set(next.Hidden, id, true) };
            }
            if (next.Pinned != current.Pinned || args["pinnedEntries"] is JsonObject)
            {
                var archived = next.Archived is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(next.Archived, StringComparer.Ordinal);
                var pins = next.Pinned.ToHashSet(StringComparer.Ordinal);
                foreach (var key in archived.Keys.Where(key => !pins.Contains(key)).ToArray())
                    archived.Remove(key);
                if (args["pinnedEntries"] is JsonObject entries)
                    foreach (var (key, entry) in entries)
                    {
                        if (!pins.Contains(key) || archived.ContainsKey(key) || entry is not JsonObject) continue;
                        var json = entry.ToJsonString();
                        archived.Add(key, protect?.Invoke(json) ?? json);
                    }
                next = next with { Archived = archived };
            }
            if (next != current)
            {
                Directory.CreateDirectory(directory);
                var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
                    File.Move(temp, _path, overwrite: true);
                    _cached = next;
                }
                finally { File.Delete(temp); }
            }
            var pinnedEntries = new JsonObject();
            if (next.Archived is not null && args["includeEntries"]?.GetValue<bool>() == true)
                foreach (var (key, record) in next.Archived)
                    pinnedEntries[key] = JsonNode.Parse(unprotect?.Invoke(record) ?? record);
            return new JsonObject
            {
                ["direction"] = next.Direction,
                ["pinned"] = JsonSerializer.SerializeToNode(next.Pinned),
                ["hidden"] = JsonSerializer.SerializeToNode(next.Hidden),
                ["pinnedEntries"] = pinnedEntries
            };
        }
    }

    private Preferences Read() => _cached ??= File.Exists(_path)
        ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path), Json) ?? new("both", [], [])
        : new("both", [], []);

    private static string[] Set(string[] values, string id, bool add)
    {
        var remaining = values.Where(value => value != id);
        return (add ? remaining.Prepend(id) : remaining).Take(RecordLimit).ToArray();
    }
}
