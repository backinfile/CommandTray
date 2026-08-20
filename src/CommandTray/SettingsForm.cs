using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray;

public sealed class SettingsForm : Form
{
    private readonly AppConfiguration _configuration;
    private readonly ConfigurationStore _store;
    private readonly ProcessSupervisor _supervisor;
    private readonly ToolTip _fieldHelp = new()
    {
        AutoPopDelay = 12000,
        InitialDelay = 450,
        ReshowDelay = 100,
        ShowAlways = true
    };

    // Left rail
    private readonly TextBox _search = new() { PlaceholderText = "Filter / 筛选…" };
    private readonly ListBox _services = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        DrawMode = DrawMode.OwnerDrawFixed,
        BorderStyle = BorderStyle.None,
        BackColor = UiTheme.RailBack
    };
    private readonly Button _add = UiTheme.CreatePrimaryButton("+ Add / 添加");
    private readonly Button _delete = UiTheme.CreateButton("Delete / 删除");
    private readonly CheckBox _enableMcp = new() { Text = "Enable MCP / 启用 MCP", AutoSize = true };

    // Content header
    private readonly Panel _contentPanel = new() { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
    private readonly Label _title = new() { AutoSize = true, Font = UiTheme.Header };
    private readonly StateBadge _badge = new() { Margin = new Padding(0, 3, 10, 0) };
    private readonly Label _stateDetail = new() { AutoSize = true, Font = UiTheme.Small, ForeColor = UiTheme.MutedText, Margin = new Padding(0, 6, 0, 0) };
    private readonly Button _start = UiTheme.CreateButton("Start / 启动");
    private readonly Button _stop = UiTheme.CreateButton("Stop / 停止");
    private readonly Button _restartButton = UiTheme.CreateButton("Restart / 重启");

    // Detail cards
    private Panel? _headerWrap;
    private readonly Panel _scrollHost = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = SystemColors.Window };
    private readonly TextBox _name = new(), _command = new(), _cwd = new(), _url = new(), _stopCommand = new();
    private readonly TextBox _environment = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 120, Font = UiTheme.Monospace };
    private readonly CheckBox _startWithManager = new() { Text = "Start with manager / 随管理器启动", AutoSize = true };
    private readonly CheckBox _restart = new() { Text = "Restart on unexpected exit / 异常退出重启", AutoSize = true };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 300, Value = 5, Width = 90 };

    // Footer
    private readonly Button _save = UiTheme.CreatePrimaryButton("Save / 保存");
    private readonly Button _logs = UiTheme.CreateButton("View log / 查看日志");
    private readonly Button _import = UiTheme.CreateButton("Import / 导入");
    private readonly Button _export = UiTheme.CreateButton("Export / 导出");

    // Empty state
    private readonly Panel _emptyPanel = new() { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
    private bool _allowExit;
    private bool _updatingMcpPreference;

    public event EventHandler? ConfigurationChanged;
    public event EventHandler? McpEnabledChanged;

    public SettingsForm(AppConfiguration configuration, ConfigurationStore store, ProcessSupervisor supervisor)
    {
        _configuration = configuration;
        _store = store;
        _supervisor = supervisor;
        Text = "CommandTray · Settings / 设置";
        Font = UiTheme.Regular;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(920, 640);
        Size = new Size(1180, 840);
        BackColor = SystemColors.Window;
        _enableMcp.Checked = _configuration.McpEnabled;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 268));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var rail = BuildRail();
        root.Controls.Add(rail, 0, 0);
        root.Controls.Add(BuildContent(), 1, 0);

        AcceptButton = _save;
        var closeHotkey = new Button { Size = Size.Empty, TabStop = false };
        closeHotkey.Click += (_, _) => Close();
        Controls.Add(closeHotkey);
        CancelButton = closeHotkey;

        _search.TextChanged += (_, _) => RefreshServiceList(Selected);
        _services.SelectedIndexChanged += (_, _) => LoadSelected();
        _add.Click += (_, _) => AddService();
        _delete.Click += (_, _) => DeleteSelected();
        _save.Click += (_, _) => SaveSelected();
        _start.Click += async (_, _) => await RunSelected(_supervisor.StartAsync);
        _stop.Click += async (_, _) => await RunSelected(_supervisor.StopAsync);
        _restartButton.Click += async (_, _) => await RunSelected(_supervisor.RestartAsync);
        _logs.Click += (_, _) => ViewLog();
        _import.Click += (_, _) => ImportConfiguration();
        _export.Click += (_, _) => ExportConfiguration();
        _enableMcp.CheckedChanged += (_, _) => SaveMcpPreference();
        _supervisor.StateChanged += (_, snapshot) => BeginInvoke(() =>
        {
            ApplySnapshot(snapshot);
            _services.Invalidate();
        });
        RefreshServiceList();
        UpdateEmptyState();
    }

    // ---------------------------------------------------------------- layout

    private Control BuildRail()
    {
        var rail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = UiTheme.RailBack,
            Padding = new Padding(14, 16, 14, 14),
            Margin = Padding.Empty
        };
        rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var brand = new Label { Text = "CommandTray", Font = UiTheme.Title, AutoSize = true, Margin = new Padding(2, 0, 0, 0) };
        var brandSub = UiTheme.CreateMutedLabel("Services / 服务");
        brandSub.Margin = new Padding(2, 0, 0, 12);

        _search.Dock = DockStyle.Top;
        _search.Margin = new Padding(0, 0, 0, 10);
        _search.BorderStyle = BorderStyle.FixedSingle;

        var mcpPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(2, 0, 0, 10)
        };
        var mcpEndpoint = UiTheme.CreateMutedLabel("AI control · 127.0.0.1:58629");
        mcpEndpoint.Margin = new Padding(20, 0, 0, 0);
        mcpPanel.Controls.Add(_enableMcp);
        mcpPanel.Controls.Add(mcpEndpoint);
        const string mcpHelp = "Starts a local MCP server for AI control at http://127.0.0.1:58629/mcp. Only apps on this computer can connect. / 启动供 AI 操作的本地 MCP 服务，仅本机应用可连接。";
        _fieldHelp.SetToolTip(_enableMcp, mcpHelp);
        _fieldHelp.SetToolTip(mcpEndpoint, mcpHelp);

        _services.Margin = Padding.Empty;
        _services.ItemHeight = (int)(Font.Height * 2.6);
        _services.DrawItem += DrawServiceItem;

        var railButtons = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        railButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        railButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        _add.Dock = DockStyle.Fill; _add.AutoSize = false; _add.Height = 32; _add.Margin = new Padding(0, 0, 4, 0);
        _delete.Dock = DockStyle.Fill; _delete.AutoSize = false; _delete.Height = 32; _delete.Margin = new Padding(4, 0, 0, 0);
        railButtons.Controls.Add(_add, 0, 0);
        railButtons.Controls.Add(_delete, 1, 0);

        rail.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // brand
        rail.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // sub
        rail.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // search
        rail.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // MCP
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // list
        rail.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons
        rail.Controls.Add(brand, 0, 0);
        rail.Controls.Add(brandSub, 0, 1);
        rail.Controls.Add(_search, 0, 2);
        rail.Controls.Add(mcpPanel, 0, 3);
        rail.Controls.Add(_services, 0, 4);
        rail.Controls.Add(railButtons, 0, 5);

        var wrapper = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        wrapper.Controls.Add(rail);
        wrapper.Controls.Add(UiTheme.CreateSeparator(DockStyle.Right));
        return wrapper;
    }

    private Control BuildContent()
    {
        // Header: title + badge + detail on the left, lifecycle buttons on the right.
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(20, 16, 20, 14),
            BackColor = SystemColors.Window
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titleStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
        var titleRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
        titleRow.Controls.Add(_badge);
        titleRow.Controls.Add(_title);
        titleStack.Controls.Add(titleRow);
        titleStack.Controls.Add(_stateDetail);
        header.Controls.Add(titleStack, 0, 0);

        var lifecycle = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0) };
        lifecycle.Controls.AddRange([_start, _stop, _restartButton]);
        header.Controls.Add(lifecycle, 1, 0);

        var headerWrap = new Panel { Dock = DockStyle.Top, AutoSize = true };
        headerWrap.Controls.Add(header);
        headerWrap.Controls.Add(UiTheme.CreateSeparator(DockStyle.Bottom));

        // Scrollable card stack.
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(20, 16, 20, 16)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.Controls.Add(CreateCard("Service / 服务", grid =>
        {
            AddRow(grid, "Name / 名称", _name,
                "The name shown in the service list and tray menu. / 显示在服务列表和托盘菜单中的名称。");
            AddRow(grid, "CMD command / 命令", _command,
                "The command CommandTray runs to start this service. / CommandTray 启动此服务时执行的命令。");
            AddRow(grid, "Working directory / 目录", WithBrowse(_cwd),
                "The folder used when running start and stop commands. Leave blank to use CommandTray's current folder. / 执行启动和停止命令时使用的目录；留空则使用 CommandTray 当前目录。");
            AddRow(grid, "Open URL / 网页", _url,
                "An optional HTTP or HTTPS address available from the tray menu while using this service. / 可选的 HTTP 或 HTTPS 地址，可从托盘菜单快速打开。");
        }));
        stack.Controls.Add(CreateCard("Shutdown / 停止", grid =>
        {
            AddRow(grid, "Stop command / 停止命令", _stopCommand,
                "An optional command run before CommandTray ends the process. Use ${PID} to insert the process ID. / 结束进程前可执行的命令；可用 ${PID} 代入进程 ID。");
            AddRow(grid, "Stop timeout / 超时秒", _timeout,
                "How many seconds to wait for a graceful stop before the process is ended. / 等待服务正常停止的秒数，超时后将结束进程。");

            var options = Stack(_startWithManager, _restart);
            AddRow(grid, "Options / 选项", options,
                "Controls automatic startup and recovery behavior for this service. / 控制此服务的自动启动和异常恢复行为。");
            _fieldHelp.SetToolTip(_startWithManager,
                "Start this service automatically when CommandTray starts. / CommandTray 启动时自动启动此服务。");
            _fieldHelp.SetToolTip(_restart,
                "Start the service again if it exits unexpectedly; a manual stop will not trigger a restart. / 服务意外退出时自动重启；手动停止不会触发重启。");
        }));
        stack.Controls.Add(CreateCard("Environment / 环境变量", grid =>
        {
            AddRow(grid, "", _environment,
                "Environment variables passed to the service, one NAME=value entry per line. Prefix a secret name with * so its stored value is protected. / 传给服务的环境变量，每行一项 NAME=value；敏感变量名前加 *，保存时会保护其值。");
            var hint = UiTheme.CreateMutedLabel("One NAME=value per line. Prefix secret names with * / 每行 NAME=value，敏感变量名前加 *");
            hint.Margin = new Padding(3, 4, 3, 0);
            AddRow(grid, "", hint);
        }));
        _scrollHost.Controls.Add(stack);

        // Footer action bar.
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(20, 10, 20, 12),
            BackColor = SystemColors.Window
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var leftHint = UiTheme.CreateMutedLabel("Changes are saved per service / 修改按服务保存");
        leftHint.Anchor = AnchorStyles.Left;
        leftHint.AutoEllipsis = false;
        leftHint.MaximumSize = Size.Empty;
        leftHint.Margin = new Padding(0, 8, 0, 0);
        var footerButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right };
        _save.Margin = new Padding(0);
        footerButtons.Controls.AddRange([_logs, _import, _export, _save]);
        footer.Controls.Add(leftHint, 0, 0);
        footer.Controls.Add(footerButtons, 1, 0);
        var footerWrap = new Panel { Dock = DockStyle.Bottom, AutoSize = true };
        footerWrap.Controls.Add(footer);
        footerWrap.Controls.Add(UiTheme.CreateSeparator(DockStyle.Top));

        // Empty state.
        var emptyStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.None
        };
        var emptyTitle = new Label { Text = "No service selected / 未选择服务", Font = UiTheme.Title, AutoSize = true, ForeColor = UiTheme.MutedText, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 0, 8), Anchor = AnchorStyles.None };
        var emptyBody = UiTheme.CreateMutedLabel("Select a service on the left, or click \"+ Add / 添加\" to create one.\r\n请在左侧选择服务，或点击 \"+ Add / 添加\" 新建。");
        emptyBody.TextAlign = ContentAlignment.MiddleCenter;
        emptyBody.Anchor = AnchorStyles.None;
        emptyStack.Controls.Add(emptyTitle);
        emptyStack.Controls.Add(emptyBody);
        _emptyPanel.Controls.Add(emptyStack);
        _emptyPanel.Resize += (_, _) => emptyStack.Location = new Point(
            Math.Max(0, (_emptyPanel.ClientSize.Width - emptyStack.Width) / 2),
            Math.Max(0, (_emptyPanel.ClientSize.Height - emptyStack.Height) / 2 - 30));
        _emptyPanel.Visible = false;

        _headerWrap = headerWrap;
        _contentPanel.Controls.Add(_emptyPanel);
        _contentPanel.Controls.Add(_scrollHost);
        _contentPanel.Controls.Add(headerWrap);
        _contentPanel.Controls.Add(footerWrap);
        return _contentPanel;
    }

    private void DrawServiceItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _services.Items.Count) return;
        if (_services.Items[e.Index] is not ServiceDefinition service) return;
        var snapshot = _supervisor.GetSnapshot(service);
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var focused = (e.State & DrawItemState.Focus) == DrawItemState.Focus &&
                      (e.State & DrawItemState.NoFocusRect) != DrawItemState.NoFocusRect;

        using (var back = new SolidBrush(selected ? UiTheme.SelectedBack : UiTheme.RailBack))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(UiTheme.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + 4, 3, e.Bounds.Height - 8);
        }

        var dotSize = Math.Max(8, Font.Height / 2 + 2);
        var dotY = e.Bounds.Y + (e.Bounds.Height - dotSize) / 2;
        var dotBounds = new Rectangle(e.Bounds.X + 12, dotY, dotSize, dotSize);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(UiTheme.StateColor(snapshot.State)))
            e.Graphics.FillEllipse(brush, dotBounds);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;

        var textX = dotBounds.Right + 10;
        var textWidth = Math.Max(0, e.Bounds.Right - textX - 10);
        var nameRect = new Rectangle(textX, e.Bounds.Y + 6, textWidth, Font.Height + 2);
        var stateRect = new Rectangle(textX, nameRect.Bottom + 1, textWidth, e.Bounds.Bottom - nameRect.Bottom - 4);
        TextRenderer.DrawText(e.Graphics, service.Name, UiTheme.Bold, nameRect, SystemColors.ControlText,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var detail = UiTheme.StateText(snapshot.State) + (snapshot.ProcessId is int pid ? $" · PID {pid}" : "");
        TextRenderer.DrawText(e.Graphics, detail, UiTheme.Small, stateRect, UiTheme.StateColor(snapshot.State),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (focused) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds);
    }

    // ---------------------------------------------------------------- behavior (unchanged semantics)

    public void AllowExit() => _allowExit = true;

    public void RefreshFromExternalChange()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshFromExternalChange);
            return;
        }
        var selectedId = Selected?.Id;
        ServiceDefinition? select;
        lock (_configuration)
            select = _configuration.Services.FirstOrDefault(service => service.Id == selectedId);
        RefreshServiceList(select);
        LoadSelected();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fieldHelp.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    private ServiceDefinition? Selected => _services.SelectedItem as ServiceDefinition;

    private void RefreshServiceList(ServiceDefinition? select = null)
    {
        var filter = _search.Text.Trim();
        List<ServiceDefinition> items;
        lock (_configuration)
            items = _configuration.Services
                .Where(s => filter.Length == 0 || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        _services.DataSource = null;
        _services.DataSource = items;
        if (select is not null && items.Contains(select)) _services.SelectedItem = select;
        else if (_services.Items.Count > 0 && _services.SelectedIndex < 0) _services.SelectedIndex = 0;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var hasSelection = Selected is not null;
        _emptyPanel.Visible = !hasSelection;
        _scrollHost.Visible = hasSelection;
        if (_headerWrap is not null) _headerWrap.Visible = hasSelection;
    }

    private void LoadSelected()
    {
        UpdateEmptyState();
        if (Selected is not { } item) return;
        lock (_configuration)
        {
            _title.Text = item.Name;
            _name.Text = item.Name;
            _command.Text = item.Command;
            _cwd.Text = item.WorkingDirectory;
            _url.Text = item.OpenUrl;
            _stopCommand.Text = item.StopCommand;
            _timeout.Value = Math.Clamp(item.StopTimeoutSeconds, 1, 300);
            _startWithManager.Checked = item.StartWithManager;
            _restart.Checked = item.RestartOnUnexpectedExit;
            _environment.Lines = item.Environment.Select(x => $"{(x.IsSecret ? "*" : "")}{x.Name}={x.Value}").ToArray();
        }
        ApplySnapshot(_supervisor.GetSnapshot(item));
    }

    private void SaveSelected()
    {
        if (Selected is not { } item) return;
        try
        {
            lock (_configuration)
            {
                item.Name = _name.Text.Trim(); item.Command = _command.Text.Trim(); item.WorkingDirectory = _cwd.Text.Trim();
                item.OpenUrl = _url.Text.Trim(); item.StopCommand = _stopCommand.Text.Trim(); item.StopTimeoutSeconds = (int)_timeout.Value;
                item.StartWithManager = _startWithManager.Checked; item.RestartOnUnexpectedExit = _restart.Checked;
                item.Environment = ParseEnvironment(_environment.Lines);
                _store.Save(_configuration);
            }
            _title.Text = item.Name;
            RefreshServiceList(item);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CommandTray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddService()
    {
        var service = new ServiceDefinition { Name = "New service" };
        lock (_configuration) _configuration.Services.Add(service);
        _search.Clear();
        RefreshServiceList(service);
    }

    private void DeleteSelected()
    {
        if (Selected is not { } item) return;
        if (_supervisor.GetSnapshot(item).State is not (ServiceState.Stopped or ServiceState.Faulted))
        {
            MessageBox.Show(this, "Stop the service before deleting it. / 删除前请先停止服务。", "CommandTray");
            return;
        }
        if (MessageBox.Show(this, $"Delete '{item.Name}'?", "CommandTray", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        lock (_configuration)
        {
            _configuration.Services.Remove(item);
            _store.Save(_configuration);
        }
        RefreshServiceList();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunSelected(Func<ServiceDefinition, CancellationToken, Task> operation)
    {
        if (Selected is not { } item) return;
        try { SaveSelected(); await operation(item, CancellationToken.None); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CommandTray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ApplySnapshot(ServiceSnapshot snapshot)
    {
        if (Selected?.Id != snapshot.Id) return;
        _badge.SetState(snapshot.State);
        _stateDetail.Text = $"{(snapshot.ProcessId is int pid ? $"PID {pid}" : UiTheme.StateText(snapshot.State))}{(snapshot.Error is { Length: > 0 } error ? $" · {error}" : "")}";
        _stateDetail.ForeColor = snapshot.Error is { Length: > 0 } ? UiTheme.StateColor(ServiceState.Faulted) : UiTheme.MutedText;
        _start.Enabled = snapshot.State is ServiceState.Stopped or ServiceState.Faulted;
        _stop.Enabled = snapshot.State == ServiceState.Running;
        _restartButton.Enabled = snapshot.State == ServiceState.Running;
    }

    private void ViewLog()
    {
        if (Selected is not { } item) return;
        var path = _supervisor.GetLogPath(item);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        new LogViewerForm(item.Name, path).Show(this);
    }

    private void ImportConfiguration()
    {
        using var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var imported = _store.Import(dialog.FileName);
            lock (_configuration)
            {
                _configuration.Services = imported.Services;
                _configuration.Language = imported.Language;
                _configuration.McpEnabled = imported.McpEnabled;
                _store.Save(_configuration);
                _enableMcp.Checked = _configuration.McpEnabled;
            }
            _search.Clear();
            RefreshServiceList();
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CommandTray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ExportConfiguration()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "CommandTray-export.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { lock (_configuration) _store.ExportWithoutSecrets(_configuration, dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CommandTray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void SaveMcpPreference()
    {
        if (_updatingMcpPreference) return;
        bool previous;
        lock (_configuration) previous = _configuration.McpEnabled;
        try
        {
            lock (_configuration)
            {
                _configuration.McpEnabled = _enableMcp.Checked;
                _store.Save(_configuration);
            }
            McpEnabledChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            lock (_configuration) _configuration.McpEnabled = previous;
            _updatingMcpPreference = true;
            try { _enableMcp.Checked = previous; }
            finally { _updatingMcpPreference = false; }
            MessageBox.Show(this, ex.Message, "CommandTray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static List<EnvironmentEntry> ParseEnvironment(IEnumerable<string> lines)
    {
        var values = new List<EnvironmentEntry>();
        foreach (var raw in lines.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var line = raw.Trim(); var secret = line.StartsWith('*'); if (secret) line = line[1..];
            var separator = line.IndexOf('=');
            if (separator < 1) throw new InvalidDataException($"Invalid environment entry: {raw}");
            values.Add(new EnvironmentEntry { Name = line[..separator].Trim(), Value = line[(separator + 1)..], IsSecret = secret });
        }
        return values;
    }

    // ---------------------------------------------------------------- helpers

    private static Control CreateCard(string title, Action<TableLayoutPanel> build)
    {
        var card = new CardPanel { Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 14) };
        var heading = new Label { Text = title, Font = UiTheme.Bold, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        build(grid);
        card.Controls.Add(grid);
        card.Controls.Add(heading);
        return card;
    }

    private static Control WithBrowse(TextBox textBox)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        textBox.Dock = DockStyle.Fill;
        var browse = new Button { Text = "…", AutoSize = true, MinimumSize = new Size(30, 0) };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog() == DialogResult.OK) textBox.Text = dialog.SelectedPath; };
        panel.Controls.Add(textBox, 0, 0); panel.Controls.Add(browse, 1, 0); return panel;
    }

    private static FlowLayoutPanel Stack(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.AddRange(controls); return panel;
    }

    private void AddRow(TableLayoutPanel form, string label, Control control, string? helpText = null)
    {
        var row = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 10, 6), ForeColor = UiTheme.MutedText };
        control.Dock = control is FlowLayoutPanel ? DockStyle.Top : DockStyle.Fill;
        control.Margin = new Padding(3, 3, 3, 6);
        form.Controls.Add(caption, 0, row); form.Controls.Add(control, 1, row);

        if (!string.IsNullOrWhiteSpace(helpText))
        {
            _fieldHelp.SetToolTip(caption, helpText);
            SetToolTipRecursively(control, helpText);
        }
    }

    private void SetToolTipRecursively(Control control, string helpText)
    {
        _fieldHelp.SetToolTip(control, helpText);
        foreach (Control child in control.Controls)
            SetToolTipRecursively(child, helpText);
    }
}
