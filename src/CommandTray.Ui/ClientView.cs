using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView : UserControl
{
    private readonly IClientBackend _backend;
    private readonly Grid _shell;
    private readonly Grid _toolbar;
    private readonly Border _navBorder;
    private readonly ContentControl _navHeader;
    private readonly Border _navFooter;
    private readonly GridSplitter _navSplitter;
    private readonly StackPanel _navigation;
    private readonly ScrollViewer _viewport;
    private readonly StackPanel _contents;
    private readonly TextBlock _status;
    private readonly Border _workspaceGrid;
    private readonly Border _paletteOverlay;
    private readonly Border _actionOverlay;
    private readonly List<TerminalTile> _terminals = [];
    private readonly DispatcherTimer _terminalTimer;
    private readonly DispatcherTimer _workspaceTimer;
    private string _page = "workspace";
    private string? _spaceId;
    private string? _requestedSpaceId;
    private string? _observedActiveSpaceId;
    private string? _componentId;
    private bool _compact = true;
    private bool _english = !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    private CancellationTokenSource? _loading;
    private JsonNode? _displayedWorkspace;
    private bool _displayedWorkspaceConflict;
    private bool _refreshingWorkspace;
    private bool _editing;
    private DateTimeOffset _lastInteraction;
    private TopLevel? _backHost;
    private MenuFlyout? _openMenu;

    public ClientView(IClientBackend backend)
    {
        _backend = backend;
        AvaloniaXamlLoader.Load(this);
        _shell = this.FindControl<Grid>("Shell")!;
        if (OperatingSystem.IsAndroid()) _shell.Classes.Add("android");
        _toolbar = this.FindControl<Grid>("Toolbar")!;
        _navBorder = this.FindControl<Border>("NavBorder")!;
        _navHeader = this.FindControl<ContentControl>("NavHeader")!;
        _navFooter = this.FindControl<Border>("NavFooter")!;
        _navSplitter = this.FindControl<GridSplitter>("NavSplitter")!;
        _navigation = this.FindControl<StackPanel>("Navigation")!;
        _viewport = this.FindControl<ScrollViewer>("Viewport")!;
        _contents = this.FindControl<StackPanel>("Contents")!;
        _status = this.FindControl<TextBlock>("StatusLine")!;
        _workspaceGrid = this.FindControl<Border>("WorkspaceGrid")!;
        _paletteOverlay = this.FindControl<Border>("PaletteOverlay")!;
        _actionOverlay = this.FindControl<Border>("ActionOverlay")!;
        _paletteOverlay.PointerPressed += (_, args) =>
        {
            if (ReferenceEquals(args.Source, _paletteOverlay)) ClosePalette();
        };
        _navSplitter.DragCompleted += async (_, _) => await Act(SaveSidebarWidthAsync);
        _navSplitter.KeyUp += async (_, args) =>
        {
            if (args.Key is Key.Left or Key.Right) await Act(SaveSidebarWidthAsync);
        };
        _viewport.SizeChanged += (_, _) => ResizeFocusedPanel();

        _terminalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _terminalTimer.Tick += async (_, _) => await RefreshTerminalsAsync();
        _workspaceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _workspaceTimer.Tick += async (_, _) => await RefreshWorkspaceAsync();
        AddHandler(PointerPressedEvent, (_, _) => _lastInteraction = DateTimeOffset.UtcNow,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _lastInteraction = DateTimeOffset.UtcNow,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, HandleWindowKey,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AttachedToVisualTree += async (_, _) =>
        {
            if (OperatingSystem.IsAndroid() && TopLevel.GetTopLevel(this) is { } host)
            {
                _backHost = host;
                host.BackRequested += HandleAndroidBack;
            }
            ApplyWidth(Bounds.Width);
            try
            {
                var settings = await Execute("settings_read", []);
                var language = settings.Text("language");
                if (language is "en" or "zh") _english = language == "en";
                _sidebarWidth = Math.Clamp(settings.Integer("sidebarWidth", DefaultSidebarWidth), MinimumSidebarWidth, MaximumSidebarWidth);
                _sidebarCollapsed = settings.Flag("sidebarCollapsed");
                ApplySidebarGeometry();
            }
            catch (Exception error) { Report(error.Message); }
            await LoadAsync();
            if (!_compact && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null) Focus();
            _terminalTimer.Start();
            _workspaceTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_backHost is { } host) host.BackRequested -= HandleAndroidBack;
            _backHost = null;
            CloseActionDialogs();
            ClosePalette(restoreFocus: false);
            _loading?.Cancel();
            foreach (var transfer in _transfers) transfer.Cancellation?.Cancel();
            _terminalTimer.Stop();
            _workspaceTimer.Stop();
        };
        SizeChanged += (_, args) =>
        {
            if (ApplyWidth(args.NewSize.Width) && (!OperatingSystem.IsAndroid() || !_editing)) _ = LoadAsync();
        };
    }

    private void HandleAndroidBack(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (_openMenu is { IsOpen: true } menu) menu.Hide();
        else if (_actionOverlay.IsVisible) CloseActionDialog();
        else if (_toolbar.Children.OfType<ComboBox>().FirstOrDefault() is { IsDropDownOpen: true } picker)
            picker.IsDropDownOpen = false;
        else if (_paletteOverlay.IsVisible) ClosePalette();
        else if (_editing) _ = LoadAsync();
        else if (_page != "workspace") Navigate("workspace");
        else return;
        args.Handled = true;
    }

    public void Navigate(string page, string? spaceId = null, string? componentId = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Navigate(page, spaceId, componentId));
            return;
        }
        CloseActionDialogs();
        ClosePalette(restoreFocus: false);
        _page = page switch
        {
            "services" or "service" or "logs" or "manager" or "commands" => Has(ClientFeatures.Commands) ? "commands" : "workspace",
            "shared" or "sharing" or "relay" or "files" or "sharedFiles" => "files",
            "clip" or "clipboard" => "clipboard",
            "tools" or "text-tools" or "text-compare" or "timestamp" or "qr" or "checksum" => page,
            "pet" => Has(ClientFeatures.Pet) ? "pet" : "workspace",
            "media" or "captures" or "capture" => Has(ClientFeatures.Capture) ? "capture" : "workspace",
            "settings" => "settings",
            "ssh" => Has(ClientFeatures.Ssh) || OperatingSystem.IsAndroid() ? "ssh" : "workspace",
            _ => "workspace"
        };
        _requestedSpaceId = spaceId;
        if (spaceId is not null) _spaceId = spaceId;
        _focusedPanelId = null;
        _componentId = componentId;
        _viewport.Offset = default;
        if (TopLevel.GetTopLevel(this) is not null)
        {
            if (!_compact) Focus();
            _ = LoadAsync();
        }
    }

    private bool ApplyWidth(double width)
    {
        bool compact = width < 720;
        if (_navigation.Children.Count != 0 && _compact == compact) return false;
        _compact = compact;
        Classes.Set("compact", compact);
        Classes.Set("wide", !compact);
        _shell.Classes.Set("compact", compact);
        Grid.SetColumn(_navBorder, _compact ? 1 : 0);
        Grid.SetRow(_navBorder, _compact ? 3 : 0);
        Grid.SetRowSpan(_navBorder, _compact ? 1 : 3);
        if (_compact)
        {
            _focusedPanelId = null;
            ClosePalette(restoreFocus: false);
        }
        ApplySidebarGeometry();
        _navBorder.BorderThickness = _compact ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 1, 0);
        _contents.Margin = _compact ? new Thickness(12, 10) : DesktopWorkbench ? new Thickness(28, 24) : new Thickness(16);
        return true;
    }

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var source = _loading = new CancellationTokenSource();
        _editing = false;
        _displayedWorkspace = null;
        _focusedPanel = null;
        _workspaceTargets.Clear();
        _locatedComponent = null;
        _terminals.Clear();
        _drawings.Clear();
        _transferRows = null;
        RenderNavigation();
        _toolbar.Children.Clear();
        _contents.Children.Clear();
        _workspaceGrid.IsVisible = false;
        _viewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Report("");
        try
        {
            if (_page is not ("workspace" or "tools" or "text-tools" or "text-compare" or "timestamp" or "qr" or "checksum" or "pet") && !_compact)
            {
                try { await RefreshSidebarSpacesAsync(source.Token); }
                catch (Exception error) when (!source.IsCancellationRequested) { Report(error.Message); }
            }
            switch (_page)
            {
                case "workspace": await RenderWorkspaceAsync(source.Token); break;
                case "files": await RenderFilesAsync(source.Token); break;
                case "clipboard": await RenderClipboardAsync(source.Token); break;
                case "tools": RenderToolsOverview(); break;
                case "text-tools": Header(L("文本转换", "Text conversion")); RenderTextTools(source.Token); break;
                case "text-compare": Header(L("文本对比", "Text comparison")); RenderTextComparison(source.Token); break;
                case "timestamp": Header(L("时间戳", "Timestamp")); RenderTimestamp(source.Token); break;
                case "qr": Header(L("二维码", "QR code")); RenderQrCode(source.Token); break;
                case "checksum": Header(L("文件校验", "File checksum")); RenderFileChecksum(source.Token); break;
                case "pet": RenderPet(); break;
                case "settings": await RenderSettingsAsync(source.Token); break;
                case "commands": await RenderCommandsAsync(source.Token); break;
                case "capture": await RenderCaptureAsync(source.Token); break;
                case "ssh": await RenderSshAsync(source.Token); break;
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error) when (!source.IsCancellationRequested) { Report(error.Message); }
    }

    private async Task RefreshWorkspaceAsync()
    {
        if (_page != "workspace" || _displayedWorkspace is null || _editing || _refreshingWorkspace ||
            _paletteOverlay.IsVisible || _actionOverlay.IsVisible ||
            _drawings.Any(board => board.Editing) ||
            DateTimeOffset.UtcNow - _lastInteraction < TimeSpan.FromSeconds(2) ||
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        var displayed = _displayedWorkspace;
        bool displayedConflict = _displayedWorkspaceConflict;
        _refreshingWorkspace = true;
        try
        {
            var current = await Execute("workspace_read", []);
            bool changed = !JsonNode.DeepEquals(current, displayed);
            if (!changed && OperatingSystem.IsAndroid())
                changed = HasWorkspaceConflict(await Execute("relay_status", [])) != displayedConflict;
            if (changed && _page == "workspace" && !_editing && !_actionOverlay.IsVisible && !_drawings.Any(board => board.Editing) &&
                ReferenceEquals(displayed, _displayedWorkspace))
                await LoadAsync();
        }
        catch (Exception error) { Report(error.Message); }
        finally { _refreshingWorkspace = false; }
    }

    private Button NavigationButton(string id, string name, Func<Task>? action = null, string? style = null)
    {
        string glyph = PageGlyph(id);
        var button = Action(name, action ?? (() => { Navigate(id); return Task.CompletedTask; }), name);
        int size = style switch { "nav-main-entry" => 18, "nav-sub-entry" => 14, _ => 16 };
        if (_compact)
        {
            var label = IconLabel(glyph, name);
            label.Orientation = Orientation.Vertical;
            label.Spacing = 1;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            if (OperatingSystem.IsAndroid() && label.Children[1] is TextBlock text)
            {
                label.HorizontalAlignment = HorizontalAlignment.Stretch;
                text.TextWrapping = TextWrapping.Wrap;
                text.TextTrimming = TextTrimming.None;
                text.TextAlignment = TextAlignment.Center;
            }
            button.Content = label;
        }
        else button.Content = _sidebarCollapsed ? TrayGlyph.Icon(glyph, size) : SidebarLabel(glyph, name, size);
        button.Classes.Add("navigation");
        if (style is not null) button.Classes.Add(style);
        if (_page == id || _compact && id == "tools" && _page is not ("workspace" or "settings")) button.Classes.Add("selected");
        return button;
    }

    private static Grid SidebarLabel(string glyph, string name, int iconSize)
    {
        var label = new TextBlock
        {
            Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 9,
            Children = { TrayGlyph.Icon(glyph, iconSize), label }
        };
    }

    private static string PageGlyph(string id) => id switch
    {
        "workspace" => "spaces", "commands" => "terminal", "tools" => "tool-grid", _ => id
    };

    private bool Has(ClientFeatures feature) => (_backend.Features & feature) != 0;
    private string L(string zh, string en) => _english ? en : zh;

    private async Task<JsonNode?> Execute(string operation, JsonObject arguments, CancellationToken token = default)
    {
        var result = await _backend.ExecuteAsync(operation, arguments, token);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<JsonNode?> ReadPageContent(string operation, CancellationToken token)
    {
        var loading = Muted(L("正在加载…", "Loading…"));
        _contents.Children.Add(loading);
        try { return await Execute(operation, [], token); }
        finally { _contents.Children.Remove(loading); }
    }

    private async Task Change(string operation, JsonObject arguments, bool preserveViewport = false)
    {
        var page = _loading;
        var offset = _viewport.Offset;
        preserveViewport |= _actionOverlay.IsVisible;
        await Execute(operation, arguments, CurrentActionCancellation);
        if (!ReferenceEquals(page, _loading)) return;
        await LoadAsync();
        if (!preserveViewport || offset == default) return;
        page = _loading;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(page, _loading)) _viewport.Offset = offset;
        }, DispatcherPriority.Loaded);
    }

    private void Report(string message)
    {
        _status.Text = message;
        _status.IsVisible = !string.IsNullOrEmpty(message);
    }

    private async Task Act(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Report(error.Message); }
    }

    private Button Action(string label, Func<Task> action, string? tooltip = null, string? glyph = null)
    {
        var iconOnly = label is "+" or "↻";
        glyph ??= label switch { "+" => "plus", "↻" => "sync", _ => null };
        var button = new Button
        {
            Content = glyph is null ? label : iconOnly ? TrayGlyph.Icon(glyph) : IconLabel(glyph, label),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("action");
        if (iconOnly) button.Classes.Add("icon-only");
        Avalonia.Automation.AutomationProperties.SetName(button, tooltip ?? label);
        if (tooltip is not null)
        {
            ToolTip.SetTip(button, tooltip);
        }
        button.Click += async (_, _) =>
        {
            var pending = Act(action);
            if (pending.IsCompleted) { await pending; return; }
            button.IsEnabled = false;
            try { await pending; }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private Button IconButton(string glyph, string label, Func<Task> action)
    {
        var button = Action(label, action, label);
        button.Content = TrayGlyph.Icon(glyph);
        button.Classes.Add("icon-only");
        return button;
    }

    private Button PrimaryAction(string label, Func<Task> action, string? glyph = null)
    {
        var button = Action(label, action, glyph: glyph);
        button.Classes.Add("primary");
        return button;
    }

    private Button SecondaryAction(string label, Func<Task> action, string? glyph = null)
    {
        var button = Action(label, action, glyph: glyph);
        button.Classes.Add("secondary");
        return button;
    }

    private static WrapPanel ActionBar(params Control[] actions)
    {
        var panel = new WrapPanel { ItemSpacing = 7, LineSpacing = 5 };
        foreach (var action in actions) panel.Children.Add(action);
        return panel;
    }

    private static void SetStatusTone(TextBlock label, string tone)
    {
        label.Classes.Set("status-success", tone == "success");
        label.Classes.Set("status-warning", tone == "warning");
        label.Classes.Set("status-error", tone == "error");
    }

    private Border EmptyState(string glyph, string title, params Control[] actions)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(IconLabel(glyph, title, prominent: true));
        var buttons = new WrapPanel { ItemSpacing = 6, LineSpacing = 4 };
        foreach (var action in actions) buttons.Children.Add(action);
        content.Children.Add(buttons);
        var card = Table(content);
        card.Padding = new Thickness(14);
        return card;
    }

    private Button Menu(string caption, params (string Label, Func<Task> Action)[] actions)
    {
        var items = new MenuFlyout();
        foreach (var option in actions)
        {
            var entry = new MenuItem { Header = option.Label };
            if (_compact) entry.MinHeight = 48;
            if (OperatingSystem.IsAndroid()) entry.Classes.Add("android");
            entry.Click += async (_, _) => await Act(option.Action);
            items.Items.Add(entry);
        }
        if (OperatingSystem.IsAndroid())
        {
            items.Opened += (_, _) => _openMenu = items;
            items.Closed += (_, _) => { if (ReferenceEquals(_openMenu, items)) _openMenu = null; };
        }
        var glyph = caption switch { "⋯" => "more", "+" => "plus", "↻" => "sync", _ => null };
        var button = new Button { Content = glyph is null ? caption : TrayGlyph.Icon(glyph),
            Flyout = items, VerticalAlignment = VerticalAlignment.Center };
        button.Classes.Add("action");
        if (glyph is not null) button.Classes.Add("icon-only");
        if (caption == "⋯")
        {
            var name = L("更多操作", "More actions");
            ToolTip.SetTip(button, name);
            Avalonia.Automation.AutomationProperties.SetName(button, name);
        }
        return button;
    }

    private Button MoreActions(string label, params (string Label, Func<Task> Action)[] actions)
    {
        var button = Menu("⋯", actions);
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private void Header(string title, params Control[] actions)
    {
        var heading = Heading(title);
        heading.Classes.Add("toolbar-title");
        if (OperatingSystem.IsAndroid())
        {
            heading.TextWrapping = TextWrapping.Wrap;
            heading.TextTrimming = TextTrimming.None;
        }
        Header(heading, actions);
    }

    private void Header(Control title, params Control[] actions)
    {
        _toolbar.Children.Clear();
        title.VerticalAlignment = VerticalAlignment.Center;
        _toolbar.Children.Add(title);
        var row = Row(actions);
        if (!_compact) row.Children.Insert(0, PaletteButton());
        Grid.SetColumn(row, 1);
        _toolbar.Children.Add(row);
    }

    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    private static StackPanel IconLabel(string icon, string label, bool prominent = false)
    {
        var text = prominent ? Heading(label) : new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = Row(TrayGlyph.Icon(icon, 15), text);
        row.Spacing = 7;
        return row;
    }

    private static TextBlock Heading(string text)
    {
        var label = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
        label.Classes.Add("section");
        return label;
    }

    private static TextBlock Muted(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        label.Classes.Add("muted");
        return label;
    }

    private static SelectableTextBlock Body(string text)
    {
        var block = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("reading");
        return block;
    }
    private static Border Section(Control child)
    {
        var line = new Border { Child = child, Padding = new Thickness(0, 2, 0, 9) };
        line.Classes.Add("divider");
        return line;
    }

    private static Border Table(Control child)
    {
        var table = new Border { Child = child };
        table.Classes.Add("table");
        return table;
    }

    private static Grid TitleRow(Control content, Control action)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        content.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        row.Children.Add(content);
        row.Children.Add(action);
        return row;
    }

    private static string TextOf(JsonNode? value, string key = "text") =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.Text(key);

}
