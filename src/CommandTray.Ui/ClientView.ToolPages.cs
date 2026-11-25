using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private string? _captureSelection;
    private string? _sshSelection;

    private async Task RenderCaptureAsync(CancellationToken token)
    {
        Header(L("屏幕捕获", "Screen capture"));
        var captures = (await ReadPageContent("capture_list", token) as JsonArray ?? [])
            .Select(entry => TextOf(entry, "path")).Where(path => path.Length > 0).ToArray();
        var recording = await Execute("capture_record_state", [], token);
        var isRecording = recording.Flag("recording");
        var capture = PrimaryAction(L("区域截图", "Capture region"), () => Change("capture_select", []), "capture");
        var record = Action(isRecording ? L("结束录屏", "Stop recording") : L("开始录屏", "Record screen"),
            () => Change(isRecording ? "capture_record_stop" : "capture_record_start", []), glyph: isRecording ? "stop" : "play");
        Field[] coordinates =
        [
            new("x", "X"), new("y", "Y"), new("width", L("宽", "Width")), new("height", L("高", "Height"))
        ];
        var more = Menu("⋯",
            (L("全屏截图", "Capture full screen"), () => Change("capture_take", [])),
            (L("输入区域坐标", "Enter region coordinates"), () => Form(L("区域截图", "Region capture"), values =>
            {
                var area = new JsonObject();
                foreach (var field in coordinates)
                {
                    if (!int.TryParse(values.Text(field.Key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                        throw new ArgumentException(L($"{field.Label} 必须是整数。", $"{field.Label} must be a whole number."));
                    if ((field.Key is "width" or "height") && value <= 0)
                        throw new ArgumentException(L($"{field.Label} 必须大于 0。", $"{field.Label} must be greater than zero."));
                    area[field.Key] = value;
                }
                return Change("capture_take", area);
            }, coordinates)));
        var heading = PageHeading(L("屏幕捕获", "Screen capture"),
            Muted(isRecording ? L("正在录制", "Recording") : L($"{captures.Length} 项", $"{captures.Length} captures")),
            _compact ? null : Row(capture, record, more));
        if (_compact) Header(L("屏幕捕获", "Screen capture"), more);
        _contents.Children.Add(heading);
        if (_compact) _contents.Children.Add(Row(capture, record));
        if (captures.Length == 0)
        {
            _contents.Children.Add(EmptyState("capture", L("暂无捕获", "No captures")));
            return;
        }

        _captureSelection = captures.Contains(_captureSelection) ? _captureSelection : captures[0];
        var list = new StackPanel();
        var detail = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var history = WorkbenchPanel(L("捕获历史", "Capture history"), "history", list);
        if (_compact) { _contents.Children.Add(history); _contents.Children.Add(detail); }
        else _contents.Children.Add(WorkbenchSplit(history, detail, "1*,1.55*"));
        Draw();

        void Draw()
        {
            list.Children.Clear();
            foreach (var path in captures)
            {
                var row = Action(Path.GetFileName(path), () => { _captureSelection = path; Draw(); if (_compact) detail.BringIntoView(); return Task.CompletedTask; },
                    glyph: Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "capture" : "play");
                row.Classes.Add("workbench-list-row");
                row.Classes.Set("selected", _captureSelection == path);
                list.Children.Add(row);
            }
            var selected = _captureSelection!;
            var arguments = new JsonObject { ["path"] = selected };
            bool picture = Path.GetExtension(selected).Equals(".png", StringComparison.OrdinalIgnoreCase);
            var body = new StackPanel { Spacing = 9, Margin = new Thickness(13) };
            if (picture)
            {
                try
                {
                    using var input = File.OpenRead(selected);
                    var bitmap = Bitmap.DecodeToWidth(input, 880);
                    var image = new Image { Source = bitmap, MaxHeight = 440, Stretch = Stretch.Uniform };
                    AutomationProperties.SetName(image, Path.GetFileName(selected));
                    image.DetachedFromVisualTree += (_, _) => bitmap.Dispose();
                    body.Children.Add(image);
                }
                catch (IOException) { body.Children.Add(Muted(L("无法预览文件", "Preview unavailable"))); }
                catch (ArgumentException) { body.Children.Add(Muted(L("无法预览文件", "Preview unavailable"))); }
            }
            var actions = new WrapPanel { ItemSpacing = 6, LineSpacing = 4,
                Children = { Action(L("打开", "Open"), async () => await Execute("capture_open", arguments, token)) } };
            if (picture)
            {
                actions.Children.Add(Action(L("标注", "Annotate"), () => Change("capture_annotate", arguments)));
                actions.Children.Add(Action(L("贴图", "Pin"), async () => await Execute("capture_pin", arguments, token)));
                actions.Children.Add(Action(L("识别文字", "OCR"), async () =>
                {
                    var result = Muted(L("正在识别…", "Recognizing…"));
                    body.Children.Add(result);
                    try
                    {
                        var reply = await Execute("capture_ocr", new() { ["path"] = selected, ["language"] = _english ? "eng" : "chi_sim+eng" }, token);
                        int at = body.Children.IndexOf(result);
                        if (at >= 0) body.Children[at] = Body(TextOf(reply));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { body.Children.Remove(result); }
                    catch (Exception error) { result.Text = error.Message; }
                }));
            }
            body.Children.Add(actions);
            detail.Content = WorkbenchPanel(Path.GetFileName(selected), picture ? "capture" : "play", body);
        }
    }

    private async Task RenderSshAsync(CancellationToken token)
    {
        Header(L("SSH 主机", "SSH hosts"));
        var workspace = await ReadPageContent("workspace_read", token);
        var hosts = workspace.Array("sshHosts").OfType<JsonObject>().ToArray();
        var add = PrimaryAction(L("添加主机", "Add host"), () => HostForm(null), "plus");
        var keys = Menu("⋯",
            (L("导入密钥", "Import key"), () => Form(L("导入加密 SSH 密钥", "Import encrypted SSH key"), values =>
                Change("ssh_import", values), new Field("path", L("私钥路径", "Private key path")),
                new Field("name", L("名称", "Name")), new Field("passphrase", L("同步口令", "Sync passphrase"), Secret: true))),
            (L("密钥", "Keys"), ShowKeysAsync));
        _contents.Children.Add(PageHeading("SSH", Muted(L($"{hosts.Length} 台主机", $"{hosts.Length} hosts")), Row(add, keys)));
        if (hosts.Length == 0)
        {
            _contents.Children.Add(EmptyState("ssh", L("暂无主机", "No hosts")));
            return;
        }

        var list = new StackPanel();
        var detail = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _sshSelection = hosts.Any(host => host.Text("id") == _sshSelection) ? _sshSelection : hosts[0].Text("id");
        var listing = WorkbenchPanel(L("主机", "Hosts"), "ssh", list);
        if (_compact) { _contents.Children.Add(listing); _contents.Children.Add(detail); }
        else _contents.Children.Add(WorkbenchSplit(listing, detail));
        RenderHosts();

        void RenderHosts()
        {
            list.Children.Clear();
            foreach (var host in hosts)
            {
                var row = Action(host.Text("name"), () =>
                {
                    _sshSelection = host.Text("id");
                    RenderHosts();
                    if (_compact) detail.BringIntoView();
                    return Task.CompletedTask;
                });
                row.Content = new StackPanel { Spacing = 4, Children = { Heading(host.Text("name")), Muted(HostAddress(host)) } };
                row.Classes.Add("workbench-list-row");
                row.Classes.Set("selected", _sshSelection == host.Text("id"));
                list.Children.Add(row);
            }
            var current = hosts.First(host => host.Text("id") == _sshSelection);
            var address = new StackPanel { Children =
            {
                SettingRow(L("地址", "Address"), Muted(current.Text("address"))),
                SettingRow(L("用户名", "Username"), Muted(current.Text("username"))),
                SettingRow(L("端口", "Port"), Muted(current.Integer("port", 22).ToString(CultureInfo.InvariantCulture))),
                SettingRow(L("身份", "Identity"), Muted(current.Text("keyId") is { Length: > 0 } id ? id : L("系统 SSH 配置", "System SSH configuration")))
            }};
            detail.Content = WorkbenchPanel(current.Text("name"), "ssh", address, Row(
                PrimaryAction(L("在空间中连接", "Connect in space"), () => ConnectHostAsync(current)),
                Menu("⋯", (L("编辑", "Edit"), () => HostForm(current)),
                    (L("删除", "Delete"), () => Confirm(L("删除 SSH 主机？", "Delete SSH host?"),
                        () => Change("ssh_delete", new() { ["id"] = current.Text("id") }))))));
        }
    }
}
