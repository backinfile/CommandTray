using System.Diagnostics;
using System.Drawing.Drawing2D;
using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray;

// THESIS: Service operations lead; configuration and application preferences have clear homes.
// OWN-WORLD: Cool white workspace, blue actions, native fields, Segoe UI, semantic state labels.
// STORY: Find a service, understand its state, act, then edit and save with explicit feedback.
// FIRST VIEWPORT: Service rail; title/status and actions; tabbed editor; persistent save bar.
// FORM: Windows service workspace, pinned by the confirmed desktop scope and language support.
public sealed class SettingsForm : Form
{
    private readonly AppConfiguration _configuration;
    private readonly ConfigurationStore _store;
    private readonly ProcessSupervisor _supervisor;
    private readonly ToolTip _fieldHelp = new() { AutoPopDelay = 12000, InitialDelay = 400 };
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly TextBox _search = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly ListBox _services = new()
    {
        Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed,
        BorderStyle = BorderStyle.None, BackColor = UiTheme.RailBack, DisplayMember = nameof(ServiceDefinition.Name)
    };
    private readonly Label _summary = UiTheme.CreateMutedLabel("");
    private readonly Label _noResults = UiTheme.CreateMutedLabel("No matching services", "没有匹配的服务");
    private readonly Button _add = UiTheme.CreatePrimaryButton("+ Add service", "+ 添加服务");
    private readonly Button _settings = UiTheme.CreateButton("Application settings", "应用设置");
    private readonly Button _delete = UiTheme.CreateQuietButton("Delete…", "删除…", danger: true);
    private readonly Label _title = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Font = UiTheme.Header };
    private readonly StateBadge _badge = new();
    private readonly Label _stateDetail = UiTheme.CreateMutedLabel("");
    private readonly Button _start = UiTheme.CreatePrimaryButton("Start", "启动");
    private readonly Button _stop = UiTheme.CreatePrimaryButton("Stop", "停止");
    private readonly Button _restartButton = UiTheme.CreateButton("Restart", "重启");
    private readonly Button _logs = UiTheme.CreateQuietButton("Logs", "查看日志");
    private readonly Button _openUrl = UiTheme.CreateQuietButton("Website", "打开网页");
    private readonly Panel _workspace = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = Padding.Empty };
    private readonly TableLayoutPanel _editor = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
    private readonly Panel _empty = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
    private readonly Panel _global = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, AutoScroll = true };
    private readonly Button _clearSearch = UiTheme.CreateButton("Clear search", "清除搜索");
    private readonly WorkspaceTabs _tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new(), _cwd = new(), _url = new(), _stopCommand = new();
    private readonly TextBox _command = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 80, Font = UiTheme.Monospace
    };
    private readonly TextBox _environment = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Height = 230, Font = UiTheme.Monospace, AcceptsReturn = true
    };
    private readonly CheckBox _startWithManager = new() { AutoSize = true };
    private readonly CheckBox _restart = new() { AutoSize = true };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 300, Value = 5, Width = 100 };
    private readonly Button _save = UiTheme.CreatePrimaryButton("Save changes", "保存修改");
    private readonly Button _reset = UiTheme.CreateQuietButton("Discard changes", "撤销修改");
    private readonly Label _feedback = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Font = UiTheme.Small, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToggleSwitch _enableMcp = new();
    private readonly Label _mcpStatus = UiTheme.CreateMutedLabel("");
    private readonly TextBox _mcpEndpoint = new()
    {
        ReadOnly = true, Text = McpServerHost.Endpoint, Font = UiTheme.Monospace, BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Button _copyMcpEndpoint = UiTheme.CreateButton("Copy address", "复制地址");
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox _runAtLogin = new() { AutoSize = true };
    private readonly Label _globalFeedback = UiTheme.CreateMutedLabel("");
    private readonly List<(Control Control, string English, string Chinese)> _help = [];
    private readonly DpiFontScope _dpiFonts;
    private ServiceDefinition? _editing;
    private ServiceDefinition? _draft;
    private bool _dirty, _loading, _refreshing, _allowExit, _updatingPreferences, _showingSettings, _busy;
    private string? _feedbackEnglish, _feedbackChinese;
    private Button? _pendingAction;
    private Guid? _pendingServiceId;
    private bool _mcpCopied;

    public event EventHandler? ConfigurationChanged;
    public event EventHandler? McpEnabledChanged;

    public SettingsForm(AppConfiguration configuration, ConfigurationStore store, ProcessSupervisor supervisor)
    {
        _configuration = configuration; _store = store; _supervisor = supervisor;
        Localizer.Bind(this, "CommandTray · Service manager", "CommandTray · 服务管理");
        Font = UiTheme.Regular; ForeColor = UiTheme.Text; BackColor = UiTheme.Surface;
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(980, 660); Size = new Size(1180, 860);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        _errors.ContainerControl = this;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildRail(), 0, 0);
        BuildEditor(); BuildEmpty(); BuildGlobalSettings();
        _workspace.Controls.AddRange([_editor, _empty, _global]);
        root.Controls.Add(_workspace, 1, 0); Controls.Add(root);
        _dpiFonts = new DpiFontScope(this, () =>
        {
            _tabs.PerformLayout();
            _services.ItemHeight = (int)(68 * DeviceDpi / 96f);
            UpdateActionWidths();
            UpdateMcpPresentation();
            _editor.PerformLayout();
        });
        AcceptButton = _save;
        _search.TextChanged += (_, _) => RefreshServiceList();
        _services.SelectedIndexChanged += (_, _) => SelectService();
        _services.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter) { SelectService(activate: true); args.Handled = args.SuppressKeyPress = true; }
        };
        _services.MouseClick += (_, _) =>
        {
            if (_showingSettings && _services.SelectedItem is ServiceDefinition selected && selected.Id == _editing?.Id)
            {
                _showingSettings = false; UpdateView();
            }
        };
        _services.DrawItem += DrawServiceItem;
        _add.Click += (_, _) => AddService();
        _delete.Click += (_, _) => DeleteSelected();
        _save.Click += (_, _) => SaveSelected();
        _reset.Click += (_, _) => DiscardChanges();
        _settings.Click += (_, _) => { SyncPreferences(); _showingSettings = true; UpdateView(); };
        _start.Click += async (_, _) => await RunSelected(_supervisor.StartAsync, saveFirst: true, _start);
        _stop.Click += async (_, _) => await RunSelected(_supervisor.StopAsync, saveFirst: false, _stop);
        _restartButton.Click += async (_, _) => await RunSelected(_supervisor.RestartAsync, saveFirst: true, _restartButton);
        _logs.Click += (_, _) => ViewLog();
        _openUrl.Click += (_, _) => OpenWebsite();
        foreach (var field in new TextBox[] { _name, _command, _cwd, _url, _stopCommand, _environment })
            field.TextChanged += (_, _) => MarkDirty();
        _timeout.ValueChanged += (_, _) => MarkDirty();
        _startWithManager.CheckedChanged += (_, _) => MarkDirty();
        _restart.CheckedChanged += (_, _) => MarkDirty();
        _enableMcp.CheckedChanged += (_, _) => SaveMcpPreference();
        _copyMcpEndpoint.Click += (_, _) => CopyMcpEndpoint();
        _language.SelectedIndexChanged += (_, _) => SaveLanguage();
        _runAtLogin.CheckedChanged += (_, _) => SaveAutoStart();
        _supervisor.StateChanged += SupervisorStateChanged;
        Localizer.LanguageChanged += LanguageChanged;
        SyncPreferences(); RefreshServiceList(); UpdateLanguage();
    }

    private Control BuildRail()
    {
        var rail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, BackColor = UiTheme.RailBack,
            Padding = new Padding(18, 24, 18, 18), Margin = Padding.Empty
        };
        rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 8; row++) rail.RowStyles.Add(new RowStyle(row == 5 ? SizeType.Percent : SizeType.AutoSize, row == 5 ? 100 : 0));
        var brand = new Label { Text = "CommandTray", Font = UiTheme.Title, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        var tagline = UiTheme.CreateMutedLabel("Your local services, within reach", "让本地服务触手可及");
        tagline.Margin = new Padding(0, 0, 0, 24);
        _add.Dock = DockStyle.Top; _add.Margin = new Padding(0, 0, 0, 22);
        _summary.Margin = new Padding(0, 0, 0, 10);
        _search.Dock = DockStyle.Top; _search.Margin = new Padding(0, 0, 0, 12);
        var listHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _noResults.Dock = DockStyle.Top; _noResults.Padding = new Padding(4, 12, 4, 0);
        listHost.Controls.Add(_services); listHost.Controls.Add(_noResults);
        _settings.Dock = DockStyle.Top; _settings.Margin = new Padding(0, 16, 0, 10);
        var trayHint = UiTheme.CreateMutedLabel("Closing this window keeps services in the tray.", "关闭窗口后，服务仍在托盘中运行。");
        trayHint.AutoSize = false; trayHint.Dock = DockStyle.Fill; trayHint.Height = 42;
        trayHint.Margin = Padding.Empty;
        rail.Controls.Add(brand, 0, 0); rail.Controls.Add(tagline, 0, 1);
        rail.Controls.Add(_add, 0, 2); rail.Controls.Add(_summary, 0, 3);
        rail.Controls.Add(_search, 0, 4); rail.Controls.Add(listHost, 0, 5);
        rail.Controls.Add(_settings, 0, 6); rail.Controls.Add(trayHint, 0, 7);
        var wrapper = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        wrapper.Controls.Add(rail); wrapper.Controls.Add(UiTheme.CreateSeparator(DockStyle.Right));
        return wrapper;
    }

    private void BuildEditor()
    {
        _editor.Margin = Padding.Empty;
        _editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(28, 24, 28, 18), Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var titleRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 2, Margin = Padding.Empty };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _title.Margin = Padding.Empty; _badge.Anchor = AnchorStyles.Right; _badge.Margin = Padding.Empty;
        titleRow.Controls.Add(_title, 0, 0); titleRow.Controls.Add(_badge, 1, 0);
        _stateDetail.AutoSize = false; _stateDetail.AutoEllipsis = true; _stateDetail.Dock = DockStyle.Top;
        _stateDetail.Height = 24; _stateDetail.Margin = new Padding(0, 2, 0, 14);
        var actionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var actions = Flow(_start, _stop, _restartButton, _logs, _openUrl);
        actions.WrapContents = true; actions.Dock = DockStyle.Fill;
        foreach (Control action in actions.Controls) action.Margin = new Padding(0, 0, 8, 4);
        _restartButton.Margin = new Padding(0, 0, 16, 4);
        // An auto-sized percentage column measured the toolbar at its minimum
        // width and reserved five rows even when every action fit on one row.
        actions.Layout += (_, _) =>
        {
            var rowWidth = 0;
            var rowHeight = 0;
            var height = 0;
            foreach (Control action in actions.Controls)
            {
                if (!action.Visible) continue;
                var size = action.GetPreferredSize(Size.Empty);
                var width = size.Width + action.Margin.Horizontal;
                if (rowWidth > 0 && rowWidth + width > actions.ClientSize.Width)
                {
                    height += rowHeight; rowWidth = rowHeight = 0;
                }
                rowWidth += width; rowHeight = Math.Max(rowHeight, size.Height + action.Margin.Vertical);
            }
            height = Math.Max((int)(44 * DeviceDpi / 96f), height + rowHeight);
            if (header.RowStyles[2].Height != height)
            {
                header.RowStyles[2].Height = height;
                // Reflow the parent after the current child layout finishes;
                // TableLayoutPanel otherwise caches the previous header size.
                PostToUi(() => _editor.PerformLayout());
            }
        };
        _delete.Margin = Padding.Empty; _delete.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        actionRow.Controls.Add(actions, 0, 0); actionRow.Controls.Add(_delete, 1, 0);
        header.Controls.Add(titleRow, 0, 0); header.Controls.Add(_stateDetail, 0, 1); header.Controls.Add(actionRow, 0, 2);
        _editor.Controls.Add(header, 0, 0);

        _tabs.Margin = new Padding(28, 0, 28, 16);
        var basic = AddTab("Configuration", "基本配置");
        AddSection(basic, "Service configuration", "服务配置", "The command and location used to run this service.", "设置服务的启动命令与运行位置。");
        AddField(basic, "Service name", "服务名称", _name);
        AddField(basic, "Start command", "启动命令", _command, "Run through CMD, just as you would in a terminal.", "通过 CMD 执行，与终端中运行命令的方式一致。");
        AddField(basic, "Working directory", "工作目录", WithBrowse(_cwd), "Optional. Leave blank to use CommandTray's current directory.", "可选；留空则使用 CommandTray 当前目录。");
        AddField(basic, "Website URL", "网页地址", _url, "Optional HTTP/HTTPS address for the Website action.", "可选的 HTTP/HTTPS 地址，可通过“打开网页”访问。");
        var behavior = AddTab("Startup & shutdown", "启动与停止");
        AddSection(behavior, "Automatic behavior", "自动运行", "Control how this service starts and recovers.", "设置服务的自动启动与异常恢复行为。");
        Localizer.Bind(_startWithManager, "Start with CommandTray", "随 CommandTray 启动");
        Localizer.Bind(_restart, "Restart after an unexpected exit", "异常退出后自动重启");
        AddOption(behavior, _startWithManager, "Start this service when the manager launches.", "管理器启动时，自动启动此服务。");
        AddOption(behavior, _restart, "Manual stops never trigger automatic restart.", "手动停止服务不会触发自动重启。");
        AddSection(behavior, "Graceful shutdown", "正常停止", "Give the service a chance to exit before ending its process tree.", "先等待服务正常退出，超时后再终止所属进程树。");
        AddField(behavior, "Stop command", "停止命令", _stopCommand, "Optional. Use ${PID} to insert the service process ID.", "可选；使用 ${PID} 代入服务进程 ID。");
        var timeoutRow = Flow(_timeout, UiTheme.CreateLabel("seconds", "秒"));
        AddField(behavior, "Stop timeout", "停止超时", timeoutRow, "Between 1 and 300 seconds.", "可设置为 1–300 秒。");
        var environment = AddTab("Environment", "环境变量");
        AddSection(environment, "Environment variables", "环境变量", "One NAME=value entry per line. These values are passed to the service.", "每行填写一项 NAME=value，运行时传递给服务。");
        AddField(environment, "Variables", "变量", _environment);
        var secretHint = UiTheme.CreateMutedLabel("Prefix secret names with *. Example: *API_KEY=value\nSecret values are encrypted for your Windows account and omitted from exports.", "敏感变量名前加 *，例如：*API_KEY=value\n敏感值使用当前 Windows 账户加密，导出时会移除。");
        secretHint.Dock = DockStyle.Top; secretHint.Margin = new Padding(0, 8, 0, 8);
        environment.Controls.Add(secretHint);
        _editor.Controls.Add(_tabs, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            Padding = new Padding(28, 14, 28, 18), BackColor = UiTheme.RailBack, Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _feedback.Margin = new Padding(0, 0, 12, 0); _feedback.MinimumSize = new Size(0, 44);
        var saveActions = Flow(_reset, _save); _save.Margin = Padding.Empty;
        footer.Controls.Add(_feedback, 0, 0); footer.Controls.Add(saveActions, 1, 0);
        _editor.Controls.Add(footer, 0, 2);
    }

    private TableLayoutPanel AddTab(string english, string chinese)
    {
        var tab = _tabs.AddPage(english, chinese);
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tab.Controls.Add(body);
        return body;
    }

    private void BuildEmpty()
    {
        var center = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 4 };
        var title = UiTheme.CreateLabel("Your services start here", "从这里管理你的服务", UiTheme.Header);
        var text = UiTheme.CreateMutedLabel("Add a command-line service, then start it with one click.\nSelect an existing service on the left to configure it.", "添加一个命令行服务，即可一键启动。\n已有服务？在左侧选择并管理它。");
        var button = UiTheme.CreatePrimaryButton("+ Add service", "+ 添加服务");
        button.Click += (_, _) => AddService();
        _clearSearch.Click += (_, _) => _search.Clear();
        center.Controls.Add(title); center.Controls.Add(text); center.Controls.Add(button); center.Controls.Add(_clearSearch);
        foreach (Control control in center.Controls) control.Margin = new Padding(0, 0, 0, 16);
        _empty.Controls.Add(center);
        _empty.Resize += (_, _) => center.Location = new Point(Math.Max(24, (_empty.Width - center.Width) / 2), Math.Max(24, (_empty.Height - center.Height) / 2));
    }

    private void BuildGlobalSettings()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(32, 28, 32, 28)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var back = UiTheme.CreateButton("Back to services", "返回服务");
        back.Click += (_, _) => { _showingSettings = false; UpdateView(); };
        body.Controls.Add(back);
        var title = UiTheme.CreateLabel("Application settings", "应用设置", UiTheme.Header);
        title.Margin = new Padding(0, 22, 0, 20); body.Controls.Add(title);
        AddSection(body, "Preferences", "偏好设置", "Changes here take effect immediately.", "这里的修改会立即生效。");
        _language.Items.AddRange(["Follow system / 跟随系统", "English", "简体中文"]);
        AddField(body, "Display language", "显示语言", _language);
        Localizer.Bind(_runAtLogin, "Start CommandTray with Windows", "登录 Windows 时启动 CommandTray");
        AddOption(body, _runAtLogin, "Keep your services available from the notification area.", "让服务管理器随时出现在系统托盘中。");
        AddSection(body, "AI connection · MCP", "AI 连接 · MCP", "Allow trusted AI clients on this computer to manage your services.", "允许本机受信任的 AI 客户端管理服务。");
        // FlowLayoutPanel top-aligned three different control heights. Give
        // the setting its own text column and a centered, full-height toggle.
        var mcpRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(0, 0, 0, 20)
        };
        mcpRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mcpRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var mcpText = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        mcpText.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var mcpLabel = UiTheme.CreateLabel("Enable local MCP server", "启用本地 MCP 服务", UiTheme.Bold);
        mcpLabel.Dock = DockStyle.Top; mcpLabel.Margin = new Padding(0, 0, 0, 4); mcpLabel.Cursor = Cursors.Hand;
        mcpLabel.Click += (_, _) => { if (_enableMcp.Enabled) _enableMcp.Checked = !_enableMcp.Checked; };
        _mcpStatus.Dock = DockStyle.Top; _mcpStatus.Margin = Padding.Empty;
        mcpText.Controls.Add(mcpLabel); mcpText.Controls.Add(_mcpStatus);
        _enableMcp.Anchor = AnchorStyles.Right; _enableMcp.Margin = new Padding(20, 0, 0, 0);
        mcpRow.Controls.Add(mcpText, 0, 0); mcpRow.Controls.Add(_enableMcp, 1, 0);
        body.Controls.Add(mcpRow);
        var endpointLabel = UiTheme.CreateLabel("Connection address", "连接地址");
        endpointLabel.Margin = new Padding(0, 0, 0, 6); body.Controls.Add(endpointLabel);
        var endpointRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
        endpointRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        endpointRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _mcpEndpoint.Anchor = AnchorStyles.Left | AnchorStyles.Right; _mcpEndpoint.Margin = new Padding(0, 0, 12, 0);
        _mcpEndpoint.BackColor = UiTheme.RailBack; _mcpEndpoint.ForeColor = UiTheme.Text;
        _copyMcpEndpoint.Anchor = AnchorStyles.Right; _copyMcpEndpoint.Margin = Padding.Empty;
        endpointRow.Controls.Add(_mcpEndpoint, 0, 0); endpointRow.Controls.Add(_copyMcpEndpoint, 1, 0);
        body.Controls.Add(endpointRow);
        var mcpHint = UiTheme.CreateMutedLabel("Listens on 127.0.0.1 only. Connect clients you trust; they can run your configured commands.", "仅监听 127.0.0.1。请只连接可信客户端；客户端能够执行已配置的命令。");
        mcpHint.Dock = DockStyle.Top; mcpHint.Margin = new Padding(0, 0, 0, 12); body.Controls.Add(mcpHint);
        AddSection(body, "Configuration files", "配置文件", "Import replaces your service list. Exports do not include secret values.", "导入会替换服务列表；导出的配置不包含敏感值。");
        var import = UiTheme.CreateButton("Import configuration…", "导入配置…");
        var export = UiTheme.CreateButton("Export configuration…", "导出配置…");
        import.Click += (_, _) => ImportConfiguration(); export.Click += (_, _) => ExportConfiguration();
        body.Controls.Add(Flow(import, export));
        _globalFeedback.Margin = new Padding(0, 18, 0, 0); body.Controls.Add(_globalFeedback);
        _global.Controls.Add(body);
    }

    private void AddSection(TableLayoutPanel body, string english, string chinese, string hintEnglish, string hintChinese)
    {
        var heading = UiTheme.CreateLabel(english, chinese, UiTheme.Title);
        heading.Margin = new Padding(0, body.Controls.Count == 0 ? 0 : 20, 0, 5);
        body.Controls.Add(heading);
        var hint = UiTheme.CreateMutedLabel(hintEnglish, hintChinese);
        hint.Margin = new Padding(0, 0, 0, 18); body.Controls.Add(hint);
    }

    private void AddField(TableLayoutPanel body, string english, string chinese, Control control,
        string? hintEnglish = null, string? hintChinese = null)
    {
        var label = UiTheme.CreateLabel(english, chinese, UiTheme.Bold);
        label.Margin = new Padding(0, 0, 0, 6); body.Controls.Add(label);
        control.Dock = control is NumericUpDown or ComboBox ? DockStyle.None : DockStyle.Top;
        control.Margin = new Padding(0, 0, 12, hintEnglish is null ? 20 : 6);
        control.AccessibleName = Localizer.T(english, chinese);
        label.TextChanged += (_, _) => control.AccessibleName = label.Text;
        _help.Add((control, english, chinese)); body.Controls.Add(control);
        if (hintEnglish is not null && hintChinese is not null)
        {
            var hint = UiTheme.CreateMutedLabel(hintEnglish, hintChinese);
            hint.Margin = new Padding(0, 0, 0, 18); body.Controls.Add(hint);
            _help.Add((control, hintEnglish, hintChinese));
        }
    }

    private void AddOption(TableLayoutPanel body, CheckBox option, string english, string chinese)
    {
        option.Margin = new Padding(0, 0, 0, 4); body.Controls.Add(option);
        var hint = UiTheme.CreateMutedLabel(english, chinese);
        hint.Margin = new Padding(22, 0, 0, 16); body.Controls.Add(hint);
    }

    private Control WithBrowse(TextBox field)
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.Dock = DockStyle.Fill; field.Margin = new Padding(0, 4, 10, 0);
        var browse = UiTheme.CreateCompactButton("Browse…", "浏览…");
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = Localizer.T("Select working directory", "选择工作目录") };
            if (Directory.Exists(field.Text)) dialog.InitialDirectory = field.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.SelectedPath;
        };
        row.Controls.Add(field, 0, 0); row.Controls.Add(browse, 1, 0);
        return row;
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        flow.Controls.AddRange(controls); return flow;
    }

    private void DrawServiceItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _services.Items[e.Index] is not ServiceDefinition service) return;
        var state = _supervisor.GetSnapshot(service);
        var selected = e.State.HasFlag(DrawItemState.Selected);
        var scale = DeviceDpi / 96f;
        int S(int value) => (int)(value * scale);
        using var back = new SolidBrush(UiTheme.RailBack);
        e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
        {
            var bounds = e.Bounds; bounds.Inflate(-1, -S(3));
            using var shape = UiTheme.RoundedRectangle(bounds, S(6));
            using var fill = new SolidBrush(UiTheme.SelectedBack); e.Graphics.FillPath(fill, shape);
        }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var dot = new SolidBrush(UiTheme.StateColor(state.State));
        e.Graphics.FillEllipse(dot, e.Bounds.X + S(12), e.Bounds.Y + S(19), S(8), S(8));
        var x = e.Bounds.X + S(32);
        var width = Math.Max(0, e.Bounds.Right - x - S(10));
        using var nameFont = new Font(_services.Font, FontStyle.Bold);
        using var detailFont = new Font(_services.Font.FontFamily, _services.Font.Size * .9f);
        var name = new Rectangle(x, e.Bounds.Y + S(10), width, nameFont.Height + S(2));
        var detail = new Rectangle(x, name.Bottom + S(3), width, detailFont.Height + S(2));
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, service.Name, nameFont, name, UiTheme.Text, flags);
        var status = ReferenceEquals(service, _draft) ? Localizer.T("New · not saved", "新服务 · 未保存") : UiTheme.StateText(state.State);
        if (state.ProcessId is int pid) status += $" · PID {pid}";
        TextRenderer.DrawText(e.Graphics, status, detailFont, detail, UiTheme.MutedText, flags);
        if (e.State.HasFlag(DrawItemState.Focus)) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds);
    }

    private void SupervisorStateChanged(object? sender, ServiceSnapshot snapshot)
    {
        PostToUi(() => { ApplySnapshot(snapshot); _services.Invalidate(); UpdateSummary(); });
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed && !Disposing) action(); }); }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
        {
            // A supervisor callback can race with window destruction during
            // shutdown; losing the final paint must not fault the service.
        }
    }

    public void AllowExit() => _allowExit = true;

    public void RefreshFromExternalChange()
    {
        if (InvokeRequired) { PostToUi(RefreshFromExternalChange); return; }
        if (IsDisposed) return;
        ServiceDefinition? current;
        lock (_configuration) current = _configuration.Services.FirstOrDefault(service => service.Id == _editing?.Id);
        // External MCP changes must not silently overwrite text being edited.
        if (!_dirty) { _editing = current; LoadEditor(); }
        else if (_draft is null && current is null)
        {
            _draft = _editing;
            SetFeedback("This service was removed externally. Save to add it again.", "此服务已被外部删除。保存可重新添加。", true);
        }
        RefreshServiceList(); SyncPreferences(); UpdateView();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _supervisor.StateChanged -= SupervisorStateChanged; Localizer.LanguageChanged -= LanguageChanged;
            _dpiFonts.Dispose(); _fieldHelp.Dispose(); _errors.Dispose(); Icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_showingSettings && _editing is not null && keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab))
        {
            _tabs.Cycle(keyData.HasFlag(Keys.Shift) ? -1 : 1); return true;
        }
        if (keyData == (Keys.Control | Keys.S) && !_showingSettings) { SaveSelected(); return true; }
        if (keyData == (Keys.Control | Keys.N)) { AddService(); return true; }
        if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); _search.SelectAll(); return true; }
        if (keyData == (Keys.Control | Keys.L)) { ViewLog(); return true; }
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RefreshServiceList()
    {
        List<ServiceDefinition> items;
        lock (_configuration) items = _configuration.Services.ToList();
        if (_draft is not null) items.Add(_draft);
        var filter = _search.Text.Trim();
        items = items.Where(service => service.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _refreshing = true;
        try
        {
            _services.DataSource = null; _services.DataSource = items;
            _services.SelectedItem = items.FirstOrDefault(service => service.Id == _editing?.Id);
            if (_editing is null && items.Count > 0) { _editing = items[0]; _services.SelectedItem = _editing; LoadEditor(); }
        }
        finally { _refreshing = false; }
        _noResults.Visible = items.Count == 0;
        UpdateSummary(); UpdateView();
    }

    private void SelectService(bool activate = false)
    {
        if (_refreshing || _services.SelectedItem is not ServiceDefinition next) return;
        if (next.Id == _editing?.Id)
        {
            // DPI changes recreate the native list handle and re-announce its
            // selection. Only an explicit click/Enter should leave settings.
            if (activate) { _showingSettings = false; UpdateView(); }
            return;
        }
        if (!ResolveEdits())
        {
            _refreshing = true; _services.SelectedItem = _editing; _refreshing = false; return;
        }
        _draft = null; _editing = next; _showingSettings = false; LoadEditor(); RefreshServiceList();
    }

    private void UpdateSummary()
    {
        ServiceDefinition[] items;
        lock (_configuration) items = _configuration.Services.ToArray();
        var running = items.Count(service => _supervisor.GetSnapshot(service).State == ServiceState.Running);
        _summary.Text = Localizer.T($"{items.Length} services · {running} running", $"{items.Length} 个服务 · {running} 个运行中");
        _noResults.Text = items.Length == 0 && _draft is null && _search.Text.Length == 0
            ? Localizer.T("No services yet", "还没有服务")
            : Localizer.T("No matching services", "没有匹配的服务");
    }

    private void UpdateView()
    {
        _global.Visible = _showingSettings;
        _editor.Visible = !_showingSettings && _editing is not null;
        _empty.Visible = !_showingSettings && _editing is null;
        _clearSearch.Visible = _search.Text.Length > 0;
        if (_showingSettings) _global.BringToFront();
        else if (_editing is not null) _editor.BringToFront();
        else _empty.BringToFront();
        _save.Enabled = _reset.Enabled = _dirty && !_busy;
        AcceptButton = _showingSettings ? null : _save;
        ((WorkspaceButton)_settings).Selected = _showingSettings;
    }

    private void LoadEditor()
    {
        _loading = true;
        try
        {
            _errors.Clear();
            if (_editing is not { } item) return;
            _title.Text = item.Name; _name.Text = item.Name; _command.Text = item.Command;
            _cwd.Text = item.WorkingDirectory; _url.Text = item.OpenUrl; _stopCommand.Text = item.StopCommand;
            _timeout.Value = Math.Clamp(item.StopTimeoutSeconds, 1, 300);
            _startWithManager.Checked = item.StartWithManager; _restart.Checked = item.RestartOnUnexpectedExit;
            _environment.Lines = item.Environment.Select(variable => $"{(variable.IsSecret ? "*" : "")}{variable.Name}={variable.Value}").ToArray();
            _dirty = ReferenceEquals(item, _draft);
            SetFeedback(_dirty ? "New service · fill in a command, then save" : "All changes saved · Ctrl+S to save",
                _dirty ? "新服务 · 填写启动命令后保存" : "所有修改已保存 · Ctrl+S 保存");
            ApplySnapshot(_supervisor.GetSnapshot(item));
        }
        finally { _loading = false; UpdateView(); }
    }

    private void MarkDirty()
    {
        if (_loading || _editing is null) return;
        _dirty = true;
        SetFeedback("Unsaved changes", "有未保存的修改"); UpdateView();
    }

    private void SetFeedback(string english, string chinese, bool error = false)
    {
        _feedbackEnglish = english; _feedbackChinese = chinese;
        _feedback.Text = Localizer.T(english, chinese);
        _feedback.ForeColor = error ? UiTheme.StateColor(ServiceState.Faulted) : UiTheme.MutedText;
        _fieldHelp.SetToolTip(_feedback, _feedback.Text);
    }

    private bool SaveSelected()
    {
        if (_editing is not { } item || _busy) return false;
        _errors.Clear();
        Control? invalid = null;
        void Error(Control control, string english, string chinese)
        {
            _errors.SetError(control, Localizer.T(english, chinese)); invalid ??= control;
        }
        if (string.IsNullOrWhiteSpace(_name.Text)) Error(_name, "Enter a service name.", "请填写服务名称。");
        if (string.IsNullOrWhiteSpace(_command.Text)) Error(_command, "Enter a start command.", "请填写启动命令。");
        if (_url.Text.Trim() is { Length: > 0 } url && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            Error(_url, "Use an HTTP or HTTPS URL.", "请使用 HTTP 或 HTTPS 地址。");
        List<EnvironmentEntry> variables = [];
        try
        {
            variables = ServiceConfigurationEditor.ParseEnvironment(_environment.Lines);
            if (variables.Select(variable => variable.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variables.Count)
                Error(_environment, "Environment variable names must be unique.", "环境变量名称不能重复。");
        }
        catch (InvalidDataException ex) { Error(_environment, ex.Message, ex.Message); }
        if (invalid is not null)
        {
            _tabs.SelectedIndex = invalid == _environment ? 2 : 0;
            invalid.Focus(); SetFeedback("Check the marked fields before saving.", "请检查标记的字段后再保存。", true); return false;
        }
        var edited = new ServiceDefinition
        {
            Id = item.Id, Name = _name.Text.Trim(), Command = _command.Text.Trim(), WorkingDirectory = _cwd.Text.Trim(),
            OpenUrl = _url.Text.Trim(), StopCommand = _stopCommand.Text.Trim(), StopTimeoutSeconds = (int)_timeout.Value,
            StartWithManager = _startWithManager.Checked, RestartOnUnexpectedExit = _restart.Checked, Environment = variables
        };
        try
        {
            ServiceConfigurationEditor.Save(_configuration, _store, item, edited);
            _draft = null; _dirty = false; _title.Text = item.Name;
            SetFeedback("Changes saved · running services use them after restart", "修改已保存 · 运行中的服务重启后应用");
            RefreshServiceList(); ConfigurationChanged?.Invoke(this, EventArgs.Empty); return true;
        }
        catch (Exception ex) { SetFeedback($"Could not save: {ex.Message}", $"保存失败：{Localizer.ChineseError(ex.Message)}", true); return false; }
    }

    private bool ResolveEdits()
    {
        if (!_dirty) return true;
        var result = MessageBox.Show(this, Localizer.T("Save your changes before switching services?\nChoose No to discard them.", "切换服务前是否保存修改？\n选择“否”将放弃这些修改。"),
            "CommandTray", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes) return SaveSelected();
        // Choosing discard grants navigation; the destination must commit it.
        // Clearing dirty state here lost edits when an import was cancelled
        // or failed after this prompt.
        return true;
    }

    private void DiscardChanges()
    {
        if (_draft is not null) { _draft = null; _editing = null; _dirty = false; RefreshServiceList(); }
        else LoadEditor();
        UpdateView();
    }

    private void AddService()
    {
        if (_busy || !ResolveEdits()) return;
        _draft = new ServiceDefinition { Name = Localizer.T("New service", "新服务") };
        _editing = _draft; _showingSettings = false; _search.Clear();
        LoadEditor(); RefreshServiceList(); _tabs.SelectedIndex = 0; _name.Focus(); _name.SelectAll();
    }

    private void DeleteSelected()
    {
        if (_editing is not { } item || _busy) return;
        if (_supervisor.GetSnapshot(item).State is not (ServiceState.Stopped or ServiceState.Faulted))
        {
            SetFeedback("Stop this service before deleting it.", "请先停止服务，再删除。", true); return;
        }
        if (MessageBox.Show(this, Localizer.T($"Delete “{item.Name}” and its configuration?", $"删除“{item.Name}”及其配置？"), "CommandTray",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            lock (_configuration)
            {
                var proposed = new AppConfiguration
                {
                    Language = _configuration.Language, RunAtLogin = _configuration.RunAtLogin, McpEnabled = _configuration.McpEnabled,
                    Services = _configuration.Services.Where(service => service.Id != item.Id).ToList()
                };
                _store.Save(proposed); _configuration.Services = proposed.Services;
            }
            _draft = _editing = null; _dirty = false; RefreshServiceList(); ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { SetFeedback($"Could not delete: {ex.Message}", $"删除失败：{Localizer.ChineseError(ex.Message)}", true); }
    }

    private async Task RunSelected(Func<ServiceDefinition, CancellationToken, Task> operation, bool saveFirst, Button? action = null)
    {
        if (_editing is not { } item || _busy) return;
        // Never run an invalid or unpersisted edit after a failed save. Stopping
        // remains available even when the user's edited fields are incomplete.
        if (saveFirst && _dirty && !SaveSelected()) return;
        _busy = true; _pendingAction = action; _pendingServiceId = item.Id;
        UpdateView(); ApplySnapshot(_supervisor.GetSnapshot(item));
        try { await operation(item, CancellationToken.None); }
        catch (Exception ex)
        {
            if (!IsDisposed && _editing?.Id == item.Id)
                SetFeedback($"Operation failed: {ex.Message}", $"操作失败：{Localizer.ChineseError(ex.Message)}", true);
        }
        finally
        {
            _busy = false; _pendingAction = null; _pendingServiceId = null;
            if (!IsDisposed) { UpdateView(); if (_editing is not null) ApplySnapshot(_supervisor.GetSnapshot(_editing)); }
        }
    }

    private void ApplySnapshot(ServiceSnapshot snapshot)
    {
        if (_editing?.Id != snapshot.Id) return;
        _badge.SetState(snapshot.State);
        _stateDetail.Text = snapshot.Error is { Length: > 0 } error ? Localizer.T(error, Localizer.ChineseError(error)) : snapshot.ProcessId is int pid
            ? Localizer.T($"Process ID {pid} · managed by CommandTray", $"进程 ID {pid} · 由 CommandTray 管理")
            : snapshot.State is ServiceState.Starting or ServiceState.Stopping ? UiTheme.StateText(snapshot.State)
            : Localizer.T("Ready when you are · start the service to begin", "准备就绪 · 启动服务即可开始运行");
        _stateDetail.ForeColor = snapshot.Error is { Length: > 0 } ? UiTheme.StateColor(ServiceState.Faulted) : UiTheme.MutedText;
        _fieldHelp.SetToolTip(_stateDetail, _stateDetail.Text);
        var pending = _pendingServiceId == snapshot.Id ? _pendingAction : null;
        _start.Text = pending == _start || snapshot.State == ServiceState.Starting
            ? Localizer.T("Starting…", "启动中…") : Localizer.T("Start", "启动");
        _stop.Text = pending == _stop || snapshot.State == ServiceState.Stopping
            ? Localizer.T("Stopping…", "停止中…") : Localizer.T("Stop", "停止");
        _restartButton.Text = pending == _restartButton ? Localizer.T("Restarting…", "重启中…") : Localizer.T("Restart", "重启");
        // Keep the active command in place until completion; a status callback
        // must not replace a pressed Start button with Stop under the pointer.
        var showStart = pending == _start || pending != _stop && pending != _restartButton &&
            snapshot.State is ServiceState.Stopped or ServiceState.Faulted or ServiceState.Starting;
        _start.Visible = showStart; _stop.Visible = !showStart;
        _start.Enabled = !_busy && snapshot.State is ServiceState.Stopped or ServiceState.Faulted;
        _stop.Enabled = _restartButton.Enabled = !_busy && snapshot.State == ServiceState.Running;
        _delete.Enabled = !_busy && snapshot.State is ServiceState.Stopped or ServiceState.Faulted;
        _openUrl.Enabled = Uri.TryCreate(_editing.OpenUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        _fieldHelp.SetToolTip(_restartButton, Localizer.T("Restart the running service; saved changes take effect.", "重启运行中的服务，并应用已保存的修改。"));
        _fieldHelp.SetToolTip(_openUrl, _openUrl.Enabled ? _editing.OpenUrl : Localizer.T("Set a Website URL below and save to enable this action.", "在基本配置中填写网页地址并保存，即可使用。"));
        _fieldHelp.SetToolTip(_delete, Localizer.T("Delete this service's configuration. Stop the service first.", "删除此服务的配置；请先停止服务。"));
    }

    private void UpdateActionWidths()
    {
        var scale = DeviceDpi / 96f;
        int WidthFor(Button button, params string[] labels) => Math.Max((int)(88 * scale),
            labels.Max(label => TextRenderer.MeasureText(label, button.Font, Size.Empty, TextFormatFlags.SingleLine).Width) +
            button.Padding.Horizontal + (int)(4 * scale));
        // Reserve progress-label space up front so service callbacks do not
        // move adjacent targets while an operation is running.
        var width = WidthFor(_start, Localizer.T("Starting…", "启动中…"), Localizer.T("Stopping…", "停止中…"));
        _start.MinimumSize = _stop.MinimumSize = new Size(width, (int)(40 * scale));
        _restartButton.MinimumSize = new Size(WidthFor(_restartButton, Localizer.T("Restarting…", "重启中…")), (int)(40 * scale));
    }

    private void ViewLog()
    {
        if (_editing is not { } item) return;
        new LogViewerForm(item.Name, _supervisor.GetLogPath(item)).Show(this);
    }

    private void OpenWebsite()
    {
        if (_editing is not { } item || !Uri.TryCreate(item.OpenUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { SetFeedback($"Could not open website: {ex.Message}", $"无法打开网页：{ex.Message}", true); }
    }

    private void ImportConfiguration()
    {
        using var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = Localizer.T("Import configuration", "导入配置") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(this, Localizer.T("Replace the current service list with the imported configuration?", "使用导入的配置替换当前服务列表？"),
            "CommandTray", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            var imported = _store.Import(dialog.FileName);
            if (!ResolveEdits()) return;
            lock (_configuration)
            {
                if (_configuration.Services.Any(service => _supervisor.GetSnapshot(service).State is not (ServiceState.Stopped or ServiceState.Faulted)))
                    throw new InvalidOperationException(Localizer.T("Stop all services before importing.", "请先停止所有服务，再导入配置。"));
                imported.RunAtLogin = _configuration.RunAtLogin;
                _store.Save(imported);
                _configuration.Services = imported.Services; _configuration.Language = imported.Language; _configuration.McpEnabled = imported.McpEnabled;
            }
            _editing = _draft = null; _dirty = false; _search.Clear();
            SyncPreferences(); Localizer.Configure(_configuration.Language); RefreshServiceList();
            ConfigurationChanged?.Invoke(this, EventArgs.Empty); McpEnabledChanged?.Invoke(this, EventArgs.Empty);
            _globalFeedback.Text = Localizer.T("Configuration imported.", "配置已导入。");
        }
        catch (Exception ex) { ShowGlobalError(ex); }
    }

    private void ExportConfiguration()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "CommandTray-export.json", Title = Localizer.T("Export saved configuration", "导出已保存的配置") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            lock (_configuration) _store.ExportWithoutSecrets(_configuration, dialog.FileName);
            _globalFeedback.Text = Localizer.T("Saved configuration exported. Secret values were omitted.", "已导出保存的配置，敏感值已移除。");
        }
        catch (Exception ex) { ShowGlobalError(ex); }
    }

    private void SyncPreferences()
    {
        _updatingPreferences = true;
        try
        {
            _enableMcp.Checked = _configuration.McpEnabled;
            _language.SelectedIndex = _configuration.Language switch { "en" => 1, "zh" => 2, _ => 0 };
            _runAtLogin.Checked = AutoStartService.IsEnabled();
        }
        finally { _updatingPreferences = false; }
        UpdateMcpPresentation();
    }

    private void SaveMcpPreference()
    {
        if (_updatingPreferences) return;
        var previous = _configuration.McpEnabled;
        try
        {
            lock (_configuration) { _configuration.McpEnabled = _enableMcp.Checked; _store.Save(_configuration); }
            UpdateMcpPresentation(); McpEnabledChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { _configuration.McpEnabled = previous; SyncPreferences(); ShowGlobalError(ex); }
    }

    private void SaveLanguage()
    {
        if (_updatingPreferences) return;
        var previous = _configuration.Language;
        try
        {
            lock (_configuration) { _configuration.Language = _language.SelectedIndex switch { 1 => "en", 2 => "zh", _ => "auto" }; _store.Save(_configuration); }
            Localizer.Configure(_configuration.Language);
            _globalFeedback.Text = Localizer.T("Language updated.", "显示语言已更新。");
            _globalFeedback.ForeColor = UiTheme.MutedText;
        }
        catch (Exception ex) { _configuration.Language = previous; SyncPreferences(); ShowGlobalError(ex); }
    }

    private void SaveAutoStart()
    {
        if (_updatingPreferences) return;
        var previous = AutoStartService.IsEnabled();
        try
        {
            AutoStartService.SetEnabled(_runAtLogin.Checked);
            lock (_configuration) { _configuration.RunAtLogin = _runAtLogin.Checked; _store.Save(_configuration); }
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { AutoStartService.SetEnabled(previous); _configuration.RunAtLogin = previous; SyncPreferences(); ShowGlobalError(ex); }
    }

    private void UpdateMcpPresentation()
    {
        _mcpStatus.Text = _enableMcp.Checked ? Localizer.T("Enabled · local only", "已启用 · 仅本机") : Localizer.T("Disabled", "已关闭");
        _mcpStatus.ForeColor = _enableMcp.Checked ? UiTheme.StateColor(ServiceState.Running) : UiTheme.MutedText;
        var copy = Localizer.T("Copy address", "复制地址");
        var copied = Localizer.T("Copied", "已复制");
        var scale = DeviceDpi / 96f;
        var width = Math.Max(TextRenderer.MeasureText(copy, _copyMcpEndpoint.Font).Width,
            TextRenderer.MeasureText(copied, _copyMcpEndpoint.Font).Width) + _copyMcpEndpoint.Padding.Horizontal + (int)(4 * scale);
        _copyMcpEndpoint.MinimumSize = new Size(Math.Max((int)(88 * scale), width), (int)(40 * scale));
        _copyMcpEndpoint.Text = _mcpCopied ? copied : copy;
        _copyMcpEndpoint.Enabled = !_mcpCopied;
    }

    private async void CopyMcpEndpoint()
    {
        if (_mcpCopied) return;
        try
        {
            Clipboard.SetText(_mcpEndpoint.Text);
            _mcpCopied = true; UpdateMcpPresentation();
            await Task.Delay(1500);
            if (IsDisposed) return;
            _mcpCopied = false; UpdateMcpPresentation();
        }
        catch (Exception ex) { ShowGlobalError(ex); }
    }

    private void ShowGlobalError(Exception ex)
    {
        _globalFeedback.Text = Localizer.T($"Could not apply changes: {ex.Message}", $"无法应用修改：{Localizer.ChineseError(ex.Message)}");
        _globalFeedback.ForeColor = UiTheme.StateColor(ServiceState.Faulted);
    }

    private void LanguageChanged(object? sender, EventArgs e) => UpdateLanguage();

    private void UpdateLanguage()
    {
        UpdateActionWidths();
        _search.PlaceholderText = Localizer.T("Search services · Ctrl+F", "搜索服务 · Ctrl+F");
        _search.AccessibleName = Localizer.T("Search services", "搜索服务");
        _services.AccessibleName = Localizer.T("Services", "服务列表");
        _name.PlaceholderText = Localizer.T("For example, local API", "例如：本地 API 服务");
        _command.PlaceholderText = "npm run dev"; _url.PlaceholderText = "http://localhost:3000";
        _environment.PlaceholderText = "PORT=3000\r\n*API_KEY=value";
        _cwd.AccessibleName = Localizer.T("Working directory", "工作目录");
        _mcpEndpoint.AccessibleName = Localizer.T("MCP endpoint", "MCP 地址");
        _enableMcp.AccessibleName = Localizer.T("Enable MCP", "启用 MCP");
        foreach (var (control, english, chinese) in _help) _fieldHelp.SetToolTip(control, Localizer.T(english, chinese));
        if (_feedbackEnglish is not null && _feedbackChinese is not null) _feedback.Text = Localizer.T(_feedbackEnglish, _feedbackChinese);
        _services.ItemHeight = (int)(68 * DeviceDpi / 96f); _services.Invalidate();
        UpdateSummary(); UpdateMcpPresentation();
        if (_editing is not null) ApplySnapshot(_supervisor.GetSnapshot(_editing));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e); _services.ItemHeight = (int)(68 * DeviceDpi / 96f);
    }
}
