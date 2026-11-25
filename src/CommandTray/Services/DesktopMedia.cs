using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CommandTray.Services;

internal sealed partial class DesktopMedia : IDisposable
{
    private readonly string _folder;
    private readonly object _gate = new();
    private Session? _recording;
    private Rectangle? _lastRegion;
    private string? _lastRecording;
    private string? _recordingError;
    private bool _disposed;

    public DesktopMedia(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public string Folder => _folder;
    public Rectangle? LastRegion { get { lock (_gate) return _lastRegion; } }
    public bool IsRecording { get { lock (_gate) return _recording is not null; } }
    public string? RecordingFailure { get { lock (_gate) return _recordingError; } }

    public object RecordingState
    {
        get
        {
            lock (_gate)
            {
                var session = _recording;
                return new
                {
                    recording = session is not null,
                    state = session?.State ?? "idle",
                    path = session?.Path,
                    elapsedSeconds = (int)(session?.Clock.Elapsed.TotalSeconds ?? 0),
                    lastPath = _lastRecording,
                    error = _recordingError
                };
            }
        }
    }

    public string Screenshot(Rectangle? region = null, bool rememberRegion = false)
    {
        var rectangle = ValidateArea(region ?? SystemInformation.VirtualScreen);
        using var bitmap = new Bitmap(rectangle.Width, rectangle.Height);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size, CopyPixelOperation.SourceCopy);
        var file = NewFile(".png");
        var temporary = file + ".partial";
        try
        {
            bitmap.Save(temporary, ImageFormat.Png);
            File.Move(temporary, file);
        }
        finally { File.Delete(temporary); }
        if (rememberRegion)
            lock (_gate) _lastRegion = rectangle;
        return file;
    }

    internal static Rectangle ValidateArea(Rectangle area)
    {
        var desktop = SystemInformation.VirtualScreen;
        if (area.Width <= 0 || area.Height <= 0 || area.X < desktop.X || area.Y < desktop.Y ||
            (long)area.X + area.Width > desktop.Right || (long)area.Y + area.Height > desktop.Bottom)
            throw new ArgumentOutOfRangeException(nameof(area), Localizer.T("Select an area within the desktop.", "请选择桌面范围内的区域。"));
        return area;
    }

    public string[] Captures() => Directory.EnumerateFiles(_folder).Where(IsCapture)
        .OrderByDescending(File.GetLastWriteTimeUtc).Take(250).ToArray();

    private static bool IsCapture(string file) => Path.GetExtension(file).ToLowerInvariant() is ".png" or ".mp4" or ".mkv";

    public string ResolveCapture(string name)
    {
        var candidate = Path.GetFullPath(Path.Combine(_folder, Path.GetFileName(name)));
        if (!IsCapture(candidate) || !File.Exists(candidate))
            throw new FileNotFoundException(Localizer.T("Capture not found.", "找不到捕获文件。"), name);
        return candidate;
    }

    public async Task<string[]> AudioDevicesAsync(CancellationToken cancellationToken)
    {
        if (FindProgram("ffmpeg.exe") is null) return [];
        var info = ExternalProgram("ffmpeg.exe", "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start FFmpeg.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var errorOutput = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return ParseAudioDevices(await errorOutput);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(Localizer.T("FFmpeg could not list audio devices in time.", "FFmpeg 读取声音设备超时。"));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    internal static string[] ParseAudioDevices(string output) => DeviceLine().Matches(output)
        .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();

    [GeneratedRegex("^[^\\r\\n]*?\\x22([^\\r\\n]+)\\x22[ \\t]+\\(audio\\)[ \\t]*\\r?$", RegexOptions.Multiline)]
    private static partial Regex DeviceLine();

    public Task<string> StartRecordingAsync(int frameRate, CancellationToken cancellationToken = default) =>
        StartRecordingAsync(frameRate, null, true, null, "mp4", cancellationToken);

    public Task<string> StartRecordingAsync(int frameRate, Rectangle? area, bool cursor, string? audio, string format,
        CancellationToken cancellationToken)
    {
        if (frameRate is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(frameRate), Localizer.T("Choose a frame rate from 1 to 60.", "帧率须在 1 到 60 之间。"));
        if (format is not ("mp4" or "mkv")) throw new ArgumentException("Choose MP4 or MKV.", nameof(format));
        var rectangle = ValidateArea(area ?? Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen);
        var file = NewFile("." + format);
        var info = ExternalProgram("ffmpeg.exe", RecordingArguments(rectangle, frameRate, cursor, audio, format, file + ".partial"));
        return StartEncoderAsync(info, file, cancellationToken);
    }

    internal static string[] RecordingArguments(Rectangle area, int frameRate, bool cursor, string? audio, string format, string temporary)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-stats_period", "0.2", "-progress", "pipe:1",
            "-f", "gdigrab", "-draw_mouse", cursor ? "1" : "0", "-framerate", frameRate.ToString(CultureInfo.InvariantCulture),
            "-offset_x", area.X.ToString(CultureInfo.InvariantCulture), "-offset_y", area.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{area.Width}x{area.Height}", "-i", "desktop"
        };
        if (!string.IsNullOrEmpty(audio))
            args.AddRange(["-thread_queue_size", "512", "-f", "dshow", "-i", "audio=" + audio, "-map", "0:v:0", "-map", "1:a:0", "-c:a", "aac"]);
        args.AddRange(["-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast"]);
        if (format == "mp4") args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-f", format == "mp4" ? "mp4" : "matroska", temporary]);
        return args.ToArray();
    }

