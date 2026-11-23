using Avalonia.Controls.ApplicationLifetimes;
using CommandTray.Models;
using CommandTray.Services;
using CommandTray.Ui;

namespace CommandTray;

internal sealed class WindowsAppStartup(
    ConfigurationStore store, AppConfiguration configuration, EventWaitHandle showEvent, EventWaitHandle shutdownEvent)
    : IDisposable
{
    private TrayApplicationContext? _tray;

    public void Start(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _tray = new TrayApplicationContext(store, configuration, showEvent, shutdownEvent, lifetime);
        if (lifetime.Args?.Contains("--show") == true) _tray.ShowSettings();
    }

    public void Dispose() => _tray?.Dispose();
}

internal sealed class WindowsClientApplication(WindowsAppStartup startup) : ClientApplication
{
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) startup.Start(lifetime);
    }
}
