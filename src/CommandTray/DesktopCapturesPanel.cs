using System.Diagnostics;
using System.Drawing.Drawing2D;
using CommandTray.Services;
using static CommandTray.DesktopWorkspacePanel;

namespace CommandTray;

internal sealed class DesktopCapturesPanel : UserControl
{
    private readonly DesktopRuntime _desktop;
    private readonly ListBox _files = new() { DisplayMember = "Name", IntegralHeight = false };
    public DesktopCapturesPanel(DesktopRuntime desktop)
    {
        _desktop = desktop;
        Dock = DockStyle.Fill;
        Controls.Add(WithToolbar(_files, Button("Screenshot", "截图", () => Safely(() => SaveScreenshot(false))),
            Button("Region…", "区域…", () => Safely(() => SaveScreenshot(true))),
            Async("Record", "录屏", async () => { await desktop.Media.StartRecordingAsync(24); desktop.Notify(Localizer.T("Recording started", "开始录屏")); }),
            Async("Stop", "停止", async () => { await desktop.Media.StopRecordingAsync(); RefreshCaptures(); desktop.Notify(Localizer.T("Recording saved", "录屏已保存")); }),
            Button("Pin", "贴图", () => Safely(() => { if (Selected() is { } path) ShowPinned(path); })),
            Button("Annotate", "标注", () => Safely(() =>
            {
                if (Selected() is not { } path) return;
                Annotate(path, this);
                RefreshCaptures();
            })),
            Async("OCR", "文字识别", async () =>
            {
                if (Selected() is not { } path) return;
                var language = Prompt(this, Localizer.T("Tesseract language", "OCR 语言"), Localizer.T("eng", "chi_sim+eng"));
                if (language is null) return;
                var output = await desktop.Media.RecognizeTextAsync(Path.GetFileName(path), language);
                if (!IsDisposed) desktop.WriteClipboard(output);
                desktop.Notify(Localizer.T("OCR copied", "已复制识别文字"));
            }),
            Button("Refresh", "刷新", RefreshCaptures)));
        _files.DoubleClick += (_, _) => Safely(() =>
        {
            if (Selected() is { } path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
        RefreshCaptures();
    }

    public void RefreshCaptures()
    {
        _files.Items.Clear();
        _files.Items.AddRange(_desktop.Media.Captures().Select(path => (object)new FileInfo(path)).ToArray());
        if (_files.Items.Count > 0) _files.SelectedIndex = 0;
    }

    public void SaveScreenshot(bool region)
    {
        Rectangle? rectangle = null;
        if (region)
        {
            rectangle = SelectRegion(this);
            if (rectangle is null) return;
        }
        var file = _desktop.Media.Screenshot(rectangle);
        using var image = Image.FromFile(file);
        Clipboard.SetImage(image);
        RefreshCaptures();
        _desktop.Notify(Localizer.T("Screenshot saved and copied", "截图已保存并复制"));
    }

    internal static Rectangle? SelectRegion(IWin32Window? owner)
    {
        using var selector = new CaptureSelectionForm();
        return selector.ShowDialog(owner) == DialogResult.OK ? selector.Selection : null;
    }

    private string? Selected() => (_files.SelectedItem as FileInfo)?.FullName;

    internal static void ShowPinned(string path)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a screenshot.");
        var form = new Form { Text = Path.GetFileName(path), TopMost = true, MaximizeBox = false, StartPosition = FormStartPosition.CenterScreen };
        using var source = Image.FromFile(path);
        var image = new Bitmap(source);
        var picture = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
        form.ClientSize = new Size(Math.Min(image.Width, 800), Math.Min(image.Height, 600));
        form.Controls.Add(picture);
        form.FormClosed += (_, _) => image.Dispose();
        form.Show();
    }

    internal static string? Annotate(string path, IWin32Window? owner)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a screenshot.");
        using var source = Image.FromFile(path);
        using var edited = new Bitmap(source);
        using var form = new Form { Text = Localizer.T("Annotate capture", "标注截图"), StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(Math.Min(1000, source.Width), Math.Min(680, source.Height + 43)) };
        var picture = new PictureBox { Image = edited, SizeMode = PictureBoxSizeMode.StretchImage, Cursor = Cursors.Cross, Dock = DockStyle.Fill };
        var previous = Point.Empty;
        Point Scale(Point point) => new(point.X * edited.Width / Math.Max(1, picture.Width), point.Y * edited.Height / Math.Max(1, picture.Height));
        picture.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) previous = Scale(e.Location); };
        picture.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var current = Scale(e.Location);
            using var graphics = Graphics.FromImage(edited);
            using var pen = new Pen(Color.OrangeRed, Math.Max(2, edited.Width / 400f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(pen, previous, current);
            previous = current;
            picture.Invalidate();
        };
        string? output = null;
        var save = Button("Save as…", "另存…", () =>
        {
            using var picker = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = Path.GetFileNameWithoutExtension(path) + "-annotated.png",
                InitialDirectory = Path.GetDirectoryName(path) };
            if (picker.ShowDialog(form) != DialogResult.OK) return;
            try
            {
                edited.Save(picker.FileName, System.Drawing.Imaging.ImageFormat.Png);
                output = picker.FileName;
                form.Close();
            }
            catch (Exception ex) { Error(form, ex); }
        });
        form.Controls.Add(picture); form.Controls.Add(Toolbar(save));
        form.ShowDialog(owner);
        return output;
    }

    private Button Async(string en, string zh, Func<Task> action)
    {
        var button = Button(en, zh, null);
        button.Click += async (_, _) =>
        {
            try { button.Enabled = false; await action(); }
            catch (Exception ex) { if (!IsDisposed) Error(this, ex); }
            finally { if (!button.IsDisposed) button.Enabled = true; }
        };
        return button;
    }

    private void Safely(Action action)
    {
        try { action(); }
        catch (Exception ex) { Error(this, ex); }
    }

    private sealed class CaptureSelectionForm : Form
    {
        private Point _start;
        private bool _selecting;
        public CaptureSelectionForm()
        {
            Bounds = SystemInformation.VirtualScreen;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None; DoubleBuffered = true;
            ShowInTaskbar = false; TopMost = true; Opacity = .27; BackColor = Color.Black; Cursor = Cursors.Cross; KeyPreview = true;
            Shown += (_, _) => Activate();
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        }
        public Rectangle Selection { get; private set; }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _start = e.Location; _selecting = true; Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_selecting) return;
            Selection = Rectangle.FromLTRB(Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y), Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.White, 3); e.Graphics.DrawRectangle(pen, Selection);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_selecting) return;
            _selecting = false;
            Capture = false;
            if (Selection.Width <= 0 || Selection.Height <= 0) { DialogResult = DialogResult.Cancel; return; }
            Selection = new Rectangle(PointToScreen(Selection.Location), Selection.Size);
            DialogResult = DialogResult.OK;
        }
    }
}

