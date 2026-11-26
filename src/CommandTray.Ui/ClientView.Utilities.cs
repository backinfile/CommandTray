using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommandTray.Core;
using QRCoder;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private enum UtilitySection { Convert, Compare, Timestamp, Qr, Checksum }
    private UtilitySection _utilitySection;
    private string _diffBefore = "", _diffAfter = "", _timestampInput = "", _qrInput = "", _expectedHash = "";
    private TimestampFormat _timestampFormat;

    private Task OpenFileChecksumAsync(string checksum)
    {
        _utilitySection = UtilitySection.Checksum;
        _expectedHash = checksum;
        Navigate("text-tools");
        return Task.CompletedTask;
    }

    private void RenderUtilities(CancellationToken token)
    {
        Header(L("实用工具", "Utilities"));
        var tabs = ActionBar();
        foreach (var (section, name) in new[]
        {
            (UtilitySection.Convert, L("文本转换", "Text conversion")),
            (UtilitySection.Compare, L("文本对比", "Text comparison")),
            (UtilitySection.Timestamp, L("时间戳", "Timestamp")),
            (UtilitySection.Qr, L("二维码", "QR code")),
            (UtilitySection.Checksum, L("文件校验", "File checksum"))
        })
        {
            var tab = Action(name, () =>
            {
                if (_utilitySection != section)
                {
                    _utilitySection = section;
                    Navigate("text-tools");
                }
                return Task.CompletedTask;
            });
            tab.Classes.Add("workbench-tab");
            tab.Classes.Set("selected", _utilitySection == section);
            tabs.Children.Add(tab);
        }
        _contents.Children.Add(tabs);
        switch (_utilitySection)
        {
            case UtilitySection.Convert: RenderTextTools(token); break;
            case UtilitySection.Compare: RenderTextComparison(token); break;
            case UtilitySection.Timestamp: RenderTimestamp(token); break;
            case UtilitySection.Qr: RenderQrCode(token); break;
            case UtilitySection.Checksum: RenderFileChecksum(token); break;
        }
    }

    private void RenderTextComparison(CancellationToken token)
    {
        var before = UtilityInput(L("旧文本", "Before"), _diffBefore, true);
        var after = UtilityInput(L("新文本", "After"), _diffAfter, true);
        var result = UtilityInput(L("差异", "Differences"), "", true, readOnly: true);
        var feedback = UtilityFeedback();
        var copy = UtilityCopy(L("复制差异", "Copy differences"), () => result.Text, token);
        var compare = PrimaryAction(L("对比", "Compare"), async () =>
        {
            var a = before.Text ?? "";
            var b = after.Text ?? "";
            try
            {
                var difference = await Task.Run(() => LocalUtilities.Compare(a, b), token);
                if (token.IsCancellationRequested || a != (before.Text ?? "") || b != (after.Text ?? "")) return;
                var newline = L("末尾换行有变化", "Final newline changed");
                result.Text = difference.IsSame ? "" : difference.Lines + (difference.FinalNewlineChanged ? $"\\ {newline}\n" : "");
                copy.IsEnabled = !difference.IsSame;
                feedback.Text = difference.IsSame ? L("两段文本相同", "The texts match") :
                    L($"新增 {difference.Added} 行 · 删除 {difference.Removed} 行", $"{difference.Added} added · {difference.Removed} removed") +
                    (difference.FinalNewlineChanged ? $" · {newline}" : "");
                SetStatusTone(feedback, difference.IsSame ? "success" : "");
            }
            catch (ArgumentOutOfRangeException) when (!token.IsCancellationRequested &&
                a == (before.Text ?? "") && b == (after.Text ?? ""))
            {
                feedback.Text = L("每段文本最多 2,000 行或 100 万字符。", "Each text is limited to 2,000 lines and one million characters.");
                SetStatusTone(feedback, "error");
            }
        });
        var left = WorkbenchPanel(L("旧文本", "Before"), "text", before);
        var right = WorkbenchPanel(L("新文本", "After"), "text", after);
        _contents.Children.Add(_compact ? new StackPanel { Spacing = 10, Children = { left, right } } : WorkbenchSplit(left, right));
        _contents.Children.Add(ActionBar(compare));
        _contents.Children.Add(feedback);
        _contents.Children.Add(WorkbenchPanel(L("差异", "Differences"), "text-tools", result, copy));
        before.TextChanged += (_, _) => { _diffBefore = before.Text ?? ""; Invalidate(); };
        after.TextChanged += (_, _) => { _diffAfter = after.Text ?? ""; Invalidate(); };
        Invalidate();
        void Invalidate() { result.Text = feedback.Text = ""; copy.IsEnabled = false; }
    }

    private void RenderTimestamp(CancellationToken token)
    {
        var options = new[] { L("Unix 秒", "Unix seconds"), L("Unix 毫秒", "Unix milliseconds"), L("ISO 8601（含时区）", "ISO 8601 (with offset)") };
        var mode = new ComboBox { ItemsSource = options, SelectedIndex = (int)_timestampFormat };
        AutomationProperties.SetName(mode, L("输入格式", "Input format"));
        var input = UtilityInput(L("时间", "Time"), _timestampInput);
        input.MaxLength = 64;
        var output = UtilityInput(L("转换结果", "Converted time"), "", true, readOnly: true);
        output.MinHeight = 125;
        var feedback = UtilityFeedback();
        (TimestampFormat Format, string Value)? evaluated = null;
        var copy = UtilityCopy(L("复制结果", "Copy result"), () => output.Text, token);
        var now = Action(L("现在", "Now"), () =>
        {
            var current = DateTimeOffset.UtcNow;
            input.Text = SelectedFormat() switch
            {
                TimestampFormat.Seconds => current.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                TimestampFormat.Milliseconds => current.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                _ => Utc(current)
            };
            return ConvertTimestamp();
        });
        var convert = PrimaryAction(L("转换时间", "Convert time"), ConvertTimestamp);
        _contents.Children.Add(ActionBar(mode, convert, now));
        _contents.Children.Add(WorkbenchPanel(L("时间", "Time"), "history", input));
        _contents.Children.Add(feedback);
        _contents.Children.Add(WorkbenchPanel(L("转换结果", "Converted time"), "text-tools", output, copy));
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex >= 0) _timestampFormat = (TimestampFormat)mode.SelectedIndex;
            RefreshInput();
        };
        input.TextChanged += (_, _) => { _timestampInput = input.Text ?? ""; RefreshInput(); };
        RefreshInput();

        void RefreshInput()
        {
            input.PlaceholderText = SelectedFormat() switch
            {
                TimestampFormat.Seconds => "1735689600",
                TimestampFormat.Milliseconds => "1735689600000",
                _ => "2025-01-01T08:00:00+08:00"
            };
            if (evaluated == (SelectedFormat(), input.Text ?? "")) return;
            evaluated = null;
            output.Text = feedback.Text = "";
            copy.IsEnabled = false;
        }

        Task ConvertTimestamp()
        {
            var format = SelectedFormat();
            var value = input.Text ?? "";
            evaluated = (format, value);
            try
            {
                var instant = LocalUtilities.ReadTimestamp(format, value);
                var local = instant.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
                output.Text = $"UTC  {Utc(instant)}\n{L("本地时间", "Local time")}  {local}\n{options[0]}  {instant.ToUnixTimeSeconds()}\n{options[1]}  {instant.ToUnixTimeMilliseconds()}";
                copy.IsEnabled = true;
                feedback.Text = "";
            }
            catch (Exception error) when (error is FormatException or ArgumentOutOfRangeException)
            {
                output.Text = "";
                copy.IsEnabled = false;
                feedback.Text = format == TimestampFormat.Iso8601
                    ? L("请输入带 Z 或时区偏移的有效时间。", "Enter a valid time with Z or a UTC offset.")
                    : L("请输入有效范围内的整数时间戳。", "Enter an integer timestamp in range.");
                SetStatusTone(feedback, "error");
            }
            return Task.CompletedTask;
        }

        static string Utc(DateTimeOffset timestamp) =>
            timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        TimestampFormat SelectedFormat() => mode.SelectedIndex >= 0 ? (TimestampFormat)mode.SelectedIndex : _timestampFormat;
    }

    private void RenderQrCode(CancellationToken token)
    {
        var input = UtilityInput(L("文本或链接", "Text or link"), _qrInput, true);
        input.MaxLength = LocalUtilities.QrByteLimit;
        var image = new Image { Width = 256, Height = 256, Stretch = Stretch.Uniform, Margin = new Thickness(12), IsVisible = false };
        AutomationProperties.SetName(image, L("二维码", "QR code"));
        var feedback = UtilityFeedback();
        byte[]? png = null;
        string? evaluated = null;
        var save = SecondaryAction(L("保存 PNG", "Save PNG"), async () =>
        {
            if (png is not { } bytes) return;
            using var file = await RequireTopLevel().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "qrcode.png", DefaultExtension = "png", ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"], MimeTypes = ["image/png"] }]
            });
            if (file is null) return;
            await using var destination = await file.OpenWriteAsync();
            if (destination.CanSeek) destination.SetLength(0);
            await destination.WriteAsync(bytes);
        }, "download");
        save.IsEnabled = false;
        var generate = PrimaryAction(L("生成二维码", "Generate QR code"), async () =>
        {
            var text = input.Text ?? "";
            if (text.Length == 0) { ShowQrError(L("请输入文本或链接。", "Enter text or a link.")); return; }
            if (Encoding.UTF8.GetByteCount(text) > LocalUtilities.QrByteLimit)
            {
                ShowQrError(L("二维码内容最多 2,000 个 UTF-8 字节。", "QR codes support up to 2,000 UTF-8 bytes."));
                return;
            }
            var bytes = await Task.Run(() =>
            {
                using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M,
                    forceUtf8: true, eciMode: QRCodeGenerator.EciMode.Utf8);
                using var renderer = new PngByteQRCode(data);
                return renderer.GetGraphic(8);
            }, token);
            if (token.IsCancellationRequested || text != (input.Text ?? "")) return;
            Reset();
            using var stream = new MemoryStream(bytes);
            image.Source = new Bitmap(stream);
            image.IsVisible = true;
            png = bytes;
            evaluated = text;
            save.IsEnabled = true;
        });
        input.TextChanged += (_, _) => { _qrInput = input.Text ?? ""; if (evaluated != _qrInput) Reset(); };
        image.DetachedFromVisualTree += (_, _) => Reset();
        var left = WorkbenchPanel(L("文本或链接", "Text or link"), "text", input, generate);
        var right = WorkbenchPanel(L("二维码预览", "QR preview"), "spaces", image, save);
        _contents.Children.Add(_compact ? new StackPanel { Spacing = 10, Children = { left, right } } : WorkbenchSplit(left, right));
        _contents.Children.Add(feedback);

        void ShowQrError(string message)
        {
            Reset();
            evaluated = input.Text ?? "";
            feedback.Text = message;
            SetStatusTone(feedback, "error");
        }
        void Reset()
        {
            if (image.Source is Bitmap previous) previous.Dispose();
            image.Source = null;
            image.IsVisible = false;
            feedback.Text = "";
            save.IsEnabled = false;
            png = null;
            evaluated = null;
        }
    }

    private void RenderFileChecksum(CancellationToken token)
    {
        var expected = UtilityInput(L("预期 SHA-256", "Expected SHA-256"), _expectedHash, true);
        expected.MaxLength = 256;
        var actual = UtilityInput(L("计算出的 SHA-256", "Calculated SHA-256"), "", true, readOnly: true);
        expected.MinHeight = actual.MinHeight = 62;
        expected.MaxHeight = actual.MaxHeight = 92;
        var progress = new ProgressBar { IsVisible = false, Margin = new Thickness(12, 6) };
        var status = UtilityFeedback();
        var fileName = Muted(L("尚未选择文件", "No file selected"));
        fileName.Margin = new Thickness(12);
        fileName.TextWrapping = TextWrapping.Wrap;
        var copy = UtilityCopy(L("复制 SHA-256", "Copy SHA-256"), () => actual.Text, token);
        copy.IsEnabled = false;
        CancellationTokenSource? active = null;
        var cancel = Action(L("取消计算", "Cancel calculation"), () => { active?.Cancel(); return Task.CompletedTask; });
        cancel.IsVisible = false;
        var choose = PrimaryAction(L("选择文件", "Choose file"), async () =>
        {
            var files = await RequireTopLevel().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
            if (files.Count == 0) return;
            using var file = files[0];
            if (token.IsCancellationRequested) return;
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
            active = pending;
            fileName.Text = file.Name;
            actual.Text = "";
            copy.IsEnabled = false;
            cancel.IsVisible = progress.IsVisible = true;
            progress.IsIndeterminate = true;
            status.Text = L("正在计算…", "Calculating…");
            SetStatusTone(status, "");
            try
            {
                await using var stream = await file.OpenReadAsync();
                pending.Token.ThrowIfCancellationRequested();
                long? total = stream.CanSeek ? stream.Length : null;
                progress.IsIndeterminate = total is null or 0;
                progress.Value = 0;
                var updates = new Progress<long>(read =>
                {
                    if (active == pending && total is > 0) progress.Value = read * 100d / total.Value;
                });
                actual.Text = await LocalUtilities.FileSha256Async(stream, updates, pending.Token);
                copy.IsEnabled = true;
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested)
            {
                status.Text = L("已取消计算", "Calculation cancelled");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                status.Text = L("无法读取所选文件。", "The selected file could not be read.");
                SetStatusTone(status, "error");
            }
            finally
            {
                active = null;
                cancel.IsVisible = progress.IsVisible = false;
            }
            if (!string.IsNullOrEmpty(actual.Text)) CompareHash();
        });
        _contents.Children.Add(ActionBar(choose, cancel));
        _contents.Children.Add(WorkbenchPanel(L("本地文件", "Local file"), "file",
            new StackPanel { Children = { fileName, progress } }));
        _contents.Children.Add(WorkbenchPanel(L("预期 SHA-256", "Expected SHA-256"), "text", expected));
        _contents.Children.Add(WorkbenchPanel(L("计算出的 SHA-256", "Calculated SHA-256"), "text-tools", actual, copy));
        _contents.Children.Add(status);
        expected.TextChanged += (_, _) => { _expectedHash = expected.Text ?? ""; if (copy.IsEnabled) CompareHash(); };

        void CompareHash()
        {
            var expectedValue = (expected.Text ?? "").Trim();
            if (expectedValue.Length == 0)
            {
                status.Text = L("已计算 SHA-256", "SHA-256 calculated");
                SetStatusTone(status, "success");
            }
            else if (!LocalUtilities.IsSha256(expectedValue))
            {
                status.Text = L("预期值需要 64 位十六进制字符。", "The expected value must be 64 hexadecimal characters.");
                SetStatusTone(status, "error");
            }
            else
            {
                var matches = expectedValue.Equals(actual.Text, StringComparison.OrdinalIgnoreCase);
                status.Text = matches ? L("SHA-256 一致", "SHA-256 matches") : L("SHA-256 不一致", "SHA-256 does not match");
                SetStatusTone(status, matches ? "success" : "error");
            }
        }
    }

    private TextBox UtilityInput(string label, string value, bool multiline = false, bool readOnly = false)
    {
        var box = new TextBox
        {
            Text = value, AcceptsReturn = multiline, IsReadOnly = readOnly,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(12), FontFamily = new FontFamily("monospace"),
            MinHeight = multiline ? 145 : 36, MaxHeight = multiline ? 330 : double.PositiveInfinity
        };
        if (!readOnly) box.MaxLength = TextTools.InputLimit;
        box.Classes.Add("entry");
        AutomationProperties.SetName(box, label);
        return box;
    }

    private TextBlock UtilityFeedback()
    {
        var status = Muted("");
        status.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        return status;
    }

    private Button UtilityCopy(string label, Func<string?> read, CancellationToken token) =>
        SecondaryAction(label, async () =>
        {
            if (read() is not { Length: > 0 } value) return;
            await Execute("clipboard_local_write", new() { ["text"] = value, ["sensitive"] = true }, token);
            Report(L("已复制", "Copied"));
        }, "copy");
}
