using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private string _textToolInput = "";
    private int _textToolOperation;

    private Task OpenTextToolsAsync(string text)
    {
        _textToolInput = text;
        _utilitySection = UtilitySection.Convert;
        Navigate("text-tools");
        return Task.CompletedTask;
    }

    private void RenderTextTools(CancellationToken token)
    {
        var operations = new (TextOperation Kind, string Label)[]
        {
            (TextOperation.FormatJson, L("格式化 JSON", "Format JSON")),
            (TextOperation.CompactJson, L("压缩 JSON", "Compact JSON")),
            (TextOperation.EncodeBase64, L("Base64 编码", "Base64 encode")),
            (TextOperation.DecodeBase64, L("Base64 解码", "Base64 decode")),
            (TextOperation.EncodeUrl, L("URL 组件编码", "URL component encode")),
            (TextOperation.DecodeUrl, L("URL 组件解码", "URL component decode")),
            (TextOperation.UniqueLines, L("按行去重", "Remove duplicate lines")),
            (TextOperation.Sha256, L("文本 SHA-256", "Text SHA-256"))
        };
        var input = Editor(L("输入", "Input"), false);
        input.Text = _textToolInput;
        input.MaxLength = TextTools.InputLimit;
        var output = Editor(L("结果", "Result"), true);
        var operation = new ComboBox
        {
            ItemsSource = operations.Select(candidate => candidate.Label),
            SelectedIndex = _textToolOperation
        };
        AutomationProperties.SetName(operation, L("文本操作", "Text operation"));
        var feedback = Muted("");
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);

        var paste = Action(L("读取剪贴板", "Read clipboard"), async () =>
        {
            var previous = input.Text;
            var text = TextOf(await Execute("clipboard_local_read", [], token));
            if (input.Text != previous) return;
            if (text.Length == 0) { Report(L("剪贴板没有文本", "The clipboard has no text")); return; }
            if (text.Length > TextTools.InputLimit) { Report(TooLong()); return; }
            input.Text = text;
            input.Focus();
        }, glyph: "clipboard");
        var copy = SecondaryAction(L("复制结果", "Copy result"), async () =>
        {
            if (output.Text is not { Length: > 0 } value) return;
            await Execute("clipboard_local_write", new() { ["text"] = value, ["sensitive"] = true }, token);
            Report(L("已复制", "Copied"));
        }, "copy");
        var reuse = Action(L("用作输入", "Use as input"), () =>
        {
            input.Text = output.Text;
            input.Focus();
            return Task.CompletedTask;
        });
        var convert = PrimaryAction(L("转换", "Convert"), async () =>
        {
            var text = input.Text ?? "";
            var selected = _textToolOperation;
            var mode = operations[selected].Kind;
            if (text.Length > TextTools.InputLimit) { SetResult("", TooLong()); return; }
            SetResult("", L("正在处理…", "Working…"), error: false);
            string value, message = "";
            try { value = await Task.Run(() => TextTools.ConvertText(mode, text), token); }
            catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
            {
                value = "";
                message = mode switch
                {
                    TextOperation.FormatJson or TextOperation.CompactJson => L("JSON 格式无效或嵌套过深。", "Invalid JSON or too many nesting levels."),
                    TextOperation.DecodeBase64 => L("请输入有效的 Base64 UTF-8 文本。", "Enter valid Base64-encoded UTF-8 text."),
                    TextOperation.DecodeUrl => L("请输入有效的 URL 编码。", "Enter valid URL encoding."),
                    _ => L("输入包含无效字符。", "The input contains invalid characters.")
                };
            }
            if (!token.IsCancellationRequested && text == (input.Text ?? "") && selected == _textToolOperation)
                SetResult(value, message, error: message.Length > 0);
        });

        _contents.Children.Add(PageActions(null, operation, convert));
        var inPanel = WorkbenchPanel(L("输入", "Input"), "text", input, paste);
        var outPanel = WorkbenchPanel(L("结果", "Result"), "text-tools", output, ActionBar(reuse, copy));
        _contents.Children.Add(_compact ? new StackPanel { Spacing = 12, Children = { inPanel, outPanel } } :
            WorkbenchSplit(inPanel, outPanel));
        _contents.Children.Add(feedback);
        input.TextChanged += (_, _) => { _textToolInput = input.Text ?? ""; SetResult("", ""); };
        operation.SelectionChanged += (_, _) =>
        {
            if (operation.SelectedIndex < 0) return;
            _textToolOperation = operation.SelectedIndex;
            SetResult("", "");
        };
        SetResult("", "");

        TextBox Editor(string label, bool readOnly)
        {
            var box = new TextBox
            {
                AcceptsReturn = true, IsReadOnly = readOnly, TextWrapping = TextWrapping.Wrap,
                MinHeight = _compact ? 150 : 285, MaxHeight = _compact ? 220 : 420, Margin = new Thickness(12),
                FontFamily = new FontFamily("monospace")
            };
            box.Classes.Add("entry");
            AutomationProperties.SetName(box, label);
            return box;
        }

        string TooLong() => L("输入最多 100 万字符。", "Input is limited to one million characters.");

        void SetResult(string value, string message, bool error = true)
        {
            output.Text = value;
            copy.IsEnabled = value.Length > 0;
            reuse.IsEnabled = value.Length is > 0 and <= TextTools.InputLimit;
            feedback.Text = message;
            SetStatusTone(feedback, error && message.Length > 0 ? "error" : "");
        }
    }
}
