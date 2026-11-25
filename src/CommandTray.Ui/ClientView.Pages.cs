using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
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

    private void PairPrompt(JsonNode? relay) => _contents.Children.Add(EmptyState("files", L("尚未配对", "Unpaired"),
        PrimaryAction(L("连接中转服务器", "Connect to a relay"), () => PairForm(relay))));

    private async Task SetLanguageAsync(bool english)
    {
        await Execute("settings_save", new() { ["language"] = english ? "en" : "zh" });
        _english = english;
        await LoadAsync();
    }

    private Task PairForm(JsonNode? relay) => Form(L("连接中转服务器", "Connect to a relay"), L("配对", "Pair"),
        L("正在配对…", "Pairing…"), PairAsync,
        new Field("server", L("服务器地址", "Server address"), relay.Text("server")),
        new Field("deviceName", L("设备名称", "Device name"), relay.Text("deviceName", Environment.MachineName)),
        new Field("pairingCode", L("配对码", "Pairing code"), Secret: true));

    private async Task PairAsync(JsonObject values, CancellationToken cancellation)
    {
        foreach (var (key, message) in new[]
        {
            ("server", L("请输入服务器地址。", "Enter the server address.")),
            ("deviceName", L("请输入设备名称。", "Enter the device name.")),
            ("pairingCode", L("请输入配对码。", "Enter the pairing code."))
        })
        {
            if (string.IsNullOrWhiteSpace(values.Text(key))) throw new InvalidOperationException(message);
        }

        try { await Execute("relay_pair", values, cancellation); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(L("配对码不正确。", "The pairing code is incorrect."), error);
        }
        cancellation.ThrowIfCancellationRequested();
        await LoadAsync();
    }

    private Task CommandForm(JsonObject? command)
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
        var environment = OperatingSystem.IsWindows() && command is not null
            ? Action(L("环境变量", "Environment variables"), () => EnvironmentForm(command))
            : null;
        var title = command is null ? L("新建命令", "Add command") : L("编辑命令", "Edit command");
        return ShowForm(title, L("保存", "Save"), L("正在保存…", "Saving…"), (values, _) =>
        {
            if (values.ContainsKey("stopTimeoutSeconds"))
            {
                if (!int.TryParse(values.Text("stopTimeoutSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
                    throw new ArgumentException(L("停止等待秒数必须是整数。", "The stop timeout must be a whole number."));
                values["stopTimeoutSeconds"] = seconds;
            }
            var id = command.Text("id", command.Text("name"));
            if (id.Length > 0) values["id"] = id;
            return Change("command_save", values);
        }, fields.ToArray(), environment);
    }

    private Task EnvironmentForm(JsonObject command)
    {
        var list = new StackPanel();
        var back = CloseActionButton(L("返回", "Back"));
        var add = PrimaryAction(L("添加变量", "Add variable"), () => EditVariable(null), "plus");
        DrawVariables();
        OpenActionDialog(L("环境变量", "Environment variables"), list, Row(back, add), add);
        return Task.CompletedTask;

        IEnumerable<JsonObject> Entries() => command.Array("environment").OfType<JsonObject>();

        void DrawVariables()
        {
            list.Children.Clear();
            foreach (var entry in Entries())
                list.Children.Add(Section(TitleRow(Heading(entry.Text("name")), Menu("⋯",
                    (L("编辑", "Edit"), () => EditVariable(entry)),
                    (L("删除", "Delete"), () => Confirm(L("删除变量？", "Delete this variable?"),
                        () => SaveVariables(Entries().Where(other => other != entry))))))));
            if (list.Children.Count == 0) list.Children.Add(Muted(L("暂无变量", "No variables")));
        }

        Task EditVariable(JsonObject? entry) => Form(L("环境变量", "Environment variable"), values =>
        {
            var value = values.Text("value");
            if (values.Flag("isSecret") && entry?.Flag("isSecret") == true && value.Length == 0) values["value"] = null;
            return SaveVariables(Entries().Where(other => other != entry).Append(values));
        }, new Field("name", L("名称", "Name"), entry.Text("name")),
            new Field("value", L("值", "Value"), entry.Flag("isSecret") ? "" : entry.Text("value"), Secret: true),
            new Field("isSecret", L("加密保存", "Encrypt on this device"), entry.Flag("isSecret") ? "true" : "false", Toggle: true));

        async Task SaveVariables(IEnumerable<JsonObject> variables)
        {
            var saved = await Execute("command_save", new()
            {
                ["id"] = command.Text("id"),
                ["environment"] = new JsonArray(variables.Select(variable => variable.DeepClone()).ToArray())
            }, CurrentActionCancellation);
            command["environment"] = saved.Array("environment").DeepClone();
            DrawVariables();
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
        var initial = await Execute("ssh_keys", [], _loading?.Token ?? CancellationToken.None) as JsonArray ?? [];
        if (_actionOverlay.IsVisible) return;
        var list = new StackPanel();
        var back = CloseActionButton(L("返回", "Back"));
        DrawKeys(initial);
        OpenActionDialog(L("SSH 密钥", "SSH keys"), list, Row(back), back);

        void DrawKeys(JsonArray keys)
        {
            list.Children.Clear();
            foreach (var key in keys)
            {
                var id = key.Text("id");
                list.Children.Add(Section(TitleRow(Heading(key.Text("name", id)),
                    Action(L("删除", "Delete"), () => Confirm(L("删除密钥？", "Delete key?"), async () =>
                    {
                        await Change("ssh_key_delete", new() { ["id"] = id });
                        var current = await Execute("ssh_keys", [], CurrentActionCancellation) as JsonArray ?? [];
                        DrawKeys(current);
                    })))));
            }
            if (list.Children.Count == 0) list.Children.Add(Muted(L("暂无密钥", "No keys")));
        }
    }

    private async Task ConnectHostAsync(JsonObject host)
    {
        var document = await Execute("workspace_read", []);
        var spaceId = document.Text("activeSpaceId");
        if (spaceId.Length == 0) throw new InvalidOperationException(L("请先新建空间。", "Create a space first."));
        var created = await Execute("item_save", new() { ["spaceId"] = spaceId, ["item"] = NewItem("terminal", host.Text("name"), new()
            { ["shell"] = "ssh", ["hostId"] = host.Text("id") }) });
        Navigate("workspace", spaceId, created.Text("id"));
    }
}
