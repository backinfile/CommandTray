using System.Buffers;
using CommandTray.Core;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandTray.Android;

// Android builds do not carry the ASP.NET Core host used by the desktop transport.
internal sealed class AndroidMcpHost : IDisposable
{
    private const string ProtocolVersion = "2025-11-25";
    private const int MaxBody = 6 * 1024 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, AndroidSettings.McpPort);
    private readonly SemaphoreSlim _clients = new(8);
    private readonly SharedMcpDispatcher _dispatcher;
    private readonly byte[] _authorization;
    private int _closed;

    public AndroidMcpHost(AndroidBackend backend, string token)
    {
        _dispatcher = new SharedMcpDispatcher(backend, AndroidMcpTools.Create(backend));
        _authorization = Encoding.UTF8.GetBytes("Bearer " + token);
        try { _listener.Start(16); }
        catch
        {
            _listener.Stop();
            throw;
        }
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (Volatile.Read(ref _closed) == 0)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                if (_clients.Wait(0))
                    _ = HandleClientAsync(client);
                else
                    client.Dispose();
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                if (Volatile.Read(ref _closed) == 0)
                    global::Android.Util.Log.Warn("CommandTray", "MCP listener stopped: " + error.GetType().Name);
                return;
            }
            catch (InvalidOperationException) when (Volatile.Read(ref _closed) != 0) { return; }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                var stream = client.GetStream();
                using var input = new BufferedStream(stream, 4096);
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var line = (await ReadLineAsync(input, readDeadline.Token)).Split(' ');
                    if (line.Length != 3 || line[2] != "HTTP/1.1")
                        throw new InvalidDataException();
                    var headers = await ReadHeadersAsync(input, readDeadline.Token);
                    if (!ValidHost(headers.GetValueOrDefault("Host")) || !ValidOrigin(headers.GetValueOrDefault("Origin")))
                    {
                        await RespondAsync(stream, "403 Forbidden", cancellation: deadline.Token);
                        return;
                    }
                    if (line[1] != "/mcp")
                    {
                        await RespondAsync(stream, "404 Not Found", cancellation: deadline.Token);
                        return;
                    }

                    var supplied = Encoding.UTF8.GetBytes(headers.GetValueOrDefault("Authorization") ?? "");
                    if (!CryptographicOperations.FixedTimeEquals(_authorization, supplied))
                    {
                        await RespondAsync(stream, "401 Unauthorized", extra: "WWW-Authenticate: Bearer\r\n", cancellation: deadline.Token);
                        return;
                    }
                    if (line[0] != "POST")
                    {
                        await RespondAsync(stream, "405 Method Not Allowed", extra: "Allow: POST\r\n", cancellation: deadline.Token);
                        return;
                    }
                    if (headers.TryGetValue("MCP-Protocol-Version", out var version) && !Supported(version))
                    {
                        await RespondAsync(stream, "400 Bad Request", Error(null, -32600, "Unsupported protocol version"), cancellation: deadline.Token);
                        return;
                    }
                    if (headers.ContainsKey("Transfer-Encoding") ||
                        !int.TryParse(headers.GetValueOrDefault("Content-Length"), out var length) || length < 0)
                        throw new InvalidDataException();
                    if (length > MaxBody)
                    {
                        await RespondAsync(stream, "413 Content Too Large", cancellation: deadline.Token);
                        return;
                    }
                    if (!string.Equals(headers.GetValueOrDefault("Content-Type")?.Split(';', 2)[0].Trim(),
                        "application/json", StringComparison.OrdinalIgnoreCase))
                    {
                        await RespondAsync(stream, "415 Unsupported Media Type", cancellation: deadline.Token);
                        return;
                    }

                    var body = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        await input.ReadExactlyAsync(body.AsMemory(0, length), readDeadline.Token);
                        JsonObject? message;
                        try { message = JsonNode.Parse(body.AsSpan(0, length)) as JsonObject; }
                        catch (JsonException)
                        {
                            await RespondAsync(stream, "400 Bad Request", Error(null, -32700, "Invalid JSON object"), cancellation: deadline.Token);
                            return;
                        }
                        await DispatchAsync(stream, message, deadline.Token);
                    }
                    finally { ArrayPool<byte>.Shared.Return(body, clearArray: true); }
                }
                catch (InvalidDataException) { await RespondAsync(stream, "400 Bad Request", cancellation: deadline.Token); }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // A disconnected client does not require a response.
        }
        finally { _clients.Release(); }
    }

    private async Task DispatchAsync(Stream stream, JsonObject? request, CancellationToken cancellation)
    {
        if (request is null)
        {
            await RespondAsync(stream, "400 Bad Request", Error(null, -32600, "Invalid request"), cancellation: cancellation);
            return;
        }
        var result = await _dispatcher.DispatchAsync(request, cancellation);
        await RespondAsync(stream, result is null ? "202 Accepted" : "200 OK", result, cancellation: cancellation);
    }

    private static bool Supported(string version) => version is ProtocolVersion or "2025-06-18" or "2025-03-26";

    private static bool ValidHost(string? host) =>
        string.Equals(host, $"127.0.0.1:{AndroidSettings.McpPort}", StringComparison.Ordinal) ||
        string.Equals(host, $"localhost:{AndroidSettings.McpPort}", StringComparison.OrdinalIgnoreCase);

    private static bool ValidOrigin(string? origin) =>
        origin is null || string.Equals(origin, $"http://127.0.0.1:{AndroidSettings.McpPort}", StringComparison.Ordinal) ||
        string.Equals(origin, $"http://localhost:{AndroidSettings.McpPort}", StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, string>> ReadHeadersAsync(Stream stream, CancellationToken cancellation)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var read = 0;
        while (true)
        {
            var line = await ReadLineAsync(stream, cancellation);
            if (line.Length == 0)
                return headers;
            read += line.Length + 2;
            var colon = line.IndexOf(':');
            if (read > 16 * 1024 || colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                throw new InvalidDataException();
        }
    }

    private static async ValueTask<string> ReadLineAsync(Stream stream, CancellationToken cancellation)
    {
        var line = new byte[4096];
        var next = new byte[1];
        var length = 0;
        while (await stream.ReadAsync(next.AsMemory(), cancellation) == 1)
        {
            var value = next[0];
            if (value == '\r' && await stream.ReadAsync(next.AsMemory(), cancellation) == 1 && next[0] == '\n')
                return Encoding.ASCII.GetString(line, 0, length);
            if (length == line.Length || (value < 32 && value != '\t') || value > 126)
                break;
            line[length++] = value;
        }
        throw new InvalidDataException();
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private static async Task RespondAsync(Stream stream, string status, JsonObject? payload = null, string extra = "",
        CancellationToken cancellation = default)
    {
        var body = payload is null ? [] : Encoding.UTF8.GetBytes(payload.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nCache-Control: no-store\r\n" +
            $"Content-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\n{extra}\r\n");
        await stream.WriteAsync(header, cancellation);
        if (body.Length > 0)
            await stream.WriteAsync(body, cancellation);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _listener.Stop();
    }
}