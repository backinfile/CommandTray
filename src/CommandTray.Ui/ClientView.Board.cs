using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
            _boardHistories[key] = history = new BoardHistory(saved) { View = history?.View, GridVisible = history?.GridVisible ?? true };

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

        var board = new DrawingBoard(history, SaveAsync) { Focusable = true };
        _drawings.Add(board);
        AutomationProperties.SetName(board, item.Text("title", L("画板", "Board")));
        var textInput = new TextBox { IsVisible = false, MaxLength = 120,
            PlaceholderText = L("画板文字", "Board text") };
        AutomationProperties.SetName(textInput, L("画板文字", "Board text"));
        textInput.TextChanged += (_, _) => board.LabelText = textInput.Text ?? "";

        Panel tools = _compact ? new WrapPanel { ItemSpacing = 4, LineSpacing = 4 } : Row();
        var toolButtons = new Dictionary<BoardTool, Button>();
        foreach (var (tool, glyph, label) in new[]
        {
            (BoardTool.Select, "pointer", L("选择", "Select")), (BoardTool.Hand, "hand", L("抓手", "Hand")),
            (BoardTool.Pen, "pen", L("画笔", "Pen")), (BoardTool.Highlighter, "highlighter", L("荧光笔", "Highlighter")),
            (BoardTool.Line, "line", L("直线", "Line")), (BoardTool.Arrow, "arrow", L("箭头", "Arrow")),
            (BoardTool.Rectangle, "rectangle", L("矩形", "Rectangle")), (BoardTool.Ellipse, "ellipse", L("椭圆", "Ellipse")),
            (BoardTool.Diamond, "diamond", L("菱形", "Diamond")),
            (BoardTool.Text, "text", L("文字", "Text")), (BoardTool.Eraser, "eraser", L("橡皮", "Eraser"))
        })
        {
            var button = IconButton(glyph, label, () =>
            {
                board.SetTool(tool);
                if (tool == BoardTool.Text) textInput.Focus();
                else board.Focus();
                return Task.CompletedTask;
            });
            button.Classes.Add("board-tool");
            if (_compact) button.MinHeight = button.Width = 44;
            if (tool == BoardTool.Hand) ToolTip.SetTip(button, L("抓手（H 或按住空格拖动）", "Hand (H or hold Space and drag)"));
            tools.Children.Add(button);
            toolButtons.Add(tool, button);
        }
        var undo = BoardButton("undo", L("撤销", "Undo"), () => _ = board.TravelAsync(true));
        var redo = BoardButton("redo", L("重做", "Redo"), () => _ = board.TravelAsync(false));
        var palette = MoreActions(L("画笔样式", "Stroke style"),
            (L("蓝色", "Blue"), () => SetInk("#8DB8FF")), (L("浅绿", "Green"), () => SetInk("#C1ED8B")),
            (L("琥珀", "Amber"), () => SetInk("#E6BD77")), (L("玫瑰", "Rose"), () => SetInk("#EA9E9E")),
            (L("细线", "Thin stroke"), () => SetWidth(2)), (L("粗线", "Thick stroke"), () => SetWidth(5)));
        tools.Children.Add(undo);
        tools.Children.Add(redo);
        tools.Children.Add(palette);

        var count = Muted("");
        count.VerticalAlignment = VerticalAlignment.Center;
        var zoom = Muted("");
        zoom.VerticalAlignment = VerticalAlignment.Center;
        var minus = BoardButton("minus", L("缩小", "Zoom out"), () => board.ZoomBy(1 / 1.25));
        var plus = BoardButton("plus", L("放大", "Zoom in"), () => board.ZoomBy(1.25));
        var fit = BoardButton("fit", L("适应内容", "Fit content"), board.FitContent);
        ToolTip.SetTip(fit, L("适应内容（Shift+1）", "Fit content (Shift+1)"));
        var more = MoreActions(L("画板操作", "Board actions"),
            (L("重置视图", "Reset view"), () => { board.ResetView(); return Task.CompletedTask; }),
            (L("切换网格", "Toggle grid"), () => { board.ToggleGrid(); return Task.CompletedTask; }),
            (L("清空画板", "Clear board"), () => Confirm(L("清空画板？清空后可撤销。", "Clear the board? You can undo this."),
                board.ClearAsync, L("清空画板", "Clear board"))));
        var clear = (MenuItem)((MenuFlyout)more.Flyout!).Items[2]!;
        var export = IconButton("download", L("导出 PNG", "Export PNG"), () => ExportBoardAsync(board, item.Text("title")));
        var duplicate = BoardButton("copy", L("复制对象", "Duplicate object"), () => _ = board.DuplicateAsync());
        var front = BoardButton("front", L("移到顶层", "Bring to front"), () => _ = board.ReorderAsync(true));
        var back = BoardButton("back", L("移到底层", "Send to back"), () => _ = board.ReorderAsync(false));
        var delete = BoardButton("trash", L("删除对象", "Delete object"), () => _ = board.DeleteAsync());
        var selection = Row(duplicate, front, back, delete);
        var context = Row(count, selection);
        context.VerticalAlignment = VerticalAlignment.Center;
        var footer = new Border { Child = ActionBar(context, Row(minus, zoom, plus, fit, export, more)) };
        footer.Classes.Add("board-chrome");
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var bar = new Border { Child = _compact ? tools : new ScrollViewer
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
        board.SizeChanged += (_, _) => board.UpdateViewport();
        RefreshControls();
        return panel;

        Task SetInk(string color) { board.Ink = color; return Task.CompletedTask; }
        Task SetWidth(double width) { board.StrokeWidth = width; return Task.CompletedTask; }
        Button BoardButton(string glyph, string label, Action action) =>
            IconButton(glyph, label, () => { action(); return Task.CompletedTask; });

        void RefreshControls()
        {
            foreach (var (tool, button) in toolButtons) button.Classes.Set("selected", board.Tool == tool);
            undo.IsEnabled = board.CanUndo;
            redo.IsEnabled = board.CanRedo;
            textInput.IsVisible = board.Tool == BoardTool.Text;
            selection.IsVisible = board.HasSelection;
            duplicate.IsEnabled = delete.IsEnabled = !board.Editing;
            front.IsEnabled = board.CanReorder(true);
            back.IsEnabled = board.CanReorder(false);
            clear.IsEnabled = board.CanClear;
            count.Text = board.Saving ? L("正在保存…", "Saving…") :
                L($"{history.Current.Count} 个对象", $"{history.Current.Count} objects");
            zoom.Text = board.Zoom.ToString(board.Zoom < .1 ? "P1" : "P0");
        }
    }

    private async Task ExportBoardAsync(DrawingBoard board, string title)
    {
        using var bitmap = board.CreateExport();
        if (bitmap is null) { Report(L("画板为空", "The board is empty")); return; }
        var options = new FilePickerSaveOptions
        {
            SuggestedFileName = string.IsNullOrWhiteSpace(title) ? "board.png" : title + ".png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
            ShowOverwritePrompt = true
        };
        var file = await RequireTopLevel().StorageProvider.SaveFilePickerAsync(options);
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        Report(L("已导出", "Exported"));
    }

    private enum BoardTool { Select, Hand, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Diamond, Text, Eraser }
    private readonly record struct BoardView(Point Center, double Zoom);

    private sealed class BoardHistory(JsonArray initial)
    {
        public JsonArray Current { get; set; } = (JsonArray)initial.DeepClone();
        public List<JsonArray> Undo { get; } = [];
        public List<JsonArray> Redo { get; } = [];
        public BoardView? View { get; set; }
        public bool GridVisible { get; set; } = true;
    }

    private sealed class DrawingBoard(BoardHistory history, Func<JsonArray, Task> save) : Control
    {
        private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#101923"));
        private static readonly IBrush Dots = new SolidColorBrush(Color.Parse("#2D3D4F"));
        private static readonly IBrush Selection = new SolidColorBrush(Color.Parse("#8DB8FF"));
        private static readonly Point Home = new(320, 150);
        private IPointer? _pointer;
        private Point _anchor;
        private Point _lastPointer;
        private bool _spaceHeld;
        private bool _panning;
        private JsonObject? _draft;
        private JsonObject? _target;
        private JsonObject? _arrowStart;
        private Vector _offset;
        private (double Scale, double Left, double Top) _sheet;

        public BoardTool Tool { get; private set; }
        public string Ink { get; set; } = "#8DB8FF";
        public double StrokeWidth { get; set; } = 2;
        public string LabelText { get; set; } = "";
        public double Zoom => history.View?.Zoom ?? 1;
        public bool Saving { get; private set; }
        public bool Editing => Saving || _pointer is not null;
        public bool HasSelection => _target is not null;
        public bool CanUndo => !Editing && history.Undo.Count > 0;
        public bool CanRedo => !Editing && history.Redo.Count > 0;
        public bool CanClear => !Editing && history.Current.Count > 0;
        public event Action? Updated;
        public event Action<Exception>? Failed;

        public void SetTool(BoardTool tool)
        {
            if (_pointer is not null) return;
            Tool = tool;
            if (tool != BoardTool.Hand) _target = null;
            UpdateCursor();
            Redraw();
        }

        public void ToggleGrid()
        {
            history.GridVisible = !history.GridVisible;
            Redraw();
        }

        public void ResetView()
        {
            if (_pointer is null) SetView(new(Home, 1));
        }

        public void FitContent()
        {
            if (_pointer is null) Fit(false);
        }

        private void Fit(bool initial)
        {
            var bounds = SceneBounds();
            var center = bounds?.Center ?? Home;
            double margin = !initial && bounds.HasValue ? 38 : 18;
            double width = Math.Max(initial || !bounds.HasValue ? 640 : 12, bounds?.Width ?? 0);
            double height = Math.Max(initial || !bounds.HasValue ? 300 : 12, bounds?.Height ?? 0);
            var zoom = Math.Min(initial || !bounds.HasValue ? 1 : 2,
                Math.Min(Math.Max(1, Bounds.Width - margin) / width, Math.Max(1, Bounds.Height - margin) / height));
            if (initial) history.View = new(center, zoom);
            else SetView(new(center, zoom));
        }

        public void ZoomBy(double factor, Point? anchor = null)
        {
            if (history.View is not { } view || _pointer is not null) return;
            var zoom = Math.Clamp(view.Zoom * factor, Math.Min(view.Zoom, .001), 4);
            var center = view.Center;
            if (anchor is { } point)
                center += Difference(point, new Point(Bounds.Width / 2, Bounds.Height / 2)) * (1 / view.Zoom - 1 / zoom);
            SetView(new(center, zoom));
        }

        private void Pan(Vector by)
        {
            if (history.View is { } view) SetView(view with { Center = view.Center - by / view.Zoom });
        }

        private void SetView(BoardView view)
        {
            history.View = view;
            UpdateSheet();
            Redraw();
        }

        private void UpdateSheet()
        {
            if (history.View is { } view)
                _sheet = (view.Zoom, Bounds.Width / 2 - view.Center.X * view.Zoom,
                    Bounds.Height / 2 - view.Center.Y * view.Zoom);
        }

        public void UpdateViewport()
        {
            if (Bounds.Width <= 18 || Bounds.Height <= 18) return;
            if (history.View is null) Fit(true);
            UpdateSheet();
            Redraw();
        }

        private void UpdateCursor() => Cursor = new Cursor(_panning || _spaceHeld || Tool == BoardTool.Hand
            ? StandardCursorType.Hand : Tool == BoardTool.Select ? StandardCursorType.Arrow : StandardCursorType.Cross);

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
            if (Bounds.Width <= 18 || Bounds.Height <= 18) return;
            if (history.View is null) Fit(true);
            UpdateSheet();
            if (history.GridVisible)
            {
                double spacing = 16 * Math.Pow(2, Math.Max(0, Math.Ceiling(Math.Log2(.75 / Zoom)))) * Zoom;
                double Phase(double coordinate) => (coordinate % spacing + spacing) % spacing;
                for (double x = Phase(_sheet.Left); x < Bounds.Width; x += spacing)
                    for (double y = Phase(_sheet.Top); y < Bounds.Height; y += spacing)
                        context.FillRectangle(Dots, new Rect(x, y, 1.1, 1.1));
            }
            Paint(context, _sheet.Scale, _sheet.Left, _sheet.Top, true);
        }

        private void Paint(DrawingContext context, double scale, double left, double top, bool editing)
        {
            using var transform = context.PushTransform(new Matrix(scale, 0, 0, scale, left, top));
            var strokes = history.Current.OfType<JsonObject>();
            if (editing && _draft is not null) strokes = strokes.Append(_draft);
            foreach (var stroke in strokes)
            {
                bool selected = editing && ReferenceEquals(stroke, _target);
                var curve = VisiblePoints(stroke, editing);
                if (selected && _offset.Length > 0) curve = curve.Select(point => point + _offset).ToArray();
                if (curve.Length == 0) continue;
                var color = stroke.Text("color");
                var brush = stroke.Flag("erase") ? Paper
                    : !color.Equals("#315545", StringComparison.OrdinalIgnoreCase) && Color.TryParse(color, out var parsed)
                        ? new SolidColorBrush(parsed) : Selection;
                var outline = selected ? new Pen(Selection, 1 / scale, DashStyle.Dash) : null;
                using var opacity = context.PushOpacity(Math.Clamp(Numeric(stroke, "opacity", 1), 0, 1));
                if (stroke.Text("text") is { Length: > 0 } label)
                {
                    var text = Format(label, brush);
                    context.DrawText(text, curve[0]);
                    if (selected) context.DrawRectangle(null, outline,
                        new Rect(curve[0], new Size(text.Width, text.Height)).Inflate(4 / scale));
                }
                else
                {
                    var width = StrokeWidthOf(stroke);
                    if (curve.Length == 1) context.DrawEllipse(brush, null, curve[0], width / 2, width / 2);
                    else
                    {
                        var line = new StreamGeometry();
                        using (var geometry = line.Open())
                        {
                            geometry.BeginFigure(curve[0], false);
                            for (var i = 1; i < curve.Length; i++) geometry.LineTo(curve[i]);
                            geometry.EndFigure(false);
                        }
                        context.DrawGeometry(null, new Pen(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
                    }
                    if (selected) context.DrawRectangle(null, outline, Extent(curve).Inflate(5 / scale));
                }
            }
        }

        private Rect? SceneBounds()
        {
            Rect? bounds = null;
            foreach (var stroke in history.Current.OfType<JsonObject>())
            {
                var points = VisiblePoints(stroke, false);
                if (points.Length == 0) continue;
                var rectangle = Extent(points).Inflate(StrokeWidthOf(stroke) / 2);
                if (stroke.Text("text") is { Length: > 0 } label)
                {
                    var text = Format(label, Paper);
                    rectangle = new Rect(points[0], new Size(text.Width, text.Height));
                }
                bounds = bounds?.Union(rectangle) ?? rectangle;
            }
            return bounds;
        }

        private static FormattedText Format(string label, IBrush brush) => new(label, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 16, brush);

        private static double StrokeWidthOf(JsonObject stroke) => stroke.Flag("erase") ? 6 :
            Math.Clamp(Numeric(stroke, "width", 2.5), 1, 16);

        public RenderTargetBitmap? CreateExport()
        {
            if (SceneBounds() is not { } bounds) return null;
            var scale = Math.Min(2, 4064 / Math.Max(1, Math.Max(bounds.Width, bounds.Height)));
            var size = new PixelSize((int)Math.Ceiling(bounds.Width * scale) + 32, (int)Math.Ceiling(bounds.Height * scale) + 32);
            var visual = new BoardPicture(context => Paint(context, scale, 16 - bounds.X * scale, 16 - bounds.Y * scale, false));
            visual.Measure(size.ToSize(1));
            visual.Arrange(new Rect(size.ToSize(1)));
            var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(visual);
            return bitmap;
        }

        private sealed class BoardPicture(Action<DrawingContext> paint) : Control
        {
            public override void Render(DrawingContext context)
            {
                context.FillRectangle(Paper, new Rect(Bounds.Size));
                paint(context);
            }
        }

        protected override async void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var properties = e.GetCurrentPoint(this).Properties;
            if (_pointer is not null || _sheet.Scale <= 0 ||
                !(properties.IsLeftButtonPressed || properties.IsMiddleButtonPressed || e.Pointer.Type == PointerType.Touch)) return;
            var pan = Tool == BoardTool.Hand || _spaceHeld || properties.IsMiddleButtonPressed;
            if (Saving && !pan) return;
            Focus();
            if (pan)
            {
                _panning = true;
                _lastPointer = e.GetPosition(this);
                _pointer = e.Pointer;
                e.Pointer.Capture(this);
                e.Handled = true;
                UpdateCursor();
                Redraw();
                return;
            }
            var point = Sheet(e.GetPosition(this));
            _target = Tool is BoardTool.Select or BoardTool.Eraser ? Hit(point) : null;
            if (Tool == BoardTool.Eraser)
            {
                e.Handled = true;
                await DeleteAsync();
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
            if (e.Pointer != _pointer) return;
            var position = e.GetPosition(this);
            if (_panning)
            {
                Pan(Difference(position, _lastPointer));
                _lastPointer = position;
            }
            else UpdateDraft(Sheet(position), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }

        protected override async void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.Pointer != _pointer) return;
            if (_panning)
            {
                Pan(Difference(e.GetPosition(this), _lastPointer));
                ResetGesture();
                e.Pointer.Capture(null);
                e.Handled = true;
                return;
            }
            var point = Sheet(e.GetPosition(this));
            UpdateDraft(point, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            var draft = _draft;
            var index = _target is null ? -1 : history.Current.IndexOf(_target);
            var offset = _offset;
            var selectionId = _target?.Text("id");
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
                }, selectionId);
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (e.Pointer == _pointer) ResetGesture();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || e.Delta.Y == 0) return;
            ZoomBy(e.Delta.Y > 0 ? 1.16 : 1 / 1.16, e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (command)
            {
                switch (e.Key)
                {
                    case Key.Z: _ = TravelAsync(!shift); break;
                    case Key.Y: _ = TravelAsync(false); break;
                    case Key.D: _ = DuplicateAsync(); break;
                    case Key.D0: ResetView(); break;
                    case Key.OemPlus or Key.Add: ZoomBy(1.25); break;
                    case Key.OemMinus or Key.Subtract: ZoomBy(1 / 1.25); break;
                    default: return;
                }
            }
            else if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                double distance = shift ? 120 : 36;
                switch (e.Key)
                {
                    case Key.Space: _spaceHeld = true; UpdateCursor(); break;
                    case Key.Escape when _pointer is not null || _target is not null:
                        var pointer = _pointer;
                        _target = null;
                        ResetGesture();
                        pointer?.Capture(null);
                        break;
                    case Key.Delete or Key.Back: _ = DeleteAsync(); break;
                    case Key.H: SetTool(BoardTool.Hand); break;
                    case Key.V: SetTool(BoardTool.Select); break;
                    case Key.P: SetTool(BoardTool.Pen); break;
                    case Key.E: SetTool(BoardTool.Eraser); break;
                    case Key.G: ToggleGrid(); break;
                    case Key.D1 when shift: FitContent(); break;
                    case Key.Left when _pointer is null: Pan(new(distance, 0)); break;
                    case Key.Right when _pointer is null: Pan(new(-distance, 0)); break;
                    case Key.Up when _pointer is null: Pan(new(0, distance)); break;
                    case Key.Down when _pointer is null: Pan(new(0, -distance)); break;
                    default: return;
                }
            }
            else return;
            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.Key != Key.Space || !_spaceHeld) return;
            _spaceHeld = false;
            UpdateCursor();
            e.Handled = true;
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            if (!_spaceHeld) return;
            _spaceHeld = false;
            UpdateCursor();
        }

        private void ResetGesture()
        {
            _pointer = null;
            _draft = null;
            _arrowStart = null;
            _offset = default;
            if (_panning)
            {
                _panning = false;
                UpdateCursor();
            }
            Redraw();
        }

        private JsonObject Create(IEnumerable<Point> points)
        {
            var stroke = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"), ["color"] = Ink, ["width"] = StrokeWidth,
                ["points"] = Nodes(points)
            };
            if (Tool == BoardTool.Highlighter)
            {
                stroke["width"] = Math.Min(16, StrokeWidth * 3 + 4);
                stroke["opacity"] = .42;
            }
            return stroke;
        }

        private void UpdateDraft(Point end, bool constrain)
        {
            if (_draft is not null)
            {
                if (Tool is BoardTool.Pen or BoardTool.Highlighter)
                {
                    var points = (JsonArray)_draft["points"]!;
                    if (points.Count < 4096) points.Add(Node(end));
                }
                else
                {
                    if (constrain) end = Constrain(Tool, _anchor, end);
                    _draft["points"] = Nodes(Shape(Tool, _anchor, end));
                }
            }
            else if (_target is not null) _offset = end - _anchor;
            InvalidateVisual();
        }

        private JsonObject? Hit(Point point)
        {
            foreach (var stroke in history.Current.OfType<JsonObject>().Reverse())
            {
                if (stroke.Flag("erase")) continue;
                var threshold = 9 / _sheet.Scale + StrokeWidthOf(stroke) / 2;
                var geometry = VisiblePoints(stroke);
                if (geometry.Length == 0) continue;
                if (stroke.Text("text") is { Length: > 0 } label)
                {
                    var text = Format(label, Paper);
                    if (new Rect(geometry[0], new Size(text.Width, text.Height)).Inflate(threshold).Contains(point))
                        return stroke;
                }
                if (geometry.Length == 1 && Difference(geometry[0], point).Length < threshold) return stroke;
                for (var i = 1; i < geometry.Length; i++)
                    if (Distance(point, geometry[i - 1], geometry[i]) <= threshold) return stroke;
                if (geometry.Length > 4 && geometry[0] == geometry[^1] && ContainsPoint(geometry, point)) return stroke;
            }
            return null;
        }

        private Point[] VisiblePoints(JsonObject stroke, bool preview = true)
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
                return preview && ReferenceEquals(node, _target) ? new Rect(box.Position + _offset, box.Size) : box;
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

        private int SelectionIndex => _target is null ? -1 : history.Current.IndexOf(_target);

        public bool CanReorder(bool front) => !Editing && SelectionIndex >= 0 &&
            SelectionIndex != (front ? history.Current.Count - 1 : 0);

        public Task DeleteAsync()
        {
            int index = SelectionIndex;
            return !Editing && index >= 0 ? EditAsync(strokes => strokes.RemoveAt(index)) : Task.CompletedTask;
        }

        public Task DuplicateAsync()
        {
            if (Editing || _target is not { } selected) return Task.CompletedTask;
            var clone = (JsonObject)selected.DeepClone();
            var id = Guid.NewGuid().ToString("N");
            clone["id"] = id;
            clone["points"] = Nodes(VisiblePoints(selected).Select(point => point + new Vector(14, 14)));
            clone.Remove("from");
            clone.Remove("to");
            return EditAsync(strokes => strokes.Add(clone), id);
        }

        public Task ReorderAsync(bool front)
        {
            if (!CanReorder(front)) return Task.CompletedTask;
            int index = SelectionIndex;
            return EditAsync(strokes =>
            {
                var node = strokes[index];
                strokes.RemoveAt(index);
                strokes.Insert(front ? strokes.Count : 0, node);
            }, _target?.Text("id"));
        }

        public Task ClearAsync() => CanClear ? EditAsync(strokes => strokes.Clear(), propagate: true) : Task.CompletedTask;

        private async Task EditAsync(Action<JsonArray> edit, string? selectionId = null, bool propagate = false)
        {
            if (Saving) return;
            var modified = (JsonArray)history.Current.DeepClone();
            edit(modified);
            if (JsonNode.DeepEquals(modified, history.Current)) return;
            await SaveAsync(modified, () =>
            {
                history.Undo.Add(history.Current);
                if (history.Undo.Count > 30) history.Undo.RemoveAt(0);
                history.Redo.Clear();
            }, selectionId, propagate);
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

        private async Task SaveAsync(JsonArray snapshot, Action updateHistory, string? selectionId = null, bool propagate = false)
        {
            Saving = true;
            Redraw();
            try
            {
                await save(snapshot);
                updateHistory();
                history.Current = snapshot;
                _target = selectionId is null ? null : snapshot.OfType<JsonObject>().FirstOrDefault(stroke => stroke.Text("id") == selectionId);
            }
            catch (Exception error) when (!propagate) { Failed?.Invoke(error); }
            finally { Saving = false; Redraw(); }
        }

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

        private static bool ContainsPoint(Point[] points, Point point)
        {
            bool inside = false;
            for (var i = 1; i < points.Length; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                    inside = !inside;
            }
            return inside;
        }

        private static Point Constrain(BoardTool tool, Point start, Point end)
        {
            var delta = Difference(end, start);
            if (tool is BoardTool.Line or BoardTool.Arrow)
            {
                double step = Math.PI / 4;
                double angle = Math.Round(Math.Atan2(delta.Y, delta.X) / step) * step;
                return start + new Vector(Math.Cos(angle), Math.Sin(angle)) * delta.Length;
            }
            double side = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
            return start + new Vector(delta.X < 0 ? -side : side, delta.Y < 0 ? -side : side);
        }

        private static IEnumerable<Point> Shape(BoardTool tool, Point start, Point end) => tool switch
        {
            BoardTool.Rectangle => [start, new Point(end.X, start.Y), end, new Point(start.X, end.Y), start],
            BoardTool.Diamond => Diamond(start, end),
            BoardTool.Ellipse => Enumerable.Range(0, 49).Select(index =>
            {
                double angle = index % 48 * Math.Tau / 48;
                return new Point((start.X + end.X + Math.Abs(end.X - start.X) * Math.Cos(angle)) / 2,
                    (start.Y + end.Y + Math.Abs(end.Y - start.Y) * Math.Sin(angle)) / 2);
            }),
            BoardTool.Arrow => Arrow(start, end), _ => [start, end]
        };

        private static Point[] Diamond(Point start, Point end)
        {
            double x = (start.X + end.X) / 2, y = (start.Y + end.Y) / 2;
            return [new(x, start.Y), new(end.X, y), new(x, end.Y), new(start.X, y), new(x, start.Y)];
        }

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
