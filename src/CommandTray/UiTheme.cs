using CommandTray.Models;

namespace CommandTray;

/// <summary>
/// Shared fonts, colors and control styling for CommandTray windows so that
/// SettingsForm and LogViewerForm stay visually consistent. All fonts are
/// process-lifetime singletons and are intentionally never disposed.
/// </summary>
internal static class UiTheme
{
    public static readonly Font Regular = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI", 12f, FontStyle.Bold);
    public static readonly Font Monospace = new("Consolas", 9.5f);

    public static readonly Color Accent = Color.FromArgb(0, 120, 215);
    public static readonly Color AccentPressed = Color.FromArgb(0, 102, 184);
    public static readonly Color MutedText = SystemColors.GrayText;
    public static readonly Color Separator = SystemColors.ControlDark;
    public static readonly Color ConsoleBack = Color.FromArgb(24, 24, 24);
    public static readonly Color ConsoleText = Color.Gainsboro;

    public static Color StateColor(ServiceState state) => state switch
    {
        ServiceState.Running => Color.FromArgb(16, 124, 16),
        ServiceState.Faulted => Color.FromArgb(196, 43, 28),
        ServiceState.Starting or ServiceState.Stopping => Color.FromArgb(157, 93, 0),
        _ => SystemColors.GrayText
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
        return new Label { Text = text, AutoSize = true, ForeColor = MutedText };
    }
}
