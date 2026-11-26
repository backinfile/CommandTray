using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommandTray.Models;
using CommandTray.Services;
using CommandTray.Sync;

namespace CommandTray;

internal sealed class TrayApplicationContext : IDisposable
{
    private readonly AppConfiguration _configuration;
    private readonly ProcessSupervisor _supervisor = new();
    private readonly CommandTrayApi _api;
    private readonly DesktopRuntime _desktop;
    private DesktopPetForm? _pet;
    private readonly McpServerHost _mcpHost = new();
    private readonly Icon _appIcon;
    private readonly Icon _recordingIcon;
    private readonly DispatcherTimer _recordingTimer;
    private bool _recordingInTray;
    private readonly NotifyIcon _tray;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly WindowsDesktopShell _shell;
    private readonly WindowsClientBackend _backend;
    private readonly CancellationTokenSource _syncLifetime = new();
    private readonly Task _backgroundSync;
    private readonly SuiteUpdates? _updates;
    private readonly string _updatePlatform = WindowsUpdateInstall.Platform();
    private string? _restartUpdate;
    private bool _installOnExit;
    private readonly RegisteredWaitHandle _showRegistration;
    private readonly RegisteredWaitHandle _shutdownRegistration;
    private readonly ShutdownWindow _shutdownWindow;
    private bool _exiting;
    private Action? _trayNoticeAction;

