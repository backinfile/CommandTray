using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private sealed record RecordingPreferences(string Source, string WindowId, int Fps, string Format, string Audio,
        int Delay, int Duration, bool Cursor, bool HideWindow, CaptureRegion? Region = null);

    private RecordingPreferences _recordingPreferences = new("primary", "", 24, "mp4", "", 0, 0, true, true);
    private CancellationTokenSource? _recordingStart;
    private bool _recordingStop;
    private bool _shortcutScreenshot;

    public async Task<JsonNode?> RunCaptureShortcutAsync(CaptureShortcut shortcut)
    {
        if (shortcut == CaptureShortcut.ToggleRecording)
        {
            if (_recordingStart is not null)
            {
                CancelPendingRecording();
                return null;
            }
            var state = await Execute("capture_record_state", []);
            return await ToggleRecordingAsync(state);
        }
        if (_shortcutScreenshot) return null;
        _shortcutScreenshot = true;
        try
        {
            string mode = shortcut == CaptureShortcut.Region ? "region" : "window";
            return await Capture(mode == "region" ? "capture_select" : "capture_take", ScreenshotArguments(mode), default);
        }
        finally { _shortcutScreenshot = false; }
    }

    public void CancelPendingRecording() => _recordingStart?.Cancel();

    private JsonObject ScreenshotArguments(string mode) =>
        new() { ["mode"] = mode, ["delay"] = _screenshotDelay, ["hideWindow"] = true, ["copy"] = _copyScreenshots };

    private async Task<JsonNode?> ToggleRecordingAsync(JsonNode? state)
    {
        if (_recordingStart is not null || _recordingStop || state.Text("state") is "starting" or "saving") return null;
        if (state.Flag("recording"))
        {
            _recordingStop = true;
            try { return await Capture("capture_record_stop", [], default); }
            finally { _recordingStop = false; }
        }

        var options = _recordingPreferences;
        if (options.Source == "window" && options.WindowId.Length == 0)
            throw new InvalidOperationException(L("请先在录屏设置中选择应用窗口。", "Select an application window in the recording settings."));
        using var start = new CancellationTokenSource();
        _recordingStart = start;
        try
        {
            var arguments = new JsonObject
            {
                ["mode"] = options.Source, ["windowId"] = options.WindowId, ["frameRate"] = options.Fps,
                ["format"] = options.Format, ["audio"] = options.Audio, ["delay"] = options.Delay,
                ["durationSeconds"] = options.Duration, ["cursor"] = options.Cursor, ["hideWindow"] = options.HideWindow
            };
            if (options.Source == "region" && options.Region is { } region)
            {
                arguments["x"] = region.X;
                arguments["y"] = region.Y;
                arguments["width"] = region.Width;
                arguments["height"] = region.Height;
            }
            return await _backend.ExecuteAsync("capture_record_start", arguments, start.Token);
        }
        catch (OperationCanceledException) when (start.IsCancellationRequested) { return null; }
        finally { _recordingStart = null; }
    }

    private (Control Panel, Action<JsonNode?> Update) RecordingPanel((string Label, string Value)[] displays,
        JsonNode? sources, JsonNode? state, CancellationToken token, Action showPreview)
    {
        var modes = new List<(string Label, string Value)>
        {
            (L("主显示器", "Primary display"), "primary"), (L("全部显示器", "All displays"), "desktop"),
            (L("应用窗口", "Application window"), "window"), (L("选择区域", "Select a region"), "region")
        };
        if (displays.Length > 1) modes.AddRange(displays);
        if (sources.Flag("lastRecordingRegion")) modes.Add((L("上次选区", "Last region"), "last"));
        if (!modes.Any(mode => mode.Value == _recordingPreferences.Source))
            _recordingPreferences = _recordingPreferences with { Source = "primary" };
        var options = _recordingPreferences;
        var source = Choice(L("录制对象", "Recording source"), modes.Select(mode => mode.Label),
            modes.FindIndex(mode => mode.Value == options.Source), 160);
        int[] rates = [15, 24, 30, 60];
        var fps = Choice(L("帧率", "Frame rate"), rates.Select(rate => $"{rate} fps"), Array.IndexOf(rates, options.Fps));
        string[] formats = ["mp4", "mkv"];
        var format = Choice(L("格式", "Format"), formats.Select(value => value.ToUpperInvariant()), Array.IndexOf(formats, options.Format));
        int[] delays = [0, 3, 5, 10];
        var delay = Choice(L("启动倒计时", "Start delay"), delays.Select(value => value == 0 ? L("立即", "None") :
            L($"{value} 秒", $"{value} seconds")), Array.IndexOf(delays, options.Delay));
        int[] limits = [0, 60, 300, 900, 1800];
        var duration = Choice(L("自动结束", "Stop after"), limits.Select(value => value == 0 ? L("不限时", "No limit") :
            L($"{value / 60} 分钟", $"{value / 60} minutes")), Array.IndexOf(limits, options.Duration));
        var cursor = new CheckBox { Content = L("录制鼠标指针", "Record cursor"), IsChecked = options.Cursor };
        var hide = new CheckBox { Content = L("录制时隐藏 CommandTray", "Hide CommandTray while recording"), IsChecked = options.HideWindow };

        string noAudio = L("无声音", "No audio");
        var audio = Choice(L("声音设备", "Audio device"), [noAudio], 0, 152);
        string[] audioDevices = [""];
        var audioStatus = Muted("");
        var windowLabel = L("选择应用窗口", "Select an application window");
        var windows = CaptureChoice(windowLabel, [windowLabel]);
        windows.HorizontalAlignment = HorizontalAlignment.Stretch;
        string[] windowIds = [""];
        var windowStatus = Muted("");
        var windowRow = new StackPanel { Spacing = 4, Children = { windowStatus } };
        var regionSize = Muted("");
        regionSize.VerticalAlignment = VerticalAlignment.Center;
        var regionRow = ActionBar();
        bool windowLoading = false, audioLoading = false, applyingList = false;

        var setup = new WrapPanel { ItemSpacing = 12, LineSpacing = 8, Children =
        {
            Field(L("录制对象", "Recording source"), source), Field(L("帧率", "Frame rate"), fps),
            Field(L("格式", "Format"), format), Field(L("声音设备", "Audio device"),
                Row(audio, IconButton("sync", L("刷新声音设备", "Refresh audio devices"), RefreshAudioAsync))),
            Field(L("启动倒计时", "Start delay"), delay), Field(L("自动结束", "Stop after"), duration)
        }};
        var summary = Muted("");
        var error = Muted("");
        error.Classes.Add("form-error");
        AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var settings = new StackPanel { Spacing = 7, Children = { setup, windowRow, regionRow, audioStatus, ActionBar(cursor, hide) } };
        var body = new StackPanel { Spacing = 7, Margin = new Thickness(12, 10), Children = { settings, summary, error } };

        var record = new Button();
        record.Classes.Add("action");
        record.Classes.Add("primary");
        var cancel = Action(L("取消启动", "Cancel recording start"), () =>
        {
            CancelPendingRecording();
            return Task.CompletedTask;
        });
        SetCaptureButton(cancel, L("取消启动", "Cancel recording start"), "close", sources, CaptureShortcut.ToggleRecording);
        var discard = MoreActions(L("录屏操作", "Recording actions"),
            (L("放弃录屏", "Discard recording"), () => Confirm(L("放弃当前录屏？", "Discard this recording?"),
                () => Capture("capture_record_discard", [], token), L("放弃录屏", "Discard recording"))));
        var windowRefresh = IconButton("sync", L("刷新窗口列表", "Refresh open windows"), RefreshWindowsAsync);
        windowRow.Children.Insert(0, TitleRow(windows, windowRefresh));
        regionRow.Children.Add(SecondaryAction(L("选择区域…", "Select region…"), async () =>
        {
            var selected = await Execute("capture_pick_region", [], token);
            if (selected is null) return;
            _recordingPreferences = _recordingPreferences with { Region =
                new CaptureRegion(selected.Integer("x"), selected.Integer("y"), selected.Integer("width"), selected.Integer("height")) };
            Update(state);
            showPreview();
        }, "capture"));
        regionRow.Children.Add(regionSize);
        record.Click += async (_, _) => await Act(async () =>
        {
            if (!record.IsEnabled || _recordingStart is not null) return;
            showPreview();
            var change = ToggleRecordingAsync(state);
            Update(state);
            try
            {
                await change;
                if (!token.IsCancellationRequested) Update(await Execute("capture_record_state", [], token));
            }
            finally { if (!token.IsCancellationRequested) Update(state); }
        });

        source.SelectionChanged += async (_, _) =>
        {
            if (source.SelectedIndex < 0) return;
            _recordingPreferences = _recordingPreferences with { Source = modes[source.SelectedIndex].Value };
            Update(state);
            showPreview();
            if (_recordingPreferences.Source == "window") await RefreshWindowsAsync();
        };
        fps.SelectionChanged += (_, _) => { if (fps.SelectedIndex >= 0) _recordingPreferences = _recordingPreferences with { Fps = rates[fps.SelectedIndex] }; };
        format.SelectionChanged += (_, _) => { if (format.SelectedIndex >= 0) _recordingPreferences = _recordingPreferences with { Format = formats[format.SelectedIndex] }; };
        delay.SelectionChanged += (_, _) => { if (delay.SelectedIndex >= 0) _recordingPreferences = _recordingPreferences with { Delay = delays[delay.SelectedIndex] }; };
        duration.SelectionChanged += (_, _) => { if (duration.SelectedIndex >= 0) _recordingPreferences = _recordingPreferences with { Duration = limits[duration.SelectedIndex] }; };
        cursor.IsCheckedChanged += (_, _) => _recordingPreferences = _recordingPreferences with { Cursor = cursor.IsChecked == true };
        hide.IsCheckedChanged += (_, _) => _recordingPreferences = _recordingPreferences with { HideWindow = hide.IsChecked == true };
        audio.SelectionChanged += (_, _) =>
        {
            if (!applyingList && audio.SelectedIndex >= 0)
                _recordingPreferences = _recordingPreferences with { Audio = audioDevices[audio.SelectedIndex] };
        };
        windows.SelectionChanged += (_, _) =>
        {
            if (applyingList || windows.SelectedIndex < 0) return;
            _recordingPreferences = _recordingPreferences with { WindowId = windowIds[windows.SelectedIndex] };
            Update(state);
            showPreview();
        };
        Update(state);
        if (sources.Flag("encoderAvailable"))
            Dispatcher.UIThread.Post(async () =>
            {
                if (token.IsCancellationRequested) return;
                var audioRequest = RefreshAudioAsync();
                if (_recordingPreferences.Source == "window") await RefreshWindowsAsync();
                await audioRequest;
            }, DispatcherPriority.Background);
        return (WorkbenchPanel(L("录屏", "Screen recording"), "play", body, ActionBar(record, cancel, discard)), Update);

        void Update(JsonNode? current)
        {
            state = current;
            bool active = state.Flag("recording"), pending = _recordingStart is not null;
            string phase = _recordingStop && active ? "saving" : state.Text("state");
            bool pickingWindow = _recordingPreferences.Source == "window";
            bool pickingRegion = _recordingPreferences.Source == "region";
            bool encoder = sources.Flag("encoderAvailable");
            windowRow.IsVisible = pickingWindow;
            regionRow.IsVisible = pickingRegion;
            regionSize.Text = _recordingPreferences.Region is { } region ? $"{region.Width} × {region.Height}" : "";
            settings.IsEnabled = !active && !pending;
            cancel.IsVisible = pending;
            record.IsVisible = !pending;
            record.IsEnabled = phase is not ("starting" or "saving") &&
                (active || encoder && (!pickingWindow || !windowLoading && _recordingPreferences.WindowId.Length > 0)
                    && (!pickingRegion || _recordingPreferences.Region.HasValue));
            discard.IsVisible = !pending && phase == "recording";
            string label = active ? L("结束录屏", "Stop recording") : L("开始录屏", "Start recording");
            if (AutomationProperties.GetName(record) != label)
                SetCaptureButton(record, label, active ? "stop" : "play", sources, CaptureShortcut.ToggleRecording);
            string elapsed = TimeSpan.FromSeconds(state.Integer("elapsedSeconds")).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            summary.Text = pending ? L("正在启动录屏…", "Starting recording…") : phase switch
            {
                "starting" => L("正在启动录屏…", "Starting recording…"),
                "saving" => L("正在结束录屏…", "Finishing recording…"),
                "recording" => L($"正在录制 · {elapsed}", $"Recording · {elapsed}"),
                _ => ""
            };
            summary.IsVisible = summary.Text.Length > 0;
            SetStatusTone(summary, phase == "recording" ? "success" : "");
            error.Text = encoder ? state.Text("error") : L("请安装 FFmpeg 并将其加入 PATH。", "Install FFmpeg and add it to PATH.");
            error.IsVisible = error.Text.Length > 0;
        }

        async Task RefreshWindowsAsync()
        {
            if (windowLoading || token.IsCancellationRequested) return;
            windowLoading = true;
            windowStatus.IsVisible = true;
            windowStatus.Text = L("正在读取应用窗口…", "Loading application windows…");
            Update(state);
            try
            {
                var data = await Execute("capture_sources", new() { ["includeWindows"] = true }, token);
                var entries = data.Array("windows").OfType<JsonObject>().Where(item => item.Text("id").Length > 0).ToArray();
                applyingList = true;
                try
                {
                    windowIds = ["", .. entries.Select(item => item.Text("id"))];
                    windows.ItemsSource = new[] { windowLabel }.Concat(entries.Select(item =>
                        $"{item.Text("application")} · {item.Text("title")}")).ToArray();
                    windows.SelectedIndex = Math.Max(0, Array.IndexOf(windowIds, _recordingPreferences.WindowId));
                    _recordingPreferences = _recordingPreferences with { WindowId = windowIds[windows.SelectedIndex] };
                }
                finally { applyingList = false; }
                windowStatus.Text = entries.Length == 0 ? L("没有可录制的窗口。", "No application windows available.") : "";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception failure) { windowStatus.Text = failure.Message; }
            finally
            {
                windowLoading = false;
                windowStatus.IsVisible = windowStatus.Text?.Length > 0;
                if (!token.IsCancellationRequested) Update(state);
            }
        }

        async Task RefreshAudioAsync()
        {
            if (audioLoading || token.IsCancellationRequested || !sources.Flag("encoderAvailable")) return;
            audioLoading = true;
            audio.IsEnabled = false;
            audioStatus.IsVisible = true;
            audioStatus.Text = L("正在读取声音设备…", "Loading audio devices…");
            try
            {
                var data = await Execute("capture_sources", new() { ["includeAudio"] = true }, token);
                applyingList = true;
                try
                {
                    audioDevices = ["", .. data.Array("audio").Select(node => TextOf(node)).Where(name => name.Length > 0)];
                    audio.ItemsSource = new[] { noAudio }.Concat(audioDevices.Skip(1)).ToArray();
                    audio.SelectedIndex = Math.Max(0, Array.IndexOf(audioDevices, _recordingPreferences.Audio));
                    _recordingPreferences = _recordingPreferences with { Audio = audioDevices[audio.SelectedIndex] };
                }
                finally { applyingList = false; }
                audioStatus.Text = "";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception failure) { audioStatus.Text = failure.Message; }
            finally
            {
                audioLoading = false;
                audio.IsEnabled = true;
                audioStatus.IsVisible = audioStatus.Text?.Length > 0;
            }
        }

        ComboBox Choice(string label, IEnumerable<string> values, int selected, int width = 104)
        {
            var combo = CaptureChoice(label, values, Math.Max(0, selected));
            combo.Width = width;
            return combo;
        }

        StackPanel Field(string label, Control control) => new() { Spacing = 3, Children = { Muted(label), control } };
    }

}
