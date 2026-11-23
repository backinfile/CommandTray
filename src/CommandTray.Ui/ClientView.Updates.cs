using Avalonia.Controls;
using Avalonia.Threading;
using CommandTray.Core;
using System.Text.Json.Nodes;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private async Task<Control> UpdateSettingsAsync(CancellationToken cancellation)
    {
        var info = await Execute("update_status", [], cancellation);
        var title = Heading(L("版本更新", "Updates"));
        var version = Muted(info.Text("currentVersion"));
        var state = Muted("");
        var automatic = new ToggleSwitch { IsChecked = info.Flag("enabled"), OnContent = "", OffContent = "" };
        string label = L("自动更新", "Automatic updates");
        Avalonia.Automation.AutomationProperties.SetName(automatic, label);
        automatic.IsCheckedChanged += async (_, _) => await Act(async () =>
        {
            await Execute("update_set", new() { ["enabled"] = automatic.IsChecked == true }, cancellation);
        });

        var apply = PrimaryAction(OperatingSystem.IsAndroid() ? L("安装更新", "Install update") :
            L("重启并更新", "Restart and update"), async () =>
            {
                await Execute("update_install", [], cancellation);
            });
        var check = Action(L("检查更新", "Check for updates"), async () =>
        {
            Refresh(await Execute("update_check", [], cancellation));
        }, glyph: "sync");

        var panel = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                TitleRow(title, version), TitleRow(Muted(label), automatic), state,
                new WrapPanel { ItemSpacing = 4, Children = { check, apply } }
            }
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        bool refreshing = false;
        timer.Tick += async (_, _) =>
        {
            if (refreshing || cancellation.IsCancellationRequested) return;
            refreshing = true;
            try { Refresh(await Execute("update_status", [], cancellation)); }
            catch (OperationCanceledException) { }
            finally { refreshing = false; }
        };
        panel.AttachedToVisualTree += (_, _) => timer.Start();
        panel.DetachedFromVisualTree += (_, _) => timer.Stop();
        Refresh(info);
        return Section(panel);

        void Refresh(JsonNode? value)
        {
            string available = value.Text("availableVersion");
            string current = value.Text("state");
            apply.IsVisible = current == "ready";
            state.Text = current switch
            {
                "checking" => L("正在检查…", "Checking…"),
                "downloading" => L($"正在下载 {available}…", $"Downloading {available}…"),
                "ready" => L($"{available} 已下载", $"{available} downloaded"),
                "current" => L("已是最新版本", "Up to date"),
                "unreachable" => L("暂时无法访问更新源", "Update sources are unavailable"),
                "downloadFailed" => L("下载失败，请重试", "Download failed. Try again."),
                "failed" => L("无法保存更新", "Could not save the update"),
                _ => ""
            };
            state.IsVisible = state.Text.Length > 0;
        }
    }
}
