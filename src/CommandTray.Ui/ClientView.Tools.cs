using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private sealed record ToolGroup(string Title, (string Id, string Zh, string En)[] Pages);

    private IEnumerable<ToolGroup> ToolGroups()
    {
        yield return new(L("设备与共享", "Devices and sharing"),
            [("files", "共享文件", "Shared files"), ("clipboard", "剪贴板", "Clipboard")]);

        var development = new List<(string Id, string Zh, string En)>();
        if (Has(ClientFeatures.Commands)) development.Add(("commands", "命令管理", "Commands"));
        if (Has(ClientFeatures.Ssh) || OperatingSystem.IsAndroid()) development.Add(("ssh", "SSH 主机", "SSH hosts"));
        if (development.Count > 0) yield return new(L("开发与运维", "Development and operations"), development.ToArray());

        var desktop = new List<(string Id, string Zh, string En)>();
        if (Has(ClientFeatures.Capture)) desktop.Add(("capture", "截图与录屏", "Screen capture"));
        if (Has(ClientFeatures.Pet)) desktop.Add(("pet", "桌宠", "Desktop pet"));
        if (desktop.Count > 0) yield return new(L("屏幕与桌面", "Screen and desktop"), desktop.ToArray());

        yield return new(L("实用工具", "Utilities"),
        [
            ("text-tools", "文本转换", "Text conversion"),
            ("text-compare", "文本对比", "Text comparison"),
            ("timestamp", "时间戳", "Timestamp"),
            ("qr", "二维码", "QR code"),
            ("checksum", "文件校验", "File checksum")
        ]);
    }

    private void RenderToolsOverview()
    {
        Header(L("工具总览", "All tools"));
        var left = new StackPanel { Spacing = 12 };
        var right = _compact ? left : new StackPanel { Spacing = 12 };
        foreach (var group in ToolGroups())
            AddGroup(group.Pages[0].Id == "text-tools" ? right : left, group.Title,
                group.Pages.Select(page => ToolButton(L(page.Zh, page.En), PageGlyph(page.Id), () =>
            {
                Navigate(page.Id);
                return Task.CompletedTask;
            })));
        AddGroup(right, L("工作区组件", "Workspace panels"), WorkspacePanelKinds().Select(panel =>
            ToolButton(panel.Label, ComponentGlyph(panel.Id), () => AddPanelFromToolsAsync(panel.Id, panel.Name))));
        _contents.Children.Add(_compact ? left : WorkbenchSplit(left, right));

        void AddGroup(StackPanel column, string title, IEnumerable<Button> buttons)
        {
            var rows = new StackPanel { Spacing = 3, Margin = new Thickness(6, 4) };
            rows.Children.AddRange(buttons);
            column.Children.Add(WorkbenchPanel(title, "tool-grid", rows));
        }

        Button ToolButton(string label, string glyph, Func<Task> run)
        {
            var button = Action(label, run, glyph: glyph);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.MinHeight = 38;
            return button;
        }
    }

    private async Task AddPanelFromToolsAsync(string kind, string name)
    {
        var token = _loading?.Token ?? CancellationToken.None;
        try
        {
            var document = await Execute("workspace_read", [], token);
            SetSidebarSpaces(document);
            var active = document.Text("activeSpaceId");
            var space = _sidebarSpaces.FirstOrDefault(item => item.Id == _spaceId);
            if (space.Id is null) space = _sidebarSpaces.FirstOrDefault(item => item.Id == active);
            if (space.Id is null) space = _sidebarSpaces.FirstOrDefault();
            if (space.Id is null) await NewSpaceAsync();
            else await ItemForm(space.Id, kind, name, space.Name);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void RenderPet()
    {
        Header(L("桌宠", "Desktop pet"));
        _contents.Children.Add(WorkbenchPanel(L("栗尾", "Chestnut"), "pet", ActionBar(
            PrimaryAction(L("显示桌宠", "Show pet"), async () => await Execute("pet_set", new() { ["visible"] = true })),
            Action(L("隐藏桌宠", "Hide pet"), async () => await Execute("pet_set", new() { ["visible"] = false })))));
    }
}
