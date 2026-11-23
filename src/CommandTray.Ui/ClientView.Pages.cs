using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private async Task RenderFilesAsync(CancellationToken token)
    {
        var relay = await Execute("relay_status", [], token);
        if (!relay.Flag("paired"))
        {
            Header(L("共享文件", "Shared files"));
            PairPrompt();
            return;
        }
        var upload = Action(_compact ? "+" : L("上传", "Upload"), () => Form(L("上传文件", "Upload files"), UploadAsync,
            new Field("folder", L("共享目录（可选）", "Shared folder (optional)"))), L("上传文件", "Upload files"), "plus");
        if (!_compact) upload.Classes.Add("primary");
        Header(L("共享文件", "Shared files"), IconButton("sync", L("刷新", "Refresh"), LoadAsync), upload);
        var files = await Execute("files_list", [], token) as JsonArray ?? [];
        if (files.Count == 0) _contents.Children.Add(Muted(L("暂无共享文件", "No shared files")));
        foreach (var group in files.OfType<JsonObject>().GroupBy(file => file.Text("folder")).OrderBy(group => group.Key))
        {
            if (group.Key.Length > 0) _contents.Children.Add(IconLabel("files", group.Key, prominent: true));
            var list = new StackPanel();
            foreach (var file in group)
            {
                var id = file.Text("id");
                var name = file.Text("name");
                var summary = new StackPanel { Spacing = 2 };
                summary.Children.Add(IconLabel("file", name, prominent: true));
                summary.Children.Add(Muted(FileSize(file["size"])));
                var row = Section(TitleRow(summary, Row(
                    IconButton("download", L("下载", "Download"), () => DownloadAsync(id, name)),
                    Menu("⋯", (L("删除", "Delete"), () => Confirm(L("删除共享文件？", "Delete shared file?"),
                        () => Change("file_delete", new() { ["id"] = id })))))));
                row.Padding = new Thickness(_compact ? 0 : 11, 5, 6, 6);
                list.Children.Add(row);
            }
            _contents.Children.Add(_compact ? list : Table(list));
        }
    }

    private async Task UploadAsync(JsonObject values)
    {
        var storage = RequireTopLevel().StorageProvider;
        var inputs = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
        foreach (var input in inputs)
        {
            var path = TemporaryFile(input.Name);
            try
            {
                await using (var original = await input.OpenReadAsync())
                await using (var staging = File.Create(path))
                    await original.CopyToAsync(staging);
                await Execute("file_upload", new()
                {
                    ["name"] = input.Name, ["folder"] = values.Text("folder"), ["path"] = path
                });
            }
            finally { RemoveTemporary(path); }
        }
        await LoadAsync();
    }

    private async Task DownloadAsync(string id, string name)
    {
        var destination = await RequireTopLevel().StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions { SuggestedFileName = name, ShowOverwritePrompt = true });
        if (destination is null) return;
        var path = TemporaryFile(name);
        try
        {
            await Execute("file_download", new() { ["id"] = id, ["path"] = path });
            await using var source = File.OpenRead(path);
            await using var output = await destination.OpenWriteAsync();
            if (output.CanSeek) output.SetLength(0);
            await source.CopyToAsync(output);
        }
        finally { RemoveTemporary(path); }
    }

    private static string FileSize(JsonNode? node)
    {
        if (!long.TryParse(node?.ToString(), out var size)) return "";
        return size switch
        {
            >= 1024L * 1024 => $"{size / (1024d * 1024):N1} MB",
            >= 1024 => $"{size / 1024d:N1} KB",
            _ => $"{size} B"
        };
    }

    private static string TemporaryFile(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "commandtray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var leaf = Path.GetFileName(name);
        if (leaf is "" or "." or ".." || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) leaf = "transfer";
        return Path.Combine(directory, leaf);
    }

    private static void RemoveTemporary(string path)
    {
        try
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private TopLevel RequireTopLevel() => TopLevel.GetTopLevel(this)
        ?? throw new InvalidOperationException(L("窗口尚未打开。", "The window is not open."));

    private async Task RenderClipboardAsync(CancellationToken token)
    {
        var relay = await Execute("relay_status", [], token);
        if (!relay.Flag("paired"))
        {
            Header(L("剪贴板", "Clipboard"));
            PairPrompt();
        }
        else
        {
            Header(L("剪贴板", "Clipboard"),
                IconButton("sync", L("刷新", "Refresh"), LoadAsync),
                IconButton("send", L("发送本机剪贴板", "Send local clipboard"), async () =>
                {
                    var local = TextOf(await Execute("clipboard_local_read", []));
                    if (local.Length == 0) throw new InvalidOperationException(L("剪贴板没有文本。", "The clipboard has no text."));
                    await Change("clipboard_send", new() { ["text"] = local });
                }),
                IconButton("copy", L("接收并复制", "Receive and copy"), async () =>
                {
                    await Execute("clipboard_receive", []);
                    Report(L("已复制到本机剪贴板", "Copied to this device"));
                }));
            var clip = await Execute("clipboard_read", [], token);
            string text = clip.Text("text");
            var preview = new StackPanel { Spacing = 6, Margin = new Thickness(_compact ? 0 : 12, 6) };
            preview.Children.Add(text.Length > 0 ? Body(text) : Muted(L("暂无同步文本", "No synchronized text")));
            if (DateTimeOffset.TryParse(clip.Text("updatedAt"), CultureInfo.InvariantCulture, out var updated) &&
                updated > DateTimeOffset.UnixEpoch)
                preview.Children.Add(Muted(updated.ToLocalTime().ToString("g")));
            _contents.Children.Add(_compact ? preview : Table(preview));
        }
        if (Has(ClientFeatures.ClipboardHistory))
        {
            _contents.Children.Add(Heading(L("本机历史", "Local history")));
            var history = await Execute("clipboard_history", [], token) as JsonArray ?? [];
            foreach (var entry in history)
            {
                string text = TextOf(entry);
                var block = Muted(text);
                block.MaxLines = 2;
                _contents.Children.Add(Section(TitleRow(block, Action(L("复制", "Copy"),
                    async () => await Execute("clipboard_local_write", new() { ["text"] = text })))));
            }
        }
    }

    private void PairPrompt() => _contents.Children.Add(Action(L("连接中转服务器", "Connect to a relay"),
        () => { Navigate("settings"); return Task.CompletedTask; }));

    private async Task RenderSettingsAsync(CancellationToken token)
    {
        Header(L("设置", "Settings"));
        var settings = await Execute("settings_read", [], token);
        var relay = await Execute("relay_status", [], token);
        _contents.Children.Add(IconLabel("files", L("中转服务器", "Relay server"), prominent: true));
        var details = new StackPanel { Spacing = 4 };
        string relayName = relay.Text("deviceName");
        bool paired = relay.Flag("paired");
        details.Children.Add(Heading(relayName.Length > 0 ? relayName : paired
            ? L("已配对", "Paired") : L("未配对", "Unpaired")));
        if (relay.Text("server") is { Length: > 0 } server) details.Children.Add(Body(server));
        var connectionRow = Section(TitleRow(details, paired
            ? Menu("⋯",
                (L("编辑连接", "Edit connection"), () => PairForm(relay)),
                (L("已配对设备", "Paired devices"), ShowDevicesAsync),
                (L("解除配对", "Unpair"), () => Confirm(L("解除本机配对？", "Unpair this device?"),
                    () => Change("relay_unpair", []))))
            : Action(L("配对", "Pair"), () => PairForm(relay))));
        if (!_compact)
        {
            connectionRow.Classes.Remove("divider");
            connectionRow.Classes.Add("table");
            connectionRow.Padding = new Thickness(12, 7);
        }
        _contents.Children.Add(connectionRow);

        var mcp = new ToggleSwitch { IsChecked = settings.Flag("mcpEnabled"), OnContent = "", OffContent = "" };
        Avalonia.Automation.AutomationProperties.SetName(mcp, L("本地 MCP", "Local MCP"));
        mcp.IsCheckedChanged += async (_, _) => await Act(() => Change("settings_save", new() { ["mcpEnabled"] = mcp.IsChecked == true }));
        _contents.Children.Add(TitleRow(IconLabel("terminal", L("本地 MCP", "Local MCP"), prominent: true), mcp));
        if (settings.Flag("mcpEnabled"))
        {
            string endpoint = settings.Text("mcpEndpoint", "http://127.0.0.1:58629/mcp");
            _contents.Children.Add(Section(TitleRow(Body(endpoint), IconButton("copy", L("复制配置", "Copy config"), async () =>
            {
                var configuration = await Execute("mcp_config", []);
                await Execute("clipboard_local_write", new() { ["text"] = configuration?.ToJsonString(), ["sensitive"] = true });
                Report(L("已复制本地 MCP 配置", "Copied local MCP configuration"));
            }))));
        }
        if (Has(ClientFeatures.Pet))
        {
            var autostart = new CheckBox { Content = L("登录后启动", "Start at login"), IsChecked = settings.Flag("runAtLogin") };
            autostart.Click += async (_, _) => await Act(() =>
                Change("settings_save", new() { ["runAtLogin"] = autostart.IsChecked == true }));
            _contents.Children.Add(autostart);
            _contents.Children.Add(Row(
                Action(L("显示桌宠", "Show desktop pet"), async () => await Execute("pet_set", new() { ["visible"] = true })),
                Action(L("隐藏桌宠", "Hide desktop pet"), async () => await Execute("pet_set", new() { ["visible"] = false }))));
        }

        _contents.Children.Add(TitleRow(Heading(L("语言", "Language")), Menu(_english ? "English" : "简体中文",
            ("简体中文", () => SetLanguageAsync(false)), ("English", () => SetLanguageAsync(true)))));
        if (_compact && Has(ClientFeatures.Commands)) _contents.Children.Add(
            Action(L("命令管理", "Commands"), () => { Navigate("commands"); return Task.CompletedTask; }));
        if (_compact && Has(ClientFeatures.Capture)) _contents.Children.Add(
            Action(L("屏幕捕获", "Capture"), () => { Navigate("capture"); return Task.CompletedTask; }));
        if (_compact && Has(ClientFeatures.Ssh)) _contents.Children.Add(
            Action(L("SSH 主机", "SSH hosts"), () => { Navigate("ssh"); return Task.CompletedTask; }));
    }

    private async Task ShowDevicesAsync()
    {
        var devices = await Execute("relay_devices", []) as JsonArray ?? [];
        _contents.Children.Clear();
        Header(L("已配对设备", "Paired devices"), Action(L("返回", "Back"), LoadAsync));
        foreach (var device in devices) _contents.Children.Add(Heading(device.Text("name", device.Text("id"))));
    }

    private async Task SetLanguageAsync(bool english)
    {
        await Execute("settings_save", new() { ["language"] = english ? "en" : "zh" });
        _english = english;
        await LoadAsync();
    }

    private Task PairForm(JsonNode? relay) => Form(L("连接中转服务器", "Connect to a relay"),
        values => Change("relay_pair", values),
        new Field("server", L("服务器地址", "Server address"), relay.Text("server")),
        new Field("deviceName", L("设备名称", "Device name"), relay.Text("deviceName", Environment.MachineName)),
        new Field("pairingCode", L("配对码", "Pairing code"), Secret: true));

    private Task Confirm(string message, Func<Task> action)
    {
        _editing = true;
        _contents.Children.Clear();
        Header(message, Action(L("取消", "Cancel"), LoadAsync));
        _contents.Children.Add(Action(L("确认", "Confirm"), action));
        return Task.CompletedTask;
    }

    private async Task RenderCommandsAsync(CancellationToken token)
    {
        var create = Action(L("新建命令", "Add command"), () => CommandForm(null), glyph: "plus");
        create.Classes.Add("primary");
        Header(L("命令管理", "Commands"), IconButton("sync", L("刷新", "Refresh"), LoadAsync), create);
        var commands = await Execute("commands_list", [], token) as JsonArray ?? [];
        if (_componentId is { } requested && commands.OfType<JsonObject>().FirstOrDefault(command =>
                command.Text("id", command.Text("name")) == requested) is { } requestedCommand)
        {
            _componentId = null;
            await CommandLogAsync(requestedCommand.Text("name"), new() { ["id"] = requested });
            return;
        }
        if (commands.Count == 0) _contents.Children.Add(Muted(L("暂无命令", "No commands")));
        var search = new TextBox { PlaceholderText = L("搜索命令", "Search commands") };
        search.Classes.Add("entry");
        search.MaxWidth = 420;
        search.HorizontalAlignment = HorizontalAlignment.Left;
        search.MinWidth = 228;
        var results = new StackPanel();
        if (commands.Count > 0)
        {
            _contents.Children.Add(search);
            _contents.Children.Add(_compact ? results : Table(results));
            if (!_compact)
                results.Children.Add(CommandColumns(ColumnLabel(L("名称", "Name")),
                    ColumnLabel(L("命令", "Command")), ColumnLabel(L("状态", "State")), new Border()));
        }
        var summaries = new List<(string Name, string Command, Control Row)>();
        foreach (var command in commands.OfType<JsonObject>())
        {
            var id = command.Text("id", command.Text("name"));
            var args = new JsonObject { ["id"] = id };
            var summary = new StackPanel { Spacing = 4 };
            summary.Children.Add(IconLabel("terminal", command.Text("name"), prominent: true));
            if (_compact) summary.Children.Add(Muted(command.Text("command")));
            string state = command.Text("state");
            var stateText = state.ToLowerInvariant() switch
            {
                "running" => L("运行中", "Running"),
                "stopped" => L("已停止", "Stopped"),
                "starting" => L("启动中", "Starting"),
                "stopping" => L("停止中", "Stopping"),
                "faulted" or "failed" => L("异常", "Failed"),
                _ => state
            };
            var badge = new Border { Child = Muted(stateText), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center };
            badge.Classes.Add("chip");
            bool running = state.Equals("Running", StringComparison.OrdinalIgnoreCase);
            badge.Classes.Set("live", running);
            if (_compact && state.Length > 0) summary.Children.Add(badge);
            var choices = new List<(string Label, Func<Task> Action)>
            {
                (L("启动", "Start"), () => Change("command_start", args)),
                (L("停止", "Stop"), () => Change("command_stop", args)),
                (L("重启", "Restart"), () => Change("command_restart", args)),
                (L("日志", "Logs"), () => CommandLogAsync(command.Text("name"), args)),
                (L("编辑", "Edit"), () => CommandForm(command)),
                (L("删除", "Delete"), () => Confirm(L("删除命令？", "Delete command?"), () => Change("command_delete", args)))
            };
            if (Uri.TryCreate(command.Text("openUrl"), UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                choices.Insert(3, (L("打开网页", "Open service URL"), async () =>
                    await RequireTopLevel().Launcher.LaunchUriAsync(url)));
            var startStop = IconButton(running ? "stop" : "play",
                running ? L("停止", "Stop") : L("启动", "Start"),
                () => Change(running ? "command_stop" : "command_start", args));
            var actions = Row(startStop, Menu("⋯", choices.ToArray()));
            var row = _compact ? Section(TitleRow(summary, actions))
                : CommandColumns(summary, Muted(command.Text("command")), badge, actions);
            summaries.Add((command.Text("name"), command.Text("command"), row));
            results.Children.Add(row);
        }
        search.TextChanged += (_, _) =>
        {
            var pattern = search.Text ?? "";
            foreach (var item in summaries)
                item.Row.IsVisible = item.Name.Contains(pattern, StringComparison.CurrentCultureIgnoreCase) ||
                                     item.Command.Contains(pattern, StringComparison.CurrentCultureIgnoreCase);
        };
    }

    private static TextBlock ColumnLabel(string value)
    {
        var label = new TextBlock { Text = value };
        label.Classes.Add("column-label");
        return label;
    }

    private static Border CommandColumns(Control name, Control command, Control state, Control actions)
    {
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("1.1*,1.7*,112,76") };
        var cells = new[] { name, command, state, actions };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            cells[i].VerticalAlignment = VerticalAlignment.Center;
            columns.Children.Add(cells[i]);
        }
        var row = Section(columns);
        row.Padding = new Thickness(12, 5, 8, 5);
        row.MinHeight = 45;
        return row;
    }

    private async Task CommandForm(JsonObject? command)
    {
        var fields = new List<Field>
        {
            new("name", L("名称", "Name"), command.Text("name")),
            new("command", L("命令", "Command"), command.Text("command"), Multiline: true),
            new("workingDirectory", L("工作目录", "Working directory"), command.Text("workingDirectory"))
        };
        if (OperatingSystem.IsWindows())
        {
            fields.Add(new("openUrl", L("服务地址", "Service URL"), command.Text("openUrl")));
            fields.Add(new("stopCommand", L("停止命令", "Stop command"), command.Text("stopCommand")));
            fields.Add(new("stopTimeoutSeconds", L("停止等待秒数", "Stop timeout in seconds"),
                command.Integer("stopTimeoutSeconds", 5).ToString(CultureInfo.InvariantCulture)));
            fields.Add(new("startWithManager", L("随应用启动", "Start with the app"),
                command.Flag("startWithManager") ? "true" : "false", Toggle: true));
            fields.Add(new("restartOnUnexpectedExit", L("异常退出后重启", "Restart after unexpected exit"),
                command.Flag("restartOnUnexpectedExit") ? "true" : "false", Toggle: true));
        }
        await Form(L("命令配置", "Command configuration"), values =>
        {
            if (values.ContainsKey("stopTimeoutSeconds"))
                values["stopTimeoutSeconds"] = int.Parse(values.RequireText("stopTimeoutSeconds"), CultureInfo.InvariantCulture);
            var id = command.Text("id", command.Text("name"));
            if (id.Length > 0) values["id"] = id;
            return Change("command_save", values);
        }, fields.ToArray());
        if (OperatingSystem.IsWindows() && command is not null)
            _contents.Children.Add(Action(L("环境变量", "Environment variables"), () => EnvironmentForm(command)));
    }

    private Task EnvironmentForm(JsonObject command)
    {
        var entries = command.Array("environment").OfType<JsonObject>().ToArray();
        _editing = true;
        _contents.Children.Clear();
        Header(L("环境变量", "Environment variables"), Action("+", () => EditVariable(null),
            L("添加变量", "Add variable")), Action(L("返回", "Back"), () => CommandForm(command)));
        foreach (var entry in entries)
        {
            _contents.Children.Add(Section(TitleRow(Heading(entry.Text("name")), Menu("⋯",
                (L("编辑", "Edit"), () => EditVariable(entry)),
                (L("删除", "Delete"), () => Confirm(L("删除变量？", "Delete this variable?"),
                    () => SaveVariables(entries.Where(other => other != entry))))))));
        }
        return Task.CompletedTask;

        Task EditVariable(JsonObject? entry) => Form(L("环境变量", "Environment variable"), values =>
        {
            var value = values.Text("value");
            if (values.Flag("isSecret") && entry?.Flag("isSecret") == true && value.Length == 0) values["value"] = null;
            return SaveVariables(entries.Where(other => other != entry).Append(values));
        }, new Field("name", L("名称", "Name"), entry.Text("name")),
            new Field("value", L("值", "Value"), entry.Flag("isSecret") ? "" : entry.Text("value"), Secret: true),
            new Field("isSecret", L("加密保存", "Encrypt on this device"), entry.Flag("isSecret") ? "true" : "false", Toggle: true));

        Task SaveVariables(IEnumerable<JsonObject> variables) => Change("command_save", new()
        {
            ["id"] = command.Text("id"), ["name"] = command.Text("name"), ["command"] = command.Text("command"),
            ["environment"] = new JsonArray(variables.Select(variable => variable.DeepClone()).ToArray())
        });
    }

    private async Task CommandLogAsync(string name, JsonObject args)
    {
        string transcript = TextOf(await Execute("command_log", args));
        _editing = true;
        _contents.Children.Clear();
        Header(name, Action(L("返回", "Back"), LoadAsync),
            Action(L("复制", "Copy"), async () => await Execute("clipboard_local_write", new() { ["text"] = transcript })));
        var logControls = new StackPanel { Spacing = 6 };
        var filter = new TextBox { PlaceholderText = L("搜索日志", "Filter logs") };
        var follow = new CheckBox { Content = L("跟随", "Follow"), IsChecked = true };
        var wrap = new CheckBox { Content = L("换行", "Wrap"), IsChecked = true };
        var text = Body(transcript);
        text.FontFamily = new Avalonia.Media.FontFamily("monospace");
        logControls.Children.Add(filter);
        logControls.Children.Add(Row(follow, wrap));
        logControls.Children.Add(text);
        _contents.Children.Add(logControls);
        void Filter()
        {
            var pattern = filter.Text ?? "";
            var displayed = pattern.Length == 0 ? transcript : string.Join(Environment.NewLine,
                transcript.Split('\n').Where(line => line.Contains(pattern, StringComparison.CurrentCultureIgnoreCase)));
            if (text.Text == displayed) return;
            text.Text = displayed;
            if (follow.IsChecked == true && pattern.Length == 0)
                Dispatcher.UIThread.Post(() => _viewport.Offset = new Vector(_viewport.Offset.X, _viewport.Extent.Height));
        }
        filter.TextChanged += (_, _) => Filter();
        wrap.Click += (_, _) =>
        {
            text.TextWrapping = wrap.IsChecked == true ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;
            _viewport.HorizontalScrollBarVisibility = wrap.IsChecked == true
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        bool running = false;
        timer.Tick += async (_, _) =>
        {
            if (running || follow.IsChecked != true) return;
            running = true;
            try
            {
                transcript = TextOf(await Execute("command_log", args));
                if (logControls.Parent is not null) Filter();
            }
            catch (Exception error) { Report(error.Message); }
            finally { running = false; }
        };
        logControls.DetachedFromVisualTree += (_, _) => timer.Stop();
        timer.Start();
    }

    private async Task RenderCaptureAsync(CancellationToken token)
    {
        var take = Action(L("截图", "Screenshot"), () => Change("capture_take", []), glyph: "capture");
        take.Classes.Add("primary");
        Header(L("屏幕捕获", "Capture"), take,
            Menu("⋯",
                (L("选择截图区域", "Select capture region"), () => Change("capture_select", [])),
                (L("区域截图", "Region capture"), () => Form(L("区域截图", "Region capture"), values =>
                {
                    var area = new JsonObject();
                    foreach (string key in new[] { "x", "y", "width", "height" })
                        area[key] = int.Parse(values.RequireText(key), CultureInfo.InvariantCulture);
                    return Change("capture_take", area);
                }, new Field("x", "X"), new Field("y", "Y"), new Field("width", L("宽", "Width")),
                    new Field("height", L("高", "Height")))),
                (L("开始录屏", "Start recording"), () => Change("capture_record_start", [])),
                (L("结束录屏", "Stop recording"), () => Change("capture_record_stop", []))));
        var captures = await Execute("capture_list", [], token) as JsonArray ?? [];
        foreach (var entry in captures)
        {
            string path = TextOf(entry, "path");
            _contents.Children.Add(Section(TitleRow(IconLabel("file", Path.GetFileName(path), prominent: true), Menu("⋯",
                (L("打开", "Open"), async () => await Execute("capture_open", new() { ["path"] = path })),
                (L("标注", "Annotate"), () => Change("capture_annotate", new() { ["path"] = path })),
                (L("贴图", "Pin"), async () => await Execute("capture_pin", new() { ["path"] = path })),
                (L("识别文字", "OCR"), async () =>
                {
                    var result = await Execute("capture_ocr", new() { ["path"] = path, ["language"] = _english ? "eng" : "chi_sim+eng" });
                    _contents.Children.Clear();
                    Header(L("识别文字", "OCR"), Action(L("返回", "Back"), LoadAsync));
                    _contents.Children.Add(Body(TextOf(result)));
                })))));
        }
    }

    private async Task RenderSshAsync(CancellationToken token)
    {
        Header(L("SSH 主机", "SSH hosts"), Action("+", () => HostForm(null), L("添加主机", "Add host")),
            Menu("⋯",
                (L("导入密钥", "Import key"), () => Form(L("导入加密 SSH 密钥", "Import encrypted SSH key"), values =>
                    Change("ssh_import", values), new Field("path", L("私钥路径", "Private key path")),
                        new Field("name", L("名称", "Name")), new Field("passphrase", L("同步口令", "Sync passphrase"), Secret: true))),
                (L("密钥", "Keys"), ShowKeysAsync)));
        var workspace = await Execute("workspace_read", [], token);
        foreach (var host in workspace.Array("sshHosts").OfType<JsonObject>())
        {
            string id = host.Text("id");
            var entry = new StackPanel { Spacing = 4 };
            entry.Children.Add(IconLabel("ssh", host.Text("name"), prominent: true));
            entry.Children.Add(Muted(HostAddress(host)));
            _contents.Children.Add(Section(TitleRow(entry, Menu("⋯",
                (L("在空间中连接", "Connect in space"), () => ConnectHostAsync(host)),
                (L("编辑", "Edit"), () => HostForm(host)),
                (L("删除", "Delete"), () => Confirm(L("删除 SSH 主机？", "Delete SSH host?"),
                    () => Change("ssh_delete", new() { ["id"] = id })))))));
        }
    }

    private static string HostAddress(JsonObject host) => $"{host.Text("username")}@{host.Text("address")}:{host.Integer("port", 22)}";

    private Task HostForm(JsonObject? host) => Form(L("SSH 主机", "SSH host"), values =>
    {
        if (host is not null) values["id"] = host.Text("id");
        values["port"] = int.Parse(values.RequireText("port"), CultureInfo.InvariantCulture);
        return Change("ssh_save", values);
    }, new Field("name", L("名称", "Name"), host.Text("name")),
        new Field("address", L("地址", "Address"), host.Text("address")),
        new Field("username", L("用户", "User"), host.Text("username")),
        new Field("port", L("端口", "Port"), host.Integer("port", 22).ToString(CultureInfo.InvariantCulture)),
        new Field("keyId", L("密钥 ID", "Key ID"), host.Text("keyId")));

    private async Task ShowKeysAsync()
    {
        var keys = await Execute("ssh_keys", []) as JsonArray ?? [];
        _contents.Children.Clear();
        Header(L("SSH 密钥", "SSH keys"), Action(L("返回", "Back"), LoadAsync));
        foreach (var key in keys)
        {
            var id = key.Text("id");
            _contents.Children.Add(Section(TitleRow(Heading(key.Text("name", id)),
                Action(L("删除", "Delete"), () => Confirm(L("删除密钥？", "Delete key?"),
                    () => Change("ssh_key_delete", new() { ["id"] = id }))))));
        }
    }

    private async Task ConnectHostAsync(JsonObject host)
    {
        var document = await Execute("workspace_read", []);
        var spaceId = document.Text("activeSpaceId");
        if (spaceId.Length == 0) throw new InvalidOperationException(L("请先新建空间。", "Create a space first."));
        await Execute("item_save", new() { ["spaceId"] = spaceId, ["item"] = NewItem("terminal", host.Text("name"), new()
            { ["shell"] = "ssh", ["hostId"] = host.Text("id") }) });
        Navigate("workspace", spaceId);
    }
}
