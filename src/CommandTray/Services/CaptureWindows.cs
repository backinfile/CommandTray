using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CommandTray.Services;

internal static class CaptureWindows
{
    internal static object[] List()
    {
        var candidates = new List<(string Title, string Application, string Id)>();
        EnumWindows((window, _) =>
        {
            if (!IsAvailable(window, out var processId)) return true;
            var name = new StringBuilder(512);
            if (GetWindowText(window, name, name.Capacity) == 0 || string.IsNullOrWhiteSpace(name.ToString())) return true;
            string application;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                application = process.ProcessName;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true;
            }
            candidates.Add((name.ToString().Trim(), application,
                FormattableString.Invariant($"{window.ToInt64()}:{processId}")));
            return true;
        }, 0);
        return candidates.OrderBy(entry => entry.Application, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => (object)new { id = entry.Id, title = entry.Title, application = entry.Application }).ToArray();
    }

    internal static nint Resolve(string? id)
    {
        var parts = id?.Split(':', 3);
        if (parts is { Length: 2 } &&
            nint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var window) &&
            uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint processId) &&
            IsAvailable(window, out var actual) && actual == processId)
            return window;
        throw new InvalidOperationException(Localizer.T(
            "Select an open application window. Restore it first if it is minimized.",
            "请选择已打开的应用窗口。如果窗口已最小化，请先还原。"));
    }

    internal static Rectangle? ClientBounds(nint window) => GetClientRect(window, out var bounds)
        ? Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom) : null;

    internal static nint ForegroundApplication()
    {
        var window = GetForegroundWindow();
        for (int step = 0; window != 0 && step < 256; step++)
        {
            if (IsAvailable(window, out _) && GetWindowTextLength(window) > 0) return window;
            window = GetWindow(window, 2); // The next window in Z order.
        }
        return 0;
    }

    private static bool IsAvailable(nint window, out uint processId)
    {
        GetWindowThreadProcessId(window, out processId);
        return processId != 0 && processId != Environment.ProcessId && window != GetShellWindow() &&
            IsWindowVisible(window) && !IsIconic(window) &&
            (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0) &&
            GetClientRect(window, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool WindowEnumerator(nint window, nint context);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowEnumerator callback, nint context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder title, int capacity);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect bounds);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
}
