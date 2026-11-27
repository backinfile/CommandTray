using System.Runtime.InteropServices;
using CommandTray.Core;

namespace CommandTray;

/// <summary>
/// Gives Windows Restart Manager a real top-level window to notify. A tray-only
/// application otherwise has no reliable route from system shutdown messages to
/// its normal service-cleanup path.
/// </summary>
internal sealed class ShutdownWindow : NativeWindow, IDisposable
{
    private const int WmClose = 0x0010;
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int WmRequestShutdown = 0x8000 + 1;
    private const int WmHotkey = 0x0312;
    private const int CaptureHotkeyBase = 0x4100;
    private readonly Dictionary<int, CaptureShortcut> _captureHotkeys = [];
    private Action<CaptureShortcut>? _captureShortcut;

    private readonly Action _requestShutdown;
    private bool _disposed;

    public ShutdownWindow(Action requestShutdown)
    {
        _requestShutdown = requestShutdown;
        CreateHandle(new CreateParams { Caption = "CommandTray.ShutdownWindow" });
    }

    internal nint WindowHandle => Handle;

    public object[] RegisterCaptureShortcuts(Action<CaptureShortcut> handler)
    {
        _captureShortcut = handler;
        const uint modifiers = 0x4000 | 0x0002 | 0x0001 | 0x0004; // No repeat, Ctrl, Alt, Shift.
        (CaptureShortcut Action, Keys Key)[] definitions =
        [
            (CaptureShortcut.Region, Keys.S),
            (CaptureShortcut.ActiveWindow, Keys.W),
            (CaptureShortcut.ToggleRecording, Keys.R)
        ];
        return definitions.Select((definition, index) =>
        {
            int id = CaptureHotkeyBase + index;
            bool available = RegisterHotKey(Handle, id, modifiers, (uint)definition.Key);
            if (available) _captureHotkeys.Add(id, definition.Action);
            return (object)new { action = definition.Action.ToString(), gesture = $"Ctrl+Alt+Shift+{definition.Key}", available };
        }).ToArray();
    }

    public void RequestShutdown()
    {
        if (!_disposed)
            PostMessage(Handle, WmRequestShutdown, 0, 0);
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case WmHotkey when _captureHotkeys.TryGetValue((int)message.WParam, out var action):
                _captureShortcut?.Invoke(action);
                message.Result = 0;
                return;
            case WmQueryEndSession:
                message.Result = 1;
                return;
            case WmEndSession when message.WParam != 0:
            case WmClose:
            case WmRequestShutdown:
                _requestShutdown();
                message.Result = 0;
                return;
            default:
                base.WndProc(ref message);
                return;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (int id in _captureHotkeys.Keys) UnregisterHotKey(Handle, id);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
}
