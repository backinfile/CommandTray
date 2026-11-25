using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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
        string? file = null;
        if (region)
        {
            SelectRegion(this, (area, bitmap) => file = _desktop.Media.SaveScreenshot(bitmap, area));
            if (file is null) return;
        }
        else file = _desktop.Media.Screenshot();
        using var image = Image.FromFile(file);
        Clipboard.SetImage(image);
        RefreshCaptures();
        _desktop.Notify(Localizer.T("Screenshot saved and copied", "截图已保存并复制"));
    }

    internal static Rectangle? SelectRegion(IWin32Window? owner, Action<Rectangle, Bitmap>? save = null)
    {
        var desktop = SystemInformation.VirtualScreen;
        using var snapshot = DesktopMedia.CapturePixels(desktop);
        using var selector = new CaptureSelectionForm(desktop, snapshot);
        if (selector.ShowDialog(owner) != DialogResult.OK) return null;
        DwmFlush();
        var area = selector.DesktopSelection;
        if (save is not null)
        {
            using var image = selector.Crop();
            save(area, image);
        }
        return area;
    }

    internal static Rectangle ForegroundBounds()
    {
        var window = GetForegroundWindow();
        if (window != 0 && window != GetShellWindow() && (DwmGetWindowAttribute(window, 9, out var bounds, 16) == 0 ||
            GetWindowRect(window, out bounds)))
        {
            var area = Rectangle.Intersect(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                SystemInformation.VirtualScreen);
            if (area.Width > 0 && area.Height > 0) return area;
        }
        throw new InvalidOperationException(Localizer.T("Bring a window to the foreground before capture.", "请先将要截图的窗口切换到前台。"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowRect bounds);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out WindowRect bounds, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

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
        var menu = new ContextMenuStrip();
        menu.Items.Add(Localizer.T("Smaller", "缩小"), null, (_, _) => ResizePinned(.8));
        menu.Items.Add(Localizer.T("Larger", "放大"), null, (_, _) => ResizePinned(1.25));
        var opacity = new ToolStripMenuItem(Localizer.T("Opacity", "不透明度"));
        foreach (int level in new[] { 100, 75, 50, 25 })
            opacity.DropDownItems.Add($"{level}%", null, (_, _) => form.Opacity = level / 100d);
        menu.Items.Add(opacity);
        menu.Items.Add(Localizer.T("Close", "关闭"), null, (_, _) => form.Close());
        picture.ContextMenuStrip = menu;
        form.KeyPreview = true;
        form.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) form.Close(); };
        form.FormClosed += (_, _) => { menu.Dispose(); image.Dispose(); };
        form.Show();

        void ResizePinned(double scale)
        {
            var workArea = Screen.FromControl(form).WorkingArea;
            form.ClientSize = new Size(Math.Clamp((int)(form.ClientSize.Width * scale), 120, Math.Max(120, workArea.Width - 20)),
                Math.Clamp((int)(form.ClientSize.Height * scale), 80, Math.Max(80, workArea.Height - 60)));
        }
    }

    internal static string? Annotate(string path, IWin32Window? owner)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a screenshot.");
        using var form = new ScreenshotAnnotationForm(path);
        form.ShowDialog(owner);
        return form.Output;
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
