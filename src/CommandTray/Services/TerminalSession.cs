using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace CommandTray.Services;

internal sealed class TerminalSession : IDisposable
{
    private readonly object _sync = new();
    private readonly object _native = new();
    private readonly StringBuilder _text = new(), _sequence = new();
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly WindowsJob _job;
    private readonly Channel<string> _keys = Channel.CreateBounded<string>(128);
    private IntPtr _console;
    private int _cursor, _escape, _disposed;
    private Action? _cleanup;
    private volatile bool _active = true;

    private TerminalSession(IntPtr console, WindowsJob job, SafeFileHandle input, SafeFileHandle output)
    {
        _console = console;
        _job = job;
        _input = new FileStream(input, FileAccess.Write);
        _output = new FileStream(output, FileAccess.Read);
        _ = Task.Run(ReadAsync);
        _ = Task.Run(WriteAsync);
    }

    public static TerminalSession Start(string shell, string? directory, string[]? arguments = null)
    {
        var (program, defaults) = shell switch
        {
            "cmd" => (Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/Q", "/D" }),
            "powershell" => (Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), new[] { "-NoLogo", "-NoProfile" }),
            "ssh" => (Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"), Array.Empty<string>()),
            _ => throw new ArgumentException("Shell must be cmd, powershell or ssh.", nameof(shell))
        };
        if (!File.Exists(program)) throw new FileNotFoundException("The Windows shell is unavailable.", program);
        directory = string.IsNullOrWhiteSpace(directory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetFullPath(directory);
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) Fail();
        using var inSide = inputRead;
        IntPtr console = IntPtr.Zero, attributes = IntPtr.Zero;
        SafeFileHandle? outputRead = null;
        WindowsJob? job = null;
        var initialized = false;
        var started = false;
        ProcessInformation process = default;
        try
        {
            if (!CreatePipe(out outputRead, out var outputWrite, IntPtr.Zero, 0)) Fail();
            using var outSide = outputWrite;
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord(100, 28), inputRead, outputWrite, 0, out console));
            nuint bytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (bytes == 0) Fail();
            attributes = Marshal.AllocHGlobal((nint)bytes);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) Fail();
            initialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, 0x00020016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) Fail();
            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            var command = new StringBuilder(string.Join(" ", new[] { program }.Concat(defaults).Concat(arguments ?? []).Select(Quote)));
            const uint suspendedAndExtendedStartup = 0x00000004 | 0x00080000;
            if (!CreateProcess(program, command, IntPtr.Zero, IntPtr.Zero, false, suspendedAndExtendedStartup,
                    IntPtr.Zero, directory, ref startup, out process)) Fail();
            job = new WindowsJob();
            using (var child = Process.GetProcessById((int)process.ProcessId)) job.Add(child);
            if (ResumeThread(process.Thread) == uint.MaxValue) Fail();
            var session = new TerminalSession(console, job, inputWrite, outputRead);
            started = true;
            return session;
        }
        finally
        {
            if (!started)
            {
                if (process.Process != IntPtr.Zero) TerminateProcess(process.Process, 1);
                job?.Dispose();
                inputWrite.Dispose();
                outputRead?.Dispose();
                if (console != IntPtr.Zero) ClosePseudoConsole(console);
            }
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
        }
    }

    public bool Active => _active && Volatile.Read(ref _disposed) == 0;
    public string Output { get { lock (_sync) return _text.ToString(); } }
    public void Send(string input)
    {
        if (input.Length > 16384) throw new ArgumentException("Terminal input is limited to 16,384 characters.", nameof(input));
        if (!Active || !_keys.Writer.TryWrite(input)) throw new InvalidOperationException("Terminal input is unavailable.");
    }

    public void Resize(int columns, int rows)
    {
        lock (_native)
            if (Active && _console != IntPtr.Zero)
                ResizePseudoConsole(_console, new Coord((short)Math.Clamp(columns, 20, 300), (short)Math.Clamp(rows, 8, 150)));
    }

    private async Task ReadAsync()
    {
        try
        {
            using var reader = new StreamReader(_output, Encoding.UTF8);
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
            {
                lock (_sync)
                {
                    for (var i = 0; i < count; i++) Append(buffer[i]);
                    if (_text.Length > 100000)
                    {
                        var remove = _text.Length - 80000;
                        _text.Remove(0, remove);
                        _cursor = Math.Max(0, _cursor - remove);
                    }
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { _active = false; _keys.Writer.TryComplete(); CompleteCleanup(); }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var input in _keys.Reader.ReadAllAsync())
            {
                await _input.WriteAsync(Encoding.UTF8.GetBytes(input));
                await _input.FlushAsync();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void Append(char value)
    {
        if (_escape == 1)
        {
            _sequence.Clear();
            _escape = value switch { '[' => 2, ']' or 'P' or '_' => 3, _ => 0 };
            return;
        }
        if (_escape == 3)
        {
            if (value == '\a') _escape = 0;
            else if (value == '\x1b') _escape = 4;
            return;
        }
        if (_escape == 4) { _escape = value == '\\' ? 0 : 3; return; }
        if (_escape == 2)
        {
            if (value is < '@' or > '~')
            {
                if (_sequence.Length < 60) _sequence.Append(value);
                return;
            }
            _escape = 0;
            var param = int.TryParse(_sequence.ToString(), out var number) ? Math.Clamp(number, 0, 300) : 0;
            switch (value)
            {
                case 'J' when param is 2 or 3: _text.Clear(); _cursor = 0; break;
                case 'K':
                    var end = _cursor;
                    while (end < _text.Length && _text[end] != '\n') end++;
                    _text.Remove(_cursor, end - _cursor);
                    break;
                case 'D': _cursor = Math.Max(LineStart(), _cursor - Math.Max(1, param)); break;
                case 'G': _cursor = Math.Min(_text.Length, LineStart() + Math.Max(0, param - 1)); break;
            }
            return;
        }
        switch (value)
        {
            case '\x1b': _escape = 1; return;
            case '\r': _cursor = LineStart(); return;
            case '\b': _cursor = Math.Max(LineStart(), _cursor - 1); return;
            case '\n':
                while (_cursor < _text.Length && _text[_cursor] != '\n') _cursor++;
                if (_cursor == _text.Length) _text.Append('\n');
                _cursor++;
                return;
            case '\t': for (var i = 0; i < 4; i++) Append(' '); return;
        }
        if (char.IsControl(value)) return;
        if (_cursor >= _text.Length) _text.Append(value);
        else if (_text[_cursor] == '\n') _text.Insert(_cursor, value);
        else _text[_cursor] = value;
        _cursor++;
    }

    private int LineStart()
    {
        var start = Math.Min(_cursor, _text.Length);
        while (start > 0 && _text[start - 1] != '\n') start--;
        return start;
    }

    public void SetCleanup(Action cleanup)
    {
        _cleanup = cleanup;
        if (!Active) CompleteCleanup();
    }

    private void CompleteCleanup()
    {
        try { Interlocked.Exchange(ref _cleanup, null)?.Invoke(); }
        catch (IOException error) { Trace.WriteLine(error.Message); }
        catch (UnauthorizedAccessException error) { Trace.WriteLine(error.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _keys.Writer.TryComplete();
        _job.Dispose();
        lock (_native)
        {
            if (_console != IntPtr.Zero)
            {
                ClosePseudoConsole(_console);
                _console = IntPtr.Zero;
            }
        }
        _input.Dispose();
        CompleteCleanup();
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? 2 * slashes + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', 2 * slashes).Append('"').ToString();
    }

    private static void Fail() => throw new Win32Exception(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedLength;
        public IntPtr ReservedData, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string program, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
