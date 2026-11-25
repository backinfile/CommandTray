using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private readonly List<DrawingBoard> _drawings = [];
    private readonly Dictionary<string, BoardHistory> _boardHistories = new(StringComparer.Ordinal);

    private Control BoardComponent(string spaceId, JsonObject item)
    {
        string id = item.Text("id");
        var saved = item.Object("data").Array("strokes");
        string key = spaceId + "\n" + id;
        if (!_boardHistories.TryGetValue(key, out var history) || !JsonNode.DeepEquals(history.Current, saved))
            _boardHistories[key] = history = new BoardHistory(saved);

        async Task SaveAsync(JsonArray strokes)
        {
            var document = await Execute("workspace_read", []);
            var current = document.Array("spaces").OfType<JsonObject>().FirstOrDefault(space => space.Text("id") == spaceId)
                .Array("items").OfType<JsonObject>().FirstOrDefault(component => component.Text("id") == id);
            if (current is null || !JsonNode.DeepEquals(history.Current, current.Object("data").Array("strokes")))
                throw new InvalidOperationException(L("画板已被修改，请刷新后重试。", "The board has changed. Refresh and try again."));
            var clone = (JsonObject)current.DeepClone();
            EnsureData(clone)["strokes"] = strokes.DeepClone();
            await Execute("item_save", new() { ["spaceId"] = spaceId, ["item"] = clone });
            EnsureData(item)["strokes"] = strokes.DeepClone();
        }

        var board = new DrawingBoard(history, SaveAsync);
        _drawings.Add(board);
        AutomationProperties.SetName(board, item.Text("title", L("画板", "Board")));
        var textInput = new TextBox { IsVisible = false, MaxLength = 120,
            PlaceholderText = L("画板文字", "Board text") };
        AutomationProperties.SetName(textInput, L("画板文字", "Board text"));
        textInput.TextChanged += (_, _) => board.LabelText = textInput.Text ?? "";

        var tools = Row();
        var toolButtons = new Dictionary<BoardTool, Button>();
        foreach (var (tool, glyph, label) in new[]
        {
            (BoardTool.Select, "pointer", L("选择", "Select")), (BoardTool.Pen, "pen", L("画笔", "Pen")),
            (BoardTool.Line, "line", L("直线", "Line")), (BoardTool.Arrow, "arrow", L("箭头", "Arrow")),
            (BoardTool.Rectangle, "rectangle", L("矩形", "Rectangle")), (BoardTool.Ellipse, "ellipse", L("椭圆", "Ellipse")),
            (BoardTool.Text, "text", L("文字", "Text")), (BoardTool.Eraser, "eraser", L("橡皮", "Eraser"))
        })
        {
            var button = IconButton(glyph, label, () =>
            {
                board.SetTool(tool);
                textInput.IsVisible = tool == BoardTool.Text;
                if (textInput.IsVisible) textInput.Focus();
                return Task.CompletedTask;
            });
            button.Classes.Add("board-tool");
            tools.Children.Add(button);
            toolButtons.Add(tool, button);
        }
        var undo = IconButton("undo", L("撤销", "Undo"), () => board.TravelAsync(true));
        var redo = IconButton("redo", L("重做", "Redo"), () => board.TravelAsync(false));
        var palette = MoreActions(L("画笔样式", "Stroke style"),
            (L("蓝色", "Blue"), () => SetInk("#8DB8FF")), (L("浅绿", "Green"), () => SetInk("#C1ED8B")),
            (L("琥珀", "Amber"), () => SetInk("#E6BD77")), (L("玫瑰", "Rose"), () => SetInk("#EA9E9E")),
            (L("细线", "Thin stroke"), () => SetWidth(2)), (L("粗线", "Thick stroke"), () => SetWidth(5)));
        tools.Children.Add(undo);
        tools.Children.Add(redo);
        tools.Children.Add(palette);

        var count = Muted("");
        var zoom = Muted("");
        zoom.VerticalAlignment = VerticalAlignment.Center;
        var minus = IconButton("minus", L("缩小", "Zoom out"), () => AdjustZoom(-.1));
        var plus = IconButton("plus", L("放大", "Zoom in"), () => AdjustZoom(.1));
        var grid = IconButton("spaces", L("显示网格", "Toggle grid"), () =>
        {
            board.GridVisible = !board.GridVisible;
            board.Redraw();
            return Task.CompletedTask;
        });
        var export = IconButton("download", L("导出 PNG", "Export PNG"), () => ExportBoardAsync(board, item.Text("title")));
        var footer = new Border { Child = TitleRow(count, Row(minus, zoom, plus, grid, export)) };
        footer.Classes.Add("board-chrome");
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var bar = new Border { Child = new ScrollViewer
        {
            Content = tools,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false
        }};
        bar.Classes.Add("board-chrome");
        Grid.SetRow(textInput, 1);
        Grid.SetRow(board, 2);
        Grid.SetRow(footer, 3);
        panel.Children.Add(bar);
        panel.Children.Add(textInput);
        panel.Children.Add(board);
        panel.Children.Add(footer);
        board.MinHeight = _compact ? 240 : 118;
        board.Updated += RefreshControls;
        board.Failed += error => Report(error.Message);
        RefreshControls();
        return panel;

        Task SetInk(string color) { board.Ink = color; return Task.CompletedTask; }
        Task SetWidth(double width) { board.StrokeWidth = width; return Task.CompletedTask; }
        Task AdjustZoom(double change)
        {
            board.Zoom = Math.Clamp(Math.Round(board.Zoom + change, 1), .6, 2);
            board.Redraw();
            return Task.CompletedTask;
        }

        void RefreshControls()
        {
            foreach (var (tool, button) in toolButtons) button.Classes.Set("selected", board.Tool == tool);
            undo.IsEnabled = board.CanUndo;
            redo.IsEnabled = board.CanRedo;
            count.Text = board.Saving ? L("正在保存…", "Saving…") :
                L($"{history.Current.Count} 个对象", $"{history.Current.Count} objects");
            zoom.Text = $"{board.Zoom:P0}";
            grid.Classes.Set("selected", board.GridVisible);
        }
    }

    private async Task ExportBoardAsync(DrawingBoard board, string title)
    {
        var options = new FilePickerSaveOptions
        {
            SuggestedFileName = string.IsNullOrWhiteSpace(title) ? "board.png" : title + ".png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
            ShowOverwritePrompt = true
        };
        using var bitmap = new RenderTargetBitmap(new PixelSize(
            Math.Max(1, (int)Math.Ceiling(board.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(board.Bounds.Height))));
        bitmap.Render(board);
        var file = await RequireTopLevel().StorageProvider.SaveFilePickerAsync(options);
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        Report(L("已导出", "Exported"));
    }

    private enum BoardTool { Select, Pen, Line, Arrow, Rectangle, Ellipse, Text, Eraser }

    private sealed class BoardHistory(JsonArray initial)
    {
        public JsonArray Current { get; set; } = (JsonArray)initial.DeepClone();
        public List<JsonArray> Undo { get; } = [];
        public List<JsonArray> Redo { get; } = [];
    }

    private sealed class DrawingBoard(BoardHistory history, Func<JsonArray, Task> save) : Control
    {
        private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#101923"));
        private static readonly IBrush Dots = new SolidColorBrush(Color.Parse("#2D3D4F"));
        private static readonly IBrush Selection = new SolidColorBrush(Color.Parse("#8DB8FF"));
        private static readonly Pen Outline = new(Selection, 1, DashStyle.Dash);
        private IPointer? _pointer;
        private Point _anchor;
        private JsonObject? _draft;
        private JsonObject? _target;
        private JsonObject? _arrowStart;
        private Vector _offset;
        private (double Scale, double Left, double Top) _sheet;

        public BoardTool Tool { get; private set; }
        public string Ink { get; set; } = "#8DB8FF";
        public double StrokeWidth { get; set; } = 2;
        public string LabelText { get; set; } = "";
        public bool GridVisible { get; set; } = true;
        public double Zoom { get; set; } = 1;
        public bool Saving { get; private set; }
        public bool Editing => Saving || _pointer is not null;
        public bool CanUndo => !Saving && history.Undo.Count > 0;
        public bool CanRedo => !Saving && history.Redo.Count > 0;
        public event Action? Updated;
        public event Action<Exception>? Failed;

        public void SetTool(BoardTool tool)
        {
            Tool = tool;
            _target = null;
            Cursor = new Cursor(tool == BoardTool.Select ? StandardCursorType.Arrow : StandardCursorType.Cross);
            Redraw();
        }

        public void Redraw()
        {
            InvalidateVisual();
            Updated?.Invoke();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            using var clip = context.PushClip(new Rect(Bounds.Size));
            context.FillRectangle(Paper, new Rect(Bounds.Size));
            if (GridVisible)
                for (double x = 8; x < Bounds.Width; x += 16)
                    for (double y = 8; y < Bounds.Height; y += 16)
                        context.FillRectangle(Dots, new Rect(x, y, 1.1, 1.1));
            var points = history.Current.OfType<JsonObject>().SelectMany(Points).ToArray();
            var bounds = Extent(points);
            double x0 = points.Length == 0 ? 0 : Math.Min(0, bounds.X);
            double y0 = points.Length == 0 ? 0 : Math.Min(0, bounds.Y);
            double w = Math.Max(640, bounds.Right) - x0;
            double h = Math.Max(300, bounds.Bottom) - y0;
            if (Bounds.Width <= 18 || Bounds.Height <= 18) return;
            var scale = Math.Min(4, Math.Min((Bounds.Width - 18) / w, (Bounds.Height - 18) / h)) * Zoom;
            _sheet = (scale, (Bounds.Width - w * scale) / 2 - x0 * scale, (Bounds.Height - h * scale) / 2 - y0 * scale);

            foreach (var stroke in history.Current.OfType<JsonObject>().Append(_draft).OfType<JsonObject>())
            {
                bool selected = ReferenceEquals(stroke, _target);
                var curve = VisiblePoints(stroke).Select(point => Screen(point + (selected ? _offset : default))).ToArray();
                if (curve.Length == 0) continue;
                var color = stroke.Text("color");
                var brush = stroke.Flag("erase") ? Paper
                    : !color.Equals("#315545", StringComparison.OrdinalIgnoreCase) && Color.TryParse(color, out var parsed)
                        ? new SolidColorBrush(parsed) : Selection;
                if (stroke.Text("text") is { Length: > 0 } label)
                {
                    var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        Typeface.Default, Math.Clamp(16 * scale, 9, 48), brush);
                    context.DrawText(text, curve[0]);
                    if (selected) context.DrawRectangle(null, Outline,
                        new Rect(curve[0], new Size(text.Width, text.Height)).Inflate(4));
                    continue;
                }
                var pen = new Pen(brush, Math.Max(stroke.Flag("erase") ? 4 : 1.4,
                    scale * Math.Clamp(Numeric(stroke, "width", 2.5), 1, 16)), lineCap: PenLineCap.Round);
                if (curve.Length == 1)
                    context.DrawEllipse(brush, null, new Rect(curve[0].X - 1.1, curve[0].Y - 1.1, 2.2, 2.2));
                for (int n = 1; n < curve.Length; n++) context.DrawLine(pen, curve[n - 1], curve[n]);
                if (selected) context.DrawRectangle(null, Outline, Extent(curve).Inflate(5));
            }
        }

        protected override async void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (Editing || _sheet.Scale <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var point = Sheet(e.GetPosition(this));
            _target = Tool is BoardTool.Select or BoardTool.Eraser ? Hit(point) : null;
            if (Tool == BoardTool.Eraser)
            {
                e.Handled = true;
                if (_target is not null)
                {
                    var index = history.Current.IndexOf(_target);
                    await EditAsync(strokes => strokes.RemoveAt(index));
                }
                return;
            }
            if (Tool == BoardTool.Text)
            {
                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(LabelText))
                {
                    var text = Create([point]);
                    text["text"] = LabelText.Trim();
                    await EditAsync(strokes => strokes.Add(text));
                }
                return;
            }
            _anchor = point;
            if (Tool != BoardTool.Select) _draft = Create([point]);
            _arrowStart = Tool == BoardTool.Arrow ? Hit(point) : null;
            _pointer = e.Pointer;
            e.Pointer.Capture(this);
            e.Handled = true;
            Redraw();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer == _pointer) UpdateDraft(Sheet(e.GetPosition(this)), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }

        protected override async void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.Pointer != _pointer) return;
            var point = Sheet(e.GetPosition(this));
            UpdateDraft(point, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            var draft = _draft;
            var index = _target is null ? -1 : history.Current.IndexOf(_target);
            var offset = _offset;
            var moved = _target is not null && offset.Length > .5
                ? VisiblePoints(_target).Select(value => value + offset).ToArray() : null;
            if (draft is not null && _arrowStart is { } from && Hit(point) is { } to &&
                !ReferenceEquals(from, to) && from.Text("id") is { Length: > 0 } fromId &&
                to.Text("id") is { Length: > 0 } toId)
            {
                draft["from"] = fromId;
                draft["to"] = toId;
            }
            ResetGesture();
            e.Pointer.Capture(null);
            e.Handled = true;
            if (draft is not null)
                await EditAsync(strokes => strokes.Add(draft));
            else if (index >= 0 && moved is not null)
            {
                await EditAsync(strokes =>
                {
                    if (strokes[index] is not JsonObject selected) return;
                    selected["points"] = Nodes(moved);
                    selected.Remove("from");
                    selected.Remove("to");
                });
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (e.Pointer == _pointer) ResetGesture();
        }

        private void ResetGesture()
        {
            _pointer = null;
            _draft = null;
            _arrowStart = null;
            _offset = default;
            Redraw();
        }

        private JsonObject Create(IEnumerable<Point> points) => new()
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["color"] = Ink, ["width"] = StrokeWidth,
            ["points"] = Nodes(points)
        };

        private void UpdateDraft(Point end, bool constrain)
        {
            if (_draft is not null)
            {
                if (Tool == BoardTool.Pen)
                {
                    var points = (JsonArray)_draft["points"]!;
                    if (points.Count < 4096) points.Add(Node(end));
                }
                else
                {
                    if (constrain && Tool is BoardTool.Line or BoardTool.Arrow)
                        end = Math.Abs(end.X - _anchor.X) > Math.Abs(end.Y - _anchor.Y)
                            ? new Point(end.X, _anchor.Y) : new Point(_anchor.X, end.Y);
                    _draft["points"] = Nodes(Shape(Tool, _anchor, end));
                }
            }
            else if (_target is not null) _offset = end - _anchor;
            InvalidateVisual();
        }

        private JsonObject? Hit(Point point)
        {
            var threshold = 9 / _sheet.Scale;
            foreach (var stroke in history.Current.OfType<JsonObject>().Reverse())
            {
                if (stroke.Flag("erase")) continue;
                var geometry = VisiblePoints(stroke);
                if (geometry.Length == 0) continue;
                if (stroke.Text("text") is { Length: > 0 } label &&
                    new Rect(geometry[0], new Size(Math.Min(1400, label.Length * 13), 21)).Inflate(threshold).Contains(point))
                    return stroke;
                if (geometry.Length == 1 && Difference(geometry[0], point).Length < threshold) return stroke;
                for (var i = 1; i < geometry.Length; i++)
                    if (Distance(point, geometry[i - 1], geometry[i]) <= threshold) return stroke;
                if (geometry.Length > 4 && geometry[0] == geometry[^1] && Extent(geometry).Contains(point)) return stroke;
            }
            return null;
        }

        private Point[] VisiblePoints(JsonObject stroke)
        {
            if (stroke.Text("from") is { Length: > 0 } from && stroke.Text("to") is { Length: > 0 } to)
            {
                var first = Box(from);
                var second = Box(to);
                if (first is { } source && second is { } destination)
                    return Arrow(Edge(source, destination.Center), Edge(destination, source.Center)).ToArray();
            }
            return Points(stroke).ToArray();

            Rect? Box(string id)
            {
                var node = history.Current.OfType<JsonObject>().FirstOrDefault(item => item.Text("id") == id);
                if (node is null) return null;
                var box = Extent(Points(node));
                return ReferenceEquals(node, _target) ? new Rect(box.Position + _offset, box.Size) : box;
            }
        }

        private static Point Edge(Rect bounds, Point toward)
        {
            var center = bounds.Center;
            var vector = toward - center;
            var factor = Math.Max(Math.Abs(vector.X) / Math.Max(3, bounds.Width / 2),
                Math.Abs(vector.Y) / Math.Max(3, bounds.Height / 2));
            return factor > 0 ? center + vector / factor : center;
        }

        private async Task EditAsync(Action<JsonArray> edit)
        {
            if (Saving) return;
            var modified = (JsonArray)history.Current.DeepClone();
            edit(modified);
            await SaveAsync(modified, () =>
            {
                history.Undo.Add(history.Current);
                if (history.Undo.Count > 30) history.Undo.RemoveAt(0);
                history.Redo.Clear();
            });
        }

        public async Task TravelAsync(bool backward)
        {
            var source = backward ? history.Undo : history.Redo;
            var target = backward ? history.Redo : history.Undo;
            if (Editing || source.Count == 0) return;
            await SaveAsync(source[^1], () =>
            {
                target.Add(history.Current);
                source.RemoveAt(source.Count - 1);
            });
        }

        private async Task SaveAsync(JsonArray snapshot, Action updateHistory)
        {
            Saving = true;
            Redraw();
            try
            {
                await save(snapshot);
                updateHistory();
                history.Current = snapshot;
                _target = null;
            }
            catch (Exception error) { Failed?.Invoke(error); }
            finally { Saving = false; Redraw(); }
        }

        private Point Screen(Point point) => new(_sheet.Left + point.X * _sheet.Scale, _sheet.Top + point.Y * _sheet.Scale);
        private Point Sheet(Point point) => new(Math.Clamp((point.X - _sheet.Left) / _sheet.Scale, -100000, 100000),
            Math.Clamp((point.Y - _sheet.Top) / _sheet.Scale, -100000, 100000));
        private static Rect Extent(IEnumerable<Point> points)
        {
            var bounds = default(Rect);
            bool first = true;
            foreach (var point in points)
            {
                double x = first ? point.X : Math.Min(bounds.X, point.X);
                double y = first ? point.Y : Math.Min(bounds.Y, point.Y);
                double right = first ? point.X : Math.Max(bounds.Right, point.X);
                double bottom = first ? point.Y : Math.Max(bounds.Bottom, point.Y);
                bounds = new Rect(x, y, right - x, bottom - y);
                first = false;
            }
            return bounds;
        }

        private static double Distance(Point point, Point start, Point end)
        {
            var line = Difference(end, start);
            var position = Difference(point, start);
            if (line.SquaredLength <= 0) return position.Length;
            return Difference(point, start + line * Math.Clamp(Vector.Dot(position, line) / line.SquaredLength, 0, 1)).Length;
        }

        private static Vector Difference(Point point, Point origin) => new(point.X - origin.X, point.Y - origin.Y);

        private static IEnumerable<Point> Shape(BoardTool tool, Point start, Point end) => tool switch
        {
            BoardTool.Rectangle => [start, new Point(end.X, start.Y), end, new Point(start.X, end.Y), start],
            BoardTool.Ellipse => Enumerable.Range(0, 49).Select(index =>
            {
                double angle = index % 48 * Math.Tau / 48;
                return new Point((start.X + end.X + Math.Abs(end.X - start.X) * Math.Cos(angle)) / 2,
                    (start.Y + end.Y + Math.Abs(end.Y - start.Y) * Math.Sin(angle)) / 2);
            }),
            BoardTool.Arrow => Arrow(start, end), _ => [start, end]
        };

        private static IEnumerable<Point> Arrow(Point start, Point end)
        {
            var direction = Difference(end, start);
            double length = Math.Min(18, direction.Length / 3);
            double angle = Math.Atan2(direction.Y, direction.X);
            Point Wing(double a) => end - new Vector(length * Math.Cos(a), length * Math.Sin(a));
            return [start, end, Wing(angle - .55), end, Wing(angle + .55)];
        }

        private static IEnumerable<Point> Points(JsonObject stroke) => stroke.Array("points").OfType<JsonObject>()
            .Select(point => new Point(Numeric(point, "x"), Numeric(point, "y")));
        private static JsonArray Nodes(IEnumerable<Point> points) => new(points.Select(point => (JsonNode)Node(point)).ToArray());
        private static JsonObject Node(Point point) => new() { ["x"] = point.X, ["y"] = point.Y };
        private static double Numeric(JsonObject point, string key, double fallback = 0) =>
            point[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : fallback;
    }
}
