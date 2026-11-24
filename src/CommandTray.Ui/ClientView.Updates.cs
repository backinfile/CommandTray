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
        var version = Muted(info.Text("currentVersion"));
        var state = Muted("");
        var source = Muted("");
        var progress = new ProgressBar { Maximum = 100, Height = 5, Margin = new Avalonia.Thickness(13, 4, 13, 9) };
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
            state.Text = L("正在检查…", "Checking…");
            await Execute("update_check", []);
        }, glyph: "sync");
        var cancel = Action(L("取消下载", "Cancel download"),
            async () => await Execute("update_cancel", [], cancellation));
        var actions = new WrapPanel { ItemSpacing = 5, LineSpacing = 4, Margin = new Avalonia.Thickness(13, 9),
            Children = { check, cancel, apply } };

        var panel = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                SettingRow(L("已安装版本", "Installed version"), version), SettingRow(label, automatic),
                SettingRow(L("更新状态", "Update status"), state), progress,
                SettingRow(L("更新源", "Update source"), source, "GitHub · ghfast.top · gh-proxy.com"),
                actions
            }
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        bool refreshing = false;
        timer.Tick += async (_, _) =>
        {
            if (refreshing || cancellation.IsCancellationRequested) return;
            refreshing = true;
            try { Refresh(await Execute("update_status", [], cancellation)); }
            catch (OperationCanceledException) { }
            catch (Exception error) { state.Text = error.Message; }
            finally { refreshing = false; }
        };
        panel.AttachedToVisualTree += (_, _) => timer.Start();
        panel.DetachedFromVisualTree += (_, _) => timer.Stop();
        Refresh(info);
        return panel;

        void Refresh(JsonNode? value)
        {
            string available = value.Text("availableVersion");
            string current = value.Text("state");
            apply.IsVisible = current == "ready";
            check.IsVisible = current is not ("ready" or "downloading");
            check.IsEnabled = current != "checking";
            cancel.IsVisible = current == "downloading";
            progress.IsVisible = current == "downloading";
            var total = value?["totalBytes"]?.GetValue<long>() ?? 0;
            var received = value?["downloadedBytes"]?.GetValue<long>() ?? 0;
            progress.IsIndeterminate = total <= 0;
            progress.Value = total > 0 ? Math.Clamp(100d * received / total, 0, 100) : 0;
            source.Text = value.Text("source", "—");
            state.Text = current switch
            {
                "checking" => L("正在检查…", "Checking…"),
                "downloading" => L($"正在下载 {available} · {progress.Value:N0}%", $"Downloading {available} · {progress.Value:N0}%"),
                "ready" => L($"{available} 已下载", $"{available} downloaded"),
                "current" => L("已是最新版本", "Up to date"),
                "unreachable" => L("暂时无法访问更新源", "Update sources are unavailable"),
                "downloadFailed" => L("下载失败，请重试", "Download failed. Try again."),
                "cancelled" => L("下载已取消", "Download cancelled"),
                "failed" => L("无法保存更新", "Could not save the update"),
                _ => L("尚未检查", "Not checked")
            };
        }
    }
}
