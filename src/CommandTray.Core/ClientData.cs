using System.Text.Json.Nodes;

namespace CommandTray.Core;

public static class ClientData
{
    public static string Text(this JsonNode? node, string key, string fallback = "") =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text : fallback;

    public static int Integer(this JsonNode? node, string key, int fallback = 0) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<int>(out var number)
            ? number : fallback;

    public static bool Flag(this JsonNode? node, string key, bool fallback = false) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag : fallback;

    public static JsonArray Array(this JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonArray list ? list : [];

    public static JsonObject Object(this JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonObject item ? item : [];

    public static string RequireText(this JsonObject arguments, string key) =>
        arguments.Text(key) is { Length: > 0 } value ? value
            : throw new ArgumentException($"Missing {key}.", nameof(arguments));

    public static JsonObject RequireObject(this JsonObject arguments, string key) =>
        arguments[key] as JsonObject ?? throw new ArgumentException($"Missing {key}.", nameof(arguments));
}
