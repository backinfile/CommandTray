using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private string _settingsSection = "devices";

    private async Task OpenSettingsAsync(string section)
    {
        _settingsSection = section;
        _page = "settings";
        _viewport.Offset = default;
        await LoadAsync();
    }

    private async Task RenderSettingsAsync(CancellationToken token)
    {
        Header(L("设置", "Settings"));
        _contents.Children.Add(PageHeading(L("设置", "Settings")));
        var settings = await Execute("settings_read", [], token);
        var sections = new List<(string Id, string Label, string Glyph)>
        {
            ("devices", L("中转与设备", "Relay and devices"), "files"),
            ("clip", L("剪贴板", "Clipboard"), "clipboard"),
            ("mcp", L("本地 MCP", "Local MCP"), "terminal"),
            ("app", L("应用", "Application"), "settings")
        };
        if (Has(ClientFeatures.Updates)) sections.Insert(3, ("updates", L("版本更新", "Updates"), "download"));
        if (Has(ClientFeatures.ConfigurationFiles)) sections.Insert(sections.Count - 1, ("data", L("数据", "Data"), "folder-open"));
        if (sections.All(item => item.Id != _settingsSection)) _settingsSection = "devices";

        var navigation = new StackPanel { Orientation = _compact ? Orientation.Horizontal : Orientation.Vertical, Spacing = 3 };
        foreach (var section in sections)
        {
            var tab = Action(section.Label, () => OpenSettingsAsync(section.Id), glyph: section.Glyph);
            tab.Classes.Add(_compact ? "workbench-tab" : "navigation");
            tab.Classes.Set("selected", section.Id == _settingsSection);
            if (section.Id == _settingsSection && _compact)
                tab.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => tab.BringIntoView());
            navigation.Children.Add(tab);
        }
        var pane = new StackPanel { Spacing = 0 };
        var active = sections.First(item => item.Id == _settingsSection);
        var detail = WorkbenchPanel(active.Label, active.Glyph, pane);
        if (_compact)
        {
            _contents.Children.Add(new ScrollViewer { Content = navigation, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
            _contents.Children.Add(detail);
        }
        else
        {
            var selector = Table(navigation);
            selector.Padding = new Thickness(6);
            _contents.Children.Add(WorkbenchSplit(selector, detail, "188,*"));
        }

        switch (_settingsSection)
        {
            case "devices":
                await RenderDeviceSettingsAsync(pane, token);
                break;
            case "clip":
                await RenderClipboardSettingsAsync(pane, settings, token);
                break;
            case "mcp":
                pane.Children.Add(SettingRow(L("本地 MCP", "Local MCP"), SettingSwitch("mcpEnabled",
                    L("本地 MCP", "Local MCP"), settings.Flag("mcpEnabled"))));
                if (settings.Flag("mcpEnabled"))
                {
                    var copy = IconButton("copy", L("复制配置", "Copy configuration"), async () =>
                    {
                        var configuration = await Execute("mcp_config", [], token);
                        await Execute("clipboard_local_write", new() { ["text"] = configuration?.ToJsonString(), ["sensitive"] = true }, token);
                        Report(L("已复制本地 MCP 配置", "Copied local MCP configuration"));
                    });
                    pane.Children.Add(SettingRow(L("连接地址", "Endpoint"), copy, settings.Text("mcpEndpoint")));
                }
                break;
            case "updates":
                pane.Children.Add(await UpdateSettingsAsync(token));
                break;
            case "data":
                pane.Children.Add(SettingRow(L("服务配置", "Service configuration"), Row(
                    Action(L("导出", "Export"), ExportConfigurationAsync),
                    PrimaryAction(L("导入", "Import"), ImportConfigurationAsync))));
                break;
            case "app":
                pane.Children.Add(SettingRow(L("语言", "Language"), Menu(_english ? "English" : "简体中文",
                    ("简体中文", () => SetLanguageAsync(false)), ("English", () => SetLanguageAsync(true)))));
                if (DesktopWorkbench)
                    pane.Children.Add(SettingRow(L("登录后启动", "Start at login"), SettingSwitch("runAtLogin",
                        L("登录后启动", "Start at login"), settings.Flag("runAtLogin"))));
                if (Has(ClientFeatures.Pet))
                    pane.Children.Add(SettingRow(L("桌宠", "Desktop pet"), Row(
                        Action(L("显示", "Show"), async () => await Execute("pet_set", new() { ["visible"] = true })),
                        Action(L("隐藏", "Hide"), async () => await Execute("pet_set", new() { ["visible"] = false })))));
                if (_compact && Has(ClientFeatures.Commands))
                    pane.Children.Add(Action(L("命令管理", "Commands"), () => { Navigate("commands"); return Task.CompletedTask; }));
                if (_compact && Has(ClientFeatures.Capture))
                    pane.Children.Add(Action(L("屏幕捕获", "Capture"), () => { Navigate("capture"); return Task.CompletedTask; }));
                if (_compact && Has(ClientFeatures.Ssh))
                    pane.Children.Add(Action(L("SSH 主机", "SSH hosts"), () => { Navigate("ssh"); return Task.CompletedTask; }));
                break;
        }
    }

    private async Task RenderDeviceSettingsAsync(StackPanel pane, CancellationToken token)
    {
        var relay = await Execute("relay_status", [], token);
        bool paired = relay.Flag("paired");
        if (DesktopWorkbench && _sidebarPaired != paired)
        {
            _sidebarPaired = paired;
            UpdateSidebarSpaces();
        }

        pane.Children.Add(SettingRow(L("中转服务器", "Relay server"), paired
            ? Action(L("编辑连接", "Edit connection"), () => PairForm(relay))
            : PrimaryAction(L("配对", "Pair"), () => PairForm(relay)),
            paired ? relay.Text("server") : L("未配对", "Not paired")));
        if (!paired) return;
        pane.Children.Add(SettingRow(L("本机", "This device"),
            Action(L("解除配对", "Unpair"), () => Confirm(L("解除本机配对？", "Unpair this device?"),
                () => Change("relay_unpair", []))), relay.Text("deviceName")));
        if (HasWorkspaceConflict(relay))
            pane.Children.Add(SettingRow(L("同步冲突", "Sync conflict"),
                WorkspaceConflictActions()));

        var devices = new StackPanel();
        Button? refresh = null;
        refresh = IconButton("sync", L("刷新设备", "Refresh devices"), LoadDevicesAsync);
        pane.Children.Add(SettingRow(L("已配对设备", "Paired devices"), refresh));
        pane.Children.Add(devices);
        await LoadDevicesAsync();

        async Task LoadDevicesAsync()
        {
            if (refresh is not null) refresh.IsEnabled = false;
            devices.Children.Clear();
            var loading = Muted(L("正在加载…", "Loading…"));
            loading.Margin = new Thickness(14, 8);
            devices.Children.Add(loading);
            try
            {
                var entries = await Execute("relay_devices", [], token) as JsonArray ?? [];
                devices.Children.Clear();
                foreach (var device in entries.OfType<JsonObject>())
                {
                    var local = device.Text("id") == relay.Text("deviceId");
                    var date = DateTimeOffset.TryParse(device.Text("pairedAt"), out var pairedAt)
                        ? pairedAt.ToLocalTime().ToString("g") : "";
                    devices.Children.Add(SettingRow(device.Text("name", device.Text("id")),
                        Muted(local ? L("本机", "This device") : ""), date));
                }
            }
            catch (HttpRequestException error) when (!token.IsCancellationRequested) { loading.Text = error.Message; }
            finally { if (refresh is not null) refresh.IsEnabled = true; }
        }
    }

    private async Task RenderClipboardSettingsAsync(StackPanel pane, JsonNode? settings, CancellationToken token)
    {
        var preferences = await Execute("clipboard_preferences", [], token);
        var relay = await Execute("relay_status", [], token);
        bool enabled = settings.Flag("clipboardAutoSync");
        (string Id, string Label)[] options = [("both", L("双向", "Two-way")), ("send", L("仅发送", "Send only")),
            ("receive", L("仅接收", "Receive only"))];
        var direction = new ComboBox { ItemsSource = options.Select(option => option.Label),
            SelectedIndex = Math.Max(0, Array.FindIndex(options, option => option.Id == preferences.Text("direction"))) };
        AutomationProperties.SetName(direction, L("同步方向", "Sync direction"));
        direction.SelectionChanged += async (_, _) => await Act(async () =>
        {
            if (direction.SelectedIndex < 0) return;
            await Execute("clipboard_preferences", new() { ["direction"] = options[direction.SelectedIndex].Id }, token);
        });
        pane.Children.Add(SettingRow(L("自动同步剪贴板", "Sync clipboard automatically"), SettingSwitch("clipboardAutoSync",
            L("自动同步剪贴板", "Sync clipboard automatically"), enabled), L("本机设置", "This device")));
        pane.Children.Add(SettingRow(L("同步方向", "Sync direction"), direction));
        pane.Children.Add(SettingRow(L("同步状态", "Sync status"), Muted(!enabled ? L("手动收发", "Manual") :
            !relay.Flag("paired") ? L("等待配对", "Waiting for pairing") : OperatingSystem.IsAndroid()
                ? L("仅在应用前台同步", "Syncs while the app is in the foreground") : L("自动同步已开启", "Automatic sync is on"))));
    }

    private ToggleSwitch SettingSwitch(string key, string label, bool enabled)
    {
        var toggle = new ToggleSwitch { IsChecked = enabled, OnContent = "", OffContent = "" };
        AutomationProperties.SetName(toggle, label);
        toggle.IsCheckedChanged += async (_, _) => await Act(() =>
            Change("settings_save", new() { [key] = toggle.IsChecked == true }));
        return toggle;
    }

    private static Border SettingRow(string label, Control action, string detail = "")
    {
        var copy = new StackPanel { Spacing = 3, Children = { Heading(label) } };
        if (detail.Length > 0) copy.Children.Add(Muted(detail));
        var row = Section(TitleRow(copy, action));
        row.Padding = new Thickness(13, 9);
        return row;
    }
}
