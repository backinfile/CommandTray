using System.Runtime.InteropServices;

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

    private readonly Action _requestShutdown;
    private bool _disposed;

    public ShutdownWindow(Action requestShutdown)
    {
        _requestShutdown = requestShutdown;
        CreateHandle(new CreateParams { Caption = "CommandTray.ShutdownWindow" });
    }

    internal nint WindowHandle => Handle;

    public void RequestShutdown()
    {
        if (!_disposed)
            PostMessage(Handle, WmRequestShutdown, 0, 0);
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
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
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);
}
