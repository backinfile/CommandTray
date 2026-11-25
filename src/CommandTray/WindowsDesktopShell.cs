using Avalonia.Controls;
using CommandTray.Ui;
using CommandTray.Services;

namespace CommandTray;

internal sealed class WindowsDesktopShell(WindowsClientBackend backend, System.Drawing.Icon applicationIcon)
{
    private Window? _window;
    private ClientView? _view;
    private bool _closing;

    public void Show()
    {
        var window = RequireWindow();
        if (!window.IsVisible) window.Show();
        if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
            window.WindowState = Avalonia.Controls.WindowState.Normal;
        window.Activate();
    }

    public void Navigate(DesktopPage page, string? spaceId = null, string? itemId = null)
    {
        RequireWindow();
        _view!.Navigate(page switch
        {
            DesktopPage.Commands => itemId is null ? "commands" : "logs",
            DesktopPage.Workspace => "workspace",
            DesktopPage.SharedFiles => "files",
            DesktopPage.Clipboard => "clipboard",
            DesktopPage.Captures => "captures",
            DesktopPage.Settings => "settings",
            _ => throw new ArgumentOutOfRangeException(nameof(page))
        }, spaceId, itemId);
        Show();
    }

    public string? PromptSecret(string title)
    {
        Show();
        return DesktopWorkspacePanel.Prompt(Owner, title, "", secret: true);
    }

    public IWin32Window Owner => new WindowOwner(RequireWindow().TryGetPlatformHandle()!.Handle);

    public bool HideForCapture()
    {
        if (_window is not { IsVisible: true, WindowState: not WindowState.Minimized }) return false;
        _window.Hide();
        return true;
    }

    public void RestoreAfterCapture()
    {
        if (!_closing && _window is { IsVisible: false }) _window.Show();
    }

    public void Close()
    {
        _closing = true;
        _window?.Close();
    }

    private Window RequireWindow()
    {
        if (_window is not null) return _window;
        _view = new ClientView(backend);
        using var icon = new MemoryStream();
        applicationIcon.Save(icon);
        icon.Position = 0;
        _window = new Window
        {
            Title = "CommandTray",
            Icon = new WindowIcon(icon),
            Content = _view,
            Width = 1280,
            Height = 820,
            MinWidth = 740,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        _window.Closing += (_, args) =>
        {
            if (_closing || args.CloseReason == WindowCloseReason.ApplicationShutdown) return;
            args.Cancel = true;
            _window.Hide();
        };
        return _window;
    }

    private sealed record WindowOwner(nint Handle) : IWin32Window;
}
