using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CommandTray.Linux;

internal static class DaemonTransport
{
    private const int MaxFrameLength = 4 * 1024 * 1024;
    private sealed record Reply(JsonElement? Result = null, string? Error = null);

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        LocalState.EnsureDirectory(LocalState.Runtime);
        using var ownership = LocalState.Lock(Path.Combine(LocalState.Runtime, "daemon.lock"));
        if (Encoding.UTF8.GetByteCount(LocalState.Socket) > 100)
            throw new InvalidOperationException("CommandTray socket path is too long. Use a shorter COMMANDTRAY_HOME.");

        File.Delete(LocalState.Socket);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(LocalState.Socket));
        File.SetUnixFileMode(LocalState.Socket, LocalState.PrivateFile);
        listener.Listen(16);
        var sessions = new List<Task>();
        await using var runtime = new ServiceRuntime();
        try
        {
            await runtime.StartConfiguredAsync();
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(cancellationToken);
                sessions.RemoveAll(task => task.IsCompleted);
                sessions.Add(RespondAsync(client, runtime, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            await Task.WhenAll(sessions);
            File.Delete(LocalState.Socket);
        }
    }

    public static async Task<JsonElement?> ExecuteAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await client.ConnectAsync(new UnixDomainSocketEndPoint(LocalState.Socket), cancellationToken); }
        catch (SocketException ex)
        {
            throw new InvalidOperationException("CommandTray daemon is unavailable. Start commandtray daemon or the commandtray user service.", ex);
        }
        await using var stream = new NetworkStream(client, ownsSocket: false);
        await WriteAsync(stream, arguments, cancellationToken);
        var reply = await ReadAsync<Reply>(stream, cancellationToken);
        if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
        return reply.Result;
    }

    private static async Task RespondAsync(Socket client, ServiceRuntime runtime, CancellationToken stopping)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            using var receive = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            receive.CancelAfter(TimeSpan.FromSeconds(10));
            Reply reply;
            try
            {
                var arguments = await ReadAsync<string[]>(stream, receive.Token);
                if (arguments.Any(argument => argument is null)) throw new ArgumentException("Command arguments must be strings.");
                var result = await runtime.ExecuteAsync(arguments, stopping);
                reply = new Reply(JsonSerializer.SerializeToElement(result, LocalState.Json));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException
                or IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
            {
                reply = new Reply(Error: ex.Message);
            }

            try
            {
                using var send = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                send.CancelAfter(TimeSpan.FromSeconds(10));
                await WriteAsync(stream, reply, send.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaxFrameLength) throw new IOException("Invalid CommandTray message size.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, LocalState.Json) ??
            throw new IOException("Empty CommandTray message.");
    }

    private static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, LocalState.Json);
        if (bytes.Length > MaxFrameLength) throw new IOException("CommandTray message is too large.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
