using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private const int RailWidth = 64;
    private const int DefaultSidebarWidth = 244;
    private const int MinimumSidebarWidth = 168;
    private const int MaximumSidebarWidth = 336;
    private bool _sidebarCollapsed;
    private int _sidebarWidth = DefaultSidebarWidth;
    private Button? _sidebarToggle;
    private StackPanel? _sidebarSpaceList;
    private TextBlock? _sidebarRelayStatus;
    private (string Id, string Name)[] _sidebarSpaces = [];
    private bool? _sidebarPaired;
    private bool DesktopWorkbench => Has(ClientFeatures.Layout);

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
        _navigation.Children.Clear();
        _sidebarSpaceList = null;
        _sidebarRelayStatus = null;
        _navHeader.IsVisible = !_compact;
        _navFooter.IsVisible = !_compact;
        if (_compact)
        {
            var strip = new UniformGrid { Columns = 3 };
            strip.Children.Add(NavigationButton("workspace", L("空间", "Spaces"), ChooseSpaceAsync));
            strip.Children.Add(NavigationButton("tools", L("工具", "Tools")));
            strip.Children.Add(NavigationButton("settings", L("设置", "Settings")));
            _navigation.Children.Add(strip);
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
            var mark = new Border { Width = DesktopWorkbench ? 32 : 22, Height = DesktopWorkbench ? 32 : 22, Child = markIcon };
            mark.Classes.Add("mark");
            var name = Heading(DesktopWorkbench ? "CommandTray Suite" : "CommandTray");
            name.Classes.Add("brand");
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(9, 0, 0, 0);
            var brand = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            brand.Children.Add(mark);
            Grid.SetColumn(name, 1);
            brand.Children.Add(name);
            heading.Child = TitleRow(brand, _sidebarToggle);
        }
        _navHeader.Content = heading;
        AutomationProperties.SetName(_navSplitter, L("调整侧栏宽度", "Resize sidebar"));
        var settings = NavigationButton("settings", L("设置", "Settings"));
        if (!_sidebarCollapsed)
        {
            _sidebarSpaceList = new StackPanel { Spacing = 4 };
            var group = new Border
            {
                Margin = new Thickness(0, 3, 0, 0), Child = _sidebarSpaceList
            };
            group.Classes.Add("nav-group");
            _navigation.Children.Add(group);
            _sidebarRelayStatus = Muted("");
            _sidebarRelayStatus.Classes.Add("nav-status");
            _navFooter.Child = new StackPanel { Spacing = 4, Children = { _sidebarRelayStatus, settings } };
            UpdateSidebarSpaces();
        }
        else
        {
            _navigation.Children.Add(NavigationButton("workspace", L("空间", "Spaces"), ChooseSpaceAsync));
            _navFooter.Child = settings;
        }
        var overview = NavigationButton("tools", L("工具总览", "All tools"));
        overview.Margin = new Thickness(0, 6, 0, 0);
        _navigation.Children.Add(overview);
        foreach (var group in ToolGroups())
        {
            if (!_sidebarCollapsed)
            {
                var groupLabel = Muted(group.Title);
                groupLabel.Classes.Add("nav-status");
                _navigation.Children.Add(groupLabel);
            }
            foreach (var page in group.Pages)
                _navigation.Children.Add(NavigationButton(page.Id, L(page.Zh, page.En)));
        }
    }

    private void UpdateSidebarSpaces()
    {
        if (_sidebarRelayStatus is { } status)
        {
            status.IsVisible = _sidebarPaired.HasValue;
            status.Text = _sidebarPaired == true ? L("中转已配对", "Relay paired") : L("中转未配对", "Relay unpaired");
        }
        if (_sidebarSpaceList is not { } group) return;
        group.Children.Clear();
        var label = Muted(L("空间", "Spaces"));
        label.Classes.Add("nav-status");
        group.Children.Add(TitleRow(label, IconButton("plus", L("新建空间", "New space"), NewSpaceAsync)));
        foreach (var (id, name) in _sidebarSpaces)
        {
            var button = Action(name, () => OpenPaletteSpaceAsync(id), name);
            var text = new TextBlock
            {
                Text = name, Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children = { TrayGlyph.Icon("spaces", 15), text }
            };
            button.Content = row;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Classes.Add("navigation");
            if (_page == "workspace" && id == _spaceId) button.Classes.Add("selected");
            group.Children.Add(button);
        }
    }

    private async Task RefreshSidebarSpacesAsync(CancellationToken token)
    {
        var document = await Execute("workspace_read", [], token);
        SetSidebarSpaces(document);
    }

    private void SetSidebarSpaces(JsonNode? document)
    {
        _sidebarSpaces = document.Array("spaces").OfType<JsonObject>()
            .Select(space => (space.Text("id"), space.Text("name"))).ToArray();
        UpdateSidebarSpaces();
    }

    private async Task ChooseSpaceAsync()
    {
        var entries = new List<PaletteEntry>();
        var update = ShowPalette(L("切换空间", "Switch space"), L("搜索空间", "Find spaces"), entries);
        var document = _displayedWorkspace ?? await Execute("workspace_read", []);
        entries.AddRange(document.Array("spaces").OfType<JsonObject>().Select(space =>
            new PaletteEntry(space.Text("name"), L("空间", "Space"), "spaces", () => OpenPaletteSpaceAsync(space.Text("id")))));
        entries.Add(new(L("新建空间", "New space"), L("操作", "Action"), "plus", NewSpaceAsync));
        update();
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
