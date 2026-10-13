using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommandTray.Services;

namespace CommandTray;

public sealed class LogViewerForm : Form
{
    private readonly string _path;
    private readonly RichTextBox _text = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = UiTheme.ConsoleBack,
        ForeColor = UiTheme.ConsoleText,
        Font = UiTheme.Monospace,
        BorderStyle = BorderStyle.None,
        WordWrap = false
    };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly CheckBox _follow = new() { AutoSize = true, Checked = true };
    private readonly CheckBox _wrap = new() { AutoSize = true };
    private readonly TextBox _search = new() { Width = 240 };
    private readonly Label _status = UiTheme.CreateMutedLabel("");
    private readonly Label _file = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Font = UiTheme.Small, ForeColor = UiTheme.MutedText };
    private readonly ToolTip _help = new();
    private readonly DpiFontScope _dpiFonts;
    private long _position;
    private bool _placeholderShown;

    public LogViewerForm(string serviceName, string path)
    {
        _path = path;
        Localizer.Bind(this, $"CommandTray · {serviceName} · Logs", $"CommandTray · {serviceName} · 日志");
        Font = UiTheme.Regular;
        ForeColor = UiTheme.Text;
        BackColor = UiTheme.Surface;
        Size = new Size(1000, 650);
        MinimumSize = new Size(760, 440);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2,
            Padding = new Padding(24, 20, 24, 16), Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label { Text = serviceName, Font = UiTheme.Header, Dock = DockStyle.Fill, AutoEllipsis = true, Height = 40, Margin = Padding.Empty };
        var folder = UiTheme.CreateButton("Open log folder", "打开日志目录");
        folder.Margin = Padding.Empty;
        folder.Click += (_, _) =>
        {
            try
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowStatusError(ex.Message); }
        };
        _file.Text = path; _file.Height = 25; _file.Margin = new Padding(0, 4, 0, 0);
        _help.SetToolTip(_file, path);
        header.Controls.Add(title, 0, 0); header.Controls.Add(folder, 1, 0);
        header.Controls.Add(_file, 0, 1); header.SetColumnSpan(_file, 2);
        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.ConsoleBack, Padding = new Padding(16, 12, 16, 12), Margin = Padding.Empty };
        body.Controls.Add(_text);
        Localizer.Bind(_wrap, "Word wrap", "自动换行");
        Localizer.Bind(_follow, "Follow output", "跟随输出");
        _wrap.Margin = _follow.Margin = new Padding(0, 9, 18, 0);
        _wrap.CheckedChanged += (_, _) => _text.WordWrap = _wrap.Checked;
        _follow.CheckedChanged += (_, _) =>
        {
            if (_follow.Checked) { _text.SelectionStart = _text.TextLength; _text.ScrollToCaret(); }
            UpdateStatus();
        };
        var clearButton = UiTheme.CreateButton("Clear view", "清空视图");
        clearButton.Click += (_, _) => { _text.Clear(); _placeholderShown = false; ShowPlaceholderIfEmpty(); };
        var copy = UiTheme.CreateButton("Copy logs", "复制日志");
        copy.Click += (_, _) =>
        {
            try
            {
                if (_placeholderShown) return;
                Clipboard.SetText(_text.SelectionLength > 0 ? _text.SelectedText : _text.Text);
                _status.Text = Localizer.T("Copied to clipboard", "已复制到剪贴板");
            }
            catch (Exception ex) { ShowStatusError(ex.Message); }
        };
        var next = UiTheme.CreateCompactButton("Next match", "下一个");
        next.Click += (_, _) => FindNext();
        _search.Margin = new Padding(0, 5, 10, 0);
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true,
            Padding = new Padding(24, 8, 24, 12), Margin = Padding.Empty, BackColor = UiTheme.RailBack
        };
        toolbar.Controls.AddRange([_search, next, _follow, _wrap, copy, clearButton]);
        _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(24, 0, 24, 0); _status.Margin = Padding.Empty;
        root.Controls.Add(header, 0, 0); root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(body, 0, 2); root.Controls.Add(_status, 0, 3); Controls.Add(root);
        _dpiFonts = new DpiFontScope(this, () => PerformLayout());
        Localizer.LanguageChanged += LanguageChanged;
        UpdateLanguage();

        _timer.Tick += (_, _) => ReadNewContent();
        Shown += (_, _) => { ReadNewContent(); ShowPlaceholderIfEmpty(); _timer.Start(); };
        _text.MouseDown += (_, _) => _follow.Checked = false;
    }

    private void ShowPlaceholderIfEmpty()
    {
        if (_text.TextLength > 0) return;
        _text.SelectionColor = Color.FromArgb(166, 183, 205);
        _text.AppendText(Localizer.T("No log output yet.", "暂无日志输出。"));
        _text.SelectionColor = UiTheme.ConsoleText;
        _placeholderShown = true;
        UpdateStatus();
    }

    private void ReadNewContent()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _position) { _position = 0; _text.Clear(); _placeholderShown = false; }
            stream.Position = _position;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var appended = reader.ReadToEnd();
            _position = stream.Position;
            if (appended.Length == 0) return;
            if (_placeholderShown) { _text.Clear(); _placeholderShown = false; }
            // Preserve the viewport while paused; restoring only selection
            // still let RichTextBox jump when new text arrived.
            var scroll = new NativePoint();
            if (!_follow.Checked) SendMessage(_text.Handle, 0x04DD, 0, ref scroll);
            AppendColorizedText(appended);
            UpdateStatus();
            if (!_follow.Checked) { SendMessage(_text.Handle, 0x04DE, 0, ref scroll); return; }
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
        }
        catch (IOException) { /* next timer tick retries during rotation */ }
        catch (UnauthorizedAccessException ex) { ShowStatusError(ex.Message); }
    }

    private void AppendColorizedText(string appended)
    {
        var previousSelectionStart = _text.SelectionStart;
        var previousSelectionLength = _text.SelectionLength;
        var oldLength = _text.TextLength;

        _text.SelectionStart = oldLength;
        _text.SelectionLength = 0;
        _text.SelectionColor = UiTheme.ConsoleText;
        _text.AppendText(appended);

        // Include the preceding character so a line split across two reads is
        // reclassified once its remaining text arrives.
        var firstAffectedCharacter = Math.Max(0, oldLength - 1);
        var firstAffectedLine = _text.GetLineFromCharIndex(firstAffectedCharacter);
        var lastAffectedLine = _text.GetLineFromCharIndex(_text.TextLength - 1);
        var totalLength = _text.TextLength;

        for (var lineNumber = firstAffectedLine; lineNumber <= lastAffectedLine; lineNumber++)
        {
            var lineStart = _text.GetFirstCharIndexFromLine(lineNumber);
            var nextLineStart = _text.GetFirstCharIndexFromLine(lineNumber + 1);
            var lineLength = nextLineStart >= 0 ? nextLineStart - lineStart : totalLength - lineStart;

            _text.Select(lineStart, lineLength);
            var severity = LogLineClassifier.Classify(_text.SelectedText);
            if (lineNumber != firstAffectedLine && severity == LogLineSeverity.Normal) continue;

            _text.SelectionColor = severity switch
            {
                LogLineSeverity.Error => UiTheme.ConsoleErrorText,
                LogLineSeverity.Warning => UiTheme.ConsoleWarningText,
                _ => UiTheme.ConsoleText
            };
        }

        // Reset the insertion color before restoring a user's selection.
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.SelectionColor = UiTheme.ConsoleText;

        if (!_follow.Checked)
        {
            _text.Select(
                Math.Min(previousSelectionStart, _text.TextLength),
                Math.Min(previousSelectionLength, Math.Max(0, _text.TextLength - previousSelectionStart)));
        }
    }

    private void FindNext()
    {
        if (_search.Text.Length == 0 || _placeholderShown) return;
        var start = _text.SelectionStart + _text.SelectionLength;
        var index = _text.Find(_search.Text, Math.Min(start, _text.TextLength), RichTextBoxFinds.None);
        if (index < 0) index = _text.Find(_search.Text, 0, RichTextBoxFinds.None);
        if (index < 0) { _status.Text = Localizer.T("No matches found", "没有找到匹配内容"); return; }
        _follow.Checked = false;
        _text.Select(index, _search.Text.Length); _text.ScrollToCaret();
        _status.Text = Localizer.T("Match selected · press Enter to find next", "已定位匹配内容 · 按 Enter 查找下一个");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); _search.SelectAll(); return true; }
        if (keyData == Keys.Enter && _search.Focused) { FindNext(); return true; }
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void UpdateStatus()
    {
        var lines = _placeholderShown || _text.TextLength == 0 ? 0 : _text.GetLineFromCharIndex(_text.TextLength - 1) + 1;
        _status.ForeColor = UiTheme.MutedText;
        _status.Text = _follow.Checked
            ? Localizer.T($"Live · {lines:N0} lines · refreshes automatically", $"实时跟随 · {lines:N0} 行 · 自动刷新")
            : Localizer.T($"Paused scrolling · {lines:N0} lines · new output is still received", $"已暂停滚动 · {lines:N0} 行 · 仍在接收新输出");
    }

    private void ShowStatusError(string message)
    {
        _status.Text = Localizer.T($"Could not complete action: {message}", $"操作未完成：{message}");
        _status.ForeColor = UiTheme.StateColor(Models.ServiceState.Faulted);
    }

    private void LanguageChanged(object? sender, EventArgs e) => UpdateLanguage();
    private void UpdateLanguage()
    {
        _search.PlaceholderText = Localizer.T("Search logs · Ctrl+F", "搜索日志 · Ctrl+F");
        _search.AccessibleName = Localizer.T("Search logs", "搜索日志");
        _text.AccessibleName = Localizer.T("Service log output", "服务日志输出");
        if (_placeholderShown) { _text.Clear(); ShowPlaceholderIfEmpty(); }
        UpdateStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _dpiFonts.Dispose(); _help.Dispose(); Localizer.LanguageChanged -= LanguageChanged; }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, ref NativePoint point);
}
