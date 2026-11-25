using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private string? _captureSelection;
    private int _captureFilter;

    private async Task RenderCaptureAsync(CancellationToken token)
    {
        Header(L("屏幕捕获", "Screen capture"));
        var captures = (await ReadPageContent("capture_list", token) as JsonArray ?? [])
            .Select(entry => TextOf(entry, "path")).Where(path => path.Length > 0).ToArray();
        var sources = await Execute("capture_sources", [], token);
        var displays = sources.Array("displays").OfType<JsonObject>().Select(display =>
            (Label: L($"显示器 {display.Integer("index")} · {display.Integer("width")}×{display.Integer("height")}",
                $"Display {display.Integer("index")} · {display.Integer("width")}×{display.Integer("height")}"),
                Value: display.Text("id"))).ToArray();
        var recording = await Execute("capture_record_state", [], token);
        var summary = Muted("");
        var recordingFile = Muted("");
        recordingFile.TextWrapping = TextWrapping.Wrap;
        var recordingError = Muted("");
        recordingError.Classes.Add("form-error");
        recordingError.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(recordingError, AutomationLiveSetting.Assertive);
        var capture = PrimaryAction(L("区域截图", "Capture region"), () => Capture("capture_select", new() { ["hideWindow"] = true }, token), "capture");
        var record = Action("", async () =>
        {
            if (recording.Flag("recording")) await Capture("capture_record_stop", [], token);
            else await RecordingOptionsAsync(displays, token);
        });
        var discard = Action(L("放弃录屏", "Discard recording"), () => Confirm(L("放弃当前录屏？", "Discard this recording?"),
            () => Capture("capture_record_discard", [], token)));
        var screenshotActions = new List<(string Label, Func<Task> Action)>
        {
            (L("活动窗口截图", "Capture active window"), () => Take("window")),
            (L("主显示器截图", "Capture primary display"), () => Take("primary")),
            (L("全屏截图", "Capture all displays"), () => Take("desktop")),
            (L("延时截图…", "Delayed screenshot…"), DelayedScreenshot),
            (L("输入区域坐标…", "Enter region coordinates…"), CoordinateScreenshot),
            (L("打开捕获目录", "Open capture folder"), async () => await Execute("capture_reveal", [], token))
        };
        if (sources.Flag("lastRegion"))
            screenshotActions.Insert(1, (L("上次区域截图", "Capture last region"), () => Take("last")));
        var more = Menu("⋯", screenshotActions.ToArray());
        var controls = new WrapPanel { ItemSpacing = 7, LineSpacing = 6, Children = { capture, record, more } };
        var heading = PageHeading(L("屏幕捕获", "Screen capture"), summary, _compact ? null : controls);
        _contents.Children.Add(heading);
        if (_compact) _contents.Children.Add(controls);
        _contents.Children.Add(new WrapPanel { ItemSpacing = 10, LineSpacing = 6, Children = { recordingFile, discard } });
        _contents.Children.Add(recordingError);
        UpdateRecording();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        bool polling = false;
        var lastPath = recording.Text("lastPath");
        timer.Tick += async (_, _) =>
        {
            if (polling || token.IsCancellationRequested) return;
            polling = true;
            try
            {
                recording = await Execute("capture_record_state", [], token);
                if (token.IsCancellationRequested) return;
                UpdateRecording();
                if (recording.Text("lastPath") is { Length: > 0 } saved && saved != lastPath && !captures.Contains(saved))
                {
                    _captureSelection = saved;
                    await LoadAsync();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (!token.IsCancellationRequested) { Report(error.Message); }
            finally { polling = false; }
        };
        heading.AttachedToVisualTree += (_, _) => timer.Start();
        heading.DetachedFromVisualTree += (_, _) => timer.Stop();
        if (TopLevel.GetTopLevel(heading) is not null) timer.Start();

        var filter = CaptureChoice(L("捕获类型", "Capture type"), new[] { L("全部", "All"), L("截图", "Screenshots"), L("录屏", "Recordings") });
        filter.SelectedIndex = _captureFilter;
        var list = new StackPanel();
        var detail = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var history = WorkbenchPanel(L("捕获历史", "Capture history"), "history", list, filter);
        if (_compact) { _contents.Children.Add(history); _contents.Children.Add(detail); }
        else _contents.Children.Add(WorkbenchSplit(history, detail, "1*,1.55*"));
        filter.SelectionChanged += (_, _) => { _captureFilter = filter.SelectedIndex; DrawList(); };
        DrawList();

        void UpdateRecording()
        {
            string state = recording.Text("state");
            var active = recording.Flag("recording");
            string elapsed = TimeSpan.FromSeconds(recording.Integer("elapsedSeconds")).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            summary.Text = state switch
            {
                "starting" => L("正在启动录屏…", "Starting recording…"),
                "saving" => L("正在结束录屏…", "Finishing recording…"),
                "recording" => L($"正在录制 · {elapsed}", $"Recording · {elapsed}"),
                _ => L($"{captures.Length} 项", $"{captures.Length} captures")
            };
            string label = active ? L("结束录屏", "Stop recording") : L("开始录屏", "Record screen");
            if (AutomationProperties.GetName(record) != label)
            {
                record.Content = IconLabel(active ? "stop" : "play", label);
                AutomationProperties.SetName(record, label);
            }
            record.IsEnabled = state is not ("starting" or "saving");
            discard.IsVisible = state == "recording";
            recordingFile.Text = recording.Text("path");
            recordingFile.IsVisible = recordingFile.Text.Length > 0;
            recordingError.Text = recording.Text("error");
            recordingError.IsVisible = recordingError.Text.Length > 0;
        }

        Task Take(string mode, int delay = 0) => Capture("capture_take",
            new() { ["mode"] = mode, ["delay"] = delay, ["hideWindow"] = true, ["copy"] = true }, token);

        Task DelayedScreenshot()
        {
            var choices = new[] { (L("活动窗口", "Active window"), "window"), (L("所有显示器", "All displays"), "desktop") }
                .Concat(displays).ToArray();
            var target = CaptureChoice(L("范围", "Source"), choices.Select(item => item.Item1));
            var wait = CaptureChoice(L("延时", "Delay"), new[] { L("3 秒", "3 seconds"), L("5 秒", "5 seconds"), L("10 秒", "10 seconds") });
            return ShowForm(L("延时截图", "Delayed screenshot"), L("截图", "Capture"), L("正在等待…", "Waiting…"),
                (_, ct) => Capture("capture_take", new() { ["mode"] = choices[target.SelectedIndex].Item2,
                    ["delay"] = new[] { 3, 5, 10 }[wait.SelectedIndex], ["hideWindow"] = true, ["copy"] = true }, ct),
                [], new StackPanel { Spacing = 8, Children = { Muted(L("范围", "Source")), target, Muted(L("延时", "Delay")), wait } },
                cancellable: true, initialFocus: target);
        }

        Task CoordinateScreenshot()
        {
            Field[] coordinates = [new("x", "X"), new("y", "Y"), new("width", L("宽", "Width")), new("height", L("高", "Height"))];
            return Form(L("区域截图", "Region capture"), L("截图", "Capture"), L("正在截图…", "Capturing…"), (values, ct) =>
            {
                var area = new JsonObject { ["hideWindow"] = true, ["copy"] = true };
                foreach (var field in coordinates)
                {
                    if (!int.TryParse(values.Text(field.Key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                        throw new ArgumentException(L($"{field.Label} 必须是整数。", $"{field.Label} must be a whole number."));
                    if ((field.Key is "width" or "height") && value <= 0)
                        throw new ArgumentException(L($"{field.Label} 必须大于 0。", $"{field.Label} must be greater than zero."));
                    area[field.Key] = value;
                }
                return Capture("capture_take", area, ct);
            }, coordinates);
        }

        void DrawList()
        {
            list.Children.Clear();
            var paths = captures.Where(path => _captureFilter == 0 || IsScreenshot(path) == (_captureFilter == 1)).ToArray();
            _captureSelection = paths.Contains(_captureSelection) ? _captureSelection : paths.FirstOrDefault();
            if (_captureSelection is null)
            {
                list.Children.Add(EmptyState("capture", L("暂无捕获", "No captures")));
                detail.Content = null;
                return;
            }
            var rows = new List<(Button Row, string Path)>();
            foreach (var path in paths)
            {
                var row = Action(Path.GetFileName(path), () =>
                {
                    if (_captureSelection == path) return Task.CompletedTask;
                    _captureSelection = path;
                    foreach (var item in rows) item.Row.Classes.Set("selected", item.Path == path);
                    DrawDetail(path);
                    if (_compact) detail.BringIntoView();
                    return Task.CompletedTask;
                }, glyph: IsScreenshot(path) ? "capture" : "play");
                row.Classes.Add("workbench-list-row");
                row.Classes.Set("selected", _captureSelection == path);
                rows.Add((row, path));
                list.Children.Add(row);
            }
            DrawDetail(_captureSelection);
        }

        void DrawDetail(string selected)
        {
            var args = new JsonObject { ["path"] = selected };
            var picture = IsScreenshot(selected);
            var body = new StackPanel { Spacing = 9, Margin = new Thickness(13) };
            var actions = new WrapPanel { ItemSpacing = 6, LineSpacing = 4, Children =
            {
                Action(picture ? L("打开", "Open") : L("播放", "Play"), async () => await Execute("capture_open", args, token)),
                Action(L("另存为…", "Save as…"), () => ExportCaptureAsync(selected)),
                Menu("⋯", (L("在资源管理器中显示", "Show in File Explorer"), async () => await Execute("capture_reveal", args, token)),
                    (L("移到回收站", "Move to Recycle Bin"), () => Confirm(L("移到回收站？", "Move to Recycle Bin?"),
                        () => Capture("capture_recycle", args, token))))
            }};
            if (picture)
            {
                actions.Children.Insert(1, Action(L("复制图片", "Copy image"), async () =>
                {
                    await Execute("capture_copy", args, token);
                    Report(L("图片已复制", "Image copied"));
                }));
                actions.Children.Insert(2, Action(L("标注", "Annotate"), () => Capture("capture_annotate", args, token)));
                actions.Children.Insert(3, Action(L("贴图", "Pin"), async () => await Execute("capture_pin", args, token)));
                actions.Children.Insert(4, Action(L("识别文字", "OCR"), () => ScreenshotOcr(selected)));
            }
            body.Children.Add(actions);
            try
            {
                var file = new FileInfo(selected);
                body.Children.Add(Muted($"{file.LastWriteTime:g} · {file.Length / 1024d:N1} KB"));
                if (picture)
                {
                    using var input = file.OpenRead();
                    var bitmap = Bitmap.DecodeToWidth(input, 880);
                    var image = new Image { Source = bitmap, MaxHeight = 440, Stretch = Stretch.Uniform };
                    AutomationProperties.SetName(image, file.Name);
                    image.DetachedFromVisualTree += (_, _) => bitmap.Dispose();
                    body.Children.Add(image);
                }
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
            {
                body.Children.Add(Muted(L("无法预览文件", "Preview unavailable")));
            }
            detail.Content = WorkbenchPanel(Path.GetFileName(selected), picture ? "capture" : "play", body);
        }
    }

    private async Task RecordingOptionsAsync((string Label, string Value)[] displays, CancellationToken token)
    {
        var sources = new[] { (L("主显示器", "Primary display"), "primary"), (L("所有显示器", "All displays"), "desktop"),
            (L("选择区域", "Select a region"), "region") }.Concat(displays).ToArray();
        var source = CaptureChoice(L("范围", "Source"), sources.Select(item => item.Item1));
        var rates = new[] { 15, 24, 30, 60 };
        var fps = CaptureChoice(L("帧率", "Frame rate"), rates.Select(rate => $"{rate} fps"), 1);
        var formats = new[] { "MP4", "MKV" };
        var format = CaptureChoice(L("格式", "Format"), formats);
        var delays = new[] { 0, 3, 5, 10 };
        var delay = CaptureChoice(L("倒计时", "Countdown"), delays.Select(value => value == 0 ? L("立即", "None") : L($"{value} 秒", $"{value} seconds")));
        var cursor = new CheckBox { Content = L("录制鼠标指针", "Record cursor"), IsChecked = true };
        var audio = CaptureChoice(L("声音设备", "Audio device"), new[] { L("无声音", "No audio") });
        var devices = new List<string> { "" };
        var audioStatus = Muted(L("正在读取声音设备…", "Loading audio devices…"));
        var fields = new StackPanel { Spacing = 7, Children =
        {
            Muted(L("范围", "Source")), source, Muted(L("帧率", "Frame rate")), fps, Muted(L("格式", "Format")), format,
            Muted(L("声音设备", "Audio device")), audio, audioStatus, Muted(L("倒计时", "Countdown")), delay, cursor
        }};
        await ShowForm(L("录屏设置", "Recording options"), L("开始录屏", "Start recording"), L("正在启动录屏…", "Starting recording…"),
            (_, ct) => Capture("capture_record_start", new()
            {
                ["mode"] = sources[source.SelectedIndex].Item2, ["frameRate"] = rates[fps.SelectedIndex],
                ["format"] = formats[format.SelectedIndex].ToLowerInvariant(), ["audio"] = devices[audio.SelectedIndex],
                ["delay"] = delays[delay.SelectedIndex], ["cursor"] = cursor.IsChecked == true, ["hideWindow"] = true
            }, ct), [], fields, cancellable: true, initialFocus: source);
        var dialogToken = CurrentActionCancellation;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, dialogToken);
        try
        {
            var result = await Execute("capture_sources", new() { ["includeAudio"] = true }, linked.Token);
            devices.AddRange(result.Array("audio").Select(node => TextOf(node)).Where(name => name.Length > 0));
            audio.ItemsSource = devices.Select(name => name.Length == 0 ? L("无声音", "No audio") : name).ToArray();
            audio.SelectedIndex = 0;
            audioStatus.IsVisible = false;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error) { audioStatus.Text = error.Message; }
    }

    private async Task Capture(string operation, JsonObject arguments, CancellationToken token)
    {
        var result = await Execute(operation, arguments, token);
        if (result.Text("status") == "cancelled" || token.IsCancellationRequested) return;
        if (operation != "capture_record_start" && result.Text("path") is { Length: > 0 } path)
        {
            _captureSelection = path;
            _captureFilter = 0;
        }
        if (_page == "capture") await LoadAsync();
    }

    private ComboBox CaptureChoice(string label, IEnumerable<string> choices, int selected = 0)
    {
        var combo = new ComboBox { ItemsSource = choices.ToArray(), SelectedIndex = selected };
        AutomationProperties.SetName(combo, label);
        return combo;
    }

    private static bool IsScreenshot(string path) => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);

    private async Task ExportCaptureAsync(string path)
    {
        var extension = Path.GetExtension(path);
        var destination = await RequireTopLevel().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = Path.GetFileName(path), DefaultExtension = extension.TrimStart('.'), ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(extension.TrimStart('.').ToUpperInvariant()) { Patterns = ["*" + extension] }]
        });
        if (destination is null) return;
        if (destination.TryGetLocalPath() is { } local && Path.GetFullPath(local).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            return;
        await using var input = File.OpenRead(path);
        await using var output = await destination.OpenWriteAsync();
        if (output.CanSeek) output.SetLength(0);
        await input.CopyToAsync(output);
    }

    private Task ScreenshotOcr(string path)
    {
        var languages = new[] { "chi_sim+eng", "eng", "chi_sim" };
        var language = CaptureChoice(L("识别语言", "Recognition language"),
            new[] { L("简体中文和英语", "Simplified Chinese and English"), L("英语", "English"), L("简体中文", "Simplified Chinese") }, _english ? 1 : 0);
        var result = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 290 };
        AutomationProperties.SetName(result, L("识别结果", "Recognized text"));
        var status = Muted("");
        status.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var body = new StackPanel { Spacing = 8, Children = { Muted(L("识别语言", "Recognition language")), language, result, status } };
        var copy = Action(L("复制文字", "Copy text"), async () => await Execute("clipboard_local_write", new() { ["text"] = result.Text }));
        copy.IsEnabled = false;
        var run = PrimaryAction(L("识别", "Recognize"), async () =>
        {
            var ct = CurrentActionCancellation;
            status.Text = L("正在识别…", "Recognizing…");
            result.Text = "";
            copy.IsEnabled = false;
            try
            {
                var reply = await Execute("capture_ocr", new() { ["path"] = path, ["language"] = languages[language.SelectedIndex] }, ct);
                result.Text = TextOf(reply).Trim();
                copy.IsEnabled = result.Text.Length > 0;
                status.Text = copy.IsEnabled ? "" : L("未识别到文字", "No text recognized");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) { status.Text = error.Message; }
        });
        OpenActionDialog(L("识别文字", "Recognize text"), body, new WrapPanel { ItemSpacing = 7, Children =
        {
            CloseActionButton(L("关闭", "Close")), run, copy
        }}, language);
        return Task.CompletedTask;
    }
}
