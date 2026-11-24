using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private string _workspaceSection = "overview";

    private async Task RenderWorkspaceAsync(CancellationToken token)
    {
        Header(L("工作空间", "Workspace"));
        var document = await Execute("workspace_read", [], token);
        var relay = await Execute("relay_status", [], token);
        _displayedWorkspace = document;
        _displayedWorkspaceConflict = HasWorkspaceConflict(relay);
        var spaces = document.Array("spaces").OfType<JsonObject>().ToArray();
        var selected = spaces.FirstOrDefault(space =>
            space.Text("id") == (_spaceId ?? document.Text("activeSpaceId"))) ?? spaces.FirstOrDefault();
        _spaceId = selected?.Text("id");

        bool paired = relay.Flag("paired");
        if (DesktopWorkbench)
        {
            _sidebarSpaces = spaces.Select(space => (space.Text("id"), space.Text("name"))).ToArray();
            _sidebarPaired = paired;
            UpdateSidebarSpaces();
        }
        if (selected is null)
        {
            Header(L("工作空间", "Workspace"));
            var other = paired
                ? Action(L("从中转拉取", "Pull from relay"), () => SyncAsync("sync_pull"), glyph: "sync")
                : Action(L("连接中转", "Connect to relay"), () => PairForm(relay));
            _contents.Children.Add(EmptyState("spaces", L("暂无空间", "No spaces"),
                PrimaryAction(L("新建空间", "New space"), NewSpaceAsync, "plus"), other));
            return;
        }

        var sync = paired
            ? Action(_compact ? "↻" : L("同步", "Sync"), () => SyncAsync("sync_push"), L("同步", "Sync"), "sync")
            : Action(L("连接", "Connect"), () => PairForm(relay));
        var more = WorkspaceMenu(selected, paired);

        var spaceId = selected.Text("id");
        {
            var picker = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = spaces.Select(space => space.Text("name")).ToArray(),
                SelectedIndex = Array.IndexOf(spaces, selected),
                MaxDropDownHeight = 280
            };
            picker.Classes.Add("space-picker");
            Avalonia.Automation.AutomationProperties.SetName(picker, L("切换空间", "Switch space"));
            picker.SelectionChanged += async (_, _) =>
            {
                if (picker.SelectedIndex < 0 || picker.SelectedIndex >= spaces.Length) return;
                var next = spaces[picker.SelectedIndex].Text("id");
                if (next != _spaceId) await Act(() => SelectSpaceAsync(next));
            };
            if (_compact) Header(picker, sync, more);
            else
            {
                var breadcrumb = Muted(L("工作区", "Workspace") + " /");
                breadcrumb.VerticalAlignment = VerticalAlignment.Center;
                picker.MinWidth = 150;
                picker.MaxWidth = 250;
                more.Content = IconLabel("plus", L("添加组件", "Add component"));
                more.Classes.Remove("icon-only");
                more.Classes.Add("primary");
                Header(Row(breadcrumb, picker));
            }
        }

        var allEntries = selected.Array("items").OfType<JsonObject>().ToArray();
        var tasks = allEntries.Where(item => ComponentSection(item.Text("kind")) == "tasks")
            .SelectMany(item => item.Object("data").Array("tasks").OfType<JsonObject>()).ToArray();
        var spaceHeading = new StackPanel { Spacing = 4 };
        var spaceTitle = Heading(selected.Text("name"));
        spaceTitle.Classes.Add("workbench-heading");
        spaceHeading.Children.Add(spaceTitle);
        if (tasks.Length > 0)
            spaceHeading.Children.Add(Muted(L($"{tasks.Count(task => task.Flag("completed"))} / {tasks.Length} 项待办已完成",
                $"{tasks.Count(task => task.Flag("completed"))} / {tasks.Length} tasks complete")));
        _contents.Children.Add(_compact ? spaceHeading : TitleRow(spaceHeading, Row(sync, more)));
        _contents.Children.Add(WorkspaceSections());

        if (_displayedWorkspaceConflict)
        {
            _contents.Children.Add(SettingRow(L("同步冲突", "Sync conflict"), WorkspaceConflictActions()));
        }

        var entries = _workspaceSection == "overview" ? allEntries :
            allEntries.Where(item => ComponentSection(item.Text("kind")) == _workspaceSection).ToArray();
        if (_focusedPanelId is { } focused && !entries.Any(item => item.Text("id") == focused)) _focusedPanelId = null;
        int visibleComponents = 0;
        if (_workspaceSection == "overview" && !_compact && Has(ClientFeatures.Layout) && entries.Length > 0)
        {
            var canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Left };
            _viewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _contents.Children.Add(canvas);
            foreach (var item in entries)
            {
                var tile = Component(spaceId, item, canvas);
                if (tile is null) continue;
                canvas.Children.Add(tile);
                visibleComponents++;
            }
            MeasureCanvas(canvas);
        }
        else
        {
            foreach (var item in entries)
            {
                var tile = Component(spaceId, item, null);
                if (tile is null) continue;
                _contents.Children.Add(tile);
                visibleComponents++;
            }
        }
        if (visibleComponents == 0)
        {
            var message = entries.Length == 0 ? L("暂无组件", "No components")
                : L("此设备暂无可用组件", "No components available on this device");
            var (kind, label) = _workspaceSection switch
            {
                "board" => ("board", L("画板", "Board")),
                "terminal" => ("terminal", L("终端", "Terminal")),
                "tasks" => ("todo", L("待办", "Checklist")),
                "directory" => ("files", L("目录", "Folder")),
                _ => ("note", L("便签", "Note"))
            };
            _contents.Children.Add(EmptyState("spaces", message,
                Action(L($"添加{label}", $"Add {label}"), () => ItemForm(spaceId, kind, label), glyph: "plus")));
        }

        if (_workspaceSection == "overview" && (_compact || !Has(ClientFeatures.Ssh)))
        {
            var hosts = document.Array("sshHosts").OfType<JsonObject>().ToArray();
            if (hosts.Length > 0) _contents.Children.Add(Heading(L("SSH 主机", "SSH hosts")));
            foreach (var host in hosts) _contents.Children.Add(Section(Body($"{host.Text("name")} · {HostAddress(host)}")));
        }
    }

    private Control WorkspaceSections()
    {
        var tabs = Row();
        foreach (var (id, title, glyph) in new[]
        {
            ("overview", L("总览", "Overview"), "spaces"), ("board", L("画板", "Board"), "board"),
            ("terminal", L("终端", "Terminal"), "terminal"), ("tasks", L("待办", "Tasks"), "tasks"),
            ("note", L("便签", "Notes"), "note"), ("directory", L("目录", "Folders"), "folder-open")
        })
        {
            if ((id == "terminal" && !Has(ClientFeatures.Terminals)) || (id == "directory" && !Has(ClientFeatures.LocalFiles))) continue;
            var tab = Action(title, async () =>
            {
                if (_workspaceSection == id) return;
                _workspaceSection = id;
                _focusedPanelId = null;
                _viewport.Offset = default;
                await LoadAsync();
            }, glyph: glyph);
            tab.Classes.Add("workbench-tab");
            tab.Classes.Set("selected", _workspaceSection == id);
            if (_workspaceSection == id) tab.AttachedToVisualTree += (_, _) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => tab.BringIntoView());
            tabs.Children.Add(tab);
        }
        return new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static string ComponentSection(string kind) => kind switch
    {
        "board" or "whiteboard" => "board", "terminal" or "ssh" or "commands" => "terminal",
        "todo" or "tasks" => "tasks", "note" or "notes" => "note",
        "folder" or "files" or "preview" => "directory", _ => "overview"
    };

    private async Task SelectSpaceAsync(string id)
    {
        await Execute("space_activate", new() { ["spaceId"] = id });
        if (_spaceId != id) _focusedPanelId = null;
        _spaceId = id;
        _workspaceSection = "overview";
        _viewport.Offset = default;
        await LoadAsync();
    }

    private async Task SyncAsync(string command)
    {
        var page = _loading;
        Report(L("正在同步…", "Syncing…"));
        var result = await Execute(command, []);
        if (!ReferenceEquals(page, _loading) || _editing) return;
        await LoadAsync();
        if (!HasWorkspaceConflict(result))
            Report(L("同步操作已完成", "Sync operation finished"));
    }

    private static bool HasWorkspaceConflict(JsonNode? state) =>
        state?["conflict"] is JsonObject || state.Flag("conflict");

    private Task NewSpaceAsync() => Form(L("新建空间", "New space"), L("创建", "Create"), async values =>
    {
        if (string.IsNullOrWhiteSpace(values.Text("name")))
            throw new InvalidOperationException(L("请输入空间名称。", "Enter a space name."));
        var result = await Execute("space_create", values);
        if (result.Text("id") is { Length: > 0 } id) await SelectSpaceAsync(id);
        else await LoadAsync();
    }, new Field("name", L("空间名称", "Space name")));

    private Button WorkspaceMenu(JsonObject selected, bool paired)
    {
        var id = selected.Text("id");
        var actions = new List<(string Label, Func<Task> Action)>
        {
            (L("添加便签", "Add note"), () => ItemForm(id, "note", L("便签", "Note"))),
            (L("添加待办", "Add checklist"), () => ItemForm(id, "todo", L("待办", "Checklist"))),
            (L("新建空间", "New space"), NewSpaceAsync)
        };
        if (Has(ClientFeatures.LocalFiles))
            actions.Add((L("添加目录", "Add folder"), () => ItemForm(id, "files", L("目录", "Folder"))));
        if (Has(ClientFeatures.Terminals))
            actions.Add((L("添加终端", "Add terminal"), () => ItemForm(id, "terminal", L("终端", "Terminal"))));
        actions.Insert(2, (L("添加画板", "Add board"), () => ItemForm(id, "board", L("画板", "Board"))));
        actions.Add((L("重命名空间", "Rename space"), () => Form(L("重命名空间", "Rename space"), values =>
            Change("space_rename", new() { ["spaceId"] = id, ["name"] = values.Text("name") }),
            new Field("name", L("名称", "Name"), selected.Text("name")))));
        actions.Add((L("删除空间", "Delete space"), () => Confirm(L("删除该空间？", "Delete this space?"), async () =>
        {
            _spaceId = null;
            await Change("space_delete", new() { ["spaceId"] = id });
        })));
        if (paired) actions.Add((L("从中转拉取", "Pull from relay"), () => SyncAsync("sync_pull")));
        var menu = Menu("+", actions.ToArray());
        menu.Classes.Add("add-component");
        var label = L("添加与空间操作", "Add and manage space");
        ToolTip.SetTip(menu, label);
        Avalonia.Automation.AutomationProperties.SetName(menu, label);
        return menu;
    }

    private Task ItemForm(string spaceId, string kind, string name)
    {
        var fields = new List<Field> { new("title", L("名称", "Name"), name) };
        if (kind == "note") fields.Add(new("text", L("便签正文", "Note"), Multiline: true));
        if (kind is "files" or "terminal") fields.Add(new("path", L("目录", "Directory")));
        if (kind == "terminal")
            fields.Add(new("shell", L("Shell", "Shell"), OperatingSystem.IsWindows() ? "cmd" : "/bin/sh"));
        return Form(name, values =>
        {
            var title = values.Text("title", name);
            var data = kind switch
            {
                "files" => new JsonObject { ["path"] = values.Text("path") },
                "terminal" => new JsonObject { ["workingDirectory"] = values.Text("path"), ["shell"] = values.Text("shell") },
                "note" => new JsonObject { ["text"] = values.Text("text") },
                "todo" => new JsonObject { ["tasks"] = new JsonArray() },
                _ => new JsonObject()
            };
            return Change("item_save", new() { ["spaceId"] = spaceId, ["item"] = NewItem(kind, title, data) });
        }, fields.ToArray());
    }

    private static JsonObject NewItem(string kind, string title, JsonObject data) =>
        new() { ["kind"] = kind, ["title"] = title, ["data"] = data };

    private Control? Component(string spaceId, JsonObject item, Canvas? canvas)
    {
        string kind = item.Text("kind");
        string id = item.Text("id");
        var title = item.Text("title", item.Object("data").Text("title", kind));
        var body = kind switch
        {
            "note" or "notes" => NoteComponent(spaceId, item),
            "todo" or "tasks" => ChecklistComponent(spaceId, item),
            "board" or "whiteboard" => BoardComponent(spaceId, item),
            "folder" or "files" when Has(ClientFeatures.LocalFiles) => FilesComponent(spaceId, item),
            "terminal" or "ssh" when Has(ClientFeatures.Terminals) => TerminalComponent(item),
            "preview" when Has(ClientFeatures.LocalFiles) => LegacyPreview(spaceId, item),
            "service" or "log" when Has(ClientFeatures.Commands) => LegacyService(spaceId, item, kind == "log"),
            "commands" when Has(ClientFeatures.Terminals) => LegacyCommand(spaceId, item, canvas),
            "clipboard" => PageTile("clipboard", L("打开剪贴板", "Open clipboard")),
            "captures" when Has(ClientFeatures.Capture) => PageTile("capture", L("打开捕获", "Open capture")),
            "sharedFiles" => PageTile("files", L("打开共享文件", "Open shared files")),
            _ => null
        };
        if (body is null) return null;
        Func<Task> rename = () => EditItem(spaceId, item, title, (copy, values) => copy["title"] = values.Text("title"),
            new Field("title", L("名称", "Name"), title));
        var operations = new List<(string Label, Func<Task> Action)>
        {
            (L("重命名", "Rename"), rename),
            (L("移除", "Remove"), () => Confirm(L("移除该组件？", "Remove component?"), () =>
                Change("item_remove", new() { ["spaceId"] = spaceId, ["itemId"] = id })))
        };
        if (kind is "note" or "notes")
        {
            var note = item.Object("data").Text("text");
            if (note.Length > 0)
                operations.Insert(0, (L("复制便签", "Copy note"), async () =>
                {
                    await Execute("clipboard_local_write", new() { ["text"] = note });
                    Report(L("已复制到本机剪贴板", "Copied to this device"));
                }));
            operations.Insert(0, (L("编辑", "Edit"), () => EditNote(spaceId, item)));
        }
        var handle = IconLabel(ComponentGlyph(kind), title, prominent: true);
        var iconTone = kind switch
        {
            "files" or "folder" or "preview" or "sharedFiles" => "folder-tone",
            "terminal" or "commands" or "service" or "log" or "ssh" => "terminal-tone",
            "note" or "notes" => "note-tone",
            _ => "accent-tone"
        };
        handle.Children[0].Classes.Add(iconTone);
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var actions = Row();
        if (!OperatingSystem.IsAndroid() || kind is "note" or "notes" or "todo" or "tasks" or "board" or "whiteboard")
            actions.Children.Add(Menu("⋯", operations.ToArray()));
        var heading = TitleRow(handle, actions);
        var headingLine = new Border { Child = heading };
        content.Children.Add(headingLine);
        Grid.SetRow(body, 1);
        content.Children.Add(body);
        var tile = Section(content);
        tile.Classes.Remove("divider");
        tile.Classes.Add("tile");
        tile.Padding = new Thickness(0);
        tile.ClipToBounds = true;
        headingLine.Classes.Add("tile-heading");
        headingLine.Padding = new Thickness(10, 1, 7, 1);
        body.Margin = new Thickness(10, 7, 10, 10);
        if (id == _componentId)
        {
            _componentId = null;
            tile.Classes.Add("located");
            tile.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => tile.BringIntoView());
            tile.PointerPressed += (_, _) => tile.Classes.Remove("located");
        }
        if (canvas is null) return tile;

        tile.Width = Math.Clamp(item.Integer("width", 320), 220, 3000);
        tile.Height = Math.Clamp(item.Integer("height", 230), 150, 2400);
        Canvas.SetLeft(tile, Math.Max(0, item.Integer("x")));
        Canvas.SetTop(tile, Math.Max(0, item.Integer("y")));
        var grip = new Border
        {
            Child = TrayGlyph.Icon("grip", 11),
            Background = Brushes.Transparent, Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner)
        };
        Grid.SetRowSpan(grip, 2);
        content.Children.Add(grip);
        AddFocusButton(id, canvas, tile, grip, actions);
        handle.Cursor = new Cursor(StandardCursorType.SizeAll);
        ToolTip.SetTip(handle, L("拖动移动，按住 Shift 吸附网格", "Drag to move, hold Shift to snap"));
        ToolTip.SetTip(grip, L("拖动调整大小，按住 Shift 吸附网格", "Drag to resize, hold Shift to snap"));
        AttachGesture(handle, canvas, tile, item, spaceId, resize: false);
        AttachGesture(grip, canvas, tile, item, spaceId, resize: true);
        return tile;
    }

    private static string ComponentGlyph(string kind) => kind switch
    {
        "files" or "folder" or "sharedFiles" => "folder-open",
        "terminal" or "commands" or "service" or "log" => "terminal",
        "note" or "notes" => "note",
        "todo" or "tasks" => "tasks",
        "board" or "whiteboard" => "board",
        "preview" => "file",
        "captures" => "capture",
        _ => kind
    };

    private void AttachGesture(Control handle, Canvas canvas, Border tile, JsonObject item, string spaceId, bool resize)
    {
        Point? origin = null;
        Rect previous = default;
        handle.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(_focusedPanel?.Tile, tile) || !e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
            origin = e.GetPosition(canvas);
            previous = new Rect(Canvas.GetLeft(tile), Canvas.GetTop(tile), tile.Width, tile.Height);
            e.Pointer.Capture(handle);
            _workspaceGrid.IsVisible = true;
            tile.Classes.Add("dragging");
            tile.ZIndex = 1;
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (origin is not Point start) return;
            var delta = e.GetPosition(canvas) - start;
            bool snap = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            static double GridPosition(double value, bool snap) => snap ? Math.Round(value / 24) * 24 : value;
            if (resize)
            {
                tile.Width = Math.Clamp(GridPosition(previous.Width + delta.X, snap), 220, 3000);
                tile.Height = Math.Clamp(GridPosition(previous.Height + delta.Y, snap), 150, 2400);
            }
            else
            {
                Canvas.SetLeft(tile, Math.Clamp(GridPosition(previous.X + delta.X, snap), 0, 20000));
                Canvas.SetTop(tile, Math.Clamp(GridPosition(previous.Y + delta.Y, snap), 0, 20000));
            }
            MeasureCanvas(canvas);
        };
        handle.PointerReleased += async (_, e) =>
        {
            if (origin is null) return;
            origin = null;
            e.Pointer.Capture(null);
            int x = (int)Canvas.GetLeft(tile), y = (int)Canvas.GetTop(tile),
                width = (int)tile.Width, height = (int)tile.Height;
            if (previous == new Rect(x, y, width, height)) return;
            await Act(async () =>
            {
                try
                {
                    await Execute("item_move", new()
                    {
                        ["spaceId"] = spaceId, ["itemId"] = item.Text("id"),
                        ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height
                    });
                    item["x"] = x;
                    item["y"] = y;
                    item["width"] = width;
                    item["height"] = height;
                }
                catch
                {
                    Canvas.SetLeft(tile, previous.X);
                    Canvas.SetTop(tile, previous.Y);
                    tile.Width = previous.Width;
                    tile.Height = previous.Height;
                    MeasureCanvas(canvas);
                    throw;
                }
            });
        };
        handle.PointerCaptureLost += (_, _) =>
        {
            _workspaceGrid.IsVisible = false;
            tile.Classes.Remove("dragging");
            tile.ZIndex = 0;
            if (origin is null) return;
            origin = null;
            Canvas.SetLeft(tile, previous.X);
            Canvas.SetTop(tile, previous.Y);
            tile.Width = previous.Width;
            tile.Height = previous.Height;
            MeasureCanvas(canvas);
        };
    }

    private static void MeasureCanvas(Canvas canvas)
    {
        canvas.Width = canvas.Children.Where(view => view.IsVisible).Select(view => Canvas.GetLeft(view) + view.Width).DefaultIfEmpty(1).Max();
        canvas.Height = canvas.Children.Where(view => view.IsVisible).Select(view => Canvas.GetTop(view) + view.Height).DefaultIfEmpty(1).Max();
    }

    private Task EditItem(string spaceId, JsonObject item, string caption,
        Action<JsonObject, JsonObject> edit, params Field[] fields) =>
        Form(caption, values =>
        {
            var copy = (JsonObject)item.DeepClone();
            edit(copy, values);
            return SaveItem(spaceId, copy);
        }, fields);

    private Task SaveItem(string spaceId, JsonObject item) =>
        Change("item_save", new() { ["spaceId"] = spaceId, ["item"] = item });

    private Control NoteComponent(string spaceId, JsonObject item)
    {
        var note = item.Object("data").Text("text");
        if (note.Length > 0) return Body(note);
        return Action(L("写便签", "Write note"), () => EditNote(spaceId, item));
    }

    private Task EditNote(string spaceId, JsonObject item) =>
        EditItem(spaceId, item, L("编辑便签", "Edit note"), (copy, values) =>
        {
            var data = EnsureData(copy);
            data["text"] = values.Text("text");
        }, new Field("text", L("便签正文", "Note"), item.Object("data").Text("text"), Multiline: true));

    private Control ChecklistComponent(string spaceId, JsonObject item)
    {
        var tasks = new StackPanel { Spacing = 3 };
        var entries = item.Object("data").Array("tasks").OfType<JsonObject>().ToArray();
        if (entries.Length > 0)
        {
            int completed = entries.Count(task => task.Flag("completed"));
            var progress = new ProgressBar { Minimum = 0, Maximum = entries.Length, Value = completed, Height = 5 };
            Avalonia.Automation.AutomationProperties.SetName(progress, L("待办完成率", "Task completion"));
            tasks.Children.Add(TitleRow(Muted(L($"{completed} / {entries.Length} 已完成", $"{completed} / {entries.Length} completed")),
                Muted($"{100d * completed / entries.Length:N0}%")));
            tasks.Children.Add(progress);
        }
        foreach (var task in entries)
        {
            var id = task.Text("id");
            var check = new CheckBox { Content = Body(task.Text("text")), IsChecked = task.Flag("completed"), MinHeight = _compact ? 48 : 40 };
            check.Click += async (_, _) => await Act(() => UpdateTask(spaceId, item, id, null, check.IsChecked));
            tasks.Children.Add(TitleRow(check, Menu("⋯",
                (L("编辑", "Edit"), () => EditTask(spaceId, item, task)),
                (L("删除", "Delete"), () => Confirm(L("删除该待办？", "Delete task?"),
                    () => UpdateTask(spaceId, item, id, null, null))))));
        }
        tasks.Children.Add(Action(L("新增待办", "Add task"), () => EditTask(spaceId, item, null), glyph: "plus"));
        return tasks;
    }

    private Task EditTask(string spaceId, JsonObject item, JsonObject? task) =>
        Form(L("待办", "Task"), values => UpdateTask(spaceId, item, task?.Text("id"), values.RequireText("text"), null),
            new Field("text", L("内容", "Text"), task.Text("text")));

    private Task UpdateTask(string spaceId, JsonObject item, string? id, string? title, bool? done)
    {
        var copy = (JsonObject)item.DeepClone();
        var data = EnsureData(copy);
        if (data["tasks"] is not JsonArray list)
        {
            list = [];
            data["tasks"] = list;
        }
        if (id is null)
            list.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["text"] = title, ["completed"] = false });
        else
        {
            var task = list.OfType<JsonObject>().FirstOrDefault(entry => entry.Text("id") == id);
            if (task is null) return Task.CompletedTask;
            if (done is bool completed) task["completed"] = completed;
            else if (title is not null) task["text"] = title;
            else list.Remove(task);
        }
        return SaveItem(spaceId, copy);
    }

    private static JsonObject EnsureData(JsonObject item)
    {
        if (item["data"] is JsonObject data) return data;
        data = [];
        item["data"] = data;
        return data;
    }
}
