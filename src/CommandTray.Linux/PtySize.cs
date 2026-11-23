using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace CommandTray.Linux;

internal static partial class PtySize
{
    private const uint SetWindowSize = 0x5414;
    private const int NoControllingTerminal = 0x100;
    private const int CloseOnExec = 0x80000;
    private const int NonBlocking = 0x800;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort HorizontalPixels;
        public ushort VerticalPixels;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle Open(string path, int mode);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(SafeFileHandle descriptor, nuint request, ref WindowSize data);

    public static async Task ApplyAsync(int scriptPid, int columns, int rows)
    {
        if (columns is < 1 or > 1000 || rows is < 1 or > 1000)
            throw new ArgumentException("Terminal columns and rows must be in the range 1 to 1000.");

        // util-linux script starts the shell on the slave PTY. Its recorded child must own that PTY.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var target = FindSlave(scriptPid);
            if (target is not null)
            {
                using var fd = Open(target, NoControllingTerminal | CloseOnExec | NonBlocking);
                if (!fd.IsInvalid)
                {
                    var size = new WindowSize { Columns = (ushort)columns, Rows = (ushort)rows };
                    if (Ioctl(fd, SetWindowSize, ref size) == 0) return;
                }
            }
            await Task.Delay(30);
        }

        throw new IOException("The Linux terminal has not attached a resizable PTY.");
    }

    private static string? FindSlave(int scriptPid)
    {
        try
        {
            var children = File.ReadAllText($"/proc/{scriptPid}/task/{scriptPid}/children")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var child in children)
            {
                if (!int.TryParse(child, out var childPid)) continue;
                var target = new FileInfo($"/proc/{childPid}/fd/0").LinkTarget;
                if (target is not null && target.StartsWith("/dev/pts/", StringComparison.Ordinal) &&
                    target.Length > 9 && target.AsSpan(9).IndexOfAnyExceptInRange('0', '9') < 0)
                    return target;
            }
        }
        catch (IOException) { }
        return null;
    }
}
