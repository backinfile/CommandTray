using System.Drawing.Drawing2D;
using CommandTray.Services;

namespace CommandTray;

/// <summary>
/// A flat tab strip with persistent, scrollable pages. Owner-drawing a native
/// TabControl leaves its 3-D frame visible, so the strip and page host are separate.
/// </summary>
internal sealed class WorkspaceTabs : UserControl
{
    private readonly TabStrip _strip = new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false, Margin = Padding.Empty, AccessibleRole = AccessibleRole.PageTabList
    };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, TabStop = false };
    private readonly List<(TabButton Button, Panel Page)> _pages = [];
    private int _selectedIndex = -1;

    public WorkspaceTabs()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Surface; Font = UiTheme.Regular; TabStop = false;
        _strip.TabIndex = 0; _host.TabIndex = 1;
        Controls.Add(_host); Controls.Add(_strip);
        Localizer.Bind(_strip, "Configuration tabs", "配置页签");
        _strip.TextChanged += (_, _) => _strip.AccessibleName = _strip.Text;
        _strip.AccessibleName = _strip.Text;
    }

    [System.ComponentModel.DefaultValue(-1)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0 || value >= _pages.Count) throw new ArgumentOutOfRangeException(nameof(value));
            if (_selectedIndex == value) return;
            var transferFocus = _selectedIndex >= 0 &&
                (_pages[_selectedIndex].Page.ContainsFocus || _pages[_selectedIndex].Button.Focused);
            _selectedIndex = value;
            SuspendLayout();
            for (var i = 0; i < _pages.Count; i++)
            {
                var (button, page) = _pages[i];
                button.Selected = button.TabStop = i == value;
                page.Visible = i == value;
                button.NotifySelection();
            }
            _pages[value].Page.BringToFront();
            ResumeLayout(true);
            if (transferFocus) _pages[value].Button.Focus();
        }
    }

    public Panel AddPage(string english, string chinese)
    {
        var index = _pages.Count;
        var button = new TabButton(this, index) { TabIndex = index, TabStop = false };
        Localizer.Bind(button, english, chinese);
        var page = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, Visible = false,
            BackColor = UiTheme.Surface, Padding = new Padding(0, 24, 16, 16),
            AccessibleRole = AccessibleRole.PropertyPage, TabIndex = index, TabStop = false
        };
        Localizer.Bind(page, english, chinese);
        page.TextChanged += (_, _) => page.AccessibleName = page.Text;
        page.AccessibleName = page.Text;
        button.Click += (_, _) => SelectedIndex = index;
        _pages.Add((button, page)); _host.Controls.Add(page); _strip.Controls.Add(button);
        if (_selectedIndex == -1) SelectedIndex = 0;
        return page;
    }

    public void Cycle(int direction)
    {
        if (_pages.Count == 0) return;
        SelectAndFocus((_selectedIndex + direction + _pages.Count) % _pages.Count);
    }

    private void SelectAndFocus(int index)
    {
        SelectedIndex = index; _pages[index].Button.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab))
        {
            Cycle(keyData.HasFlag(Keys.Shift) ? -1 : 1); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private sealed class TabStrip : FlowLayoutPanel
    {
        public TabStrip() => SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (SystemInformation.HighContrast) e.Graphics.Clear(SystemColors.Control);
            using var line = new Pen(SystemInformation.HighContrast ? SystemColors.ControlText : UiTheme.Separator);
            e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    private sealed class TabButton : WorkspaceButton
    {
        private readonly WorkspaceTabs _owner;
        private readonly int _index;
        public TabButton(WorkspaceTabs owner, int index)
        {
            _owner = owner; _index = index;
            Margin = Padding.Empty; Padding = new Padding(20, 0, 20, 0);
            AccessibleRole = AccessibleRole.PageTab;
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            // Reserve the bold text width on every tab so selection never moves
            // neighboring targets, including after a live language switch.
            using var font = new Font(Font, FontStyle.Bold);
            var text = TextRenderer.MeasureText(Text, font, Size.Empty, TextFormatFlags.SingleLine);
            return new Size(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal),
                Math.Max(MinimumSize.Height, Math.Max(text.Height + (int)(24 * LayoutDpi / 96f), (int)(48 * LayoutDpi / 96f))));
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End)
            {
                var index = keyData switch
                {
                    Keys.Home => 0, Keys.End => _owner._pages.Count - 1,
                    Keys.Left => (_index - 1 + _owner._pages.Count) % _owner._pages.Count,
                    _ => (_index + 1) % _owner._pages.Count
                };
                _owner.SelectAndFocus(index); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            int S(int value) => Math.Max(1, (int)(value * LayoutDpi / 96f));
            e.Graphics.Clear(SystemInformation.HighContrast ? SystemColors.Control : Parent?.BackColor ?? UiTheme.Surface);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var foreground = Selected ? UiTheme.Accent : UiTheme.MutedText;
            if (SystemInformation.HighContrast) foreground = SystemColors.ControlText;
            var highlight = new Rectangle(S(4), S(4), Width - S(8), Height - S(12));
            if (IsHovered || IsPressed)
            {
                using var path = UiTheme.RoundedRectangle(highlight, S(6));
                using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Control : IsPressed ? UiTheme.PressedBack : UiTheme.RailBack);
                e.Graphics.FillPath(fill, path);
            }
            using var font = new Font(Font, Selected ? FontStyle.Bold : FontStyle.Regular);
            var textBounds = ClientRectangle; textBounds.Height -= S(4);
            TextRenderer.DrawText(e.Graphics, Text, font, textBounds, foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            using var line = new Pen(SystemInformation.HighContrast ? SystemColors.ControlText : UiTheme.Separator);
            e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
            if (Selected)
            {
                using var accent = new SolidBrush(foreground);
                e.Graphics.FillRectangle(accent, S(16), Height - S(3), Width - S(32), S(3));
            }
            if (Focused && ShowFocusCues)
            {
                using var path = UiTheme.RoundedRectangle(highlight, S(6));
                using var pen = new Pen(foreground);
                e.Graphics.DrawPath(pen, path);
            }
        }
        public void NotifySelection()
        {
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
        protected override AccessibleObject CreateAccessibilityInstance() => new TabAccessibility(this);
        private sealed class TabAccessibility(TabButton owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleRole Role => AccessibleRole.PageTab;
            public override AccessibleStates State => base.State | AccessibleStates.Selectable |
                (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);
            public override string? DefaultAction => Localizer.T("Select", "选择");
            public override void DoDefaultAction() => owner.PerformClick();
            public override void Select(AccessibleSelection flags)
            {
                if (flags.HasFlag(AccessibleSelection.TakeSelection)) owner.PerformClick();
                if (flags.HasFlag(AccessibleSelection.TakeFocus)) owner.Focus();
            }
        }
    }
}
