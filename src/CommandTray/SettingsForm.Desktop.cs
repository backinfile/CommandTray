using System.Text.Json.Nodes;
using CommandTray.Services;

namespace CommandTray;

internal enum DesktopPage { Commands, Workspace, SharedFiles, Clipboard, Captures, Settings }

public sealed partial class SettingsForm
{
    private readonly DesktopRuntime? _desktop;
    private readonly Dictionary<DesktopPage, Control> _desktopPages = [];
    private readonly Dictionary<DesktopPage, Button> _desktopButtons = [];
    private DesktopPage _desktopPage = DesktopPage.Commands;

    private void BuildDesktopPages(Control commands, DesktopRuntime desktop, CommandTrayApi api)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        _desktopPages.Add(DesktopPage.Commands, commands);
        _desktopPages.Add(DesktopPage.Workspace, new DesktopWorkspacePanel(desktop, api));
        _desktopPages.Add(DesktopPage.SharedFiles, new DesktopSharedFilesPanel(desktop));
        _desktopPages.Add(DesktopPage.Clipboard, new DesktopClipboardPanel(desktop));
        _desktopPages.Add(DesktopPage.Captures, new DesktopCapturesPanel(desktop));
        host.Controls.AddRange(_desktopPages.Values.ToArray());
        var navigation = DesktopWorkspacePanel.Toolbar();
        var links = new[]
        {
            (DesktopPage.Commands, "Commands", "命令管理"),
            (DesktopPage.Workspace, "Files & terminal", "文件与终端"),
            (DesktopPage.SharedFiles, "Shared files", "文件共享"),
            (DesktopPage.Clipboard, "Clipboard", "剪贴板"),
            (DesktopPage.Captures, "Capture history", "捕获历史"),
            (DesktopPage.Settings, "Settings", "设置")
        };
        foreach (var (page, english, chinese) in links)
        {
            var button = DesktopWorkspacePanel.Button(english, chinese, () => ShowDesktopPage(page));
            navigation.Controls.Add(button);
            _desktopButtons.Add(page, button);
        }
        Controls.Add(host);
        Controls.Add(navigation);
        ShowDesktopPage(DesktopPage.Commands);
    }

    internal void ShowDesktopPage(DesktopPage page)
    {
        if (_desktopPages.Count == 0) return;
        _desktopPage = page;
        _showingSettings = page == DesktopPage.Settings;
        if (_showingSettings) SyncPreferences();
        var key = _showingSettings ? DesktopPage.Commands : page;
        foreach (var (id, control) in _desktopPages) control.Visible = id == key;
        var selected = _desktopPages[key];
        selected.BringToFront();
        if (selected is DesktopSharedFilesPanel files) files.RefreshWhenPaired();
        if (selected is DesktopCapturesPanel captures) captures.RefreshCaptures();
        UpdateView();
    }

    internal string? ConnectDesktopSsh(JsonObject host, string spaceId)
    {
        ShowDesktopPage(DesktopPage.Workspace);
        return ((DesktopWorkspacePanel)_desktopPages[DesktopPage.Workspace]).ConnectSsh(host, spaceId);
    }

    internal void RevealWorkspaceComponent(string spaceId, string itemId) =>
        ((DesktopWorkspacePanel)_desktopPages[DesktopPage.Workspace]).RevealComponent(spaceId, itemId);

    internal object GetDesktopSettings() => new
    {
        language = _configuration.Language, runAtLogin = _configuration.RunAtLogin, mcpEnabled = _configuration.McpEnabled
    };

    internal object SetDesktopSettings(string? language, bool? runAtLogin, bool? mcpEnabled)
    {
        if (language is not null)
            _language.SelectedIndex = language switch { "auto" => 0, "en" => 1, "zh" => 2, _ => throw new ArgumentException("Language must be auto, en or zh.") };
        if (runAtLogin is bool start) _runAtLogin.Checked = start;
        if (mcpEnabled is bool enabled) _enableMcp.Checked = enabled;
        return GetDesktopSettings();
    }

    private void AddDesktopSettings(TableLayoutPanel body)
    {
        if (_desktop is null) return;
        var copyCredential = UiTheme.CreateButton("Copy MCP credential", "复制 MCP 凭据");
        copyCredential.Click += (_, _) =>
        {
            try { Clipboard.SetText(_desktop.McpCredential); }
            catch (Exception error) { ShowGlobalError(error); }
        };
        body.Controls.Add(copyCredential);
        AddSection(body, "Relay & pairing", "中转服务器与配对", "", "");
        body.Controls.Add(new DesktopRelaySettingsPanel(_desktop));
    }
}
