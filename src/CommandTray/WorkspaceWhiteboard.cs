using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using CommandTray.Services;

namespace CommandTray;

internal sealed class WorkspaceWhiteboard : UserControl
{
    private readonly Canvas _drawing;
    private readonly JsonObject _data;
    private readonly Action<JsonObject> _save;

    public WorkspaceWhiteboard(JsonObject data, Action<JsonObject> save)
    {
        _data = (JsonObject)data.DeepClone();
        _save = save;
        var strokes = data["strokes"] is JsonArray stored ? stored.OfType<JsonObject>().Select(stroke =>
        {
            var color = ColorTranslator.FromHtml(WorkspaceStore.Text(stroke["color"]) ?? "#245dda");
            var points = stroke["points"] is JsonArray array ? array.OfType<JsonObject>().Select(point =>
                new Point(WorkspaceStore.Number(point["x"], 0), WorkspaceStore.Number(point["y"], 0))).ToList() : [];
            return new Stroke(color, points, stroke["erase"]?.ToJsonString() == "true");
        }).Where(stroke => stroke.Points.Count > 0).ToList() : [];
        _drawing = new Canvas(strokes) { Dock = DockStyle.Fill };
        _drawing.Finished += (_, _) => Save();
        Controls.Add(_drawing);
        Controls.Add(DesktopWorkspacePanel.Toolbar(
            DesktopWorkspacePanel.Button("Color", "颜色", () =>
            {
                using var picker = new ColorDialog { Color = _drawing.Ink };
                if (picker.ShowDialog(this) == DialogResult.OK) { _drawing.Ink = picker.Color; _drawing.Eraser = false; }
            }),
            DesktopWorkspacePanel.Button("Pen", "画笔", () => _drawing.Eraser = false),
            DesktopWorkspacePanel.Button("Eraser", "橡皮", () => _drawing.Eraser = true),
            DesktopWorkspacePanel.Button("Undo", "撤销", () =>
            {
                if (_drawing.Strokes.Count == 0) return;
                _drawing.Strokes.RemoveAt(_drawing.Strokes.Count - 1); _drawing.Invalidate(); Save();
            })));
    }

    private void Save()
    {
        _data["strokes"] = new JsonArray(_drawing.Strokes.Select(stroke => (JsonNode)new JsonObject
        {
            ["color"] = ColorTranslator.ToHtml(stroke.Color), ["erase"] = stroke.Erase,
            ["points"] = new JsonArray(stroke.Points.Select(point =>
                (JsonNode)new JsonObject { ["x"] = point.X, ["y"] = point.Y }).ToArray())
        }).ToArray());
        _save(_data);
    }

    private sealed record Stroke(Color Color, List<Point> Points, bool Erase);

    private sealed class Canvas : Panel
    {
        private Stroke? _stroke;
        public Canvas(List<Stroke> strokes)
        {
            Strokes = strokes; DoubleBuffered = true; BackColor = Color.White; Cursor = Cursors.Cross;
        }
        public List<Stroke> Strokes { get; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color Ink { get; set; } = UiTheme.Accent;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Eraser { get; set; }
        public event EventHandler? Finished;
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Strokes.Count >= 2000) return;
            _stroke = new Stroke(Ink, [e.Location], Eraser);
            Strokes.Add(_stroke); Capture = true; Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_stroke is not { Points.Count: < 5000 } stroke || stroke.Points[^1] == e.Location) return;
            stroke.Points.Add(e.Location); Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_stroke is null) return;
            _stroke = null; Capture = false; Finished?.Invoke(this, EventArgs.Empty);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var stroke in Strokes)
            {
                var color = stroke.Erase ? BackColor : stroke.Color;
                using var pen = new Pen(color, stroke.Erase ? 14 : 3) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                if (stroke.Points.Count > 1) e.Graphics.DrawLines(pen, stroke.Points.ToArray());
                else
                {
                    var point = stroke.Points[0];
                    using var brush = new SolidBrush(color);
                    e.Graphics.FillEllipse(brush, point.X - pen.Width / 2, point.Y - pen.Width / 2, pen.Width, pen.Width);
                }
            }
        }
    }
}
