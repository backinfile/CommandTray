using System.Text.Json.Nodes;
using CommandTray.Core;

namespace CommandTray.Android;

internal static class AndroidMcpTools
{
    private const int FileLimit = 4 * 1024 * 1024;

    public static IEnumerable<ClientMcpTool> Create(AndroidBackend backend)
    {
        // The Android host cannot use a filesystem path chosen by an external MCP client.
        yield return new ClientMcpTool("file_upload", "Upload shared file content as Base64 up to 4 MiB.",
            Schema(["name", "base64"], "name", "folder", "base64"), async (args, cancellation) =>
            {
                var encoded = AndroidBackend.Text(args, "base64");
                if (encoded.Length > ((FileLimit + 2) / 3) * 4)
                    throw new ArgumentException("MCP file transfer is limited to 4 MiB.");
                var content = Convert.FromBase64String(encoded);
                if (content.Length > FileLimit)
                    throw new ArgumentException("MCP file transfer is limited to 4 MiB.");
                var temporary = TemporaryPath(backend);
                try
                {
                    await File.WriteAllBytesAsync(temporary, content, cancellation);
                    return await backend.ExecuteAsync("file_upload", new JsonObject
                    {
                        ["path"] = temporary, ["name"] = AndroidBackend.Text(args, "name"), ["folder"] = AndroidBackend.Text(args, "folder")
                    }, cancellation);
                }
                finally { File.Delete(temporary); }
            });
        yield return new ClientMcpTool("file_download", "Download shared file content as Base64 up to 4 MiB.",
            Schema(["id"], "id"), async (args, cancellation) =>
            {
                var id = AndroidBackend.Text(args, "id");
                var files = await backend.ExecuteAsync("files_list", new JsonObject(), cancellation) as JsonArray;
                var file = files?.OfType<JsonObject>().FirstOrDefault(record => AndroidBackend.Text(record, "id") == id)
                    ?? throw new KeyNotFoundException("The shared file was not found.");
                if (file["size"]?.GetValue<long>() > FileLimit)
                    throw new ArgumentException("MCP file transfer is limited to 4 MiB.");

                var temporary = TemporaryPath(backend);
                try
                {
                    await backend.ExecuteAsync("file_download", new JsonObject { ["id"] = id, ["path"] = temporary }, cancellation);
                    if (new FileInfo(temporary).Length > FileLimit)
                        throw new ArgumentException("MCP file transfer is limited to 4 MiB.");
                    var content = await File.ReadAllBytesAsync(temporary, cancellation);
                    return new JsonObject { ["id"] = id, ["size"] = content.Length, ["base64"] = Convert.ToBase64String(content) };
                }
                finally { File.Delete(temporary); }
            });
    }

    private static string TemporaryPath(AndroidBackend backend) =>
        Path.Combine(backend.CacheDirectory, "mcp-" + Guid.NewGuid().ToString("N"));

    private static JsonObject Schema(string[] required, params string[] text)
    {
        var properties = new JsonObject();
        foreach (var field in text)
            properties.Add(field, new JsonObject { ["type"] = "string" });
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(static field => (JsonNode?)JsonValue.Create(field)).ToArray()),
            ["additionalProperties"] = false
        };
    }
}