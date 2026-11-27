using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private bool _previewRecording = true;

    private (Control Control, Action<bool> Select) CapturePreviewPanel(ICapturePreviewBackend backend,
        Func<bool, CapturePreviewTarget?> readTarget, CancellationToken token)
    {
        var image = new Image { Stretch = Stretch.Uniform, IsVisible = false };
        AutomationProperties.SetName(image, L("当前捕获画面", "Current capture view"));
        var placeholder = Muted("");
        placeholder.Margin = new Thickness(14);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.TextAlignment = TextAlignment.Center;
        var canvas = new Grid { Height = 280, Background = new SolidColorBrush(Color.FromRgb(12, 18, 26)),
            Children = { image, placeholder } };
        var status = Muted("");
        var body = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 5, Children = { canvas, status }
        };
        var screenshot = new Button { Content = L("截图", "Screenshot") };
        screenshot.Classes.Add("action");
        screenshot.Classes.Add("workbench-tab");
        AutomationProperties.SetName(screenshot, L("预览截图", "Preview screenshot"));
        var recording = new Button { Content = L("录屏", "Recording") };
        recording.Classes.Add("action");
        recording.Classes.Add("workbench-tab");
        AutomationProperties.SetName(recording, L("预览录屏", "Preview recording"));
        var panel = WorkbenchPanel(L("实时预览", "Live preview"), "capture", body, ActionBar(screenshot, recording));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        var owner = TopLevel.GetTopLevel(this) as Window;
        Bitmap? bitmap = null;
        CapturePreviewTarget? requested = null;
        bool fetching = false, attached = false;

        screenshot.Click += (_, _) => Select(false);
        recording.Click += (_, _) => Select(true);
        timer.Tick += async (_, _) => await Refresh();
        panel.AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            timer.Start();
            _ = Refresh();
        };
        panel.DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            timer.Stop();
            Clear("");
        };
        Select(_previewRecording);
        return (panel, Select);

        void Select(bool useRecording)
        {
            _previewRecording = useRecording;
            screenshot.Classes.Set("selected", !useRecording);
            recording.Classes.Set("selected", useRecording);
            AutomationProperties.SetItemStatus(screenshot, useRecording ? "" : L("当前预览", "Current preview"));
            AutomationProperties.SetItemStatus(recording, useRecording ? L("当前预览", "Current preview") : "");
            if (readTarget(useRecording) != requested)
                Clear(L("正在读取画面…", "Reading preview…"));
            _ = Refresh();
        }

        async Task Refresh()
        {
            if (token.IsCancellationRequested || !attached || fetching) return;
            if (owner is { IsVisible: false } || owner?.WindowState == WindowState.Minimized)
            {
                if (bitmap is not null) Clear("");
                return;
            }
            var target = readTarget(_previewRecording);
            if (target is null)
            {
                Clear(L("选择捕获对象后显示画面", "Select a capture source to see its preview"));
                requested = null;
                return;
            }
            if (target != requested)
            {
                requested = target;
                Clear(L("正在读取画面…", "Reading preview…"));
            }
            fetching = true;
            try
            {
                var corner = canvas.PointToScreen(default);
                var opposite = canvas.PointToScreen(new Point(canvas.Bounds.Width, canvas.Bounds.Height));
                var exclusion = new CaptureRegion(corner.X, corner.Y, opposite.X - corner.X, opposite.Y - corner.Y);
                var frame = await backend.ReadCapturePreviewAsync(target.Value, exclusion, token);
                if (!IsCurrent(target)) return;
                using var stream = new MemoryStream(frame.Image, writable: false);
                var updated = new Bitmap(stream);
                image.Source = updated;
                bitmap?.Dispose();
                bitmap = updated;
                image.IsVisible = true;
                placeholder.IsVisible = false;
                var size = $"{frame.Width} × {frame.Height}";
                status.Text = !target.Value.Recording && target.Value.Mode == "region"
                    ? L($"主显示器 · 等待拖选 · {size}", $"Primary display · Select a region · {size}") : size;
                status.IsVisible = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || !attached) { }
            catch (Exception failure)
            {
                if (IsCurrent(target)) Clear(failure.Message);
            }
            finally { fetching = false; }
        }

        bool IsCurrent(CapturePreviewTarget? target) => attached && !token.IsCancellationRequested &&
            owner is not { IsVisible: false } && owner?.WindowState != WindowState.Minimized && readTarget(_previewRecording) == target;

        void Clear(string message)
        {
            image.Source = null;
            image.IsVisible = false;
            bitmap?.Dispose();
            bitmap = null;
            placeholder.Text = message;
            placeholder.IsVisible = true;
            status.IsVisible = false;
        }
    }

    private static Control LayoutCapturePreview(Control controls, Control preview)
    {
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 12,
            RowDefinitions = new RowDefinitions("Auto,Auto"), Children = { controls, preview } };
        Grid.SetRow(preview, 1);
        bool sideBySide = false;
        grid.SizeChanged += (_, args) =>
        {
            bool wide = args.NewSize.Width >= 880;
            if (sideBySide == wide) return;
            sideBySide = wide;
            grid.ColumnDefinitions = wide ? new ColumnDefinitions("1.24*,*") : new ColumnDefinitions("*");
            Grid.SetRow(preview, wide ? 0 : 1);
            Grid.SetColumn(preview, wide ? 1 : 0);
        };
        return grid;
    }

    private static void SetCaptureButton(Button button, string label, string glyph, JsonNode? sources, CaptureShortcut? shortcut)
    {
        var content = IconLabel(glyph, label);
        string gesture = "";
        if (shortcut is { } action)
        {
            var name = action.ToString();
            var binding = sources.Array("shortcuts").OfType<JsonObject>()
                .FirstOrDefault(entry => entry.Text("action") == name && entry.Flag("available"));
            gesture = binding.Text("gesture");
            if (gesture.Length > 0)
                content.Children.Add(new TextBlock { Text = gesture, FontSize = 11, Opacity = .78, VerticalAlignment = VerticalAlignment.Center });
        }
        button.Content = content;
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, gesture);
        ToolTip.SetTip(button, gesture.Length == 0 ? null : $"{label} ({gesture})");
    }
}
