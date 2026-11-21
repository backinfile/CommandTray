using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;

namespace CommandTray.Services;

internal sealed class DesktopMedia : IDisposable
{
    private readonly string _folder;
    private readonly SemaphoreSlim _recordingGate = new(1, 1);
    private Process? _recorder;
    private Task<string>? _errorOutput;
    private string? _recordingFile;

    public DesktopMedia(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public object RecordingState => new { recording = _recorder is { HasExited: false }, path = _recordingFile };

    public string Screenshot(Rectangle? region = null)
    {
        var rectangle = region ?? SystemInformation.VirtualScreen;
        if (rectangle.Width <= 0 || rectangle.Height <= 0 || !SystemInformation.VirtualScreen.Contains(rectangle))
            throw new ArgumentOutOfRangeException(nameof(region), "Select an area within the desktop.");
        using var bitmap = new Bitmap(rectangle.Width, rectangle.Height);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size, CopyPixelOperation.SourceCopy);
        var file = NewFile(".png");
        bitmap.Save(file, ImageFormat.Png);
        return file;
    }

    public string[] Captures() => Directory.EnumerateFiles(_folder).Where(file =>
        Path.GetExtension(file).ToLowerInvariant() is ".png" or ".mp4").OrderByDescending(File.GetLastWriteTimeUtc).Take(250).ToArray();

    public string ResolveCapture(string name)
    {
        var candidate = Path.GetFullPath(Path.Combine(_folder, Path.GetFileName(name)));
        if (!Captures().Contains(candidate, StringComparer.OrdinalIgnoreCase))
            throw new FileNotFoundException("Capture not found.", name);
        return candidate;
    }

    public async Task<string> StartRecordingAsync(int frameRate, CancellationToken cancellationToken = default)
    {
        if (frameRate is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(frameRate));
        await _recordingGate.WaitAsync(cancellationToken);
        try
        {
            if (_recorder is not null) throw new InvalidOperationException("Stop the current recording first.");
            var area = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
            var file = NewFile(".mp4");
            var info = ExternalProgram("ffmpeg.exe",
                "-hide_banner", "-loglevel", "error", "-f", "gdigrab", "-framerate", frameRate.ToString(CultureInfo.InvariantCulture),
                "-offset_x", area.X.ToString(CultureInfo.InvariantCulture), "-offset_y", area.Y.ToString(CultureInfo.InvariantCulture),
                "-video_size", $"{area.Width}x{area.Height}", "-i", "desktop", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast", file);
            info.RedirectStandardInput = true;
            _recorder = Process.Start(info) ?? throw new InvalidOperationException("Unable to start FFmpeg.");
            _errorOutput = _recorder.StandardError.ReadToEndAsync(CancellationToken.None);
            _recordingFile = file;
            return file;
        }
        finally { _recordingGate.Release(); }
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _recordingGate.WaitAsync(cancellationToken);
        try
        {
            var process = _recorder ?? throw new InvalidOperationException("There is no active recording.");
            var path = _recordingFile!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("q");
                    await process.StandardInput.FlushAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("FFmpeg: " + await _errorOutput!);
                return path;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
                _recorder = null;
                _errorOutput = null;
                _recordingFile = null;
            }
        }
        finally { _recordingGate.Release(); }
    }

    public async Task<string> RecognizeTextAsync(string capture, string language = "eng", CancellationToken cancellationToken = default)
    {
        var file = ResolveCapture(capture);
        if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OCR requires a screenshot.");
        if (string.IsNullOrWhiteSpace(language) || !language.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '+'))
            throw new ArgumentException("Enter one or more Tesseract language codes.");
        var info = ExternalProgram("tesseract.exe", file, "stdout", "-l", language);
        info.RedirectStandardOutput = true;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start Tesseract.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException("Tesseract: " + await error);
            return await output;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private string NewFile(string extension) => Path.Combine(_folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{extension}");

    private static ProcessStartInfo ExternalProgram(string executable, params string[] arguments)
    {
        var searchPaths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var program = searchPaths.Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, executable)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Add {executable} to PATH to use this operation.");
        var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public void Dispose()
    {
        if (_recorder is null) return;
        try { StopRecordingAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Trace.WriteLine(ex.Message); }
    }
}
