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
    private string _captureQuery = "";
    private string _screenshotSource = "region";
    private int _screenshotDelay;
    private bool _copyScreenshots = true;
    private RecordingPreferences _recordingPreferences = new("primary", 24, "mp4", "", 0, true);

    private sealed record RecordingPreferences(string Source, int Fps, string Format, string Audio, int Delay, bool Cursor);

    private async Task RenderCaptureAsync(CancellationToken token)
    {
        Header(L("截图与录屏", "Screen capture"));
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
        var modes = new List<(string Label, string Value)>
        {
            (L("选择区域", "Select a region"), "region"),
            (L("活动窗口", "Active window"), "window"),
            (L("主显示器", "Primary display"), "primary"),
            (L("全部显示器", "All displays"), "desktop")
        };
        if (displays.Length > 1) modes.AddRange(displays);
        if (sources.Flag("lastRegion")) modes.Add((L("上次区域", "Last region"), "last"));
        modes.Add((L("输入坐标…", "Enter coordinates…"), "coordinates"));
        var scope = CaptureChoice(L("范围", "Source"), modes.Select(mode => mode.Label), Math.Max(0, modes.FindIndex(mode => mode.Value == _screenshotSource)));
        var delays = new[] { 0, 3, 5, 10 };
        var wait = CaptureChoice(L("延时", "Delay"), delays.Select(delay => delay == 0 ? L("立即", "Now") : L($"{delay} 秒", $"{delay} seconds")),
            Array.IndexOf(delays, _screenshotDelay));
        var copy = new CheckBox { Content = L("截图后复制", "Copy after capture"), IsChecked = _copyScreenshots };
        scope.SelectionChanged += (_, _) => { if (scope.SelectedIndex >= 0) _screenshotSource = modes[scope.SelectedIndex].Value; };
        wait.SelectionChanged += (_, _) => { if (wait.SelectedIndex >= 0) _screenshotDelay = delays[wait.SelectedIndex]; };
        copy.IsCheckedChanged += (_, _) => _copyScreenshots = copy.IsChecked == true;
        var capture = PrimaryAction(L("截图", "Take screenshot"), StartScreenshotAsync, "capture");
        var record = SecondaryAction("", async () =>
        {
            if (recording.Flag("recording")) await Capture("capture_record_stop", [], token);
            else await RecordingOptionsAsync(displays, token);
        });
        var discard = MoreActions(L("录屏操作", "Recording actions"),
            (L("放弃录屏", "Discard recording"), () => Confirm(L("放弃当前录屏？", "Discard this recording?"),
                () => Capture("capture_record_discard", [], token), L("放弃录屏", "Discard recording"))));
        Header(L("截图与录屏", "Screen capture"));
        var controls = ActionBar(CaptureField(L("范围", "Source"), scope), CaptureField(L("延时", "Delay"), wait), copy, capture, record);
        _contents.Children.Add(controls);
        var recordingStatus = new WrapPanel { ItemSpacing = 10, LineSpacing = 6, Children = { summary, recordingFile, discard } };
        _contents.Children.Add(recordingStatus);
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
        controls.AttachedToVisualTree += (_, _) => timer.Start();
        controls.DetachedFromVisualTree += (_, _) => timer.Stop();
        if (TopLevel.GetTopLevel(controls) is not null) timer.Start();

        var filter = CaptureChoice(L("捕获类型", "Capture type"), new[] { L("全部", "All"), L("截图", "Screenshots"), L("录屏", "Recordings") });
        filter.SelectedIndex = _captureFilter;
        var search = new TextBox { Text = _captureQuery, PlaceholderText = L("搜索文件名", "Search file names"), Margin = new Thickness(9, 8) };
        AutomationProperties.SetName(search, L("搜索捕获历史", "Search captures"));
        var folder = IconButton("folder-open", L("打开捕获目录", "Open capture folder"), async () => await Execute("capture_reveal", [], token));
        var list = new StackPanel();
        var detail = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var history = WorkbenchPanel(L("捕获历史", "Capture history"), "history", new StackPanel { Children = { search, list } }, Row(filter, folder));
        if (_compact) { _contents.Children.Add(history); _contents.Children.Add(detail); }
        else _contents.Children.Add(WorkbenchSplit(history, detail, "1*,1.55*"));
        filter.SelectionChanged += (_, _) => { _captureFilter = filter.SelectedIndex; DrawList(); };
        search.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty) return;
            _captureQuery = search.Text ?? "";
            DrawList();
        };
        DrawList();

        void UpdateRecording()
        {
            string state = recording.Text("state");
            var active = recording.Flag("recording");
            recordingStatus.IsVisible = active;
            SetStatusTone(summary, state == "recording" ? "success" : active ? "warning" : "");
            record.Classes.Set("primary", active);
            record.Classes.Set("secondary", !active);
            capture.Classes.Set("primary", !active);
            string elapsed = TimeSpan.FromSeconds(recording.Integer("elapsedSeconds")).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            summary.Text = state switch
            {
                "starting" => L("正在启动录屏…", "Starting recording…"),
                "saving" => L("正在结束录屏…", "Finishing recording…"),
                "recording" => L($"正在录制 · {elapsed}", $"Recording · {elapsed}"),
                _ => ""
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

        Task StartScreenshotAsync()
        {
            var mode = modes[scope.SelectedIndex].Value;
            if (mode == "coordinates") return CoordinateScreenshot();
            return Capture(mode == "region" ? "capture_select" : "capture_take", ScreenshotArguments(mode), token);
        }

        JsonObject ScreenshotArguments(string mode) =>
            new() { ["mode"] = mode, ["delay"] = _screenshotDelay, ["hideWindow"] = true, ["copy"] = _copyScreenshots };

        Task CoordinateScreenshot()
        {
            Field[] coordinates = [new("x", "X"), new("y", "Y"), new("width", L("宽", "Width")), new("height", L("高", "Height"))];
            return Form(L("区域截图", "Region capture"), L("截图", "Capture"), L("正在截图…", "Capturing…"), (values, ct) =>
            {
                var area = ScreenshotArguments("desktop");
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
            var query = _captureQuery.Trim();
            var paths = captures.Where(path => (_captureFilter == 0 || IsScreenshot(path) == (_captureFilter == 1)) &&
                Path.GetFileName(path).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            var previous = _captureSelection;
            _captureSelection = paths.Contains(_captureSelection) ? _captureSelection : paths.FirstOrDefault();
            if (_captureSelection is null)
            {
                list.Children.Add(EmptyState("capture", query.Length > 0 ? L("没有匹配的捕获", "No matching captures") : L("暂无捕获", "No captures")));
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
            if (detail.Content is null || previous != _captureSelection) DrawDetail(_captureSelection);
        }

        void DrawDetail(string selected)
        {
            var args = new JsonObject { ["path"] = selected };
            var picture = IsScreenshot(selected);
            var body = new StackPanel { Spacing = 9, Margin = new Thickness(13) };
            var actions = ActionBar();
            var choices = new List<(string Label, Func<Task> Action)>
            {
                (L("上传到共享文件…", "Upload to shared files…"), () => ShareCaptureAsync(selected, token)),
                (L("重命名…", "Rename…"), () => Form(L("重命名", "Rename"), values =>
                        Capture("capture_rename", new() { ["path"] = selected, ["name"] = values.RequireText("name") }, token),
                        new Field("name", L("文件名", "File name"), Path.GetFileNameWithoutExtension(selected)))),
                    (L("复制文件路径", "Copy file path"), async () =>
                    {
                        await Execute("clipboard_local_write", new() { ["text"] = selected }, token);
                        Report(L("路径已复制", "Path copied"));
                    }),
                    (L("在资源管理器中显示", "Show in File Explorer"), async () => await Execute("capture_reveal", args, token)),
                    (L("移到回收站", "Move to Recycle Bin"), () => Confirm(L("移到回收站？", "Move to Recycle Bin?"),
                        () => Capture("capture_recycle", args, token), L("移到回收站", "Move to Recycle Bin")))
            };
            if (picture)
            {
                actions.Children.Add(SecondaryAction(L("复制图片", "Copy image"), async () =>
                {
                    await Execute("capture_copy", args, token);
                    Report(L("图片已复制", "Image copied"));
                }, "copy"));
                actions.Children.Add(Action(L("标注", "Annotate"), () => Capture("capture_annotate", args, token)));
                choices.InsertRange(0, new (string, Func<Task>)[]
                {
                    (L("打开原图", "Open original"), async () => await Execute("capture_open", args, token)),
                    (L("贴图", "Pin"), async () => await Execute("capture_pin", args, token)),
                    (L("识别文字", "OCR"), () => ScreenshotOcr(selected)),
                    (L("识别二维码", "Read QR code"), () => RecognizeQrImageAsync(
                        () => Task.FromResult<Stream>(File.OpenRead(selected)), token, OpenQrInputAsync))
                });
            }
            else actions.Children.Add(SecondaryAction(L("播放", "Play"), async () => await Execute("capture_open", args, token), "play"));
            actions.Children.Add(Action(L("另存为…", "Save as…"), () => ExportCaptureAsync(selected)));
            actions.Children.Add(MoreActions(L("捕获文件操作", "Capture file actions"), choices.ToArray()));
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

    private Control CaptureField(string label, Control control)
    {
        var text = Muted(label);
        text.VerticalAlignment = VerticalAlignment.Center;
        return Row(text, control);
    }

    private async Task RecordingOptionsAsync((string Label, string Value)[] displays, CancellationToken token)
    {
        var sources = new[] { (L("主显示器", "Primary display"), "primary"), (L("所有显示器", "All displays"), "desktop"),
            (L("选择区域", "Select a region"), "region") }.Concat(displays).ToArray();
        var preferences = _recordingPreferences;
        var source = CaptureChoice(L("范围", "Source"), sources.Select(item => item.Item1),
            Math.Max(0, Array.FindIndex(sources, option => option.Item2 == preferences.Source)));
        var rates = new[] { 15, 24, 30, 60 };
        var fps = CaptureChoice(L("帧率", "Frame rate"), rates.Select(rate => $"{rate} fps"), Array.IndexOf(rates, preferences.Fps));
        var formats = new[] { "MP4", "MKV" };
        var format = CaptureChoice(L("格式", "Format"), formats, Array.IndexOf(formats, preferences.Format.ToUpperInvariant()));
        var delays = new[] { 0, 3, 5, 10 };
        var delay = CaptureChoice(L("倒计时", "Countdown"), delays.Select(value => value == 0 ? L("立即", "None") : L($"{value} 秒", $"{value} seconds")),
            Array.IndexOf(delays, preferences.Delay));
        var cursor = new CheckBox { Content = L("录制鼠标指针", "Record cursor"), IsChecked = preferences.Cursor };
        var audio = CaptureChoice(L("声音设备", "Audio device"), new[] { L("无声音", "No audio") });
        var devices = new List<string> { "" };
        var audioStatus = Muted(L("正在读取声音设备…", "Loading audio devices…"));
        var fields = new StackPanel { Spacing = 7, Children =
        {
            Muted(L("范围", "Source")), source, Muted(L("帧率", "Frame rate")), fps, Muted(L("格式", "Format")), format,
            Muted(L("声音设备", "Audio device")), audio, audioStatus, Muted(L("倒计时", "Countdown")), delay, cursor
        }};
        await ShowForm(L("录屏设置", "Recording options"), L("开始录屏", "Start recording"), L("正在启动录屏…", "Starting recording…"),
            (_, ct) =>
            {
                var chosen = new RecordingPreferences(sources[source.SelectedIndex].Item2, rates[fps.SelectedIndex],
                    formats[format.SelectedIndex].ToLowerInvariant(), devices[Math.Max(0, audio.SelectedIndex)],
                    delays[delay.SelectedIndex], cursor.IsChecked == true);
                _recordingPreferences = chosen;
                return Capture("capture_record_start", new()
                {
                    ["mode"] = chosen.Source, ["frameRate"] = chosen.Fps, ["format"] = chosen.Format, ["audio"] = chosen.Audio,
                    ["delay"] = chosen.Delay, ["cursor"] = chosen.Cursor, ["hideWindow"] = true
                }, ct);
            }, [], fields, cancellable: true, initialFocus: source);
        var dialogToken = CurrentActionCancellation;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, dialogToken);
        try
        {
            var result = await Execute("capture_sources", new() { ["includeAudio"] = true }, linked.Token);
            devices.AddRange(result.Array("audio").Select(node => TextOf(node)).Where(name => name.Length > 0));
            audio.ItemsSource = devices.Select(name => name.Length == 0 ? L("无声音", "No audio") : name).ToArray();
            audio.SelectedIndex = Math.Max(0, devices.IndexOf(preferences.Audio));
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
            _captureQuery = "";
        }
        if (_page == "capture")
        {
            await LoadAsync();
            if (result.Text("warning") is { Length: > 0 } warning) Report(warning);
        }
    }

    private ComboBox CaptureChoice(string label, IEnumerable<string> choices, int selected = 0)
    {
        var combo = new ComboBox { ItemsSource = choices.ToArray(), SelectedIndex = selected };
        AutomationProperties.SetName(combo, label);
        return combo;
    }

    private static bool IsScreenshot(string path) => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);

    private async Task ShareCaptureAsync(string path, CancellationToken token)
    {
        var relay = await Execute("relay_status", [], token);
        if (!relay.Flag("paired"))
            throw new InvalidOperationException(L("请先在共享文件页配对中转服务器。", "Pair a relay from Shared files first."));
        if (!File.Exists(path))
            throw new FileNotFoundException(L("捕获文件已不存在。", "The capture file no longer exists."));
        await Form(L("上传捕获文件", "Upload capture"), L("上传", "Upload"), values =>
        {
            QueueUpload(Path.GetFileName(path), () => Task.FromResult<Stream>(File.OpenRead(path)),
                values.Text("folder"), relay.Text("deviceId"));
            Navigate("files");
            _ = ProcessTransfersAsync();
            return Task.CompletedTask;
        }, new Field("folder", L("共享目录", "Shared folder"), "captures"));
    }

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
        var transform = Action(L("处理文本", "Process text"), () => OpenTextToolsAsync(result.Text ?? ""));
        copy.IsEnabled = transform.IsEnabled = false;
        var run = PrimaryAction(L("识别", "Recognize"), async () =>
        {
            var ct = CurrentActionCancellation;
            status.Text = L("正在识别…", "Recognizing…");
            result.Text = "";
            copy.IsEnabled = transform.IsEnabled = false;
            try
            {
                var reply = await Execute("capture_ocr", new() { ["path"] = path, ["language"] = languages[language.SelectedIndex] }, ct);
                result.Text = TextOf(reply).Trim();
                copy.IsEnabled = transform.IsEnabled = result.Text.Length > 0;
                status.Text = copy.IsEnabled ? "" : L("未识别到文字", "No text recognized");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) { status.Text = error.Message; }
        });
        OpenActionDialog(L("识别文字", "Recognize text"), body, new WrapPanel { ItemSpacing = 7, Children =
        {
            CloseActionButton(L("关闭", "Close")), run, copy, transform
        }}, language);
        return Task.CompletedTask;
    }
}
