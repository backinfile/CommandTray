using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandTray.Sync;

public sealed class ClipboardPreferences(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(directory, "clipboard-preferences.json");
    private readonly object _gate = new();
    private Preferences? _cached;

    private sealed record Preferences(string Direction, string[] Pinned, string[] Hidden);

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
                    next = next with { Pinned = Set(next.Pinned, id, pin) };
                if (args["hide"]?.GetValue<bool>() == true)
                    next = next with { Pinned = Set(next.Pinned, id, false), Hidden = Set(next.Hidden, id, true) };
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
            return JsonSerializer.SerializeToNode(next, Json)!.AsObject();
        }
    }

    private Preferences Read() => _cached ??= File.Exists(_path)
        ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path), Json) ?? new("both", [], [])
        : new("both", [], []);

    private static string[] Set(string[] values, string id, bool add)
    {
        var remaining = values.Where(value => value != id);
        return (add ? remaining.Prepend(id) : remaining).Take(256).ToArray();
    }
}
