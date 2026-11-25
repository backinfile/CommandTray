using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CommandTray.Services;
using static CommandTray.DesktopWorkspacePanel;

namespace CommandTray;

internal sealed class ScreenshotAnnotationForm : Form
{
    private readonly string _path;
    private readonly Bitmap _image;
    private readonly Canvas _canvas;
    private readonly ToolStripComboBox _tools = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ToolStripButton _color;
    private readonly ToolStripTextBox _text = new() { Visible = false };
    private readonly ToolStripButton _undo;
    private readonly ToolStripButton _redo;
    private Color _ink = Color.OrangeRed;

    public ScreenshotAnnotationForm(string path)
    {
        _path = path;
        using (var source = Image.FromFile(path)) _image = new Bitmap(source);
        _canvas = new Canvas(_image) { Dock = DockStyle.Fill, Cursor = Cursors.Cross };
        Text = Localizer.T("Annotate screenshot", "标注截图");
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(540, 360);
        ClientSize = new Size(Math.Clamp(_image.Width, 680, 1080), Math.Clamp(_image.Height + 38, 420, 720));

        _tools.Items.AddRange(new object[] { Localizer.T("Pen", "画笔"), Localizer.T("Line", "直线"), Localizer.T("Arrow", "箭头"),
            Localizer.T("Rectangle", "矩形"), Localizer.T("Text", "文字"), Localizer.T("Number", "编号"),
            Localizer.T("Redact", "遮挡"), Localizer.T("Crop", "裁剪") });
        _tools.AccessibleName = Localizer.T("Annotation tool", "标注工具");
        _tools.SelectedIndexChanged += (_, _) => { _canvas.Tool = (Tool)_tools.SelectedIndex; _text.Visible = _canvas.Tool == Tool.Text; };
        _tools.SelectedIndex = 0;
        _text.AccessibleName = Localizer.T("Annotation text", "标注文字");
        _canvas.ReadText = () => _text.Text.Trim();
        _color = new ToolStripButton(Localizer.T("Color", "颜色")) { ForeColor = _ink };
        _color.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = _ink };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            _ink = _canvas.Ink = _color.ForeColor = picker.Color;
        };
        _undo = new ToolStripButton(Localizer.T("Undo", "撤销"), null, (_, _) => _canvas.Undo());
        _redo = new ToolStripButton(Localizer.T("Redo", "重做"), null, (_, _) => _canvas.Redo());
        var save = new ToolStripButton(Localizer.T("Save as…", "另存…"), null, (_, _) => Save());
        var toolbar = new ToolStrip(_tools, _color, _text, new ToolStripSeparator(), _undo, _redo, new ToolStripSeparator(), save)
        {
            GripStyle = ToolStripGripStyle.Hidden, TabStop = true
        };
        _canvas.HistoryChanged += (_, _) => UpdateActions();
        Controls.Add(_canvas);
        Controls.Add(toolbar);
        UpdateActions();
    }

    public string? Output { get; private set; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_text.Focused)
        {
            if (keyData == (Keys.Control | Keys.Z)) { _canvas.Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y)) { _canvas.Redo(); return true; }
        }
        if (keyData == (Keys.Control | Keys.S)) { Save(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _image.Dispose();
    }

    private void UpdateActions()
    {
        _undo.Enabled = _canvas.CanUndo;
        _redo.Enabled = _canvas.CanRedo;
    }

    private void Save()
    {
        using var picker = new SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png", DefaultExt = "png", InitialDirectory = Path.GetDirectoryName(_path),
            FileName = Path.GetFileNameWithoutExtension(_path) + "-annotated.png"
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (Path.GetFullPath(picker.FileName).Equals(Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                throw new IOException(Localizer.T("Choose a new file to preserve the original.", "请选择新文件以保留原图。"));
            using var output = _canvas.Flatten();
            output.Save(picker.FileName, ImageFormat.Png);
            Output = picker.FileName;
            Close();
        }
        catch (Exception error) { Error(this, error); }
    }

    private enum Tool { Pen, Line, Arrow, Rectangle, Text, Number, Redact, Crop }

    private sealed record Mark(Tool Tool, Point[] Points, Color Color, string Text)
    {
        public Rectangle Bounds => Rectangle.FromLTRB(Math.Min(Points[0].X, Points[^1].X), Math.Min(Points[0].Y, Points[^1].Y),
            Math.Max(Points[0].X, Points[^1].X), Math.Max(Points[0].Y, Points[^1].Y));
    }

    private sealed class Canvas : Control
    {
        private readonly Bitmap _image;
        private readonly List<Mark> _marks = [];
        private readonly Stack<Mark> _redo = new();
        private readonly List<Point> _stroke = [];
        private readonly float _lineWidth;
        private Rectangle Original => new(0, 0, _image.Width, _image.Height);
        private Rectangle VisibleRegion => _marks.LastOrDefault(mark => mark.Tool == Tool.Crop)?.Bounds ?? Original;
        private RectangleF Preview
        {
            get
            {
                var region = VisibleRegion;
                float fit = Math.Min((float)Width / region.Width, (float)Height / region.Height);
                var size = new SizeF(region.Width * fit, region.Height * fit);
                return new RectangleF((Width - size.Width) / 2, (Height - size.Height) / 2, size.Width, size.Height);
            }
        }

        public Canvas(Bitmap image)
        {
            _image = image;
            _lineWidth = Math.Clamp(_image.Width / 360f, 3, 12);
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(29, 34, 42);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Tool Tool { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color Ink { get; set; } = Color.OrangeRed;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Func<string>? ReadText { get; set; }
        public bool CanUndo => _marks.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public event EventHandler? HistoryChanged;
        private void Changed() { Invalidate(); HistoryChanged?.Invoke(this, EventArgs.Empty); }

        public void Undo()
        {
            if (!CanUndo) return;
            _redo.Push(_marks[^1]);
            _marks.RemoveAt(_marks.Count - 1);
            Changed();
        }

        public void Redo()
        {
            if (!_redo.TryPop(out var mark)) return;
            _marks.Add(mark);
            Changed();
        }

        public Bitmap Flatten()
        {
            var region = VisibleRegion;
            var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                Draw(graphics, new Rectangle(0, 0, region.Width, region.Height), region);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var preview = Preview;
            if (preview.Width <= 0 || preview.Height <= 0) return;
            Draw(e.Graphics, preview, VisibleRegion, _stroke.Count > 1 ? new Mark(Tool, _stroke.ToArray(), Ink, "") : null);
        }

        private void Draw(Graphics graphics, RectangleF destination, Rectangle region, Mark? pending = null)
        {
            var state = graphics.Save();
            graphics.SetClip(destination);
            graphics.TranslateTransform(destination.Left, destination.Top);
            graphics.ScaleTransform(destination.Width / region.Width, destination.Height / region.Height);
            graphics.TranslateTransform(-region.Left, -region.Top);
            graphics.DrawImage(_image, region, region, GraphicsUnit.Pixel);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var mark in _marks)
                if (mark.Tool != Tool.Crop) DrawMark(graphics, mark);
            if (pending is not null) DrawMark(graphics, pending);
            graphics.Restore(state);
        }

        private void DrawMark(Graphics graphics, Mark mark)
        {
            using var brush = new SolidBrush(mark.Color);
            using var pen = new Pen(brush, _lineWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var start = mark.Points[0];
            var end = mark.Points[^1];
            switch (mark.Tool)
            {
                case Tool.Pen:
                    if (mark.Points.Length == 1) graphics.FillEllipse(brush, start.X, start.Y, _lineWidth, _lineWidth);
                    else graphics.DrawLines(pen, mark.Points);
                    break;
                case Tool.Line: graphics.DrawLine(pen, start, end); break;
                case Tool.Arrow:
                    using (var cap = new AdjustableArrowCap(4, 5)) { pen.CustomEndCap = cap; graphics.DrawLine(pen, start, end); }
                    break;
                case Tool.Rectangle: graphics.DrawRectangle(pen, mark.Bounds); break;
                case Tool.Redact:
                    var smoothing = graphics.SmoothingMode;
                    graphics.SmoothingMode = SmoothingMode.None;
                    graphics.FillRectangle(Brushes.Black, mark.Bounds);
                    graphics.SmoothingMode = smoothing;
                    break;
                case Tool.Text:
                    using (var font = new Font(SystemFonts.MessageBoxFont!.FontFamily, _lineWidth * 5, FontStyle.Bold, GraphicsUnit.Pixel))
                        graphics.DrawString(mark.Text, font, brush, start);
                    break;
                case Tool.Number:
                    using (var font = new Font(SystemFonts.MessageBoxFont!.FontFamily, _lineWidth * 4, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var align = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        float radius = _lineWidth * 4;
                        var circle = new RectangleF(start.X - radius, start.Y - radius, radius * 2, radius * 2);
                        graphics.FillEllipse(brush, circle);
                        graphics.DrawString(mark.Text, font, mark.Color.GetBrightness() > .6 ? Brushes.Black : Brushes.White, circle, align);
                    }
                    break;
                case Tool.Crop:
                    pen.DashStyle = DashStyle.Dash;
                    graphics.DrawRectangle(pen, mark.Bounds);
                    break;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Preview.Contains(e.Location)) return;
            var point = Map(e.Location);
            if (Tool is Tool.Text or Tool.Number)
            {
                var text = Tool == Tool.Text ? ReadText?.Invoke() : (_marks.Count(mark => mark.Tool == Tool.Number) + 1).ToString();
                if (!string.IsNullOrWhiteSpace(text)) Add(new Mark(Tool, [point], Ink, text));
                return;
            }
            _stroke.Add(point);
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_stroke.Count == 0) return;
            Track(Map(e.Location));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || _stroke.Count == 0) return;
            Track(Map(e.Location));
            var mark = new Mark(Tool, _stroke.ToArray(), Ink, "");
            _stroke.Clear();
            Capture = false;
            if (Tool is Tool.Redact or Tool.Crop or Tool.Rectangle ? mark.Bounds.Width > 0 && mark.Bounds.Height > 0 :
                Tool == Tool.Pen || mark.Points[0] != mark.Points[^1]) Add(mark);
            else Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || _stroke.Count == 0) return;
            _stroke.Clear();
            Invalidate();
        }

        private void Track(Point point)
        {
            if (Tool != Tool.Pen && _stroke.Count == 2) _stroke[1] = point;
            else if (_stroke[^1] != point) _stroke.Add(point);
        }

        private Point Map(Point point)
        {
            var region = VisibleRegion;
            var preview = Preview;
            return new Point(Math.Clamp(region.Left + (int)((point.X - preview.Left) * region.Width / preview.Width), region.Left, region.Right),
                Math.Clamp(region.Top + (int)((point.Y - preview.Top) * region.Height / preview.Height), region.Top, region.Bottom));
        }

        private void Add(Mark mark)
        {
            _marks.Add(mark);
            _redo.Clear();
            Changed();
        }
    }
}
