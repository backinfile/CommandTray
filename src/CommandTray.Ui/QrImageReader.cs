using System.Buffers;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace CommandTray.Ui;

internal static class QrImageReader
{
    internal const int MaxImageBytes = 32 * 1024 * 1024;
    private const long MaxPixels = 24_000_000;
    private const int ScanEdge = 2_560;

    public static async Task<string?> ReadAsync(Stream source, CancellationToken token)
    {
        if (source.CanSeek && source.Length - source.Position > MaxImageBytes)
            throw new InvalidDataException("image-limit");
        using var encoded = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (int count; (count = await source.ReadAsync(buffer.AsMemory(), token)) > 0;)
            {
                if (encoded.Length + count > MaxImageBytes) throw new InvalidDataException("image-limit");
                encoded.Write(buffer, 0, count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

        encoded.Position = 0;
        return await Task.Run(() => Decode(encoded, token), token);
    }

    private static string? Decode(Stream stream, CancellationToken token)
    {
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            throw new InvalidDataException("image-invalid");
        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels) throw new InvalidDataException("image-limit");
        token.ThrowIfCancellationRequested();
        using var original = SKBitmap.Decode(codec);
        if (original is null) throw new InvalidDataException("image-invalid");

        double scale = Math.Min(1, ScanEdge / (double)Math.Max(original.Width, original.Height));
        int w = Math.Max(1, (int)Math.Round(original.Width * scale));
        int h = Math.Max(1, (int)Math.Round(original.Height * scale));
        using var pixels = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromBitmap(original);
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear));
            canvas.Flush();
        }
        token.ThrowIfCancellationRequested();
        var decoder = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.QR_CODE],
                TryHarder = true,
                TryInverted = true
            }
        };
        var result = decoder.Decode(pixels.Bytes, w, h, RGBLuminanceSource.BitmapFormat.BGRA32)?.Text;
        token.ThrowIfCancellationRequested();
        return result;
    }
}