    internal async Task<string> StartEncoderAsync(ProcessStartInfo info, string file, CancellationToken cancellationToken = default)
    {
        Session session;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recording is not null)
                throw new InvalidOperationException(Localizer.T("Stop the current recording first.", "请先结束当前录屏。"));
            info.RedirectStandardInput = info.RedirectStandardOutput = true;
            var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start FFmpeg.");
            session = new Session(process, file);
            _recordingError = _lastRecording = null;
            _recording = session;
        }
        _ = Task.Run(() => ObserveRecordingAsync(session));
        try
        {
            if (await session.Ready.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken)) return file;
            throw new InvalidOperationException(session.Error ?? Localizer.T("FFmpeg captured no frames.", "FFmpeg 没有捕获到画面。"));
        }
        catch (TimeoutException)
        {
            await FinishAsync(session, discard: true);
            throw new InvalidOperationException(Localizer.T("FFmpeg did not produce video within 12 seconds.", "FFmpeg 在 12 秒内没有输出画面。"));
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(session, discard: true);
            throw;
        }
    }

    public Task<string> StopRecordingAsync(CancellationToken cancellationToken = default) =>
        EndRecordingAsync(discard: false, cancellationToken);

    public async Task<string> EndRecordingAsync(bool discard, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session session;
        lock (_gate)
            session = _recording ?? throw new InvalidOperationException(Localizer.T("There is no active recording.", "当前没有录屏。"));
        await FinishAsync(session, discard);
        if (session.Error is { } error) throw new InvalidOperationException(error);
        return session.SavedPath ?? "";
    }

    private async Task FinishAsync(Session session, bool discard)
    {
        Task completion;
        lock (_gate)
        {
            if (!ReferenceEquals(_recording, session)) return;
            if (session.StopTask is null)
            {
                session.Discard = discard;
                session.State = "saving";
                session.StopTask = Task.Run(() => StopEncoderAsync(session));
            }
            completion = session.StopTask;
        }
        await completion;
    }

    private static async Task StopEncoderAsync(Session session)
    {
        var process = session.Process;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            try
            {
                await process.StandardInput.WriteLineAsync("q".AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            await session.Completed.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await session.Completed.Task;
        }
    }

    private async Task ObserveRecordingAsync(Session session)
    {
        var process = session.Process;
        var temporary = session.Path + ".partial";
        var stderr = process.StandardError.ReadToEndAsync();
        var progress = ReadFramesAsync(session);
        try
        {
            await process.WaitForExitAsync();
            await progress;
            var error = (await stderr).Trim();
            lock (_gate)
            {
                if (!session.Discard && process.ExitCode == 0 && session.Clock.IsRunning && new FileInfo(temporary).Length > 0)
                {
                    File.Move(temporary, session.Path);
                    _lastRecording = session.SavedPath = session.Path;
                }
                else if (!session.Discard)
                {
                    session.Error = error.Length > 0 ? "FFmpeg: " + error : Localizer.T("FFmpeg did not save a recording.", "FFmpeg 未生成录屏文件。");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            session.Error = error.Message;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { session.Error ??= error.Message; }
            lock (_gate)
            {
                _recordingError = session.Error;
                _recording = null;
                process.Dispose();
                session.Ready.TrySetResult(false);
                session.Completed.TrySetResult();
            }
        }
    }

    private async Task ReadFramesAsync(Session session)
    {
        while (await session.Process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("frame=", StringComparison.Ordinal) ||
                !int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0 ||
                session.Ready.Task.IsCompleted) continue;
            lock (_gate)
            {
                if (session.State == "starting") session.State = "recording";
                session.Clock.Start();
                session.Ready.TrySetResult(true);
            }
        }
    }

    private sealed class Session(Process process, string path)
    {
        public Process Process { get; } = process;
        public string Path { get; } = path;
        public Stopwatch Clock { get; } = new();
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string State { get; set; } = "starting";
        public string? SavedPath { get; set; }
        public string? Error { get; set; }
        public Task? StopTask { get; set; }
        public bool Discard { get; set; }
    }

    public async Task<string> RecognizeTextAsync(string capture, string language = "eng", CancellationToken cancellationToken = default)
    {
        var file = ResolveCapture(capture);
        if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OCR requires a screenshot.");
        if (string.IsNullOrWhiteSpace(language) || !language.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '+'))
            throw new ArgumentException("Enter one or more Tesseract language codes.");
        var info = ExternalProgram("tesseract.exe", file, "stdout", "-l", language);
        info.RedirectStandardOutput = true;
        info.StandardOutputEncoding = Encoding.UTF8;
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

    private static string? FindProgram(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, executable)).FirstOrDefault(File.Exists);

    private static ProcessStartInfo ExternalProgram(string executable, params string[] arguments)
    {
        var program = FindProgram(executable)
            ?? throw new FileNotFoundException(Localizer.T($"Add {executable} to PATH to use this operation.", $"请安装 {executable} 并将它加入 PATH。"));
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public void Dispose()
    {
        Session? session;
        lock (_gate)
        {
            _disposed = true;
            session = _recording;
        }
        if (session is not null) FinishAsync(session, discard: false).GetAwaiter().GetResult();
    }
}
