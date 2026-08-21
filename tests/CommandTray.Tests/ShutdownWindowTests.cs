using System.Runtime.InteropServices;

namespace CommandTray.Tests;

public sealed class ShutdownWindowTests
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int EndSessionCloseApp = 0x1;

    [Fact]
    public void RestartManagerMessagesRequestNormalApplicationShutdown()
    {
        var shutdownRequests = 0;
        using var window = new ShutdownWindow(() => shutdownRequests++);

        var accepted = SendMessage(
            window.WindowHandle,
            WmQueryEndSession,
            0,
            EndSessionCloseApp);
        SendMessage(
            window.WindowHandle,
            WmEndSession,
            1,
            EndSessionCloseApp);

        Assert.Equal(1, accepted);
        Assert.Equal(1, shutdownRequests);
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
}
