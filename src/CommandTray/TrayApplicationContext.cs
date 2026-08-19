using System.Diagnostics;
using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ConfigurationStore _store;
    private readonly AppConfiguration _configuration;
    private readonly ProcessSupervisor _supervisor = new();
    private readonly NotifyIcon _tray;
    private readonly SettingsForm _settings;
    private readonly RegisteredWaitHandle _showRegistration;
    private bool _exiting;

    public TrayApplicationContext(ConfigurationStore store, AppConfiguration configuration, EventWaitHandle showEvent)
    {
        _store = store;
        _configuration = configuration;
        _settings = new SettingsForm(configuration, store, _supervisor);
        // Create the UI handle before registering cross-thread callbacks. Without
        // it, a second instance can signal successfully while BeginInvoke fails,
        // leaving a background process with no reachable UI.
        _ = _settings.Handle;
        _settings.ConfigurationChanged += (_, _) => { SaveConfiguration(); RebuildMenu(); };
        _supervisor.StateChanged += (_, _) => RebuildMenuOnUiThread();
        _supervisor.Failure += (_, failure) => ShowFailure(failure.Service.Name, failure.Message);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "CommandTray",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
        RebuildMenu();

        // Re-register once the WinForms message loop is actually pumping. This
        // keeps the running state aligned with a live notification-area icon.
        Application.Idle += EnsureTrayVisibleOnFirstIdle;

        _showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            if (!_exiting && !_settings.IsDisposed)
                _settings.BeginInvoke(ShowSettings);
        }, null, Timeout.Infinite, false);

        _ = _supervisor.StartConfiguredServicesAsync(_configuration.Services);
    }

    public void ShowSettings()
    {
        if (!_settings.Visible) _settings.Show();
        if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
        _settings.Activate();
        _settings.BringToFront();
    }

    private void EnsureTrayVisibleOnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= EnsureTrayVisibleOnFirstIdle;
        if (_exiting) return;
        _tray.Visible = false;
        _tray.Visible = true;
    }

    private void RebuildMenuOnUiThread()
    {
        if (_settings.IsHandleCreated) _settings.BeginInvoke(RebuildMenu);
    }

    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip();
        var header = new ToolStripMenuItem("CommandTray") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        foreach (var service in _configuration.Services)
        {
            var snapshot = _supervisor.GetSnapshot(service);
            var item = new ToolStripMenuItem($"{UiTheme.StateGlyph(snapshot.State)} {service.Name}");
            if (!string.IsNullOrWhiteSpace(service.OpenUrl))
                item.DropDownItems.Add(Localizer.T("Open", "打开"), null, (_, _) => OpenUrl(service.OpenUrl));
            if (snapshot.State is ServiceState.Stopped or ServiceState.Faulted)
                item.DropDownItems.Add(Localizer.T("Start", "启动"), null, async (_, _) => await RunSafe(() => _supervisor.StartAsync(service)));
            if (snapshot.State == ServiceState.Running)
            {
                item.DropDownItems.Add(Localizer.T("Stop", "停止"), null, async (_, _) => await RunSafe(() => _supervisor.StopAsync(service)));
                item.DropDownItems.Add(Localizer.T("Restart", "重启"), null, async (_, _) => await RunSafe(() => _supervisor.RestartAsync(service)));
            }
            item.DropDownItems.Add(Localizer.T("View log", "查看日志"), null, (_, _) =>
                new LogViewerForm(service.Name, _supervisor.GetLogPath(service)).Show());
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Localizer.T("Settings…", "设置…"), null, (_, _) => ShowSettings());
        var autoStart = new ToolStripMenuItem(Localizer.T("Start with Windows", "登录时启动"))
        {
            Checked = AutoStartService.IsEnabled(),
            CheckOnClick = true
        };
        autoStart.CheckedChanged += (_, _) =>
        {
            AutoStartService.SetEnabled(autoStart.Checked);
            _configuration.RunAtLogin = autoStart.Checked;
            SaveConfiguration();
        };
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripMenuItem(Localizer.T("Exit", "退出"), null, async (_, _) => await ExitAsync()));
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private async Task RunSafe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowFailure("CommandTray", ex.Message); }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        Application.Idle -= EnsureTrayVisibleOnFirstIdle;
        _tray.Visible = false;
        await _supervisor.StopAllAsync(_configuration.Services);
        await _supervisor.DisposeAsync();
        _showRegistration.Unregister(null);
        _settings.AllowExit();
        _settings.Close();
        ExitThread();
    }

    private void SaveConfiguration()
    {
        try { _store.Save(_configuration); }
        catch (Exception ex) { ShowFailure("Configuration", ex.Message); }
    }

    private void ShowFailure(string service, string message)
    {
        if (_settings.IsHandleCreated && _settings.InvokeRequired)
        {
            _settings.BeginInvoke(() => ShowFailure(service, message));
            return;
        }
        _tray.ShowBalloonTip(5000, $"{service} - {Localizer.T("failed", "发生故障")}", message, ToolTipIcon.Error);
    }

    private static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }
}
