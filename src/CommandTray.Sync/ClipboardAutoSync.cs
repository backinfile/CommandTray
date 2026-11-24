namespace CommandTray.Sync;

public sealed record ClipboardSyncConnection(string DeviceId, RelayClient Relay, string Direction = "both");

public sealed class ClipboardAutoSync(
    Func<ClipboardSyncConnection?> connection,
    Func<CancellationToken, Task<string?>> readLocal,
    Func<string, string, CancellationToken, Task<bool>> replaceLocal)
{
    private readonly SemaphoreSlim _poll = new(1);
    private string? _deviceId;
    private string? _direction;
    private string? _local;
    private long? _remoteRevision;

    public async Task RunAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            do
            {
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                request.CancelAfter(TimeSpan.FromSeconds(10));
                try { await SyncOnceAsync(request.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or HttpRequestException
                    or IOException or InvalidOperationException or System.Runtime.InteropServices.ExternalException) { }
            }
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public async Task SyncOnceAsync(CancellationToken cancellation)
    {
        await _poll.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var current = connection();
            if (current?.DeviceId != _deviceId || current?.Direction != _direction)
            {
                _deviceId = current?.DeviceId;
                _direction = current?.Direction;
                _local = null;
                _remoteRevision = null;
            }
            if (current is null || await readLocal(cancellation).ConfigureAwait(false) is not { } local) return;

            // The clipboard present when sync starts is only a baseline. Send subsequent changes.
            _local ??= local;
            var latest = await current.Relay.GetClipboardAsync(cancellation).ConfigureAwait(false);
            var revision = latest["revision"]!.GetValue<long>();
            var remote = latest["text"]!.GetValue<string>();
            bool IsCurrent() => connection() is { } active &&
                active.DeviceId == current.DeviceId && active.Direction == current.Direction;
            if (!IsCurrent()) return;
            if (local != _local)
            {
                if (await readLocal(cancellation).ConfigureAwait(false) != local ||
                    !IsCurrent()) return;
                if (current.Direction == "receive")
                {
                    _local = local;
                    return;
                }
                if (local.Length is > 0 and <= 1_000_000 && local != remote)
                {
                    var sent = await current.Relay.PutClipboardAsync(local, cancellation).ConfigureAwait(false);
                    revision = sent["revision"]!.GetValue<long>();
                }
                _local = local;
            }
            else if (current.Direction != "send" && revision != _remoteRevision && remote.Length > 0 && remote != local &&
                (string?)latest["deviceId"] != current.DeviceId)
            {
                if (!await replaceLocal(local, remote, cancellation).ConfigureAwait(false)) return;
                _local = remote;
            }
            _remoteRevision = revision;
        }
        finally { _poll.Release(); }
    }
}
