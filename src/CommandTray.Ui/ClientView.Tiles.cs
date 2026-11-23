using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private Control LegacyPreview(string spaceId, JsonObject item)
    {
        string path = item.Object("data").Text("path", item.Object("data").Text("file"));
        var body = new StackPanel { Spacing = 5 };
        body.Children.Add(Action(L("选择预览文件", "Choose preview file"), async () =>
        {
            var files = await RequireTopLevel().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions());
            if (files.FirstOrDefault()?.TryGetLocalPath() is not string selected) return;
            await SaveTilePath(spaceId, item, selected);
        }));
        if (path.Length > 0)
        {
            body.Children.Add(Muted(path));
            body.AttachedToVisualTree += async (_, _) => await Act(async () =>
                body.Children.Add(Body(TextOf(await Execute("local_preview", new() { ["path"] = path })))));
        }
        return new ScrollViewer { Content = body };
    }

    private Control LegacyService(string spaceId, JsonObject item, bool log)
    {
        var body = new StackPanel { Spacing = 4 };
        body.AttachedToVisualTree += async (_, _) => await Act(async () =>
        {
            var commands = (await Execute("commands_list", []) as JsonArray ?? []).OfType<JsonObject>().ToArray();
            var current = item.Object("data").Text("serviceId");
            var list = new ComboBox
            {
                ItemsSource = commands.Select(command => command.Text("name")).ToArray(),
                SelectedIndex = Array.FindIndex(commands, command => command.Text("id") == current),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var detail = Muted("");
            string Selected() => list.SelectedIndex >= 0 ? commands[list.SelectedIndex].Text("id") : "";
            async Task Update(string operation)
            {
                string id = Selected();
                if (id.Length == 0) return;
                var reply = await Execute(operation, new() { ["id"] = id });
                detail.Text = log ? TextOf(reply) : reply.Text("state", L("已提交", "Requested"));
            }
            body.Children.Add(list);
            if (log) body.Children.Add(Action(L("刷新日志", "Refresh log"), () => Update("command_log")));
            else body.Children.Add(Row(
                Action(L("启动", "Start"), () => Update("command_start")),
                Action(L("停止", "Stop"), () => Update("command_stop")),
                Action(L("重启", "Restart"), () => Update("command_restart"))));
            body.Children.Add(detail);
            if (log) await Update("command_log");
            list.SelectionChanged += async (_, _) => await Act(async () =>
            {
                string id = Selected();
                if (id.Length == 0) return;
                var copy = (JsonObject)item.DeepClone();
                EnsureData(copy)["serviceId"] = id;
                await Execute("item_save", new() { ["spaceId"] = spaceId, ["item"] = copy });
                if (log) await Update("command_log");
            });
        });
        return new ScrollViewer { Content = body };
    }

    private Control LegacyCommand(string spaceId, JsonObject item, Canvas? _)
    {
        var body = new StackPanel { Spacing = 4 };
        var command = new TextBox { Text = item.Object("data").Text("command"), AcceptsReturn = true, MinHeight = 52 };
        body.Children.Add(command);
        body.Children.Add(Row(
            Action(L("保存", "Save"), async () =>
            {
                var next = (JsonObject)item.DeepClone();
                EnsureData(next)["command"] = command.Text ?? "";
                await Execute("item_save", new() { ["spaceId"] = spaceId, ["item"] = next });
                Report(L("已保存", "Saved"));
            }),
            Action(L("发送到终端", "Send to terminal"), async () =>
            {
                var available = _terminals.Where(terminal => terminal.Running).ToArray();
                if (available.Length == 0) throw new InvalidOperationException(L("请先打开一个终端。", "Open a terminal first."));
                var document = await Execute("workspace_read", []);
                var active = document.Array("spaces").OfType<JsonObject>().FirstOrDefault(space => space.Text("id") == spaceId);
                string Name(TerminalTile terminal) => active.Array("items").OfType<JsonObject>()
                    .FirstOrDefault(candidate => candidate.Text("id") == terminal.Id).Text("title", terminal.Id);
                var choose = Menu(L("选择终端", "Choose terminal"), available.Select(terminal =>
                    (Name(terminal), (Func<Task>)(async () => await Execute("terminal_write", new()
                    { ["itemId"] = terminal.Id, ["text"] = command.Text + "\r" })))).ToArray());
                body.Children.Add(choose);
                choose.Flyout?.ShowAt(choose);
            })));
        return body;
    }

    private Control PageTile(string page, string label)
    {
        var body = new StackPanel { Spacing = 4 };
        var summary = Muted("");
        body.Children.Add(summary);
        body.Children.Add(Action(label, () => { Navigate(page); return Task.CompletedTask; }));
        body.AttachedToVisualTree += async (_, _) => await Act(async () =>
        {
            if (page == "capture")
            {
                summary.Text = string.Join(Environment.NewLine, (await Execute("capture_list", []) as JsonArray ?? [])
                    .Take(5).Select(entry => Path.GetFileName(TextOf(entry, "path"))));
                return;
            }
            if (!(await Execute("relay_status", [])).Flag("paired")) return;
            if (page == "clipboard") summary.Text = (await Execute("clipboard_read", [])).Text("text");
            else summary.Text = string.Join(Environment.NewLine, (await Execute("files_list", []) as JsonArray ?? [])
                .Take(5).Select(file => file.Text("name")));
        });
        return new ScrollViewer { Content = body };
    }
    private Control FilesComponent(string spaceId, JsonObject item)
    {
        var contents = new StackPanel { Spacing = 4 };
        string path = item.Object("data").Text("path");

        async Task Show(string folder)
        {
            var result = await Execute("local_list", new() { ["path"] = folder });
            contents.Children.Clear();
            path = result.Text("path", folder);
            var options = new List<(string Label, Func<Task> Action)>
            {
                (L("选择目录", "Choose folder"), async () =>
                {
                    var folders = await RequireTopLevel().StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
                    if (folders.FirstOrDefault()?.TryGetLocalPath() is string selected)
                        await SaveTilePath(spaceId, item, selected);
                }),
                (L("输入路径", "Enter path"), () => Form(L("目录", "Folder"), values =>
                    SaveTilePath(spaceId, item, values.RequireText("path")), new Field("path", L("路径", "Path"), path))),
                (L("新建目录", "New folder"), () => Form(L("新建目录", "New folder"), async values =>
                {
                    var name = values.RequireText("name");
                    if (name != Path.GetFileName(name) || name is "." or "..") throw new ArgumentException(L("请输入目录名。", "Enter a folder name."));
                    await Change("local_mkdir", new() { ["path"] = Path.Combine(path, name) });
                }, new Field("name", L("名称", "Name"))))
            };
            if (Has(ClientFeatures.Terminals))
                options.Add((L("在此目录添加终端", "Add terminal here"), () =>
                    SaveItem(spaceId, NewItem("terminal", L("终端", "Terminal"), new()
                    {
                        ["workingDirectory"] = path, ["shell"] = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh"
                    }))));
            var location = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            if (Directory.GetParent(path) is { } parent)
            {
                var crumb = Action(parent.Name, () => SaveTilePath(spaceId, item, parent.FullName), parent.FullName);
                crumb.MaxWidth = 130;
                crumb.Content = new TextBlock { Text = parent.Name, TextTrimming = TextTrimming.CharacterEllipsis };
                location.Children.Add(crumb);
                var separator = TrayGlyph.Icon("next", 11);
                Grid.SetColumn(separator, 1);
                location.Children.Add(separator);
            }
            var current = new TextBlock
            {
                Text = new DirectoryInfo(path).Name, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(current, 2);
            location.Children.Add(current);
            ToolTip.SetTip(location, path);
            var pathBar = new Border { Child = TitleRow(location, Menu("⋯", options.ToArray())) };
            pathBar.Classes.Add("file-path");
            contents.Children.Add(pathBar);
            foreach (var entry in result.Array("entries").OfType<JsonObject>())
            {
                var leaf = entry.Text("path");
                var name = entry.Text("name");
                bool directory = entry.Flag("isDirectory");
                var link = Action(name, directory
                    ? () => SaveTilePath(spaceId, item, leaf) : () => PreviewAsync(leaf), glyph: directory ? "files" : "file");
                if (directory) link.Classes.Add("folder-entry");
                link.Classes.Add("file-entry");
                var menu = Menu("⋯", (L("复制到…", "Copy to…"), () => Form(name, values => Change("local_copy",
                    new() { ["source"] = leaf, ["destination"] = values.RequireText("path") }),
                    new Field("path", L("目标路径", "Destination")))));
                menu.Classes.Add("file-entry-menu");
                contents.Children.Add(TitleRow(link, menu));
            }
        }

        contents.AttachedToVisualTree += async (_, _) =>
        {
            try { await Show(path); }
            catch (Exception error)
            {
                contents.Children.Clear();
                contents.Children.Add(Muted(error.Message));
                contents.Children.Add(Action(L("选择目录", "Choose folder"), () => Form(L("目录", "Folder"),
                    values => SaveTilePath(spaceId, item, values.RequireText("path")), new Field("path", L("路径", "Path"), path))));
            }
        };
        return new ScrollViewer { Content = contents, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private Task SaveTilePath(string spaceId, JsonObject item, string path)
    {
        var clone = (JsonObject)item.DeepClone();
        EnsureData(clone)["path"] = path;
        return SaveItem(spaceId, clone);
    }

    private async Task PreviewAsync(string path)
    {
        var preview = await Execute("local_preview", new() { ["path"] = path });
        _contents.Children.Clear();
        Header(Path.GetFileName(path), Action(L("返回", "Back"), LoadAsync));
        _contents.Children.Add(Body(TextOf(preview)));
    }

    private Control TerminalComponent(JsonObject item)
    {
        string id = item.Text("id");
        var data = item.Object("data");
        var terminal = new TerminalTile(id, new TextBox
        {
            Text = data.Text("history"),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily(OperatingSystem.IsWindows() ? "Cascadia Mono,Consolas" : "DejaVu Sans Mono,monospace")
        });
        terminal.Output.Classes.Add("terminal");
        ScrollViewer.SetHorizontalScrollBarVisibility(terminal.Output, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(terminal.Output, ScrollBarVisibility.Auto);
        _terminals.Add(terminal);
        var input = new TextBox { PlaceholderText = L("输入命令后按 Enter", "Type and press Enter") };
        input.Classes.Add("entry");
        input.Classes.Add("terminal-input");

        async Task Start(string? passphrase = null)
        {
            var args = new JsonObject
            {
                ["itemId"] = id,
                ["shell"] = data.Text("shell", OperatingSystem.IsWindows() ? "cmd" : "/bin/sh"),
                ["workingDirectory"] = data.Text("workingDirectory", data.Text("directory")),
                ["hostId"] = data.Text("hostId")
            };
            if (passphrase is not null) args["passphrase"] = passphrase;
            var opened = await Execute("terminal_open", args);
            if (opened.Text("status") == "cancelled") return;
            terminal.Running = true;
            await ResizeTerminalAsync(terminal);
            input.Focus();
        }

        async Task Send()
        {
            if (!terminal.Running) await Start();
            if (input.Text is not { Length: > 0 } text) return;
            await Execute("terminal_write", new() { ["itemId"] = id, ["text"] = text + "\r" });
            input.Clear();
        }

        var controls = Row(
            Action(L("打开", "Open"), () => data.Text("hostId").Length == 0 ? Start()
                : Form(L("SSH 连接", "SSH connection"), async values =>
                {
                    await Start(values.Text("passphrase"));
                    await LoadAsync();
                }, new Field("passphrase", L("私钥口令（可选）", "Key passphrase (optional)"), Secret: true))),
            Action(L("关闭", "Close"), async () =>
            {
                await Execute("terminal_close", new() { ["itemId"] = id });
                terminal.Running = false;
            }));
        if (data.Text("hostId").Length == 0) controls.Children.Add(Action(L("中断", "Interrupt"), async () =>
            await Execute("terminal_write", new() { ["itemId"] = id, ["text"] = "\u0003" })));

        input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await Act(Send);
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(controls);
        Grid.SetRow(terminal.Output, 1);
        layout.Children.Add(terminal.Output);
        var command = TitleRow(input, IconButton("send", L("发送", "Send"), Send));
        command.Margin = new Thickness(0, 4, 0, 0);
        Grid.SetRow(command, 2);
        layout.Children.Add(command);
        terminal.Output.SizeChanged += async (_, _) =>
        {
            if (terminal.Running) await Act(() => ResizeTerminalAsync(terminal));
        };
        return layout;
    }

    private sealed class TerminalTile(string id, TextBox output)
    {
        public string Id { get; } = id;
        public TextBox Output { get; } = output;
        public bool Running { get; set; }
        public bool Reading { get; set; }
        public bool Checked { get; set; }
        public (int Columns, int Rows) Size { get; set; }
    }

    private static readonly Regex AnsiSequence = new(
        "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private async Task RefreshTerminalsAsync()
    {
        foreach (var terminal in _terminals.ToArray())
        {
            if (terminal.Reading || terminal.Checked && !terminal.Running) continue;
            terminal.Reading = true;
            try
            {
                var value = await Execute("terminal_read", new() { ["itemId"] = terminal.Id });
                var output = AnsiSequence.Replace(TextOf(value), "");
                if (output.Length > 0 && _terminals.Contains(terminal) && terminal.Output.Text != output)
                {
                    terminal.Output.Text = output;
                    terminal.Output.CaretIndex = output.Length;
                    terminal.Running = value.Flag("active", true);
                }
                if (value is JsonObject state && state.ContainsKey("active")) terminal.Running = state.Flag("active");
            }
            catch { }
            finally { terminal.Reading = false; terminal.Checked = true; }
        }
    }

    private async Task ResizeTerminalAsync(TerminalTile terminal)
    {
        var bounds = terminal.Output.Bounds;
        if (bounds.Width < 10 || bounds.Height < 10) return;
        var size = (Columns: Math.Clamp((int)(bounds.Width / 8), 20, 360),
            Rows: Math.Clamp((int)(bounds.Height / 17), 4, 180));
        if (terminal.Size == size) return;
        terminal.Size = size;
        await Execute("terminal_resize", new()
        {
            ["itemId"] = terminal.Id, ["columns"] = size.Columns, ["rows"] = size.Rows
        });
    }

    private Control BoardComponent(string spaceId, JsonObject item, bool editable)
    {
        var board = new DrawingBoard(item.Object("data").Array("strokes"), editable);
        if (!editable) return board;
        var panel = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        panel.Children.Add(board);
        var saves = new SemaphoreSlim(1);
        async Task Save()
        {
            await saves.WaitAsync();
            try
            {
                var clone = (JsonObject)item.DeepClone();
                var strokes = board.Strokes.DeepClone();
                EnsureData(clone)["strokes"] = strokes;
                await Execute("item_save", new() { ["spaceId"] = spaceId, ["item"] = clone });
                EnsureData(item)["strokes"] = strokes.DeepClone();
            }
            finally { saves.Release(); }
        }
        board.Edited += async () => await Act(Save);
        var tools = new WrapPanel { ItemSpacing = 3, LineSpacing = 2 };
        Button? selected = null;
        foreach (var (tool, label) in new[]
        {
            (BoardTool.Pen, L("画笔", "Pen")), (BoardTool.Line, L("直线", "Line")),
            (BoardTool.Arrow, L("箭头", "Arrow")), (BoardTool.Rectangle, L("矩形", "Rectangle")),
            (BoardTool.Ellipse, L("椭圆", "Ellipse")), (BoardTool.Eraser, L("橡皮", "Eraser"))
        })
        {
            Button? option = null;
            option = Action(label, () =>
            {
                selected?.Classes.Remove("primary");
                selected = option;
                selected!.Classes.Add("primary");
                board.Tool = tool;
                return Task.CompletedTask;
            });
            if (tool == BoardTool.Pen)
            {
                selected = option;
                option.Classes.Add("primary");
            }
            tools.Children.Add(option);
        }
        tools.Children.Add(Action(L("撤销", "Undo"), async () =>
        {
            if (board.Strokes.Count == 0) return;
            board.Strokes.RemoveAt(board.Strokes.Count - 1);
            board.InvalidateVisual();
            await Save();
        }));
        Grid.SetRow(tools, 1);
        panel.Children.Add(tools);
        return panel;
    }

    private enum BoardTool { Pen, Line, Arrow, Rectangle, Ellipse, Eraser }

    private sealed class DrawingBoard : Control
    {
        private const double SheetWidth = 640;
        private const double SheetHeight = 300;
        private readonly bool _editable;
        private JsonObject? _draft;
        private IPointer? _pointer;
        private Point _anchor;
        private BoardTool _activeTool;
        private (double Scale, double Left, double Top) _sheet;

        public DrawingBoard(JsonArray strokes, bool editable)
        {
            Strokes = (JsonArray)strokes.DeepClone();
            _editable = editable;
            MinHeight = 114;
            ClipToBounds = true;
            if (editable) Cursor = new Cursor(StandardCursorType.Cross);
        }

        public JsonArray Strokes { get; }
        public BoardTool Tool { get; set; }
        public event Func<Task>? Edited;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            bool dark = ActualThemeVariant == ThemeVariant.Dark;
            var paper = dark ? Color.Parse("#101923") : Colors.White;
            var defaultStroke = Color.Parse(dark ? "#C1ED8B" : "#315545");
            context.FillRectangle(new SolidColorBrush(paper), new Rect(Bounds.Size));
            double minX = _editable ? 0 : double.PositiveInfinity;
            double minY = _editable ? 0 : double.PositiveInfinity;
            double maxX = _editable ? SheetWidth : double.NegativeInfinity;
            double maxY = _editable ? SheetHeight : double.NegativeInfinity;
            foreach (var stroke in Strokes.OfType<JsonObject>())
                foreach (var point in stroke.Array("points").OfType<JsonObject>())
                {
                    double x = Coordinate(point, "x"), y = Coordinate(point, "y");
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            if (!double.IsFinite(minX) || Bounds.Width <= 20 || Bounds.Height <= 20) return;
            var width = Math.Max(1, maxX - minX);
            var height = Math.Max(1, maxY - minY);
            var scale = Math.Min(4, Math.Min((Bounds.Width - 20) / width, (Bounds.Height - 20) / height));
            _sheet = (scale, (Bounds.Width - width * scale) / 2 - minX * scale, (Bounds.Height - height * scale) / 2 - minY * scale);
            foreach (var stroke in Strokes.OfType<JsonObject>().Append(_draft).OfType<JsonObject>())
            {
                var storedColor = stroke.Text("color");
                var color = stroke.Flag("erase") ? paper
                    : !storedColor.Equals("#315545", StringComparison.OrdinalIgnoreCase) &&
                        Color.TryParse(storedColor, out var parsed) ? parsed : defaultStroke;
                var pen = new Pen(new SolidColorBrush(color), Math.Max(stroke.Flag("erase") ? 4 : 1.5, scale * 2.5),
                    lineCap: PenLineCap.Round);
                Point? previous = null;
                foreach (var point in stroke.Array("points").OfType<JsonObject>())
                {
                    var position = new Point(_sheet.Left + Coordinate(point, "x") * scale, _sheet.Top + Coordinate(point, "y") * scale);
                    if (previous is Point start) context.DrawLine(pen, start, position);
                    else context.DrawEllipse(pen.Brush, null, new Rect(position.X - .8, position.Y - .8, 1.6, 1.6));
                    previous = position;
                }
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!_editable || _pointer is not null || _sheet.Scale <= 0 ||
                !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _pointer = e.Pointer;
            _activeTool = Tool;
            _anchor = SheetPoint(e.GetPosition(this));
            _draft = new JsonObject { ["color"] = "#315545", ["points"] = new JsonArray() };
            if (_activeTool == BoardTool.Eraser) _draft["erase"] = true;
            UpdateDraft(e.GetPosition(this));
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer == _pointer) UpdateDraft(e.GetPosition(this));
        }

        protected override async void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.Pointer != _pointer || _draft is null) return;
            UpdateDraft(e.GetPosition(this));
            Strokes.Add(_draft);
            _draft = null;
            _pointer = null;
            e.Pointer.Capture(null);
            if (Edited is not null) await Edited.Invoke();
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (e.Pointer != _pointer) return;
            _pointer = null;
            _draft = null;
            InvalidateVisual();
        }

        private Point SheetPoint(Point point) => new(
            Math.Clamp((point.X - _sheet.Left) / _sheet.Scale, 0, 100000),
            Math.Clamp((point.Y - _sheet.Top) / _sheet.Scale, 0, 100000));

        private void UpdateDraft(Point position)
        {
            var end = SheetPoint(position);
            var points = (JsonArray)_draft!["points"]!;
            if (_activeTool is BoardTool.Pen or BoardTool.Eraser) points.Add(PointNode(end));
            else
            {
                points.Clear();
                foreach (var point in Shape(_activeTool, _anchor, end)) points.Add(PointNode(point));
            }
            InvalidateVisual();
        }

        // Shapes use the existing stroke format so older and read-only clients can render them.
        private static IEnumerable<Point> Shape(BoardTool tool, Point start, Point end)
        {
            switch (tool)
            {
                case BoardTool.Rectangle:
                    return [start, new Point(end.X, start.Y), end, new Point(start.X, end.Y), start];
                case BoardTool.Ellipse:
                    return Enumerable.Range(0, 49).Select(index =>
                    {
                        double angle = index * Math.Tau / 48;
                        return new Point((start.X + end.X + Math.Abs(end.X - start.X) * Math.Cos(angle)) / 2,
                            (start.Y + end.Y + Math.Abs(end.Y - start.Y) * Math.Sin(angle)) / 2);
                    });
                case BoardTool.Arrow:
                    var direction = end - start;
                    double length = Math.Min(24, Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y) / 3);
                    double heading = Math.Atan2(direction.Y, direction.X);
                    Point Wing(double angle) => new(Math.Max(0, end.X - length * Math.Cos(angle)),
                        Math.Max(0, end.Y - length * Math.Sin(angle)));
                    return [start, end, Wing(heading - 0.55), end, Wing(heading + 0.55)];
                default:
                    return [start, end];
            }
        }

        private static JsonObject PointNode(Point point) => new() { ["x"] = point.X, ["y"] = point.Y };

        private static double Coordinate(JsonObject point, string axis) =>
            point[axis] is JsonValue value && value.TryGetValue<double>(out var coordinate) && double.IsFinite(coordinate) ? coordinate : 0;
    }
}
