using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private string? _commandSelection;
    private string _commandQuery = "";
    private bool _showCommandDetails;
    private string _logQuery = "";
    private int _logLevel;
    private bool _followLogs = true;
    private bool _wrapLogs = true;

    private async Task RenderCommandsAsync(CancellationToken token)
    {
        var create = PrimaryAction(L("新建命令", "Add command"), () => CommandForm(null), "plus");
        Header(L("命令", "Commands"));
        var commands = (await ReadPageContent("commands_list", token) as JsonArray ?? []).OfType<JsonObject>().ToArray();
        if (_componentId is { } requested && commands.Any(command => CommandId(command) == requested))
        {
            _componentId = null;
            _commandSelection = requested;
            _showCommandDetails = true;
        }
        var active = commands.FirstOrDefault(command => CommandId(command) == _commandSelection) ?? commands.FirstOrDefault();
        _commandSelection = active is null ? null : CommandId(active);
        var summary = Muted("");
        var heading = PageHeading(L("命令", "Commands"), summary, Row(IconButton("sync", L("刷新", "Refresh"), LoadAsync), create));
        var rows = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
        Action? refreshDetails = null;
        var snapshots = commands.ToDictionary(CommandId, StringComparer.Ordinal);
        UpdateSummary();
        if (snapshots.Count > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            bool refreshing = false;
            timer.Tick += async (_, _) =>
            {
                if (refreshing || token.IsCancellationRequested) return;
                refreshing = true;
                try
                {
                    var latest = await Execute("commands_list", [], token) as JsonArray ?? [];
                    foreach (var entry in latest.OfType<JsonObject>())
                    {
                        var id = CommandId(entry);
                        if (!snapshots.TryGetValue(id, out var command)) continue;
                        command["state"] = entry["state"]?.DeepClone();
                        command["processId"] = entry["processId"]?.DeepClone();
                        if (rows.TryGetValue(id, out var state))
                            state.Text = ServiceStateLabel(command.Text("state"));
                    }
                    UpdateSummary();
                    refreshDetails?.Invoke();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error) when (!token.IsCancellationRequested) { Report(error.Message); }
                finally { refreshing = false; }
            };
            heading.AttachedToVisualTree += (_, _) => timer.Start();
            heading.DetachedFromVisualTree += (_, _) => timer.Stop();
        }
        _contents.Children.Add(heading);

        if (_compact && _showCommandDetails && active is not null)
        {
            Header(L("命令", "Commands"), Action(L("返回", "Back"), async () => { _showCommandDetails = false; await LoadAsync(); }));
            _contents.Children.Add(ShowDetails(active));
            return;
        }

        var list = new StackPanel();
        var search = new TextBox { Text = _commandQuery, PlaceholderText = L("搜索命令", "Search commands"),
            Margin = new Thickness(10) };
        AutomationProperties.SetName(search, L("搜索命令", "Search commands"));
        var left = WorkbenchPanel(L("命令列表", "Commands"), "terminal",
            new StackPanel { Children = { search, list } });
        var details = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (!_compact)
            details.Content = active is null ? EmptyState("terminal", L("暂无命令", "No commands")) : ShowDetails(active);
        _contents.Children.Add(_compact ? left : WorkbenchSplit(left, details, "1*,1.75*"));
        search.TextChanged += (_, _) => { _commandQuery = search.Text ?? ""; DrawList(); };
        DrawList();

        Control ShowDetails(JsonObject command)
        {
            var (panel, refresh) = CommandDetails(command, token);
            refreshDetails = refresh;
            return panel;
        }

        void UpdateSummary()
        {
            int running = commands.Count(IsCommandRunning);
            summary.Text = L($"{running} 个运行中 · {commands.Length} 个命令", $"{running} running · {commands.Length} commands");
        }

        void DrawList()
        {
            var query = _commandQuery.Trim();
            list.Children.Clear();
            rows.Clear();
            foreach (var command in commands.Where(item => item.Text("name").Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Text("command").Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                string id = CommandId(command);
                var row = Action(L("查看命令", "View command"), async () =>
                {
                    if (_commandSelection != id) { _logQuery = ""; _logLevel = 0; }
                    _commandSelection = id;
                    if (_compact)
                    {
                        _showCommandDetails = true;
                        await LoadAsync();
                    }
                    else
                    {
                        details.Content = ShowDetails(command);
                        DrawList();
                    }
                });
                var line = new TextBlock
                {
                    Text = command.Text("command"), TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = new FontFamily("monospace")
                };
                line.Classes.Add("muted");
                var state = Muted(ServiceStateLabel(command.Text("state")));
                rows[id] = state;
                row.Content = new StackPanel { Spacing = 4, Children =
                {
                    TitleRow(Heading(command.Text("name")), state), line
                }};
                row.Classes.Add("workbench-list-row");
                row.Classes.Set("selected", _commandSelection == id && !_compact);
                AutomationProperties.SetName(row, command.Text("name"));
                list.Children.Add(row);
            }
            if (list.Children.Count == 0)
                list.Children.Add(SettingRow(L("没有符合条件的命令", "No matching commands"), new Border()));
        }
    }

    private static string CommandId(JsonObject command) => command.Text("id", command.Text("name"));

    private static bool IsCommandRunning(JsonObject command) =>
        command.Text("state").Equals("running", StringComparison.OrdinalIgnoreCase);

    private string ServiceStateLabel(string state) => state.ToLowerInvariant() switch
    {
        "running" => L("运行中", "Running"), "stopped" => L("已停止", "Stopped"),
        "starting" => L("启动中", "Starting"), "stopping" => L("停止中", "Stopping"),
        "faulted" or "failed" => L("异常", "Failed"), _ => state
    };

    private (Border Panel, Action RefreshStatus) CommandDetails(JsonObject command, CancellationToken pageCancellation)
    {
        var arguments = new JsonObject { ["id"] = CommandId(command) };
        var name = command.Text("name");
        bool attached = false;
        bool changingState = false;
        var actions = Row();
        var state = Muted("");
        var processId = Muted("");
        var lifecycle = PrimaryAction("", () => RunCommandAsync(IsCommandRunning(command) ? "command_stop" : "command_start"));
        RefreshStatus();
        var choices = new List<(string Label, Func<Task> Action)>
        {
            (L("重启", "Restart"), () => RunCommandAsync("command_restart")),
            (L("编辑", "Edit"), () => CommandForm(command)),
            (L("删除", "Delete"), () => Confirm(L("删除命令？", "Delete command?"), () => Change("command_delete", arguments)))
        };
        if (Uri.TryCreate(command.Text("openUrl"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            choices.Insert(0, (L("打开网页", "Open service URL"), async () => await RequireTopLevel().Launcher.LaunchUriAsync(uri)));
        var menu = Menu("⋯", choices.ToArray());
        actions.Children.Add(lifecycle);
        actions.Children.Add(menu);
        var details = new StackPanel
        {
            Children =
            {
                SettingRow(L("工作目录", "Working directory"), state, command.Text("workingDirectory", "—")),
                SettingRow(L("进程 ID", "Process ID"), processId)
            }
        };
        var search = new TextBox { Text = _logQuery, PlaceholderText = L("搜索日志", "Search logs"), MinWidth = 138 };
        AutomationProperties.SetName(search, L("搜索日志", "Search logs"));
        var level = new ComboBox
        {
            ItemsSource = new[] { L("全部级别", "All levels"), L("普通", "Normal"), L("警告", "Warnings"), L("错误", "Errors") },
            SelectedIndex = _logLevel
        };
        AutomationProperties.SetName(level, L("日志级别", "Log level"));
        var follow = new CheckBox { Content = L("跟随", "Follow"), IsChecked = _followLogs };
        var wrap = new CheckBox { Content = L("换行", "Wrap"), IsChecked = _wrapLogs };
        var transcript = "";
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = _wrapLogs ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"), MinHeight = 180, MaxHeight = 425 };
        text.Classes.Add("terminal");
        var status = Muted(L("正在加载…", "Loading…"));
        var filters = new WrapPanel
        {
            ItemSpacing = 6, LineSpacing = 4, Margin = new Thickness(11, 8),
            Children = { search, level, follow, wrap, IconButton("copy", L("复制日志", "Copy logs"), async () =>
                await Execute("clipboard_local_write", new() { ["text"] = text.Text })) }
        };
        details.Children.Add(filters);
        status.Margin = new Thickness(11, 0, 11, 4);
        details.Children.Add(status);
        details.Children.Add(text);
        search.TextChanged += (_, _) => { _logQuery = search.Text ?? ""; DrawLog(); };
        level.SelectionChanged += (_, _) => { _logLevel = Math.Max(0, level.SelectedIndex); DrawLog(); };
        follow.IsCheckedChanged += (_, _) => _followLogs = follow.IsChecked == true;
        wrap.IsCheckedChanged += (_, _) =>
        {
            _wrapLogs = wrap.IsChecked == true;
            text.TextWrapping = _wrapLogs ? TextWrapping.Wrap : TextWrapping.NoWrap;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var reading = false;
        timer.Tick += async (_, _) => { if (follow.IsChecked == true) await ReadLogAsync(); };
        details.AttachedToVisualTree += async (_, _) => { attached = true; timer.Start(); await ReadLogAsync(); };
        details.DetachedFromVisualTree += (_, _) => { attached = false; timer.Stop(); };
        return (WorkbenchPanel(name, "terminal", details, actions), RefreshStatus);

        void RefreshStatus()
        {
            if (changingState) return;
            var serviceState = command.Text("state").ToLowerInvariant();
            state.Text = ServiceStateLabel(serviceState);
            processId.Text = command["processId"]?.ToString() ?? "—";
            bool running = serviceState == "running";
            string label = running ? L("停止", "Stop") : L("启动", "Start");
            if (AutomationProperties.GetName(lifecycle) != label)
            {
                lifecycle.Content = IconLabel(running ? "stop" : "play", label);
                AutomationProperties.SetName(lifecycle, label);
            }
            lifecycle.IsEnabled = serviceState is not ("starting" or "stopping");
        }

        async Task RunCommandAsync(string operation)
        {
            changingState = true;
            actions.IsEnabled = false;
            state.Text = operation switch
            {
                "command_stop" => L("停止中…", "Stopping…"), "command_restart" => L("重启中…", "Restarting…"),
                _ => L("启动中…", "Starting…")
            };
            try { await Change(operation, arguments); }
            finally
            {
                changingState = false;
                if (attached)
                {
                    state.Text = ServiceStateLabel(command.Text("state"));
                    actions.IsEnabled = true;
                }
            }
        }

        async Task ReadLogAsync()
        {
            if (reading || pageCancellation.IsCancellationRequested) return;
            reading = true;
            try
            {
                var result = TextOf(await Execute("command_log", arguments, pageCancellation));
                if (!attached) return;
                status.IsVisible = result.Length == 0;
                status.Text = L("暂无输出", "No output");
                if (transcript == result) return;
                transcript = result;
                DrawLog();
            }
            catch (OperationCanceledException) when (pageCancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (attached) { status.IsVisible = true; status.Text = error.Message; }
            }
            finally { reading = false; }
        }

        void DrawLog()
        {
            var query = search.Text ?? "";
            var shown = string.Join("\n", transcript.Split('\n').Where(line =>
                (_logLevel == 0 || (int)LogLineClassifier.Classify(line) == _logLevel - 1) &&
                line.Contains(query, StringComparison.OrdinalIgnoreCase)));
            if (text.Text == shown) return;
            int previous = text.CaretIndex;
            text.Text = shown;
            text.CaretIndex = follow.IsChecked == true ? shown.Length : Math.Min(shown.Length, previous);
        }
    }
}
