using System.Text.Json.Nodes;
using CommandTray.Services;
using static CommandTray.DesktopWorkspacePanel;

namespace CommandTray;

internal sealed class DesktopSharedFilesPanel : UserControl
{
    private readonly DesktopRuntime _desktop;
    private readonly TextBox _folder = new() { Width = 145 };
    private readonly Label _status = UiTheme.CreateMutedLabel("");
    private readonly ListView _files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };

    public DesktopSharedFilesPanel(DesktopRuntime desktop)
    {
        _desktop = desktop;
        Dock = DockStyle.Fill;
        _folder.PlaceholderText = Localizer.T("Folder", "共享目录");
        _files.Columns.Add(Localizer.T("Name", "名称"), 200);
        _files.Columns.Add(Localizer.T("Folder", "目录"), 110);
        _files.Columns.Add(Localizer.T("Size", "大小"), 90);
        _files.Columns.Add(Localizer.T("Device", "设备"), 150);
        Controls.Add(_files);
        _status.Dock = DockStyle.Bottom;
        Controls.Add(_status);
        var refresh = AsyncButton("Refresh", "刷新", RefreshAsync);
        var upload = AsyncButton("Upload…", "上传…", UploadAsync);
        var download = AsyncButton("Download…", "下载…", DownloadAsync);
        var delete = AsyncButton("Delete…", "删除…", DeleteAsync);
        Controls.Add(Toolbar(_folder, refresh, upload, download, delete));
        _folder.TextChanged += (_, _) => Filter();
        _files.DoubleClick += async (_, _) => await RunAsync(DownloadAsync);
    }

    private JsonArray _latest = new();

    public async Task RefreshAsync()
    {
        _latest = await _desktop.Relay.ListFilesAsync();
        if (!IsDisposed) Filter();
    }

    public void RefreshWhenPaired()
    {
        if (_desktop.Relay.Server is null)
        {
            _latest = new(); Filter();
            _status.Text = Localizer.T("Pair a relay in settings.", "请先在设置中配对中转服务器。");
            return;
        }
        _ = RunAsync(RefreshAsync);
    }

    private void Filter()
    {
        _files.BeginUpdate();
        try
        {
            _files.Items.Clear();
            foreach (var file in _latest.OfType<JsonObject>())
            {
                var folder = WorkspaceStore.Text(file["folder"]) ?? "";
                if (_folder.Text.Trim().Length > 0 && !folder.StartsWith(_folder.Text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                var row = new ListViewItem(WorkspaceStore.Text(file["name"]) ?? "") { Tag = file };
                row.SubItems.Add(folder);
                row.SubItems.Add(file["size"]?.ToString() ?? "");
                row.SubItems.Add(WorkspaceStore.Text(file["sourceDevice"]) ?? WorkspaceStore.Text(file["deviceId"]) ?? "");
                _files.Items.Add(row);
            }
        }
        finally { _files.EndUpdate(); }
    }

    private async Task UploadAsync()
    {
        using var dialog = new OpenFileDialog { Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in dialog.FileNames)
            await _desktop.Relay.UploadAsync(file, _folder.Text.Trim());
        await RefreshAsync();
        _desktop.Notify(Localizer.T("Files uploaded", "共享文件已上传"));
    }

    private async Task DownloadAsync()
    {
        if (_files.SelectedItems.Count == 0 || _files.SelectedItems[0].Tag is not JsonObject file) return;
        using var dialog = new SaveFileDialog { FileName = WorkspaceStore.Text(file["name"]) ?? "file", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await _desktop.Relay.DownloadAsync(WorkspaceStore.Text(file["id"])!, dialog.FileName, true);
    }

    private async Task DeleteAsync()
    {
        if (_files.SelectedItems.Count == 0 || _files.SelectedItems[0].Tag is not JsonObject file) return;
        if (MessageBox.Show(this, Localizer.T("Delete the shared file?", "删除此共享文件？"), "CommandTray",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await _desktop.Relay.DeleteFileAsync(WorkspaceStore.Text(file["id"])!);
        await RefreshAsync();
    }

    private Button AsyncButton(string en, string zh, Func<Task> action)
    {
        var button = Button(en, zh, null);
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            _status.Text = Localizer.T("Working…", "正在处理…");
            await action();
            if (!IsDisposed) _status.Text = "";
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; }
    }
}

internal sealed class DesktopClipboardPanel : UserControl
{
    private readonly DesktopRuntime _desktop;
    private readonly TextBox _text = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MaxLength = 262144 };
    private readonly ComboBox _history = new() { Width = 185, DropDownStyle = ComboBoxStyle.DropDownList };

    public DesktopClipboardPanel(DesktopRuntime desktop)
    {
        _desktop = desktop;
        Dock = DockStyle.Fill;
        var capture = Button("Read", "读取", () => Safe(() => { _text.Text = _desktop.ReadClipboard(); History(); }));
        var copy = Button("Copy", "复制", () => Safe(() => { _desktop.WriteClipboard(_text.Text); History(); }));
        var send = Button("Send", "发送", null);
        var receive = Button("Receive", "接收", null);
        send.Click += async (_, _) =>
        {
            try
            {
                await _desktop.Relay.PutClipboardAsync(_text.Text);
                _desktop.Notify(Localizer.T("Clipboard sent", "剪贴板已发送"));
            }
            catch (Exception ex) { if (!IsDisposed) Error(this, ex); }
        };
        receive.Click += async (_, _) =>
        {
            try
            {
                var clipboard = await _desktop.Relay.GetClipboardAsync();
                if (IsDisposed) return;
                _text.Text = WorkspaceStore.Text(clipboard["text"]) ?? "";
                _desktop.WriteClipboard(_text.Text); History();
            }
            catch (Exception ex) { if (!IsDisposed) Error(this, ex); }
        };
        _history.SelectionChangeCommitted += (_, _) => { if (_history.SelectedItem is string content) _text.Text = content; };
        Controls.Add(_text); Controls.Add(Toolbar(capture, copy, send, receive, _history));
        Safe(History);
    }

    private void History()
    {
        _history.Items.Clear();
        _history.Items.AddRange(_desktop.ClipboardHistory());
    }
    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { if (!IsDisposed) Error(this, ex); }
    }
}

internal sealed class DesktopRelaySettingsPanel : UserControl
{
    public DesktopRelaySettingsPanel(DesktopRuntime desktop)
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        BackColor = UiTheme.RailBack;
        var url = new TextBox { Width = 250, Text = desktop.Relay.Server ?? "", PlaceholderText = "https://" };
        var device = new TextBox { Width = 155, Text = Environment.MachineName };
        var code = new TextBox { Width = 140, UseSystemPasswordChar = true, AccessibleName = Localizer.T("Pairing code", "配对码") };
        var pair = Button("Pair", "配对", null, true);
        var disconnect = Button("Disconnect", "断开配对", null);
        var devices = Button("Devices", "设备", null);
        var feedback = UiTheme.CreateMutedLabel("");
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, Padding = new Padding(10) };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rows.Controls.Add(Toolbar(UiTheme.CreateLabel("Relay", "中转服务器"), url));
        rows.Controls.Add(Toolbar(UiTheme.CreateLabel("Device name", "设备名称"), device));
        rows.Controls.Add(Toolbar(UiTheme.CreateLabel("Pairing code", "配对码"), code));
        rows.Controls.Add(Toolbar(pair, disconnect, devices));
        rows.Controls.Add(feedback);
        Controls.Add(rows);
        Status();
        pair.Click += async (_, _) => await Apply(async () =>
        {
            await desktop.Relay.PairAsync(url.Text.Trim(), device.Text.Trim(), code.Text);
            code.Clear();
        });
        disconnect.Click += async (_, _) => await Apply(() => desktop.Relay.DisconnectAsync());
        devices.Click += async (_, _) =>
        {
            try
            {
                var values = await desktop.Relay.ListDevicesAsync();
                if (IsDisposed) return;
                MessageBox.Show(this, string.Join(Environment.NewLine, values.OfType<JsonObject>().Select(value =>
                    WorkspaceStore.Text(value["name"]) ?? WorkspaceStore.Text(value["id"]))), Localizer.T("Paired devices", "已配对设备"));
            }
            catch (Exception ex) { if (!IsDisposed) feedback.Text = ex.Message; }
        };
        void Status()
        {
            var paired = desktop.Relay.Server is not null;
            disconnect.Enabled = devices.Enabled = paired;
            feedback.Text = paired ? Localizer.T("Paired", "已配对") : Localizer.T("Not paired", "未配对");
        }
        async Task Apply(Func<Task> operation)
        {
            pair.Enabled = disconnect.Enabled = false;
            try { await operation(); if (!IsDisposed) Status(); }
            catch (Exception ex) { if (!IsDisposed) { Status(); feedback.Text = ex.Message; } }
            finally { if (!IsDisposed) pair.Enabled = true; }
        }
    }
}
