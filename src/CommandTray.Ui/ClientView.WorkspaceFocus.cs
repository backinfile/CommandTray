using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private sealed record FocusedPanel(Canvas Canvas, Border Tile, Border Grip, Button Toggle, Rect Original);
    private FocusedPanel? _focusedPanel;
    private string? _focusedPanelId;
    private Vector _focusReturnOffset;

    private void AddFocusButton(string itemId, Canvas canvas, Border tile, Border grip, StackPanel actions)
    {
        var toggle = new Button();
        toggle.Classes.Add("action");
        toggle.Classes.Add("icon-only");
        toggle.Classes.Add("panel-focus");
        SetFocusButton(toggle, focused: false);
        toggle.Click += (_, _) =>
        {
            if (_focusedPanel is not null)
            {
                RestoreFocusedPanel();
                return;
            }
            _focusReturnOffset = _viewport.Offset;
            FocusPanel(itemId, canvas, tile, grip, toggle);
        };
        actions.Children.Insert(0, toggle);
        if (_focusedPanelId == itemId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_focusedPanelId == itemId && ReferenceEquals(canvas.Parent, _contents))
                    FocusPanel(itemId, canvas, tile, grip, toggle);
            }, DispatcherPriority.Loaded);
        }
    }

    private void SetFocusButton(Button button, bool focused)
    {
        var label = focused ? L("还原组件", "Restore panel") : L("最大化组件", "Maximize panel");
        button.Content = TrayGlyph.Icon(focused ? "restore" : "maximize", 15);
        ToolTip.SetTip(button, focused ? $"{label} (Esc)" : label);
        AutomationProperties.SetName(button, label);
    }

    private void FocusPanel(string itemId, Canvas canvas, Border tile, Border grip, Button toggle)
    {
        _focusedPanelId = itemId;
        _focusedPanel = new(canvas, tile, grip, toggle,
            new Rect(Canvas.GetLeft(tile), Canvas.GetTop(tile), tile.Width, tile.Height));
        foreach (var sibling in canvas.Children) sibling.IsVisible = ReferenceEquals(sibling, tile);
        grip.IsVisible = false;
        tile.Classes.Add("focused-panel");
        SetFocusButton(toggle, focused: true);
        Canvas.SetLeft(tile, 0);
        Canvas.SetTop(tile, 0);
        ResizeFocusedPanel();
        _viewport.Offset = default;
    }

    private void ResizeFocusedPanel()
    {
        if (_focusedPanel is not { } focus || !ReferenceEquals(focus.Canvas.Parent, _contents)) return;
        var area = _viewport.Bounds;
        var margin = _contents.Margin;
        focus.Tile.Width = Math.Max(220, area.Width - margin.Left - margin.Right - 2);
        focus.Tile.Height = Math.Max(150, area.Height - margin.Top - margin.Bottom - focus.Canvas.Bounds.Y - 2);
        MeasureCanvas(focus.Canvas);
    }

    private void RestoreFocusedPanel()
    {
        _focusedPanelId = null;
        if (_focusedPanel is not { } focus) return;
        _focusedPanel = null;
        focus.Tile.Width = focus.Original.Width;
        focus.Tile.Height = focus.Original.Height;
        Canvas.SetLeft(focus.Tile, focus.Original.X);
        Canvas.SetTop(focus.Tile, focus.Original.Y);
        focus.Grip.IsVisible = true;
        focus.Tile.Classes.Remove("focused-panel");
        SetFocusButton(focus.Toggle, focused: false);
        foreach (var sibling in focus.Canvas.Children) sibling.IsVisible = true;
        MeasureCanvas(focus.Canvas);
        var offset = _focusReturnOffset;
        Dispatcher.UIThread.Post(() =>
        {
            if (_focusedPanel is null && ReferenceEquals(focus.Canvas.Parent, _contents)) _viewport.Offset = offset;
        }, DispatcherPriority.Loaded);
    }
}
