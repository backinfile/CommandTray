using System.Diagnostics;
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