internal sealed class DesktopPetForm : Form
{
    private readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = 4500 };
    private string? _notice;
    private Action? _openNotice;
    private Point? _dragStart;
    private bool _dragged;

    public DesktopPetForm(Action showWorkspace)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        BackColor = Color.Magenta; TransparencyKey = Color.Magenta; Size = new Size(280, 110);
        var screen = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        StartPosition = FormStartPosition.Manual; Location = new Point(screen.Right - Width - 20, screen.Bottom - Height - 10);
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice = null; _openNotice = null; Invalidate(); };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragStart = e.Location; _dragged = false; } };
        MouseMove += (_, _) =>
        {
            if (_dragStart is not Point start) return;
            var next = new Point(Cursor.Position.X - start.X, Cursor.Position.Y - start.Y);
            if (!_dragged && Math.Abs(next.X - Left) + Math.Abs(next.Y - Top) <= 4) return;
            Location = next; _dragged = true;
        };
        MouseUp += (_, e) =>
        {
            if (_dragStart is null || e.Button != MouseButtons.Left) return;
            _dragStart = null;
            if (_dragged) return;
            if (e.Y is >= 3 and <= 46 && _openNotice is { } open)
            {
                _noticeTimer.Stop();
                _notice = null;
                _openNotice = null;
                Invalidate();
                open();
            }
            else showWorkspace();
        };
    }
    protected override bool ShowWithoutActivation => true;

    public void Notice(string message, Action? openSource)
    {
        _notice = message; _openNotice = openSource; _noticeTimer.Stop(); _noticeTimer.Start(); Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var x = Width - 65;
        var y = 51;
        using var fur = new SolidBrush(Color.FromArgb(247, 181, 120));
        using var shadow = new SolidBrush(Color.FromArgb(95, 61, 60));
        graphics.FillPolygon(shadow, [(new Point(x + 8, y + 24)), new Point(x + 10, y), new Point(x + 28, y + 15)]);
        graphics.FillPolygon(shadow, [new Point(x + 31, y + 15), new Point(x + 49, y), new Point(x + 51, y + 25)]);
        graphics.FillEllipse(fur, x + 5, y + 13, 51, 37);
        graphics.FillEllipse(shadow, x + 18, y + 29, 4, 5);
        graphics.FillEllipse(shadow, x + 38, y + 29, 4, 5);
        graphics.FillEllipse(shadow, x + 27, y + 37, 6, 3);
        if (_notice is null) return;
        using var paper = new SolidBrush(UiTheme.Surface);
        using var border = new Pen(UiTheme.Separator);
        var rectangle = new Rectangle(3, 3, Width - 9, 43);
        using var outline = UiTheme.RoundedRectangle(rectangle, 9);
        graphics.FillPath(paper, outline); graphics.DrawPath(border, outline);
        TextRenderer.DrawText(graphics, _notice, UiTheme.Small, Rectangle.Inflate(rectangle, -8, -5), UiTheme.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _noticeTimer.Dispose();
        base.Dispose(disposing);
    }
}
