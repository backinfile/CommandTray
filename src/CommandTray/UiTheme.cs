using System.Drawing.Drawing2D;
using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray;

/// <summary>Process-lifetime typography and the shared Windows workspace palette.</summary>
internal static class UiTheme
{
    public static readonly Font Regular = new("Segoe UI", 10f);
    public static readonly Font Bold = new("Segoe UI", 10f, FontStyle.Bold);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font SmallBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI", 13f, FontStyle.Bold);
    public static readonly Font Header = new("Segoe UI", 18f, FontStyle.Bold);
    public static readonly Font Monospace = new("Consolas", 10f);
    public static readonly Color Accent = Color.FromArgb(36, 93, 218);
    public static readonly Color AccentHover = Color.FromArgb(30, 82, 199);
    public static readonly Color AccentPressed = Color.FromArgb(25, 74, 184);
    public static readonly Color Text = Color.FromArgb(32, 43, 61);
    public static readonly Color MutedText = Color.FromArgb(88, 103, 124);
    public static readonly Color Separator = Color.FromArgb(218, 225, 237);
    public static readonly Color RailBack = Color.FromArgb(246, 248, 252);
    public static readonly Color Surface = Color.White;
    public static readonly Color SelectedBack = Color.FromArgb(232, 239, 255);
    public static readonly Color PressedBack = Color.FromArgb(217, 229, 253);
    public static readonly Color Danger = Color.FromArgb(173, 43, 51);
    public static readonly Color DangerBack = Color.FromArgb(253, 237, 238);
    public static readonly Color ConsoleBack = Color.FromArgb(24, 33, 49);
    public static readonly Color ConsoleText = Color.FromArgb(221, 230, 242);
    public static readonly Color ConsoleErrorText = Color.FromArgb(255, 151, 144);
    public static readonly Color ConsoleWarningText = Color.FromArgb(246, 205, 115);

