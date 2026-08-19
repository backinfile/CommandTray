using System.Text;

namespace CommandTray.Services;

public sealed class RollingLog : IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int RetainedFiles = 4;
    private readonly string _path;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public event EventHandler<string>? LineWritten;

    public RollingLog(Guid serviceId, string? root = null)
    {
        var directory = Path.Combine(root ?? AppPaths.LogDirectory, serviceId.ToString("N"));
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "current.log");
    }

    public string PathName => _path;

    public void Write(string stream, string line)
    {
        var rendered = $"{DateTimeOffset.Now:O} [{stream}] {line}";
        lock (_gate)
        {
            EnsureWriter();
            _writer!.WriteLine(rendered);
            _writer.Flush();
            if (_writer.BaseStream.Length >= MaxBytes) Rotate();
        }
        LineWritten?.Invoke(this, rendered);
    }

    private void EnsureWriter() => _writer ??= new StreamWriter(
        new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        var oldest = $"{_path}.{RetainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = RetainedFiles - 1; i >= 1; i--)
        {
            var source = $"{_path}.{i}";
            if (File.Exists(source)) File.Move(source, $"{_path}.{i + 1}");
        }
        if (File.Exists(_path)) File.Move(_path, $"{_path}.1");
    }

    public void Dispose()
    {
        lock (_gate) _writer?.Dispose();
    }
}
