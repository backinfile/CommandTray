using System.Text;
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
    private Func<bool> _follow = static () => true;
    private long _position;
    private bool _placeholderShown;

    public LogViewerForm(string serviceName, string path)
    {
        _path = path;
        Text = $"CommandTray · {serviceName} · Log / 日志";
        Font = UiTheme.Regular;
        Size = new Size(920, 580);
        MinimumSize = new Size(520, 320);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        var title = new Label { Text = serviceName, Font = UiTheme.Bold, AutoSize = true };
        var file = UiTheme.CreateMutedLabel(path);
        file.Margin = new Padding(0, 2, 0, 0);
        var headerText = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true
        };
        headerText.Controls.Add(title);
        headerText.Controls.Add(file);
        var header = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 8, 12, 8) };
        header.Controls.Add(headerText);
        var headerSeparator = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = UiTheme.Separator };
        header.Controls.Add(headerSeparator);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.ConsoleBack, Padding = new Padding(8) };
        body.Controls.Add(_text);

        var wrapToggle = new CheckBox { Text = Localizer.T("Word wrap", "自动换行"), AutoSize = true, Margin = new Padding(0, 5, 16, 0) };
        wrapToggle.CheckedChanged += (_, _) => _text.WordWrap = wrapToggle.Checked;
        var followToggle = new CheckBox { Text = Localizer.T("Auto-scroll", "自动滚动"), AutoSize = true, Checked = true, Margin = new Padding(0, 5, 16, 0) };
        var clearButton = UiTheme.CreateButton(Localizer.T("Clear view", "清空视图"));
        clearButton.Margin = Padding.Empty;
        clearButton.Click += (_, _) => { _text.Clear(); _placeholderShown = false; ShowPlaceholderIfEmpty(); };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(12, 4, 12, 6) };
        toolbar.Controls.AddRange([followToggle, wrapToggle, clearButton]);
        var toolbarWrap = new Panel { Dock = DockStyle.Top, AutoSize = true };
        toolbarWrap.Controls.Add(toolbar);
        toolbarWrap.Controls.Add(UiTheme.CreateSeparator(DockStyle.Bottom));
        _follow = () => followToggle.Checked;

        var status = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 5, 12, 5) };
        var statusSeparator = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Separator };
        status.Controls.Add(UiTheme.CreateMutedLabel(Localizer.T("Live view · refreshes automatically", "实时视图 · 自动刷新")));
        status.Controls.Add(statusSeparator);

        Controls.Add(body);
        Controls.Add(toolbarWrap);
        Controls.Add(status);
        Controls.Add(header);

        _timer.Tick += (_, _) => ReadNewContent();
        Shown += (_, _) => { ReadNewContent(); ShowPlaceholderIfEmpty(); _timer.Start(); };
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void ShowPlaceholderIfEmpty()
    {
        if (_text.TextLength > 0) return;
        _text.SelectionColor = Color.Gray;
        _text.AppendText(Localizer.T("No log output yet.", "暂无日志输出。"));
        _text.SelectionColor = UiTheme.ConsoleText;
        _placeholderShown = true;
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
            _text.AppendText(appended);
            if (!_follow()) return;
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
        }
        catch (IOException) { /* next timer tick retries during rotation */ }
    }
}