    public TrayApplicationContext(
        ConfigurationStore store,
        AppConfiguration configuration,
        EventWaitHandle showEvent,
        EventWaitHandle shutdownEvent,
        IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _configuration = configuration;
        _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
        _recordingIcon = CreateRecordingIcon(_appIcon);
        _api = new CommandTrayApi(configuration, store, _supervisor);
        _lifetime = lifetime;
        _desktop = new DesktopRuntime(Path.GetDirectoryName(store.PathName)!);
        if (typeof(Program).Assembly.Location.Length == 0)
            _updates = new SuiteUpdates(Path.GetDirectoryName(store.PathName)!, _updatePlatform,
                SuiteUpdates.AssemblyVersion(typeof(Program).Assembly));
        _backend = new WindowsClientBackend(_desktop, _api, store, configuration, _updates,
            (ready, cancellation) => Dispatcher.UIThread.InvokeAsync(() =>
            {
                _restartUpdate = ready;
                BeginExit();
            }, DispatcherPriority.Normal, cancellation).GetTask());
        _shell = new WindowsDesktopShell(_backend, _appIcon);
        _desktop.AttachShell(_shell, _backend);
        _desktop.SetPetVisible = SetPetVisible;
        _desktop.Notification += (_, message) => ShowNotice(message);
        _backend.SettingsChanged += (_, _) =>
        {
            RebuildMenu();
            Dispatcher.UIThread.Post(async () => await ApplyMcpStateAsync());
        };
        _api.ConfigurationChanged += (_, _) =>
        {
            PublishCommandTemplates();
            RebuildMenuOnUiThread();
        };
        _supervisor.StateChanged += (_, _) => RebuildMenuOnUiThread();
        _supervisor.Failure += (_, failure) => ShowFailure(failure.Service.Name, failure.Message);

        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "CommandTray Suite",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => _trayNoticeAction?.Invoke();
        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _recordingTimer.Tick += (_, _) =>
        {
            var recording = _desktop.Media.IsRecording;
            if (_recordingInTray == recording || _exiting) return;
            _recordingInTray = recording;
            _tray.Icon = recording ? _recordingIcon : _appIcon;
            _tray.Text = recording ? Localizer.T("CommandTray Suite · Recording", "CommandTray Suite · 正在录屏") : "CommandTray Suite";
            RebuildMenu();
            if (!recording && _desktop.Media.RecordingFailure is { } error)
                ShowFailure(Localizer.T("Screen recording", "屏幕录制"), error);
        };
        _recordingTimer.Start();
        RebuildMenu();
        Localizer.LanguageChanged += OnLanguageChanged;

        _shutdownWindow = new ShutdownWindow(BeginExit);
        lifetime.ShutdownRequested += (_, args) =>
        {
            args.Cancel = true;
            BeginExit();
        };
        // Re-register after the Avalonia Win32 message loop starts pumping.
        Dispatcher.UIThread.Post(EnsureTrayVisible, DispatcherPriority.ApplicationIdle);

        _showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            Dispatcher.UIThread.Post(ShowSettings);
        }, null, Timeout.Infinite, false);
        _shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(shutdownEvent, (_, _) =>
            _shutdownWindow.RequestShutdown(), null, Timeout.Infinite, false);

        PublishCommandTemplates();
        _ = _supervisor.StartConfiguredServicesAsync(_configuration.Services);
        _ = ApplyMcpStateAsync();
        var clipboard = new ClipboardAutoSync(
            () =>
            {
                lock (_configuration)
                    return _configuration.ClipboardAutoSync && _desktop.Relay.ClipboardConnection() is { } connection
                        ? connection with { Direction = _desktop.ClipboardPreferences.Direction } : null;
            },
            cancellation => _desktop.OnUiAsync(_desktop.ReadClipboardForSync, cancellation),
            (expected, text, cancellation) => _desktop.OnUiAsync(() =>
            {
                lock (_configuration)
                    if (!_configuration.ClipboardAutoSync || _desktop.ClipboardPreferences.Direction == "send") return false;
                if (_desktop.ReadClipboardForSync() != expected) return false;
                _desktop.WriteClipboard(text);
                return true;
            }, cancellation));
        _backgroundSync = Task.WhenAll(
            _desktop.Relay.RunBackgroundSyncAsync(_desktop.ReapRemovedTerminals, _syncLifetime.Token),
            clipboard.RunAsync(_syncLifetime.Token), _updates?.RunAsync(_syncLifetime.Token) ?? Task.CompletedTask);
    }

    public void ShowSettings()
    {
        if (!_exiting) _shell.Show();
    }

    private void ShowPage(DesktopPage page)
    {
        if (!_exiting) _shell.Navigate(page);
    }

    private bool SetPetVisible(bool visible)
    {
        if (_exiting) return false;
        if (visible && _pet is null)
        {
            _pet = new DesktopPetForm(() => ShowPage(DesktopPage.Workspace));
            _pet.FormClosed += (_, _) => { _pet = null; if (!_exiting) RebuildMenu(); };
            _pet.Show();
        }
        else if (!visible) _pet?.Close();
        RebuildMenu();
        return _pet is { Visible: true };
    }

    private void ShowNotice(string message) => ShowNotice(new DesktopNotice(message));

    private void ShowNotice(DesktopNotice notice)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowNotice(notice));
            return;
        }
        if (_exiting) return;
        Action? openSource = notice.SpaceId is { } spaceId && notice.ItemId is { } itemId
            ? () => _shell.Navigate(DesktopPage.Workspace, spaceId, itemId)
            : null;
        if (_pet is { Visible: true } pet)
            pet.Notice(notice.Message, openSource);
        else
        {
            _trayNoticeAction = openSource;
            _tray.ShowBalloonTip(4500, "CommandTray", notice.Message, ToolTipIcon.Info);
        }
    }

    private void EnsureTrayVisible()
    {
        if (_exiting) return;
        _tray.Visible = false;
        _tray.Visible = true;
    }

    private void PublishCommandTemplates()
    {
        try { _backend.PublishCommandTemplates(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowFailure("Workspace", error.Message);
        }
    }

    private void RebuildMenuOnUiThread() => Dispatcher.UIThread.Post(RebuildMenu);

    private void OnLanguageChanged(object? sender, EventArgs e) => RebuildMenuOnUiThread();

    private void RebuildMenu()
    {
        if (_exiting) return;
        var menu = new ContextMenuStrip();
        var header = new ToolStripMenuItem("CommandTray") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        ServiceDefinition[] services;
        lock (_configuration) services = _configuration.Services.ToArray();
        foreach (var service in services)
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
                _shell.Navigate(DesktopPage.Commands, itemId: service.Id.ToString()));
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Localizer.T("Commands", "命令管理"), null, (_, _) => ShowPage(DesktopPage.Commands));
        menu.Items.Add(Localizer.T("Files & terminal", "文件与终端"), null, (_, _) => ShowPage(DesktopPage.Workspace));
        menu.Items.Add(Localizer.T("Shared files", "文件共享"), null, (_, _) => ShowPage(DesktopPage.SharedFiles));
        menu.Items.Add(Localizer.T("Clipboard", "剪贴板"), null, (_, _) => ShowPage(DesktopPage.Clipboard));
        menu.Items.Add(Localizer.T("Capture history", "捕获历史"), null, (_, _) => ShowPage(DesktopPage.Captures));
        if (_desktop.Media.IsRecording)
            menu.Items.Add(Localizer.T("Stop recording", "结束录屏"), null, async (_, _) => await RunSafe(async () =>
            {
                await _desktop.Media.StopRecordingAsync();
                ShowNotice(Localizer.T("Recording saved", "录屏已保存"));
            }));
        menu.Items.Add(Localizer.T("Screenshot", "截图"), null, (_, _) =>
        {
            try
            {
                using var image = Image.FromFile(_desktop.Media.Screenshot());
                Clipboard.SetImage(image);
                ShowNotice(Localizer.T("Screenshot saved and copied", "截图已保存并复制"));
            }
            catch (Exception ex) { ShowFailure("CommandTray", ex.Message); }
        });
        menu.Items.Add(Localizer.T("Desktop pet", "桌面宠物"), null, (_, _) => SetPetVisible(_pet is null));
        menu.Items.Add(Localizer.T("Settings", "设置"), null, (_, _) => ShowPage(DesktopPage.Settings));
        var autoStart = new ToolStripMenuItem(Localizer.T("Start with Windows", "开机自启"))
        {
            Checked = AutoStartService.IsEnabled(),
            CheckOnClick = true
        };
        autoStart.CheckedChanged += (_, _) =>
        {
            try { _backend.SaveSettings(null, autoStart.Checked, null); }
            catch (Exception error) { ShowFailure("Configuration", error.Message); }
        };
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripMenuItem(Localizer.T("Exit", "退出"), null, (_, _) =>
        {
            _installOnExit = true;
            _shutdownWindow.RequestShutdown();
        }));
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private async Task RunSafe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowFailure("CommandTray", ex.Message); }
    }

    private static Icon CreateRecordingIcon(Icon original)
    {
        using var image = original.ToBitmap();
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int diameter = Math.Max(6, image.Width / 2);
            graphics.FillEllipse(Brushes.White, image.Width - diameter, image.Height - diameter, diameter, diameter);
            graphics.FillEllipse(Brushes.Red, image.Width - diameter + 1, image.Height - diameter + 1, diameter - 2, diameter - 2);
        }
        var handle = image.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);

    private void BeginExit()
    {
        if (_exiting) return;
        _exiting = true;
        _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        _tray.Visible = false;
        _syncLifetime.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            try { await _backgroundSync.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { }

            try { await _mcpHost.StopAsync(timeout.Token); }
            catch { /* shutdown must continue even if one component fails */ }

            ServiceDefinition[] services;
            lock (_configuration) services = _configuration.Services.ToArray();
            try { await _supervisor.StopAllAsync(services, timeout.Token); }
            catch { /* DisposeAsync force-terminates any remaining owned jobs */ }
            try { await _supervisor.DisposeAsync(); }
            catch { /* the application itself must still terminate */ }

            if (_updates is not null && (_restartUpdate is not null || _installOnExit && _updates.Enabled))
            {
                try
                {
                    var ready = _restartUpdate ?? await _updates.ReadyDirectoryAsync(timeout.Token);
                    if (ready is not null) SuiteDesktopUpdate.Launch(ready, _updatePlatform, restart: _restartUpdate is not null);
                }
                catch (Exception error)
                {
                    Debug.WriteLine("Suite update could not start: " + error.Message);
                }
            }
        }
        finally
        {
            try
            {
                _showRegistration.Unregister(null);
                _shutdownRegistration.Unregister(null);
                _pet?.Close();
                _shell.Close();
            }
            finally
            {
                // A cleanup or window-disposal failure must never leave the
                // tray process alive after the user requested Exit.
                _lifetime.Shutdown();
            }
        }
    }

    private async Task ApplyMcpStateAsync()
    {
        try
        {
            bool enabled;
            lock (_configuration) enabled = _configuration.McpEnabled;
            if (enabled) await _mcpHost.StartDesktopAsync(_api, _desktop);
            else await _mcpHost.StopAsync();
        }
        catch (Exception ex) { ShowFailure("MCP server", ex.Message); }
    }

    private void ShowFailure(string service, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowFailure(service, message));
            return;
        }
        if (_exiting) return;
        _trayNoticeAction = null;
        _tray.ShowBalloonTip(5000, $"{service} - {Localizer.T("failed", "发生故障")}",
            Localizer.T(message, Localizer.ChineseError(message)), ToolTipIcon.Error);
    }

    private static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _exiting = true;
        Localizer.LanguageChanged -= OnLanguageChanged;
        _showRegistration.Unregister(null);
        _shutdownRegistration.Unregister(null);
        _syncLifetime.Cancel();
        _syncLifetime.Dispose();
        _mcpHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _desktop.Dispose();
        _shutdownWindow.Dispose();
        _recordingTimer.Stop();
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _recordingIcon.Dispose();
        _appIcon.Dispose();
    }
}