    public static Color StateColor(ServiceState state) => state switch
    {
        ServiceState.Running => Color.FromArgb(25, 115, 77),
        ServiceState.Faulted => Color.FromArgb(186, 49, 54),
        ServiceState.Starting or ServiceState.Stopping => Color.FromArgb(139, 93, 13),
        _ => MutedText
    };
    public static Color BadgeBack(ServiceState state) => state switch
    {
        ServiceState.Running => Color.FromArgb(229, 245, 236),
        ServiceState.Faulted => Color.FromArgb(253, 234, 235),
        ServiceState.Starting or ServiceState.Stopping => Color.FromArgb(255, 244, 219),
        _ => RailBack
    };
    public static string StateText(ServiceState state) => state switch
    {
        ServiceState.Running => Localizer.T("Running", "运行中"),
        ServiceState.Starting => Localizer.T("Starting", "启动中"),
        ServiceState.Stopping => Localizer.T("Stopping", "停止中"),
        ServiceState.Faulted => Localizer.T("Faulted", "故障"),
        _ => Localizer.T("Stopped", "已停止")
    };
    public static string StateGlyph(ServiceState state) => state switch
    {
        ServiceState.Running => "●", ServiceState.Starting or ServiceState.Stopping => "◐",
        ServiceState.Faulted => "!", _ => "○"
    };
    public static Button CreateButton(string english, string? chinese = null)
    {
        var button = new WorkspaceButton();
        if (chinese is null) button.Text = english;
        else Localizer.Bind(button, english, chinese);
        return button;
    }
    public static Button CreatePrimaryButton(string english, string? chinese = null)
    {
        var button = (WorkspaceButton)CreateButton(english, chinese);
        button.Tone = ButtonTone.Primary;
        return button;
    }
    public static Button CreateQuietButton(string english, string chinese, bool danger = false)
    {
        var button = (WorkspaceButton)CreateButton(english, chinese);
        button.Tone = danger ? ButtonTone.Danger : ButtonTone.Quiet;
        return button;
    }
    public static Button CreateCompactButton(string english, string? chinese = null)
    {
        var button = CreateButton(english, chinese);
        button.MinimumSize = new Size(64, 32);
        button.Padding = new Padding(10, 4, 10, 4);
        return button;
    }
    public static Label CreateLabel(string english, string chinese, Font? font = null)
    {
        var label = new Label { AutoSize = true, Font = font ?? Regular, ForeColor = Text };
        Localizer.Bind(label, english, chinese);
        return label;
    }
    public static Label CreateMutedLabel(string english, string? chinese = null)
    {
        var label = new Label { AutoSize = true, ForeColor = MutedText, Font = Small };
        if (chinese is null) label.Text = english;
        else Localizer.Bind(label, english, chinese);
        return label;
    }
    public static Panel CreateSeparator(DockStyle dock) => new()
    {
        Dock = dock, Height = dock is DockStyle.Top or DockStyle.Bottom ? 1 : 0,
        Width = dock is DockStyle.Left or DockStyle.Right ? 1 : 0,
        BackColor = Separator, Margin = Padding.Empty
    };
    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum ButtonTone { Secondary, Primary, Quiet, Danger }

internal class WorkspaceButton : Button
{
    private bool _hovered;
    private bool _mouseDown, _keyDown, _default, _selected;
    private ButtonTone _tone;
    [System.ComponentModel.DefaultValue(ButtonTone.Secondary)]
    public ButtonTone Tone { get => _tone; set { _tone = value; Invalidate(); } }
    [System.ComponentModel.DefaultValue(false)]
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }
    protected bool IsHovered => _hovered;
    protected bool IsPressed => _keyDown || (_mouseDown && _hovered);
    protected int LayoutDpi => FindForm()?.DeviceDpi ?? DeviceDpi;
    public WorkspaceButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true; MinimumSize = new Size(88, 40);
        Padding = new Padding(14, 8, 14, 8); Margin = new Padding(0, 0, 8, 0);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false; Cursor = Cursors.Hand; Font = UiTheme.Regular;
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        var focusSpace = (int)(4 * LayoutDpi / 96f);
        return new Size(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal + focusSpace),
            Math.Max(MinimumSize.Height, text.Height + Padding.Vertical + focusSpace));
    }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    // Painting must follow native button semantics: only the left button or
    // Space presses it, and dragging out or losing capture cancels that state.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _mouseDown = true; _hovered = ClientRectangle.Contains(e.Location); Invalidate(); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hovered = ClientRectangle.Contains(e.Location); Invalidate(); base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) { _mouseDown = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) _mouseDown = false;
        Invalidate(); base.OnMouseCaptureChanged(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyData == Keys.Space) { _keyDown = true; Invalidate(); }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { _keyDown = false; Invalidate(); }
        base.OnKeyUp(e);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { _keyDown = _mouseDown = false; base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        _keyDown = _mouseDown = _hovered = false; base.OnEnabledChanged(e); Invalidate();
    }
    protected override void OnChangeUICues(UICuesEventArgs e) { base.OnChangeUICues(e); Invalidate(); }
    public override void NotifyDefault(bool value) { _default = value; base.NotifyDefault(value); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var surface = Parent?.BackColor ?? UiTheme.Surface;
        e.Graphics.Clear(surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int S(int value) => Math.Max(1, (int)(value * LayoutDpi / 96f));
        var bounds = Rectangle.Inflate(ClientRectangle, -S(2), -S(2));
        bounds.Width--; bounds.Height--;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var shape = UiTheme.RoundedRectangle(bounds, S(6));
        var pressed = _keyDown || (_mouseDown && _hovered);
        var quiet = Tone is ButtonTone.Quiet or ButtonTone.Danger;
        var background = !Enabled ? (quiet ? surface : UiTheme.RailBack) : Tone == ButtonTone.Primary
            ? (pressed ? UiTheme.AccentPressed : _hovered ? UiTheme.AccentHover : UiTheme.Accent)
            : Tone == ButtonTone.Danger && (pressed || _hovered) ? (pressed ? Color.FromArgb(248, 217, 220) : UiTheme.DangerBack)
            : pressed ? UiTheme.PressedBack : _hovered || Selected ? UiTheme.SelectedBack : quiet ? surface : UiTheme.Surface;
        var foreground = !Enabled ? UiTheme.MutedText : Tone == ButtonTone.Primary ? Color.White
            : Tone == ButtonTone.Danger ? UiTheme.Danger : Selected ? UiTheme.Accent : UiTheme.Text;
        var borderColor = quiet || Tone == ButtonTone.Primary && Enabled ? background
            : _hovered || _default ? UiTheme.Accent : UiTheme.Separator;
        if (SystemInformation.HighContrast)
        {
            background = Enabled && (pressed || Selected) ? SystemColors.Highlight : SystemColors.Control;
            foreground = !Enabled ? SystemColors.GrayText : pressed || Selected ? SystemColors.HighlightText : SystemColors.ControlText;
            borderColor = foreground;
        }
        using var fill = new SolidBrush(background);
        e.Graphics.FillPath(fill, shape);
        using var border = new Pen(borderColor);
        e.Graphics.DrawPath(border, shape);
        var textBounds = ClientRectangle;
        if (pressed && Enabled) textBounds.Offset(0, S(1));
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, foreground,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
        if (Enabled && Focused && ShowFocusCues)
        {
            bounds.Inflate(S(2), S(2));
            using var focus = UiTheme.RoundedRectangle(bounds, S(8));
            using var focusPen = new Pen(SystemInformation.HighContrast ? SystemColors.ControlText : UiTheme.Accent, S(1));
            e.Graphics.DrawPath(focusPen, focus);
        }
    }
}

