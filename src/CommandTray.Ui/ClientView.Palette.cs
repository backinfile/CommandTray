using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private IInputElement? _focusBeforePalette;

    private sealed record PaletteEntry(string Label, string Group, string Icon, Func<Task> Run);

    private Button PaletteButton()
    {
        var label = L("快速查找", "Quick find");
        var button = Action(label, OpenPaletteAsync, $"{label} (Ctrl+K)");
        var keycap = new Border { Child = new TextBlock { Text = "Ctrl K" } };
        keycap.Classes.Add("keycap");
        var caption = Row(TrayGlyph.Icon("search", 15));
        if (Bounds.Width >= 940) caption.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        caption.Children.Add(keycap);
        caption.Spacing = 7;
        button.Content = caption;
        button.Classes.Add("palette-toggle");
        return button;
    }

    private void HandleWindowKey(object? sender, KeyEventArgs args)
    {
        _lastInteraction = DateTimeOffset.UtcNow;
        if (args.Key == Key.K && args.KeyModifiers == KeyModifiers.Control && !_compact)
        {
            args.Handled = true;
            if (_paletteOverlay.IsVisible) ClosePalette();
            else _ = Act(OpenPaletteAsync);
        }
        else if (args.Key == Key.Escape && _paletteOverlay.IsVisible)
        {
            args.Handled = true;
            ClosePalette();
        }
        else if (args.Key == Key.B && args.KeyModifiers == KeyModifiers.Control && !_compact && !_paletteOverlay.IsVisible)
        {
            args.Handled = true;
            _ = Act(ToggleSidebarAsync);
        }
        else if (args.Key == Key.Escape && ReferenceEquals(_focusedPanel?.Canvas.Parent, _contents) && !_editing)
        {
            args.Handled = true;
            RestoreFocusedPanel();
        }
    }

    private async Task OpenPaletteAsync()
    {
        if (_paletteOverlay.IsVisible || _compact) return;
        _focusBeforePalette = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var label = L("快速查找", "Quick find");
        var query = new TextBox { PlaceholderText = L("搜索空间、组件和操作", "Find spaces, panels and actions") };
        query.Classes.Add("palette-query");
        Avalonia.Automation.AutomationProperties.SetName(query, label);
        var matches = new ListBox { MaxHeight = 320, SelectionMode = SelectionMode.Single };
        matches.Classes.Add("palette-results");
        var empty = Muted(L("没有匹配项", "No matches"));
        empty.Margin = new Thickness(9, 10);
        var results = new Panel();
        results.Children.Add(matches);
        results.Children.Add(empty);
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(TitleRow(Heading(label), IconButton("close", L("关闭", "Close"), () =>
        {
            ClosePalette();
            return Task.CompletedTask;
        })));
        body.Children.Add(query);
        body.Children.Add(results);
        var panel = new Border
        {
            Width = Math.Min(560, Bounds.Width - 48),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 68, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = body
        };
        panel.Classes.Add("palette");
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        _paletteOverlay.Child = panel;
        _paletteOverlay.IsVisible = true;
        query.Focus();

        var entries = NavigationPages().Select(page => new PaletteEntry(L(page.Zh, page.En), L("页面", "Page"),
            PageGlyph(page.Id), () =>
            {
                Navigate(page.Id);
                return Task.CompletedTask;
            })).ToList();
        entries.Add(new(_sidebarCollapsed ? L("展开侧栏", "Expand sidebar") : L("收起侧栏", "Collapse sidebar"),
            L("操作", "Action"), _sidebarCollapsed ? "rail-expand" : "rail-collapse", ToggleSidebarAsync));

        void UpdateMatches()
        {
            var search = query.Text?.Trim() ?? "";
            matches.Items.Clear();
            foreach (var entry in entries.Where(entry => entry.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                         entry.Group.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            {
                var group = Muted(entry.Group);
                group.Margin = new Thickness(14, 0, 1, 0);
                var item = new ListBoxItem { Content = TitleRow(IconLabel(entry.Icon, entry.Label), group), Tag = entry };
                item.Tapped += async (_, args) =>
                {
                    args.Handled = true;
                    await RunPaletteEntryAsync(entry);
                };
                matches.Items.Add(item);
            }
            matches.SelectedIndex = matches.ItemCount > 0 ? 0 : -1;
            empty.IsVisible = matches.ItemCount == 0;
        }

        query.TextChanged += (_, _) => UpdateMatches();
        panel.AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.Key is Key.Down or Key.Up && matches.ItemCount > 0)
            {
                args.Handled = true;
                matches.SelectedIndex = Math.Clamp(matches.SelectedIndex + (args.Key == Key.Down ? 1 : -1), 0, matches.ItemCount - 1);
                matches.ScrollIntoView(matches.SelectedIndex);
            }
            else if (args.Key == Key.Enter && matches.SelectedItem is ListBoxItem { Tag: PaletteEntry entry })
            {
                args.Handled = true;
                await RunPaletteEntryAsync(entry);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        UpdateMatches();

        var document = _displayedWorkspace ?? await Execute("workspace_read", []);
        if (!ReferenceEquals(_paletteOverlay.Child, panel)) return;
        foreach (var space in document.Array("spaces").OfType<JsonObject>())
        {
            var id = space.Text("id");
            var name = space.Text("name");
            entries.Add(new(name, L("空间", "Space"), "spaces", () => OpenPaletteSpaceAsync(id)));
            foreach (var component in space.Array("items").OfType<JsonObject>())
            {
                var itemId = component.Text("id");
                var kind = component.Text("kind");
                entries.Add(new(component.Text("title", kind), name, ComponentGlyph(kind),
                    () => OpenPaletteSpaceAsync(id, itemId)));
            }
        }
        if (_page == "workspace" && _spaceId is { } current)
        {
            if (Has(ClientFeatures.LocalFiles)) Add("files", L("添加目录", "Add folder"), L("目录", "Folder"));
            if (Has(ClientFeatures.Terminals)) Add("terminal", L("添加终端", "Add terminal"), L("终端", "Terminal"));
            Add("note", L("添加便签", "Add note"), L("便签", "Note"));
            Add("todo", L("添加待办", "Add checklist"), L("待办", "Checklist"));
            if (Has(ClientFeatures.Layout)) Add("board", L("添加画板", "Add board"), L("画板", "Board"));

            void Add(string kind, string title, string componentName) =>
                entries.Insert(0, new(title, L("操作", "Action"), ComponentGlyph(kind), () => ItemForm(current, kind, componentName)));
        }
        UpdateMatches();
    }

    private async Task RunPaletteEntryAsync(PaletteEntry entry)
    {
        if (!_paletteOverlay.IsVisible) return;
        ClosePalette();
        await Act(entry.Run);
    }

    private void ClosePalette(bool restoreFocus = true)
    {
        if (!_paletteOverlay.IsVisible) return;
        _paletteOverlay.IsVisible = false;
        _paletteOverlay.Child = null;
        if (restoreFocus) _focusBeforePalette?.Focus();
        _focusBeforePalette = null;
    }

    private async Task OpenPaletteSpaceAsync(string spaceId, string? componentId = null)
    {
        await Execute("space_activate", new() { ["spaceId"] = spaceId });
        Navigate("workspace", spaceId, componentId);
    }
}
