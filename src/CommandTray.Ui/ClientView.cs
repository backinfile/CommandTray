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
    private readonly StackPanel _navigation;
    private readonly ScrollViewer _viewport;
    private readonly StackPanel _contents;
    private readonly TextBlock _status;
    private readonly Border _workspaceGrid;
    private readonly Border _paletteOverlay;
    private readonly List<TerminalTile> _terminals = [];
    private readonly DispatcherTimer _terminalTimer;
    private readonly DispatcherTimer _workspaceTimer;
    private string _page = "workspace";
    private string? _spaceId;
    private string? _componentId;
    private bool _compact = true;
    private bool _english = !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    private CancellationTokenSource? _loading;
    private JsonNode? _displayedWorkspace;
    private bool _refreshingWorkspace;
    private bool _editing;
    private DateTimeOffset _lastInteraction;

    public ClientView(IClientBackend backend)
    {
        _backend = backend;
        AvaloniaXamlLoader.Load(this);
        _shell = this.FindControl<Grid>("Shell")!;
        _toolbar = this.FindControl<Grid>("Toolbar")!;
        _navBorder = this.FindControl<Border>("NavBorder")!;
        _navigation = this.FindControl<StackPanel>("Navigation")!;
        _viewport = this.FindControl<ScrollViewer>("Viewport")!;
        _contents = this.FindControl<StackPanel>("Contents")!;
        _status = this.FindControl<TextBlock>("StatusLine")!;
        _workspaceGrid = this.FindControl<Border>("WorkspaceGrid")!;
        _paletteOverlay = this.FindControl<Border>("PaletteOverlay")!;
        _paletteOverlay.PointerPressed += (_, args) =>
        {
            if (ReferenceEquals(args.Source, _paletteOverlay)) ClosePalette();
        };

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
            ApplyWidth(Bounds.Width);
            try
            {
                var settings = await Execute("settings_read", []);
                var language = settings.Text("language");
                if (language is "en" or "zh") _english = language == "en";
            }
            catch (Exception error) { Report(error.Message); }
            await LoadAsync();
            if (!_compact && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null) Focus();
            _terminalTimer.Start();
            _workspaceTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            ClosePalette(restoreFocus: false);
            _loading?.Cancel();
            _terminalTimer.Stop();
            _workspaceTimer.Stop();
        };
        SizeChanged += (_, args) =>
        {
            if (ApplyWidth(args.NewSize.Width)) _ = LoadAsync();
        };
    }

    public void Navigate(string page, string? spaceId = null, string? componentId = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Navigate(page, spaceId, componentId));
            return;
        }
        _page = page switch
        {
            "services" or "service" or "logs" or "manager" or "commands" => Has(ClientFeatures.Commands) ? "commands" : "workspace",
            "shared" or "sharing" or "relay" or "files" or "sharedFiles" => "files",
            "clip" or "clipboard" => "clipboard",
            "media" or "captures" or "capture" => Has(ClientFeatures.Capture) ? "capture" : "workspace",
            "settings" => "settings",
            "ssh" => Has(ClientFeatures.Ssh) ? "ssh" : "workspace",
            _ => "workspace"
        };
        if (spaceId is not null) _spaceId = spaceId;
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
        Grid.SetColumn(_navBorder, _compact ? 1 : 0);
        Grid.SetRow(_navBorder, _compact ? 3 : 0);
        Grid.SetRowSpan(_navBorder, _compact ? 1 : 3);
        _navBorder.Width = _compact ? double.NaN : 146;
        _navBorder.BorderThickness = _compact ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 1, 0);
        _navBorder.Padding = _compact ? new Thickness(5, 3) : new Thickness(9, 12);
        _contents.Margin = _compact ? new Thickness(12, 10) : new Thickness(16);
        return true;
    }

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var source = _loading = new CancellationTokenSource();
        _editing = false;
        _displayedWorkspace = null;
        _terminals.Clear();
        RenderNavigation();
        _toolbar.Children.Clear();
        _contents.Children.Clear();
        _workspaceGrid.IsVisible = !_compact && _page == "workspace" && Has(ClientFeatures.Layout);
        _viewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Report("");
        try
        {
            switch (_page)
            {
                case "workspace": await RenderWorkspaceAsync(source.Token); break;
                case "files": await RenderFilesAsync(source.Token); break;
                case "clipboard": await RenderClipboardAsync(source.Token); break;
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
        if (_page != "workspace" || _displayedWorkspace is null || _editing || _refreshingWorkspace || _paletteOverlay.IsVisible ||
            DateTimeOffset.UtcNow - _lastInteraction < TimeSpan.FromSeconds(2) ||
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        var displayed = _displayedWorkspace;
        _refreshingWorkspace = true;
        try
        {
            var current = await Execute("workspace_read", []);
            if (_page == "workspace" && !_editing && ReferenceEquals(displayed, _displayedWorkspace) &&
                !JsonNode.DeepEquals(current, displayed)) await LoadAsync();
        }
        catch (Exception error) { Report(error.Message); }
        finally { _refreshingWorkspace = false; }
    }

    private List<(string Id, string Zh, string En)> NavigationPages()
    {
        var pages = new List<(string Id, string Zh, string En)>
        {
            ("workspace", "空间", "Spaces"), ("files", "文件", "Files"), ("clipboard", "剪贴板", "Clipboard")
        };
        if (!_compact && Has(ClientFeatures.Commands)) pages.Insert(1, ("commands", "命令", "Commands"));
        if (!_compact && Has(ClientFeatures.Capture)) pages.Add(("capture", "捕获", "Capture"));
        if (!_compact && Has(ClientFeatures.Ssh)) pages.Add(("ssh", "SSH", "SSH"));
        pages.Add(("settings", "设置", "Settings"));
        return pages;
    }

    private void RenderNavigation()
    {
        var pages = NavigationPages();
        _navigation.Children.Clear();
        if (_compact)
        {
            var strip = new UniformGrid { Columns = pages.Count, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var page in pages) strip.Children.Add(NavigationButton(page.Id, L(page.Zh, page.En)));
            _navigation.HorizontalAlignment = HorizontalAlignment.Stretch;
            _navigation.Orientation = Orientation.Vertical;
            _navigation.Children.Add(strip);
        }
        else
        {
            _navigation.HorizontalAlignment = HorizontalAlignment.Stretch;
            _navigation.Orientation = Orientation.Vertical;
            var markIcon = TrayGlyph.Icon("spaces", 15);
            markIcon.Classes.Add("mark");
            var mark = new Border { Width = 28, Height = 28, Child = markIcon };
            mark.Classes.Add("mark");
            var name = Heading("CommandTray");
            name.Classes.Add("brand");
            name.VerticalAlignment = VerticalAlignment.Center;
            var brand = Row(mark, name);
            brand.Spacing = 7;
            brand.Margin = new Thickness(1, 0, 0, 14);
            _navigation.Children.Add(brand);
            foreach (var page in pages) _navigation.Children.Add(NavigationButton(page.Id, L(page.Zh, page.En)));
        }
    }

    private Button NavigationButton(string id, string name)
    {
        string glyph = PageGlyph(id);
        var button = Action(name, () => { Navigate(id); return Task.CompletedTask; }, glyph: glyph);
        if (_compact)
        {
            var label = IconLabel(glyph, name);
            label.Orientation = Orientation.Vertical;
            label.Spacing = 1;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            button.Content = label;
        }
        button.Classes.Add("navigation");
        if (_page == id) button.Classes.Add("selected");
        return button;
    }

    private static string PageGlyph(string id) => id switch { "workspace" => "spaces", "commands" => "terminal", _ => id };

    private bool Has(ClientFeatures feature) => (_backend.Features & feature) != 0;
    private string L(string zh, string en) => _english ? en : zh;

    private async Task<JsonNode?> Execute(string operation, JsonObject arguments, CancellationToken token = default)
    {
        var result = await _backend.ExecuteAsync(operation, arguments, token);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private async Task Change(string operation, JsonObject arguments)
    {
        await Execute(operation, arguments);
        await LoadAsync();
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
        button.Click += async (_, _) => await Act(action);
        return button;
    }

    private Button IconButton(string glyph, string label, Func<Task> action)
    {
        var button = Action(label, action, label);
        button.Content = TrayGlyph.Icon(glyph);
        button.Classes.Add("icon-only");
        return button;
    }

    private Button Menu(string caption, params (string Label, Func<Task> Action)[] actions)
    {
        var items = new MenuFlyout();
        foreach (var option in actions)
        {
            var entry = new MenuItem { Header = option.Label };
            entry.Click += async (_, _) => await Act(option.Action);
            items.Items.Add(entry);
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

    private void Header(string title, params Control[] actions)
    {
        var heading = Heading(title);
        heading.Classes.Add("toolbar-title");
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

    private readonly record struct Field(string Key, string Label, string Value = "", bool Secret = false,
        bool Multiline = false, bool Toggle = false);

    private Task Form(string title, Func<JsonObject, Task> save, params Field[] fields)
    {
        _editing = true;
        _workspaceGrid.IsVisible = false;
        _terminals.Clear();
        _contents.Children.Clear();
        Header(title, Action(L("取消", "Cancel"), LoadAsync));
        var inputs = new List<(string Key, Func<JsonNode?> Value)>();
        foreach (var field in fields)
        {
            if (field.Toggle)
            {
                var checkbox = new CheckBox { Content = field.Label, IsChecked = field.Value == "true" };
                _contents.Children.Add(checkbox);
                inputs.Add((field.Key, () => JsonValue.Create(checkbox.IsChecked == true)));
                continue;
            }
            var input = new TextBox
            {
                Text = field.Value, AcceptsReturn = field.Multiline, TextWrapping = TextWrapping.Wrap,
                PasswordChar = field.Secret ? '●' : '\0', MinHeight = field.Multiline ? 100 : 36
            };
            input.Classes.Add("entry");
            Avalonia.Automation.AutomationProperties.SetName(input, field.Label);
            _contents.Children.Add(Muted(field.Label));
            _contents.Children.Add(input);
            inputs.Add((field.Key, () => JsonValue.Create(input.Text ?? "")));
        }
        var button = Action(L("保存", "Save"), async () =>
        {
            var values = new JsonObject();
            foreach (var input in inputs) values[input.Key] = input.Value();
            await save(values);
        });
        button.Classes.Add("primary");
        _contents.Children.Add(button);
        return Task.CompletedTask;
    }
}
