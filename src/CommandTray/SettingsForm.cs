using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray;

public sealed class SettingsForm : Form
{
    private readonly AppConfiguration _configuration;
    private readonly ConfigurationStore _store;
    private readonly ProcessSupervisor _supervisor;

    private readonly ListBox _services = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        DrawMode = DrawMode.OwnerDrawFixed,
        DisplayMember = nameof(ServiceDefinition.Name),
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Panel _detailPanel = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel _emptyPanel = new() { Dock = DockStyle.Fill };
    private readonly Label _title = new() { AutoSize = true, Font = UiTheme.Title };
    private readonly Label _state = new() { AutoSize = true, Font = UiTheme.Bold };
    private readonly TextBox _name = new(), _command = new(), _cwd = new(), _url = new(), _stopCommand = new();
    private readonly TextBox _environment = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 120 };
    private readonly CheckBox _startWithManager = new() { Text = "Start with manager / 随管理器启动", AutoSize = true };
    private readonly CheckBox _restart = new() { Text = "Restart on unexpected exit / 异常退出重启", AutoSize = true };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 300, Value = 5, Width = 90 };
    private readonly Button _add = UiTheme.CreateButton("+ Add / 添加");
    private readonly Button _delete = UiTheme.CreateButton("Delete / 删除");
    private readonly Button _save = UiTheme.CreatePrimaryButton("Save / 保存");
    private readonly Button _start = UiTheme.CreateButton("Start / 启动");
    private readonly Button _stop = UiTheme.CreateButton("Stop / 停止");
    private readonly Button _restartButton = UiTheme.CreateButton("Restart / 重启");
    private readonly Button _logs = UiTheme.CreateButton("View log / 查看日志");
    private readonly Button _import = UiTheme.CreateButton("Import / 导入");
    private readonly Button _export = UiTheme.CreateButton("Export / 导出");
    private bool _allowExit;

    public event EventHandler? ConfigurationChanged;

    public SettingsForm(AppConfiguration configuration, ConfigurationStore store, ProcessSupervisor supervisor)
    {
        _configuration = configuration;
        _store = store;
        _supervisor = supervisor;
        Text = "CommandTray · Settings / 设置";
        Font = UiTheme.Regular;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(880, 620);
        Size = new Size(1020, 720);
        Padding = new Padding(10);

        var root = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6 };
        Controls.Add(root);
        BuildServiceListPanel(root.Panel1);
        BuildDetailPanel(root.Panel2);

        AcceptButton = _save;
        var closeHotkey = new Button { Size = Size.Empty, TabStop = false };
        closeHotkey.Click += (_, _) => Close();
        Controls.Add(closeHotkey);
        CancelButton = closeHotkey;

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
        _supervisor.StateChanged += (_, snapshot) => BeginInvoke(() =>
        {
            ApplySnapshot(snapshot);
            _services.Invalidate();
        });
        RefreshServiceList();
        UpdateEmptyState();
    }

    private void BuildServiceListPanel(SplitterPanel panel)
    {
        panel.Padding = new Padding(0, 0, 6, 0);
        var header = UiTheme.CreateMutedLabel("Services / 服务");
        header.Font = UiTheme.Bold;
        header.ForeColor = SystemColors.ControlText;
        header.Dock = DockStyle.Top;
        header.Padding = new Padding(2, 0, 0, 6);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        buttons.Controls.AddRange([_add, _delete]);

        _services.Margin = new Padding(0);
        _services.ItemHeight = (int)(Font.Height * 1.6);
        _services.DrawItem += DrawServiceItem;

        panel.Controls.Add(_services);
        panel.Controls.Add(buttons);
        panel.Controls.Add(header);
    }

    private void BuildDetailPanel(SplitterPanel panel)
    {
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(4, 10, 0, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var separator = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Separator, Margin = new Padding(0) };
        actions.Controls.Add(separator, 0, 0);
        actions.SetColumnSpan(separator, 2);
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var processActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        processActions.Controls.AddRange([_start, _stop, _restartButton]);
        var configActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Anchor = AnchorStyles.Right };
        configActions.Controls.AddRange([_logs, _import, _export, _save]);
        actions.Controls.Add(processActions, 0, 1);
        actions.Controls.Add(configActions, 1, 1);

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(6, 2, 6, 6)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 10) };
        header.Controls.Add(_title);
        var stateRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        var stateCaption = UiTheme.CreateMutedLabel("State / 状态");
        stateCaption.Margin = new Padding(0, 0, 8, 0);
        stateRow.Controls.Add(stateCaption);
        stateRow.Controls.Add(_state);
        header.Controls.Add(stateRow);
        stack.Controls.Add(header);

        stack.Controls.Add(CreateGroup("General / 常规", grid =>
        {
            AddRow(grid, "Name / 名称", _name);
            AddRow(grid, "CMD command / 命令", _command);
            AddRow(grid, "Working directory / 目录", WithBrowse(_cwd));
            AddRow(grid, "Open URL / 网页", _url);
        }));

        stack.Controls.Add(CreateGroup("Shutdown / 停止", grid =>
        {
            AddRow(grid, "Stop command / 停止命令", _stopCommand);
            AddRow(grid, "Stop timeout / 超时秒", _timeout);
            AddRow(grid, "Options / 选项", Stack(_startWithManager, _restart));
        }));

        stack.Controls.Add(CreateGroup("Environment / 环境变量", grid =>
        {
            AddRow(grid, "", _environment);
            var hint = UiTheme.CreateMutedLabel("One NAME=value per line. Prefix secret names with * / 每行 NAME=value，敏感变量名前加 *");
            hint.Margin = new Padding(3, 2, 3, 0);
            AddRow(grid, "", hint);
        }));

        _detailPanel.Controls.Add(stack);
        _detailPanel.Padding = new Padding(6, 4, 6, 4);

        var emptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = UiTheme.MutedText,
            Text = "No services yet. Click \"+ Add / 添加\" to create one.\r\n还没有服务，请点击 \"+ Add / 添加\" 创建。"
        };
        _emptyPanel.Controls.Add(emptyLabel);

        panel.Controls.Add(_emptyPanel);
        panel.Controls.Add(_detailPanel);
        panel.Controls.Add(actions);
        _detailPanel.BringToFront();
    }

    private void DrawServiceItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _services.Items.Count) return;
        e.DrawBackground();
        if (_services.Items[e.Index] is not ServiceDefinition service) return;
        var state = _supervisor.GetSnapshot(service).State;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var dotSize = Math.Max(8, e.Bounds.Height / 3);
        var dotBounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + (e.Bounds.Height - dotSize) / 2, dotSize, dotSize);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(selected ? Color.White : UiTheme.StateColor(state)))
            e.Graphics.FillEllipse(brush, dotBounds);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        var textBounds = new Rectangle(dotBounds.Right + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Right - dotBounds.Right - 12), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, service.Name, e.Font ?? _services.Font, textBounds,
            selected ? SystemColors.HighlightText : SystemColors.ControlText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) == DrawItemState.Focus && (e.State & DrawItemState.NoFocusRect) != DrawItemState.NoFocusRect)
            e.DrawFocusRectangle();
    }

    public void AllowExit() => _allowExit = true;

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
        _services.DataSource = null;
        _services.DataSource = _configuration.Services.ToList();
        if (select is not null) _services.SelectedItem = select;
        else if (_services.Items.Count > 0 && _services.SelectedIndex < 0) _services.SelectedIndex = 0;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var hasSelection = Selected is not null;
        _emptyPanel.Visible = !hasSelection;
        if (hasSelection) _detailPanel.BringToFront();
        else _emptyPanel.BringToFront();
    }

    private void LoadSelected()
    {
        UpdateEmptyState();
        if (Selected is not { } item) return;
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
        ApplySnapshot(_supervisor.GetSnapshot(item));
    }

    private void SaveSelected()
    {
        if (Selected is not { } item) return;
        try
        {
            item.Name = _name.Text.Trim(); item.Command = _command.Text.Trim(); item.WorkingDirectory = _cwd.Text.Trim();
            item.OpenUrl = _url.Text.Trim(); item.StopCommand = _stopCommand.Text.Trim(); item.StopTimeoutSeconds = (int)_timeout.Value;
            item.StartWithManager = _startWithManager.Checked; item.RestartOnUnexpectedExit = _restart.Checked;
            item.Environment = ParseEnvironment(_environment.Lines);
            _store.Save(_configuration);
            _title.Text = item.Name;
            RefreshServiceList(item);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CommandTray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddService()
    {
        var service = new ServiceDefinition { Name = "New service" };
        _configuration.Services.Add(service);
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
        _configuration.Services.Remove(item);
        _store.Save(_configuration);
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
        _state.Text = $"{snapshot.State}{(snapshot.ProcessId is int pid ? $" · PID {pid}" : "")}{(snapshot.Error is { Length: > 0 } error ? $" · {error}" : "")}";
        _state.ForeColor = UiTheme.StateColor(snapshot.State);
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
            _configuration.Services = imported.Services;
            _configuration.Language = imported.Language;
            _store.Save(_configuration);
            RefreshServiceList();
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CommandTray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ExportConfiguration()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "CommandTray-export.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _store.ExportWithoutSecrets(_configuration, dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CommandTray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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

    private static GroupBox CreateGroup(string text, Action<TableLayoutPanel> build)
    {
        var group = new GroupBox
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(10, 4, 10, 10),
            Margin = new Padding(0, 0, 0, 10)
        };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        build(grid);
        group.Controls.Add(grid);
        return group;
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

    private static void AddRow(TableLayoutPanel form, string label, Control control)
    {
        var row = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 10, 6) };
        control.Dock = control is FlowLayoutPanel ? DockStyle.Top : DockStyle.Fill;
        control.Margin = new Padding(3, 3, 3, 6);
        form.Controls.Add(caption, 0, row); form.Controls.Add(control, 1, row);
    }
}
