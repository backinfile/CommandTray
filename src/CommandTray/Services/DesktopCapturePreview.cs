using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CommandTray.Core;

namespace CommandTray.Services;

internal static class DesktopCapturePreview
{
    internal static CapturePreviewFrame Read(nint window, Rectangle bounds, Rectangle? exclusion, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException(Localizer.T("The preview source is unavailable.", "预览对象已不可用。"));

        double scale = Math.Min(1, Math.Min(768d / bounds.Width, 432d / bounds.Height));
        int width = Math.Max(1, (int)Math.Round(bounds.Width * scale)), height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        using var pixels = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(pixels))
        {
            var source = GetDC(window);
            if (source == 0) throw new InvalidOperationException(Localizer.T("Could not read the preview source.", "无法读取预览画面。"));
            try
            {
                var output = graphics.GetHdc();
                try
                {
                    SetStretchBltMode(output, 4);
                    const uint copyPixels = 0x00CC0020;
                    if (!StretchBlt(output, 0, 0, width, height, source, bounds.X, bounds.Y, bounds.Width, bounds.Height, copyPixels))
                        throw new InvalidOperationException(Localizer.T("Could not read the preview source.", "无法读取预览画面。"));
                }
                finally { graphics.ReleaseHdc(output); }
            }
            finally { ReleaseDC(window, source); }

            if (window == 0 && exclusion is { } area)
            {
                var overlap = Rectangle.Intersect(area, bounds);
                if (overlap.Width > 0 && overlap.Height > 0)
                {
                    using var mask = new SolidBrush(Color.FromArgb(20, 29, 41));
                    graphics.FillRectangle(mask, Rectangle.FromLTRB(
                        (int)Math.Floor((overlap.Left - bounds.Left) * scale), (int)Math.Floor((overlap.Top - bounds.Top) * scale),
                        (int)Math.Ceiling((overlap.Right - bounds.Left) * scale), (int)Math.Ceiling((overlap.Bottom - bounds.Top) * scale)));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        using var encoded = new MemoryStream();
        pixels.Save(encoded, ImageFormat.Jpeg);
        return new CapturePreviewFrame(encoded.ToArray(), bounds.Width, bounds.Height);
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint context);
    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint destination, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(nint destination, int x, int y, int width, int height, nint source,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
