using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CommandTray.Sync;

public sealed record PairingResult(string DeviceId, string Token);

public class RelayException(HttpStatusCode status, string message) : HttpRequestException(message, null, status);

public sealed class RelayConflictException(JsonObject current)
    : RelayException(HttpStatusCode.Conflict, "The server workspace changed. Keep the local work and resolve the conflict before pushing.")
{
    public JsonObject Current { get; } = current;
}

public sealed class RelayClient
{
    private static readonly HttpClient DefaultHttp = new();
    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly string? _token;

    public RelayClient(Uri server, string? token = null, HttpClient? http = null)
    {
        if (!server.IsAbsoluteUri || server.Scheme is not ("http" or "https") ||
            server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0)
            throw new ArgumentException("Use an absolute HTTP or HTTPS relay address without credentials, query, or fragment.", nameof(server));

        _server = new Uri(server.AbsoluteUri.TrimEnd('/') + "/");
        _token = token;
        _http = http ?? DefaultHttp;
    }

    public async Task<PairingResult> PairAsync(string name, string pairingCode, CancellationToken cancellationToken = default)
    {
        using var request = Create(HttpMethod.Post, "v1/devices/pair");
        request.Content = JsonContent.Create(new { deviceName = name, pairingCode });
        using var response = await _http.SendAsync(request, cancellationToken);
        await CheckAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PairingResult>(cancellationToken)
               ?? throw new InvalidDataException("Relay returned an empty pairing response.");
    }

    public Task<JsonArray> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        GetListAsync("v1/devices", "devices", cancellationToken);

    public async Task DeleteDeviceAsync(string id, CancellationToken cancellationToken = default)
    {
        using var request = Create(HttpMethod.Delete, "v1/devices/" + Uri.EscapeDataString(id));
        using var response = await _http.SendAsync(request, cancellationToken);
        await CheckAsync(response, cancellationToken);
    }

    public Task<JsonObject> GetWorkspaceAsync(CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Get, "v1/workspace", null, cancellationToken);

    public Task<JsonObject> PutWorkspaceAsync(long baseRevision, JsonNode document, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Put, "v1/workspace", new { baseRevision, document }, cancellationToken);

    public Task<JsonObject> GetWorkspaceChangesAsync(long after, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Get, "v1/workspace/changes?after=" + after.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null, cancellationToken);

    public Task<JsonObject> ApplyWorkspaceChangesAsync(long baseRevision, string operationId,
        WorkspaceChange[] changes, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Patch, "v1/workspace/changes", new { baseRevision, operationId, changes }, cancellationToken);

    public Task<JsonObject> GetClipboardAsync(CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Get, "v1/clipboard", null, cancellationToken);

    public Task<JsonObject> PutClipboardAsync(string text, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Put, "v1/clipboard", new { text }, cancellationToken);

    public Task<JsonArray> ListFilesAsync(CancellationToken cancellationToken = default) =>
        GetListAsync("v1/files", "files", cancellationToken);

    public async Task<JsonObject> UploadAsync(string name, Stream content, string? folder = null, CancellationToken cancellationToken = default)
    {
        var path = "v1/files?name=" + Uri.EscapeDataString(name);
        if (!string.IsNullOrWhiteSpace(folder))
            path += "&folder=" + Uri.EscapeDataString(folder);

        using var request = Create(HttpMethod.Post, path);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.SendAsync(request, cancellationToken);
        await CheckAsync(response, cancellationToken);
        return await ReadObjectAsync(response, cancellationToken);
    }

    public async Task DownloadAsync(string id, Stream destination, CancellationToken cancellationToken = default)
    {
        using var request = Create(HttpMethod.Get, "v1/files/" + Uri.EscapeDataString(id));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await CheckAsync(response, cancellationToken);
        await response.Content.CopyToAsync(destination, cancellationToken);
    }

    public async Task DeleteFileAsync(string id, CancellationToken cancellationToken = default)
    {
        using var request = Create(HttpMethod.Delete, "v1/files/" + Uri.EscapeDataString(id));
        using var response = await _http.SendAsync(request, cancellationToken);
        await CheckAsync(response, cancellationToken);
    }

    private async Task<JsonArray> GetListAsync(string path, string field, CancellationToken cancellationToken)
    {
        var response = await SendJsonAsync(HttpMethod.Get, path, null, cancellationToken);
        return response[field] as JsonArray ?? throw new InvalidDataException($"Relay response omitted its {field} array.");
    }

    private async Task<JsonObject> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = Create(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict && path is "v1/workspace" or "v1/workspace/changes")
            throw new RelayConflictException(await ReadObjectAsync(response, cancellationToken));
        await CheckAsync(response, cancellationToken);
        return await ReadObjectAsync(response, cancellationToken);
    }

    private HttpRequestMessage Create(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, new Uri(_server, relativePath));
        if (!string.IsNullOrWhiteSpace(_token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken)
        ?? throw new InvalidDataException("Relay returned an empty JSON object.");

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = $"Relay request failed ({(int)response.StatusCode}).";
        if (response.Content.Headers.ContentType?.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var payload = await ReadObjectAsync(response, cancellationToken);
                if (payload["error"] is JsonValue error && error.TryGetValue<string>(out var detail))
                    message = detail;
            }
            catch (System.Text.Json.JsonException) { }
            catch (InvalidDataException) { }
        }
        throw new RelayException(response.StatusCode, message);
    }
}
