using System.Drawing.Drawing2D;
using CommandTray.Models;

namespace CommandTray;

/// <summary>
/// Shared visual system for CommandTray windows: fonts, colors, button styles
/// and small reusable controls (card panel, state badge). Fonts are
/// process-lifetime singletons and are intentionally never disposed.
/// </summary>
internal static class UiTheme
{
    public static readonly Font Regular = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font SmallBold = new("Segoe UI", 8f, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI", 12f, FontStyle.Bold);
    public static readonly Font Header = new("Segoe UI", 15f, FontStyle.Bold);
    public static readonly Font Monospace = new("Consolas", 9.5f);

    public static readonly Color Accent = Color.FromArgb(0, 120, 215);
    public static readonly Color AccentPressed = Color.FromArgb(0, 102, 184);
    public static readonly Color MutedText = Color.FromArgb(96, 96, 96);
    public static readonly Color Separator = Color.FromArgb(225, 225, 225);
    public static readonly Color RailBack = Color.FromArgb(243, 243, 243);
    public static readonly Color CardBorder = Color.FromArgb(225, 225, 225);
    public static readonly Color SelectedBack = Color.FromArgb(229, 241, 251);
    public static readonly Color ConsoleBack = Color.FromArgb(24, 24, 24);
    public static readonly Color ConsoleText = Color.Gainsboro;
    public static readonly Color ConsoleErrorText = Color.FromArgb(255, 123, 114);
    public static readonly Color ConsoleWarningText = Color.FromArgb(227, 179, 65);

    public static Color StateColor(ServiceState state) => state switch
    {
        ServiceState.Running => Color.FromArgb(16, 124, 16),
        ServiceState.Faulted => Color.FromArgb(196, 43, 28),
        ServiceState.Starting or ServiceState.Stopping => Color.FromArgb(157, 93, 0),
        _ => Color.FromArgb(97, 97, 97)
    };

    public static Color BadgeBack(ServiceState state) => state switch
    {
        ServiceState.Running => Color.FromArgb(223, 246, 226),
        ServiceState.Faulted => Color.FromArgb(253, 231, 233),
        ServiceState.Starting or ServiceState.Stopping => Color.FromArgb(255, 244, 206),
        _ => Color.FromArgb(240, 240, 240)
    };

    public static string StateText(ServiceState state) => state switch
    {
        ServiceState.Running => "Running / 运行中",
        ServiceState.Starting => "Starting / 启动中",
        ServiceState.Stopping => "Stopping / 停止中",
        ServiceState.Faulted => "Faulted / 故障",
        _ => "Stopped / 已停止"
    };

    public static string StateGlyph(ServiceState state) => state switch
    {
        ServiceState.Running => "●",
        ServiceState.Starting or ServiceState.Stopping => "◐",
        ServiceState.Faulted => "!",
        _ => "○"
    };

    /// <summary>Standard push button: auto-sized with a consistent minimum size.</summary>
    public static Button CreateButton(string text)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(84, 30),
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(6, 1, 6, 1)
        };
    }

    /// <summary>Primary action button with the system accent color.</summary>
    public static Button CreatePrimaryButton(string text)
    {
        var button = CreateButton(text);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = AccentPressed;
        button.UseVisualStyleBackColor = false;
        return button;
    }

    /// <summary>Muted secondary label (hints, captions).</summary>
    public static Label CreateMutedLabel(string text)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = MutedText, Font = Small };
    }

    /// <summary>1px separator line.</summary>
    public static Panel CreateSeparator(DockStyle dock)
    {
        return new Panel
        {
            Dock = dock,
            Height = dock is DockStyle.Top or DockStyle.Bottom ? 1 : 0,
            Width = dock is DockStyle.Left or DockStyle.Right ? 1 : 0,
            BackColor = Separator,
            Margin = Padding.Empty
        };
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>White content card with a thin border, used to group settings sections.</summary>
internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        BackColor = SystemColors.Window;
        Padding = new Padding(16, 12, 16, 14);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiTheme.CardBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Colored pill showing a service state (Running / Faulted / ...).</summary>
internal sealed class StateBadge : Control
{
    private ServiceState _state = ServiceState.Stopped;

    public StateBadge()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = SystemColors.Window;
        AutoSize = true;
        TabStop = false;
    }

    public void SetState(ServiceState state)
    {
        if (_state == state) return;
        _state = state;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = UiTheme.StateText(_state);
        var size = TextRenderer.MeasureText(text, UiTheme.SmallBold);
        return new Size(size.Width + 18, size.Height + 8);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiTheme.RoundedRectangle(bounds, 4))
        using (var brush = new SolidBrush(UiTheme.BadgeBack(_state)))
            e.Graphics.FillPath(brush, path);
        e.Graphics.SmoothingMode = SmoothingMode.Default;
        TextRenderer.DrawText(e.Graphics, UiTheme.StateText(_state), UiTheme.SmallBold, ClientRectangle,
            UiTheme.StateColor(_state), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
