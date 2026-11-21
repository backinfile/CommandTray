using System.Diagnostics;
using System.Text.Json.Nodes;
using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray;

internal sealed partial class DesktopWorkspacePanel : UserControl
{
    private readonly DesktopRuntime _desktop;
    private readonly CommandTrayApi _api;
    private readonly TabControl _spaces = new() { Dock = DockStyle.Top, Height = 32 };
    private readonly Panel _canvas = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.RailBack };
    private bool _changing;
    private WorkspaceSpace? _space;

    public DesktopWorkspacePanel(DesktopRuntime desktop, CommandTrayApi api)
    {
        _desktop = desktop;
        _api = api;
        Dock = DockStyle.Fill;
        var addItem = Button("Add component ▾", "添加组件 ▾", null, true);
        var choices = new ContextMenuStrip();
        var kinds = new[]
        {
            ("files", "Files", "文件"), ("terminal", "Terminal", "终端"), ("note", "Note", "便签"),
            ("todo", "To-do list", "待办清单"), ("board", "Whiteboard", "画板"), ("preview", "File preview", "文件预览"),
            ("service", "Service controls", "服务"), ("log", "Service log", "日志"), ("commands", "Command shortcuts", "常用命令"),
            ("clipboard", "Clipboard", "剪贴板"), ("captures", "Capture history", "捕获历史"), ("ssh", "SSH hosts", "SSH 主机"),
            ("sharedFiles", "Shared files", "共享文件")
        };
        foreach (var (kind, english, chinese) in kinds)
            choices.Items.Add(Localizer.T(english, chinese), null, (_, _) => Safe(() => AddItem(kind, Localizer.T(english, chinese))));
        addItem.ContextMenuStrip = choices;
        addItem.Click += (_, _) => choices.Show(addItem, new Point(0, addItem.Height));
        var tools = Toolbar(addItem, Button("New space", "新建空间", () => SpaceName(false)),
            Button("Rename space", "重命名空间", () => SpaceName(true)),
            Button("Remove space", "移除空间", RemoveSpace),
            Button("Tile components", "自动平铺", () => Safe(() => _desktop.Workspace.Arrange(Space.Id, _canvas.ClientSize.Width))),
            Button("Push", "推送", null), Button("Pull", "拉取", null));
        ((Button)tools.Controls[^2]).Click += async (_, _) => await SyncWorkspace(true);
        ((Button)tools.Controls[^1]).Click += async (_, _) => await SyncWorkspace(false);
        Controls.Add(_canvas);
        Controls.Add(_spaces);
        Controls.Add(tools);
        _spaces.SelectedIndexChanged += (_, _) =>
        {
            if (_changing || _spaces.SelectedTab?.Tag is not string id) return;
            Safe(() => Commit(() => _desktop.Workspace.ActivateSpace(id)));
            Reload();
        };
        _desktop.Workspace.Changed += WorkspaceChanged;
        Disposed += (_, _) => { _desktop.Workspace.Changed -= WorkspaceChanged; choices.Dispose(); };
        Reload();
    }

    internal void RevealComponent(string spaceId, string itemId)
    {
        var snapshot = _desktop.Workspace.Snapshot();
        if (!snapshot.Spaces.Any(space => space.Id == spaceId && space.Items.Any(item => item.Id == itemId))) return;
        if (snapshot.ActiveSpaceId != spaceId) _desktop.Workspace.ActivateSpace(spaceId);
        var component = _canvas.Controls.Cast<Control>().FirstOrDefault(control => Equals(control.Tag, itemId));
        if (component is null) return;
        component.BringToFront();
        _canvas.ScrollControlIntoView(component);
    }

    private WorkspaceSpace Space => _space ?? throw new InvalidOperationException("Open a workspace first.");

    private void WorkspaceChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing) return;
        if (_desktop.Window.InvokeRequired)
        {
            var window = _desktop.Window;
            try { window.BeginInvoke(Reload); }
            catch (InvalidOperationException) when (window.IsDisposed || window.Disposing || !window.IsHandleCreated) { }
        }
        else if (!_changing) Reload();
    }

    private void Reload()
    {
        if (IsDisposed || Disposing) return;
        var snapshot = _desktop.Workspace.Snapshot();
        _changing = true;
        _canvas.SuspendLayout();
        try
        {
            foreach (var child in _canvas.Controls.Cast<Control>().ToArray()) child.Dispose();
            var ids = snapshot.Spaces.Select(s => s.Id).ToArray();
            if (!_spaces.TabPages.Cast<TabPage>().Select(page => (string)page.Tag!).SequenceEqual(ids))
            {
                foreach (var page in _spaces.TabPages.Cast<TabPage>().ToArray()) page.Dispose();
                foreach (var space in snapshot.Spaces)
                    _spaces.TabPages.Add(new TabPage(space.Name) { Tag = space.Id });
            }
            for (var i = 0; i < snapshot.Spaces.Count; i++) _spaces.TabPages[i].Text = snapshot.Spaces[i].Name;
            _space = snapshot.Spaces.First(space => space.Id == snapshot.ActiveSpaceId);
            _spaces.SelectedIndex = Array.IndexOf(ids, Space.Id);
            _canvas.AutoScrollPosition = Point.Empty;
            foreach (var item in Space.Items) _canvas.Controls.Add(CreateComponent(item));
            if (Space.Items.Count == 0)
            {
                var empty = UiTheme.CreateMutedLabel("Add a component", "添加组件");
                empty.Location = new Point(20, 20);
                _canvas.Controls.Add(empty);
            }
        }
        finally { _canvas.ResumeLayout(); _changing = false; }
    }

    private void Commit(Action action)
    {
        _changing = true;
        try { action(); }
        finally { _changing = false; }
    }

    private void SpaceName(bool rename)
    {
        var name = Prompt(this, rename ? Localizer.T("Rename space", "重命名空间") : Localizer.T("New space", "新建空间"), rename ? Space.Name : "");
        if (name is null) return;
        Safe(() =>
        {
            if (rename) _desktop.Workspace.RenameSpace(Space.Id, name);
            else _desktop.Workspace.AddSpace(name);
        });
    }

    private void RemoveSpace()
    {
        if (MessageBox.Show(this, Localizer.T($"Remove “{Space.Name}” and its components?", $"移除「{Space.Name}」及其中的组件？"),
            "CommandTray", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        Safe(() => { _desktop.Workspace.DeleteSpace(Space.Id); _desktop.ReapRemovedTerminals(); });
    }

    private void AddItem(string kind, string title)
    {
        var data = new JsonObject();
        if (kind == "files")
        {
            using var picker = new FolderBrowserDialog();
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            data["path"] = picker.SelectedPath;
        }
        _desktop.Workspace.AddItem(Space.Id, kind, title, data);
    }

    private Panel CreateComponent(WorkspaceItem item)
    {
        var component = new Panel
        {
            Bounds = item.Bounds, BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = item.Title, Tag = item.Id
        };
        var header = new Panel { Dock = DockStyle.Top, Height = 31, BackColor = UiTheme.SelectedBack };
        var title = new Label { Text = item.Title, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
        var close = Button("×", "×", () => Safe(() =>
        {
            _desktop.Workspace.RemoveItem(Space.Id, item.Id);
            _desktop.CloseTerminal(item.Id);
        }));
        close.Dock = DockStyle.Right; close.AutoSize = false; close.Width = 34; close.MinimumSize = Size.Empty;
        var grip = new Label { Text = "◢", Dock = DockStyle.Bottom, Height = 13, TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.SizeNWSE, ForeColor = UiTheme.MutedText };
        header.Controls.Add(title);
        header.Controls.Add(close);
        title.DoubleClick += (_, _) =>
        {
            var name = Prompt(this, Localizer.T("Component name", "组件名称"), item.Title);
            if (name is not null) Safe(() => _desktop.Workspace.RenameItem(Space.Id, item.Id, name));
        };
        TrackBounds(title, component, item.Id, resize: false);
        TrackBounds(grip, component, item.Id, resize: true);
        var body = item.Kind switch
        {
            "files" => FileComponent(item),
            "terminal" => TerminalComponent(item),
            "note" => NoteComponent(item),
            "todo" => TodoComponent(item),
            "board" => new WorkspaceWhiteboard(item.Data, data => Safe(() => SaveData(item.Id, data))),
            "preview" => PreviewComponent(item),
            "service" => ServiceComponent(item, log: false),
            "log" => ServiceComponent(item, log: true),
            "commands" => CommandComponent(item),
            "clipboard" => new DesktopClipboardPanel(_desktop),
            "captures" => new DesktopCapturesPanel(_desktop),
            "ssh" => new DesktopSshPanel(_desktop, host => Safe(() => ConnectSsh(host))),
            "sharedFiles" => new DesktopSharedFilesPanel(_desktop),
            _ => UiTheme.CreateMutedLabel(item.Kind)
        };
        body.Dock = DockStyle.Fill;
        component.Controls.Add(body);
        component.Controls.Add(grip);
        component.Controls.Add(header);
        return component;
    }

    private void TrackBounds(Control handle, Panel component, string itemId, bool resize)
    {
        if (!resize) handle.Cursor = Cursors.SizeAll;
        Point start = Point.Empty;
        Rectangle previous = Rectangle.Empty;
        handle.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            component.BringToFront(); start = Cursor.Position; previous = component.Bounds; handle.Capture = true;
        };
        handle.MouseMove += (_, _) =>
        {
            if (!handle.Capture) return;
            var offset = _canvas.AutoScrollPosition;
            var delta = new Size(Cursor.Position.X - start.X, Cursor.Position.Y - start.Y);
            component.Bounds = resize
                ? new Rectangle(previous.Location, new Size(Math.Clamp(previous.Width + delta.Width, 220, 4000), Math.Clamp(previous.Height + delta.Height, 150, 4000)))
                : new Rectangle(new Point(Math.Clamp(previous.X + delta.Width - offset.X, 0, 24000) + offset.X,
                    Math.Clamp(previous.Y + delta.Height - offset.Y, 0, 24000) + offset.Y), previous.Size);
        };
        handle.MouseUp += (_, _) =>
        {
            if (!handle.Capture) return;
            handle.Capture = false;
            var bounds = component.Bounds;
            if (bounds == previous) return;
            bounds.Offset(-_canvas.AutoScrollPosition.X, -_canvas.AutoScrollPosition.Y);
            Safe(() => Commit(() => _desktop.Workspace.MoveItem(Space.Id, itemId, bounds)));
        };
    }

    private void SaveData(string itemId, JsonObject data) =>
        Commit(() => _desktop.Workspace.SetData(Space.Id, itemId, data));

    private Control NoteComponent(WorkspaceItem item)
    {
        var input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            MaxLength = 64000, Text = WorkspaceStore.Text(item.Data["text"]) ?? "" };
        return WithToolbar(input, Button("Save", "保存", () => Safe(() =>
            Commit(() => _desktop.Workspace.EditData(Space.Id, item.Id, data => data["text"] = input.Text)))));
    }

    private Control TodoComponent(WorkspaceItem item)
    {
        var tasks = item.Data["tasks"] is JsonArray stored ? (JsonArray)stored.DeepClone() : new JsonArray();
        var list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false };
        var loading = false;
        void LoadTasks()
        {
            loading = true;
            list.Items.Clear();
            foreach (var node in tasks.OfType<JsonObject>())
                list.Items.Add(WorkspaceStore.Text(node["text"]) ?? "",
                    node["completed"] is JsonValue value && value.TryGetValue<bool>(out var completed) && completed);
            loading = false;
        }
        void Save() => Commit(() => _desktop.Workspace.EditData(Space.Id, item.Id, data => data["tasks"] = tasks.DeepClone()));
        LoadTasks();
        list.ItemCheck += (_, args) =>
        {
            if (loading || args.Index >= tasks.Count) return;
            Safe(() =>
            {
                tasks[args.Index]!["completed"] = args.NewValue == CheckState.Checked;
                Save();
                if (args.NewValue == CheckState.Checked)
                    _desktop.Notify(Localizer.T($"Completed: {tasks[args.Index]!["text"]}", $"已完成：{tasks[args.Index]!["text"]}"), Space.Id, item.Id);
            });
        };
        return WithToolbar(list, Button("Add", "添加", () => Safe(() =>
        {
            var text = Prompt(this, Localizer.T("New task", "新待办"), "");
            if (text is null) return;
            tasks.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["text"] = text, ["completed"] = false });
            Save(); LoadTasks();
        })), Button("Remove", "移除", () => Safe(() =>
        {
            if (list.SelectedIndex < 0) return;
            tasks.RemoveAt(list.SelectedIndex); Save(); LoadTasks();
        })));
    }

    private async Task SyncWorkspace(bool push)
    {
        try
        {
            if (!push && MessageBox.Show(this, Localizer.T("Replace local spaces with the relay version? The current workspace will be backed up locally.",
                    "使用服务器版本替换当前空间？本地空间将保留备份。"),
                    "CommandTray", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var revision = push ? await _desktop.Relay.PushWorkspaceAsync() : await _desktop.Relay.PullWorkspaceAsync();
            if (!push) _desktop.ReapRemovedTerminals();
            _desktop.Notify(Localizer.T($"Workspace synchronized · revision {revision}", $"空间已同步 · 版本 {revision}"));
        }
        catch (Exception ex) { Error(this, ex); }
    }

    private void Safe(Action operation)
    {
        try { operation(); }
        catch (Exception ex) { Error(this, ex); }
    }

    internal static void Error(IWin32Window? owner, Exception error) =>
        MessageBox.Show(owner, error.Message, "CommandTray", MessageBoxButtons.OK, MessageBoxIcon.Error);

    internal static Button Button(string english, string chinese, Action? click, bool primary = false)
    {
        var button = primary ? UiTheme.CreatePrimaryButton(english, chinese) : UiTheme.CreateCompactButton(english, chinese);
        button.Margin = new Padding(3, 2, 4, 2);
        if (click is not null) button.Click += (_, _) => click();
        return button;
    }

    internal static FlowLayoutPanel Toolbar(params Control[] controls)
    {
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4), BackColor = UiTheme.RailBack, WrapContents = true };
        tools.Controls.AddRange(controls);
        return tools;
    }

    internal static Panel WithToolbar(Control body, params Control[] tools)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        body.Dock = DockStyle.Fill;
        panel.Controls.Add(body);
        panel.Controls.Add(Toolbar(tools));
        return panel;
    }

    internal static string? Prompt(IWin32Window owner, string title, string initial, bool secret = false)
    {
        using var form = new Form
        {
            Text = title, ClientSize = new Size(385, 86), FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        var text = new TextBox { Dock = DockStyle.Top, Text = initial, UseSystemPasswordChar = secret, MaxLength = secret ? 1024 : 2048 };
        var confirm = Button("OK", "确定", null, true);
        confirm.DialogResult = DialogResult.OK;
        var cancel = Button("Cancel", "取消", null);
        cancel.DialogResult = DialogResult.Cancel;
        form.AcceptButton = confirm; form.CancelButton = cancel;
        var buttons = Toolbar(confirm, cancel); buttons.Dock = DockStyle.Bottom;
        form.Controls.Add(text); form.Controls.Add(buttons); text.SelectAll();
        return form.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(text.Text)
            ? (secret ? text.Text : text.Text.Trim()) : null;
    }
}
