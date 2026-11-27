namespace CommandTray.Core;

public readonly record struct CaptureRegion(int X, int Y, int Width, int Height);

public readonly record struct CapturePreviewTarget(bool Recording, string Mode, string? WindowId = null,
    CaptureRegion? Region = null);

public sealed record CapturePreviewFrame(byte[] Image, int Width, int Height);

public interface ICapturePreviewBackend
{
    ValueTask<CapturePreviewFrame> ReadCapturePreviewAsync(CapturePreviewTarget target, CaptureRegion? exclusion,
        CancellationToken token);
}
