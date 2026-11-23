using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private const int RailWidth = 56;
    private const int DefaultSidebarWidth = 176;
    private const int MinimumSidebarWidth = 132;
    private const int MaximumSidebarWidth = 252;
    private bool _sidebarCollapsed;
    private int _sidebarWidth = DefaultSidebarWidth;
    private Button? _sidebarToggle;

    private void ApplySidebarGeometry()
    {
        var column = _shell.ColumnDefinitions[0];
        bool resize = !_compact && !_sidebarCollapsed;
        int width = _compact ? 0 : _sidebarCollapsed ? RailWidth : _sidebarWidth;
        column.MinWidth = resize ? MinimumSidebarWidth : width;
        column.MaxWidth = resize ? MaximumSidebarWidth : width;
        column.Width = new GridLength(width);
        _navSplitter.IsVisible = resize;
        _navBorder.Classes.Set("rail", !_compact && _sidebarCollapsed);
        _navBorder.Padding = _compact ? new Thickness(5, 3)
            : _sidebarCollapsed ? new Thickness(6, 10) : new Thickness(8, 10, 9, 10);
    }

    private void RenderNavigation()
    {
        var pages = NavigationPages();
        _navigation.Children.Clear();
        _navHeader.IsVisible = !_compact;
        _navFooter.IsVisible = !_compact;
        if (_compact)
        {
            if (OperatingSystem.IsAndroid() && _english)
            {
                // Give the longer Clipboard label room at large system font sizes.
                var strip = new Grid { ColumnDefinitions = new ColumnDefinitions("*,0.75*,1.25*,*") };
                foreach (var page in pages)
                {
                    var button = NavigationButton(page.Id, page.En);
                    Grid.SetColumn(button, strip.Children.Count);
                    strip.Children.Add(button);
                }
                _navigation.Children.Add(strip);
            }
            else
            {
                var strip = new UniformGrid { Columns = pages.Count };
                foreach (var page in pages) strip.Children.Add(NavigationButton(page.Id, L(page.Zh, page.En)));
                _navigation.Children.Add(strip);
            }
            _navHeader.Content = null;
            _navFooter.Child = null;
            return;
        }

        var label = _sidebarCollapsed ? L("展开侧栏", "Expand sidebar") : L("收起侧栏", "Collapse sidebar");
        _sidebarToggle = IconButton(_sidebarCollapsed ? "rail-expand" : "rail-collapse", label, ToggleSidebarAsync);
        ToolTip.SetTip(_sidebarToggle, $"{label} (Ctrl+B)");
        _sidebarToggle.Width = _sidebarCollapsed ? 40 : 26;
        _sidebarToggle.HorizontalAlignment = HorizontalAlignment.Center;
        var heading = new Border { Padding = new Thickness(0, 0, 0, 10) };
        if (_sidebarCollapsed) heading.Child = _sidebarToggle;
        else
        {
            var markIcon = TrayGlyph.Icon("spaces", 15);
            markIcon.Classes.Add("mark");
            var mark = new Border { Width = 22, Height = 22, Child = markIcon };
            mark.Classes.Add("mark");
            var name = Heading("CommandTray");
            name.Classes.Add("brand");
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(5, 0, 0, 0);
            var brand = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            brand.Children.Add(mark);
            Grid.SetColumn(name, 1);
            brand.Children.Add(name);
            heading.Child = TitleRow(brand, _sidebarToggle);
        }
        _navHeader.Content = heading;
        AutomationProperties.SetName(_navSplitter, L("调整侧栏宽度", "Resize sidebar"));
        foreach (var page in pages.Where(page => page.Id != "settings"))
            _navigation.Children.Add(NavigationButton(page.Id, L(page.Zh, page.En)));
        _navFooter.Child = NavigationButton("settings", L("设置", "Settings"));
    }

    private async Task ToggleSidebarAsync()
    {
        if (_compact) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Button;
        bool restore = focused == _sidebarToggle || focused?.Classes.Contains("navigation") == true;
        _sidebarCollapsed = !_sidebarCollapsed;
        ApplySidebarGeometry();
        RenderNavigation();
        if (restore) _sidebarToggle?.Focus();
        await Execute("settings_save", new() { ["sidebarCollapsed"] = _sidebarCollapsed });
    }

    private async Task SaveSidebarWidthAsync()
    {
        if (_compact || _sidebarCollapsed) return;
        int width = Math.Clamp((int)Math.Round(_shell.ColumnDefinitions[0].Width.Value), MinimumSidebarWidth, MaximumSidebarWidth);
        if (width == _sidebarWidth) return;
        _sidebarWidth = width;
        await Execute("settings_save", new() { ["sidebarWidth"] = width });
    }
}
