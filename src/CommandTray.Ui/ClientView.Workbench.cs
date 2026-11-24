using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private static Control PageHeading(string title, Control? caption = null, Control? actions = null)
    {
        var heading = Heading(title);
        heading.Classes.Add("workbench-heading");
        var copy = new StackPanel { Spacing = 3, Children = { heading } };
        if (caption is not null) copy.Children.Add(caption);
        if (actions is null) return copy;
        return TitleRow(copy, actions);
    }

    private static Border WorkbenchPanel(string title, string glyph, Control content, Control? actions = null)
    {
        var heading = IconLabel(glyph, title, prominent: true);
        var top = new Border { Child = actions is null ? heading : TitleRow(heading, actions) };
        top.Classes.Add("workbench-panel-heading");
        return Table(new StackPanel { Children = { top, content } });
    }

    private static Grid WorkbenchSplit(Control first, Control second, string columns = "*,*")
    {
        Grid.SetColumn(second, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(columns),
            ColumnSpacing = 15,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { first, second }
        };
    }

    private Button WorkspaceConflictActions() => Action(L("比较版本", "Compare versions"), CompareWorkspaceAsync, glyph: "history");

    private async Task CompareWorkspaceAsync()
    {
        var cancellation = _loading?.Token ?? CancellationToken.None;
        var conflict = await Execute("sync_conflict", [], cancellation);
        if (conflict is not JsonObject versions)
        {
            await LoadAsync();
            return;
        }
        var local = await Execute("workspace_read", [], cancellation) as JsonObject ?? versions.Object("local");
        var remote = versions.Object("remote");
        _editing = true;
        _workspaceGrid.IsVisible = false;
        _contents.Children.Clear();
        Header(L("同步冲突", "Sync conflict"), Action(L("返回", "Back"), LoadAsync));
        _viewport.Offset = default;
        _contents.Children.Add(PageHeading(L("比较版本", "Compare versions")));
        var localView = VersionPanel(L("本机", "This device"), local);
        var remoteView = VersionPanel(L("中转", "Relay"), remote);
        if (_compact)
        {
            _contents.Children.Add(localView);
            _contents.Children.Add(remoteView);
        }
        else _contents.Children.Add(WorkbenchSplit(localView, remoteView));
        _contents.Children.Add(new WrapPanel
        {
            ItemSpacing = 6, LineSpacing = 4, Children =
            {
                PrimaryAction(L("保留本机", "Keep local"), () => Change("sync_resolve", new() { ["choice"] = "local" })),
                Action(L("使用中转", "Use relay version"), () => Confirm(L("采用中转版本？", "Use the relay version?"),
                    () => Change("sync_resolve", new() { ["choice"] = "remote" }))),
                Action(L("导出两份", "Export both"), async () =>
                {
                    var file = await RequireTopLevel().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        SuggestedFileName = "workspace-conflict.json", DefaultExtension = "json", ShowOverwritePrompt = true
                    });
                    if (file is null) return;
                    var snapshot = new JsonObject
                    {
                        ["local"] = local.DeepClone(), ["remote"] = remote.DeepClone(),
                        ["remoteRevision"] = versions["remoteRevision"]?.DeepClone()
                    };
                    await using var stream = await file.OpenWriteAsync();
                    if (stream.CanSeek) stream.SetLength(0);
                    await JsonSerializer.SerializeAsync(stream, snapshot, new JsonSerializerOptions { WriteIndented = true });
                    Report(L("已导出", "Exported"));
                }, glyph: "download")
            }
        });

        Border VersionPanel(string title, JsonObject document)
        {
            var content = new StackPanel { Spacing = 8, Margin = new Thickness(13) };
            foreach (var space in document.Array("spaces").OfType<JsonObject>())
            {
                content.Children.Add(Heading(space.Text("name")));
                foreach (var item in space.Array("items").OfType<JsonObject>())
                {
                    var data = item.Object("data");
                    var summary = item.Text("kind") switch
                    {
                        "note" or "notes" => data.Text("text"),
                        "todo" or "tasks" => string.Join("\n", data.Array("tasks").OfType<JsonObject>()
                            .Select(task => (task.Flag("completed") ? "✓ " : "○ ") + task.Text("text"))),
                        "board" or "whiteboard" => L($"{data.Array("strokes").Count} 个对象", $"{data.Array("strokes").Count} objects"),
                        _ => ""
                    };
                    content.Children.Add(Muted(item.Text("title", item.Text("kind"))));
                    if (summary.Length > 0)
                    {
                        var text = Body(summary);
                        text.MaxHeight = 180;
                        content.Children.Add(text);
                    }
                }
            }
            if (content.Children.Count == 0) content.Children.Add(Muted(L("暂无空间", "No spaces")));
            return WorkbenchPanel(title, "spaces", content);
        }
    }

    private async Task ExportConfigurationAsync()
    {
        var file = await RequireTopLevel().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "CommandTray-services.json", DefaultExtension = "json", ShowOverwritePrompt = true
        });
        if (file is null) return;
        var staged = TemporaryFile("services.json");
        try
        {
            await Execute("configuration_export", new() { ["path"] = staged });
            await using var source = File.OpenRead(staged);
            await using var target = await file.OpenWriteAsync();
            if (target.CanSeek) target.SetLength(0);
            await source.CopyToAsync(target);
            Report(L("已导出服务配置，敏感值未包含", "Service configuration exported without secret values"));
        }
        finally { RemoveTemporary(staged); }
    }

    private async Task ImportConfigurationAsync()
    {
        var files = await RequireTopLevel().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });
        if (files.FirstOrDefault() is not { } selected) return;
        if (selected.TryGetLocalPath() is not string path) throw new IOException(L("无法读取此文件。", "Could not read this file."));
        var arguments = new JsonObject { ["path"] = path };
        var envelope = await Execute("configuration_preview", arguments);
        var preview = envelope.Array("services");
        arguments["sha256"] = envelope.Text("sha256");
        _editing = true;
        _contents.Children.Clear();
        _viewport.Offset = default;
        Header(L("导入服务配置", "Import service configuration"), Action(L("取消", "Cancel"), LoadAsync));
        var commands = new StackPanel { Spacing = 6, Margin = new Thickness(13) };
        commands.Children.Add(Body(L("导入会备份并替换当前服务配置。", "Import backs up and replaces the current service configuration.")));
        foreach (var name in preview.OfType<JsonValue>()) commands.Children.Add(Muted(name.GetValue<string>()));
        _contents.Children.Add(WorkbenchPanel(L($"{preview.Count} 条服务配置", $"{preview.Count} service definitions"), "folder-open", commands,
            PrimaryAction(L("备份并导入", "Back up and import"), async () =>
            {
                await Change("configuration_import", arguments);
                Report(L("服务配置已导入", "Service configuration imported"));
            })));
    }
}
