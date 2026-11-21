using System.Diagnostics;
using System.Text.Json.Nodes;
using CommandTray.Services;

namespace CommandTray;

internal sealed partial class DesktopWorkspacePanel
{
    private readonly Dictionary<string, Label> _previewLabels = new(StringComparer.Ordinal);

    private Control FileComponent(WorkspaceItem item)
    {
        var list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        list.Columns.Add(Localizer.T("Name", "名称"), 180);
        list.Columns.Add(Localizer.T("Size", "大小"), 65);
        var pathBox = new TextBox { Width = 200, Text = WorkspaceStore.Text(item.Data["path"]) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        var spaceId = Space.Id;
        var request = 0;
        var browse = Button("Browse", "浏览", () =>
        {
            using var picker = new FolderBrowserDialog { SelectedPath = pathBox.Text };
            if (picker.ShowDialog(this) == DialogResult.OK) _ = Navigate(picker.SelectedPath);
        });
        var up = Button("Up", "上一级", () => Safe(() =>
        {
            var parent = Directory.GetParent(Path.GetFullPath(pathBox.Text));
            if (parent is not null) _ = Navigate(parent.FullName);
        }));
        var terminal = Button("Terminal here", "在此开启终端", () => Safe(() =>
        {
            var directory = Path.GetFullPath(pathBox.Text);
            var terminalId = _desktop.Workspace.AddItem(spaceId, "terminal", Localizer.T("Terminal", "终端"), new JsonObject { ["directory"] = directory });
            _desktop.OpenTerminal(terminalId, "cmd", directory);
        }));
        var copy = Button("Copy to…", "复制到…", () => Safe(() =>
        {
            if (list.SelectedItems.Count == 0) return;
            var source = (string)list.SelectedItems[0].Tag!;
            if (Directory.Exists(source)) throw new InvalidOperationException("Select a file to copy.");
            using var picker = new FolderBrowserDialog();
            if (picker.ShowDialog(this) == DialogResult.OK)
                File.Copy(source, Path.Combine(picker.SelectedPath, Path.GetFileName(source)));
        }));
        var refresh = Button("Refresh", "刷新", () => _ = Navigate(pathBox.Text));
        var panel = WithToolbar(list, pathBox, browse, up, terminal, copy, refresh);
        list.DoubleClick += (_, _) => Safe(() =>
        {
            if (list.SelectedItems.Count == 0) return;
            var selected = (string)list.SelectedItems[0].Tag!;
            if (Directory.Exists(selected)) _ = Navigate(selected);
            else Process.Start(new ProcessStartInfo(selected) { UseShellExecute = true });
        });
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            var selected = (string)list.SelectedItems[0].Tag!;
            var preview = _previewLabels.GetValueOrDefault(spaceId);
            if (preview is not null && !preview.IsDisposed && File.Exists(selected))
                _ = LoadPreviewAsync(preview, selected);
        };
        pathBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true; _ = Navigate(pathBox.Text);
        };
        panel.HandleCreated += (_, _) =>
        {
            // A synced UNC path is displayed but never opened until the user navigates to it.
            if (!pathBox.Text.StartsWith(@"\\", StringComparison.Ordinal)) _ = Navigate(pathBox.Text, persist: false);
        };
        async Task Navigate(string path, bool persist = true)
        {
            var ticket = ++request;
            try
            {
                path = Path.GetFullPath(path);
                var results = await Task.Run(() => LocalFiles.List(path));
                if (list.IsDisposed || ticket != request) return;
                pathBox.Text = path;
                list.BeginUpdate();
                try
                {
                    list.Items.Clear();
                    foreach (var entry in results)
                    {
                        var row = new ListViewItem(entry.Directory ? entry.Name + "\\" : entry.Name) { Tag = entry.Path };
                        row.SubItems.Add(entry.Directory ? "" : entry.Size.ToString("N0"));
                        list.Items.Add(row);
                    }
                }
                finally { list.EndUpdate(); }
                if (persist) Commit(() => _desktop.Workspace.EditData(spaceId, item.Id, data => data["path"] = path));
            }
            catch (Exception ex)
            {
                if (list.IsDisposed || ticket != request) return;
                if (persist) Error(this, ex);
                else list.Items.Add(new ListViewItem(ex.Message));
            }
        }
        return panel;
    }

    private Control PreviewComponent(WorkspaceItem item)
    {
        var preview = new Label { Padding = new Padding(10), AutoEllipsis = true, Font = UiTheme.Monospace,
            Text = Localizer.T("Select a file", "选择文件") };
        var spaceId = Space.Id;
        _previewLabels[spaceId] = preview;
        preview.Disposed += (_, _) =>
        {
            if (_previewLabels.GetValueOrDefault(spaceId) == preview) _previewLabels.Remove(spaceId);
        };
        return WithToolbar(preview, Button("Open…", "打开…", () =>
        {
            using var picker = new OpenFileDialog();
            if (picker.ShowDialog(this) == DialogResult.OK) _ = LoadPreviewAsync(preview, picker.FileName);
        }));
    }

    private static async Task LoadPreviewAsync(Label preview, string path)
    {
        try
        {
            var text = await LocalFiles.PreviewAsync(path);
            if (!preview.IsDisposed) preview.Text = text;
        }
        catch (Exception ex) { if (!preview.IsDisposed) preview.Text = ex.Message; }
    }

    private Control TerminalComponent(WorkspaceItem item)
    {
        var output = new RichTextBox { ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = false,
            BackColor = UiTheme.ConsoleBack, ForeColor = UiTheme.ConsoleText, Font = UiTheme.Monospace, BorderStyle = BorderStyle.None,
            DetectUrls = false, ShortcutsEnabled = false, Text = WorkspaceStore.Text(item.Data["history"]) ?? "" };
        var settings = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 108 };
        settings.Items.AddRange(["cmd", "powershell"]);
        settings.SelectedIndex = WorkspaceStore.Text(item.Data["shell"]) == "powershell" ? 1 : 0;
        var spaceId = Space.Id;
        var start = Button("Start", "启动", () => Safe(() =>
        {
            var shell = (string)settings.SelectedItem!;
            var session = _desktop.OpenTerminal(item.Id, shell, WorkspaceStore.Text(item.Data["directory"]));
            SizeSession(session);
            Commit(() => _desktop.Workspace.EditData(spaceId, item.Id, data => data["shell"] = shell));
            output.Focus();
        }));
        var stop = Button("Close session", "关闭会话", () => Safe(() =>
        {
            var history = _desktop.Terminal(item.Id)?.Output ?? output.Text;
            _desktop.CloseTerminal(item.Id);
            output.Text = history;
            Commit(() => _desktop.Workspace.EditData(spaceId, item.Id, data => data["history"] = history));
        }));
        var directory = Button("Directory", "目录", () =>
        {
            using var picker = new FolderBrowserDialog();
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            Safe(() => _desktop.Workspace.EditData(spaceId, item.Id, data => data["directory"] = picker.SelectedPath));
        });
        output.KeyPress += (_, e) =>
        {
            if (char.IsControl(e.KeyChar) || _desktop.Terminal(item.Id) is not { Active: true } session) return;
            e.Handled = true;
            Safe(() => session.Send(e.KeyChar.ToString()));
        };
        output.KeyDown += (_, e) =>
        {
            if (_desktop.Terminal(item.Id) is not { Active: true } session) return;
            var sequence = e.KeyCode switch
            {
                Keys.Enter => "\r", Keys.Back => "\x7f", Keys.Tab => "\t",
                Keys.Up => "\x1b[A", Keys.Down => "\x1b[B", Keys.Right => "\x1b[C", Keys.Left => "\x1b[D",
                Keys.Home => "\x1b[H", Keys.End => "\x1b[F", Keys.Delete => "\x1b[3~",
                Keys.C when e.Control && output.SelectionLength == 0 => "\x03",
                Keys.V when e.Control => Clipboard.ContainsText() ? Clipboard.GetText() : "",
                _ => null
            };
            if (e.Control && e.KeyCode == Keys.C && output.SelectionLength > 0)
            {
                Clipboard.SetText(output.SelectedText); e.SuppressKeyPress = true; return;
            }
            if (sequence is null) return;
            e.SuppressKeyPress = true;
            Safe(() => session.Send(sequence));
        };
        output.Resize += (_, _) => { if (_desktop.Terminal(item.Id) is { } session) SizeSession(session); };
        var timer = new System.Windows.Forms.Timer { Interval = 130 };
        timer.Tick += (_, _) =>
        {
            var session = _desktop.Terminal(item.Id);
            start.Enabled = settings.Enabled = session is not { Active: true };
            stop.Enabled = session is not null;
            if (session is null || output.Text == session.Output) return;
            var follow = output.SelectionLength == 0 && output.SelectionStart >= output.TextLength - 1;
            var selection = (output.SelectionStart, output.SelectionLength);
            output.Text = session.Output;
            if (follow) { output.SelectionStart = output.TextLength; output.ScrollToCaret(); }
            else output.Select(Math.Min(selection.Item1, output.TextLength), Math.Min(selection.Item2, output.TextLength - Math.Min(selection.Item1, output.TextLength)));
        };
        timer.Start();
        output.Disposed += (_, _) => timer.Dispose();
        return WithToolbar(output, settings, start, stop, directory);

        void SizeSession(TerminalSession session) => session.Resize(
            output.ClientSize.Width / Math.Max(1, TextRenderer.MeasureText("MM", output.Font, Size.Empty, TextFormatFlags.NoPadding).Width / 2),
            output.ClientSize.Height / Math.Max(1, output.Font.Height));
    }

    private Control ServiceComponent(WorkspaceItem item, bool log)
    {
        var choices = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = "Name", ValueMember = "Id", DataSource = _api.ListServices().ToArray() };
        if (Guid.TryParse(WorkspaceStore.Text(item.Data["serviceId"]), out var id)) choices.SelectedValue = id;
        var display = new TextBox { ReadOnly = true, Multiline = true, Font = log ? UiTheme.Monospace : UiTheme.Regular,
            WordWrap = !log, ScrollBars = ScrollBars.Vertical, BackColor = log ? UiTheme.ConsoleBack : UiTheme.Surface,
            ForeColor = log ? UiTheme.ConsoleText : UiTheme.Text };
        var spaceId = Space.Id;
        string? Selected() => choices.SelectedItem is ServiceView view ? view.Id.ToString() : null;
        void Update()
        {
            try
            {
                if (Selected() is not string serviceId) return;
                var content = log ? _api.GetServiceLog(serviceId, 200).Content : _api.GetService(serviceId).State;
                if (display.Text == content) return;
                display.Text = content;
                display.SelectionStart = display.TextLength;
                display.ScrollToCaret();
            }
            catch (Exception ex) { display.Text = ex.Message; }
        }
        choices.SelectionChangeCommitted += (_, _) => Safe(() =>
        {
            Commit(() => _desktop.Workspace.EditData(spaceId, item.Id, data => data["serviceId"] = Selected()));
            Update();
        });
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) => Update(); timer.Start(); display.Disposed += (_, _) => timer.Dispose();
        Update();
        if (log) return WithToolbar(display, choices);
        Button ActionButton(string en, string zh, Func<string, CancellationToken, Task<ServiceView>> operation)
        {
            var button = Button(en, zh, null);
            button.Click += async (_, _) =>
            {
                try
                {
                    if (Selected() is not string service) return;
                    button.Enabled = false;
                    var result = await operation(service, default);
                    _desktop.Notify($"{result.Name} · {result.State}", spaceId, item.Id);
                }
                catch (Exception ex) { Error(this, ex); }
                finally { if (!button.IsDisposed) button.Enabled = true; }
            };
            return button;
        }
        return WithToolbar(display, choices, ActionButton("Start", "启动", _api.StartServiceAsync),
            ActionButton("Stop", "停止", _api.StopServiceAsync), ActionButton("Restart", "重启", _api.RestartServiceAsync));
    }

    private Control CommandComponent(WorkspaceItem item)
    {
        var command = new TextBox { Multiline = true, Text = WorkspaceStore.Text(item.Data["command"]) ?? "", Font = UiTheme.Monospace };
        var spaces = Space.Id;
        var terminals = Space.Items.Where(entry => entry.Kind == "terminal").ToArray();
        var chooser = new ComboBox { Width = 145, DropDownStyle = ComboBoxStyle.DropDownList, DataSource = terminals, DisplayMember = "Title" };
        return WithToolbar(command, chooser, Button("Save", "保存", () => Safe(() =>
            Commit(() => _desktop.Workspace.EditData(spaces, item.Id, data => data["command"] = command.Text)))),
            Button("Send", "发送", () => Safe(() =>
            {
                if (chooser.SelectedItem is not WorkspaceItem terminal || _desktop.Terminal(terminal.Id) is not { Active: true } session)
                    throw new InvalidOperationException("Start a terminal in this space first.");
                session.Send(command.Text.ReplaceLineEndings("\r").TrimEnd('\r') + "\r");
                _desktop.Notify(Localizer.T("Command sent", "已发送命令"), spaces, item.Id);
            })));
    }

    public string? ConnectSsh(JsonObject host, string? spaceId = null)
    {
        string? identityFile = null;
        var name = WorkspaceStore.Text(host["name"]) ?? "SSH";
        if (WorkspaceStore.Text(host["keyId"]) is { Length: > 0 } keyId)
        {
            var passphrase = Prompt(this, Localizer.T("SSH vault passphrase", "SSH 密钥库口令"), "", secret: true);
            if (passphrase is null) return null;
            identityFile = _desktop.CreateIdentityFile(keyId, passphrase);
        }
        try
        {
            var terminalId = _desktop.Workspace.AddItem(spaceId ?? Space.Id, "terminal", name, new JsonObject { ["hostId"] = WorkspaceStore.Text(host["id"]) });
            var session = _desktop.OpenTerminal(terminalId, "ssh", null, SshArguments.ForHost(host, identityFile).ToArray());
            if (identityFile is not null)
            {
                var path = identityFile;
                session.SetCleanup(() => File.Delete(path));
                identityFile = null;
            }
            return terminalId;
        }
        finally { if (identityFile is not null) File.Delete(identityFile); }
    }
}