internal sealed class ToggleSwitch : CheckBox
{
    private bool _hovered;
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false; MinimumSize = Size = new Size(56, 40); Cursor = Cursors.Hand;
        AccessibleName = Localizer.T("Enable MCP", "启用 MCP");
    }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { _hovered = false; base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnChangeUICues(UICuesEventArgs e) { base.OnChangeUICues(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var dpi = FindForm()?.DeviceDpi ?? DeviceDpi;
        int S(int value) => Math.Max(1, (int)(value * dpi / 96f));
        // Keep the track proportional inside a 40px target, independent of
        // label height and of native child DPI messages arriving later.
        var width = Math.Min(S(44), Width - S(8));
        var height = Math.Min(S(24), Height - S(8));
        if (width <= 0 || height <= 0) return;
        var bounds = new Rectangle((Width - width) / 2, (Height - height) / 2, width, height);
        using var track = UiTheme.RoundedRectangle(bounds, bounds.Height / 2);
        var color = !Enabled ? UiTheme.Separator : Checked ? (_hovered ? UiTheme.AccentHover : UiTheme.Accent)
            : _hovered ? UiTheme.Text : UiTheme.MutedText;
        if (SystemInformation.HighContrast) color = Checked ? SystemColors.Highlight : SystemColors.ControlText;
        using var brush = new SolidBrush(color);
        e.Graphics.FillPath(brush, track);
        var inset = S(3);
        var diameter = bounds.Height - inset * 2;
        using var thumb = new SolidBrush(SystemInformation.HighContrast ? (Checked ? SystemColors.HighlightText : SystemColors.Control) : Color.White);
        e.Graphics.FillEllipse(thumb, Checked ? bounds.Right - diameter - inset : bounds.Left + inset,
            bounds.Top + inset, diameter, diameter);
        if (Focused && ShowFocusCues)
        {
            bounds.Inflate(S(3), S(3));
            using var focus = UiTheme.RoundedRectangle(bounds, bounds.Height / 2);
            using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.ControlText : UiTheme.Accent, S(1));
            e.Graphics.DrawPath(pen, focus);
        }
    }
}

internal sealed class StateBadge : Control
{
    private ServiceState _state;
    public StateBadge()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = UiTheme.Surface; AutoSize = true; TabStop = false; Font = UiTheme.SmallBold;
        Localizer.LanguageChanged += LanguageChanged; UpdateSize();
    }
    public void SetState(ServiceState state) { _state = state; UpdateSize(); Invalidate(); }
    private void LanguageChanged(object? sender, EventArgs e) { UpdateSize(); Invalidate(); }
    private void UpdateSize() { Size = GetPreferredSize(Size.Empty); AccessibleName = UiTheme.StateText(_state); }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(UiTheme.StateText(_state), Font);
        return new Size(text.Width + (int)(20 * DeviceDpi / 96f), text.Height + (int)(10 * DeviceDpi / 96f));
    }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateSize(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateSize(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), (int)(5 * DeviceDpi / 96f));
        using var brush = new SolidBrush(UiTheme.BadgeBack(_state));
        e.Graphics.FillPath(brush, path);
        TextRenderer.DrawText(e.Graphics, UiTheme.StateText(_state), Font, ClientRectangle,
            UiTheme.StateColor(_state), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Localizer.LanguageChanged -= LanguageChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// WinForms scales inherited fonts on monitor changes, but explicitly assigned
/// heading/mono fonts can stay at 96-DPI sizes. Keep a logical font baseline and
/// apply it after the native DPI layout completes, without cumulative scaling.
/// </summary>
internal sealed class DpiFontScope : IDisposable
{
    private readonly Form _form;
    private readonly Action _afterScale;
    private readonly Dictionary<Control, Font> _baselines = [];
    private readonly Dictionary<(string Name, float Size, FontStyle Style), Font> _scaled = [];
    private bool _queued, _disposed;

    public DpiFontScope(Form form, Action afterScale)
    {
        _form = form; _afterScale = afterScale;
        Capture(form);
        form.HandleCreated += HandleCreated;
        form.DpiChanged += DpiChanged;
        if (form.IsHandleCreated) QueueScale();
    }

    private void Capture(Control control)
    {
        _baselines[control] = control.Font;
        foreach (Control child in control.Controls) Capture(child);
    }
    private void HandleCreated(object? sender, EventArgs e) => QueueScale();
    private void DpiChanged(object? sender, DpiChangedEventArgs e) => QueueScale();
    private void QueueScale()
    {
        if (_queued || _disposed || !_form.IsHandleCreated) return;
        _queued = true;
        _form.BeginInvoke(() =>
        {
            _queued = false;
            if (_disposed || _form.IsDisposed) return;
            _form.SuspendLayout();
            try
            {
                var factor = _form.DeviceDpi / 96f;
                foreach (var (control, baseline) in _baselines)
                {
                    if (control.IsDisposed) continue;
                    var key = (baseline.Name, baseline.Size * factor, baseline.Style);
                    if (!_scaled.TryGetValue(key, out var font))
                    {
                        font = new Font(key.Item1, key.Item2, key.Item3);
                        _scaled[key] = font;
                    }
                    control.Font = font;
                }
                _afterScale();
            }
            finally { _form.ResumeLayout(true); }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _form.HandleCreated -= HandleCreated; _form.DpiChanged -= DpiChanged;
        foreach (var font in _scaled.Values) font.Dispose();
        _scaled.Clear();
    }
}
