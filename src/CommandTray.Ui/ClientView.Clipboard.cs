using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private string? _clipboardSelection;
    private string _clipboardFilter = "all";
    private string _clipboardQuery = "";
    private sealed record ClipEntry(string Id, string Source, string Text, string Time, bool Local);

    private async Task RenderClipboardAsync(CancellationToken token)
    {
        Header(L("剪贴板", "Clipboard"));
        var relay = await ReadPageContent("relay_status", token);
        var settings = await Execute("settings_read", [], token);
        var preferences = await Execute("clipboard_preferences", new() { ["includeEntries"] = true }, token);
        bool paired = relay.Flag("paired");

        var actions = Row(
            IconButton("sync", L("刷新", "Refresh"), LoadAsync),
            Action(L("同步设置", "Sync settings"), () => OpenSettingsAsync("clip"), glyph: "settings"));
        if (paired)
        {
            actions.Children.Add(PrimaryAction(_compact ? "+" : L("发送文本", "Send text"), () =>
                Form(L("发送文本", "Send text"), L("发送", "Send"), L("正在发送…", "Sending…"),
                    async (values, cancellation) =>
                    {
                        await Execute("clipboard_send", new() { ["text"] = values.RequireText("text") }, cancellation);
                        await LoadAsync();
                        Report(L("已发送", "Sent"));
                    }, new Field("text", L("文本", "Text"), Multiline: true)), "send"));
            actions.Children.Add(IconButton("clipboard", L("发送本机剪贴板", "Send local clipboard"), async () =>
            {
                var local = TextOf(await Execute("clipboard_local_read", [], token));
                if (local.Length == 0) throw new InvalidOperationException(L("剪贴板没有文本。", "The clipboard has no text."));
                await Change("clipboard_send", new() { ["text"] = local });
                Report(L("已发送", "Sent"));
            }));
        }
        Header(L("剪贴板", "Clipboard"));
        _contents.Children.Add(PageHeading(L("剪贴板", "Clipboard"),
            Muted(settings.Flag("clipboardAutoSync") ? ClipboardDirectionLabel(preferences.Text("direction", "both")) :
                L("手动同步", "Manual sync")), actions));

        var entries = new List<ClipEntry>();
        if (paired)
        {
            try
            {
                var records = await ReadPageContent("clipboard_relay_history", token) as JsonArray ?? [];
                foreach (var item in records.OfType<JsonObject>())
                {
                    var id = $"{relay.Text("server")}\n{item.Text("deviceId")}\n{item["revision"]}";
                    var time = DateTimeOffset.TryParse(item.Text("updatedAt"), CultureInfo.InvariantCulture, out var date) &&
                        date > DateTimeOffset.UnixEpoch ? date.ToLocalTime().ToString("g") : "";
                    entries.Add(new ClipEntry(ClipId(id), item.Text("deviceName", L("未知设备", "Unknown device")),
                        item.Text("text"), time, false));
                }
            }
            catch (HttpRequestException error) { Report(error.Message); }
        }
        if (Has(ClientFeatures.ClipboardHistory))
        {
            var local = await Execute("clipboard_history", [], token) as JsonArray ?? [];
            entries.AddRange(local.Select(TextOfLocal).Where(entry => entry.Text.Length > 0));
        }
        var liveIds = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var pinnedIds = Keys(preferences, "pinned");
        var savedPins = preferences.Object("pinnedEntries");
        var missingPins = new JsonObject();
        foreach (var entry in entries)
            if (pinnedIds.Contains(entry.Id) && !savedPins.ContainsKey(entry.Id))
                missingPins[entry.Id] = Snapshot(entry);
        if (missingPins.Count > 0)
            preferences = await Execute("clipboard_preferences", new()
            {
                ["includeEntries"] = true,
                ["pinnedEntries"] = missingPins
            }, token);

        if (!paired)
            _contents.Children.Add(Section(TitleRow(Muted(L("中转未配对", "Relay not paired")),
                Action(L("配对", "Pair"), () => PairForm(relay)))));

        var query = new TextBox { Text = _clipboardQuery, PlaceholderText = L("搜索剪贴板", "Search clipboard"), MinWidth = 154 };
        AutomationProperties.SetName(query, L("搜索剪贴板", "Search clipboard"));
        var filters = new List<(string Key, string Label)>
        {
            ("all", L("所有记录", "All entries")), ("remote", L("同步记录", "Synced")),
            ("pinned", L("已置顶", "Pinned"))
        };
        if (Has(ClientFeatures.ClipboardHistory)) filters.Insert(1, ("local", L("本机", "Local")));
        filters.AddRange(CurrentEntries().Where(entry => !entry.Local).Select(entry => entry.Source)
            .Distinct(StringComparer.Ordinal).Select(source => ("device:" + source, source)));
        var filter = new ComboBox { ItemsSource = filters.Select(item => item.Label), MaxWidth = 210,
            SelectedIndex = Math.Max(0, filters.FindIndex(item => item.Key == _clipboardFilter)) };
        AutomationProperties.SetName(filter, L("筛选剪贴板", "Filter clipboard"));
        _contents.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { query, filter } });

        var list = new StackPanel();
        var inspector = new StackPanel { Spacing = 9, Margin = new Thickness(13) };
        var historyPanel = WorkbenchPanel(L("历史", "History"), "history", list);
        var detailPanel = WorkbenchPanel(L("内容", "Content"), "clipboard", inspector);
        if (_compact)
            _contents.Children.Add(historyPanel);
        else
            _contents.Children.Add(WorkbenchSplit(historyPanel, detailPanel));
        query.TextChanged += (_, _) => { _clipboardQuery = query.Text ?? ""; DrawHistory(); };
        filter.SelectionChanged += (_, _) =>
        {
            if ((uint)filter.SelectedIndex >= (uint)filters.Count) return;
            _clipboardFilter = filters[filter.SelectedIndex].Key;
            DrawHistory();
        };
        _clipboardFilter = filters[filter.SelectedIndex].Key;
        DrawHistory();

        ClipEntry TextOfLocal(JsonNode? value)
        {
            var text = TextOf(value);
            return new ClipEntry(ClipId("local\n" + text), L("本机", "This device"), text, "", true);
        }

        IEnumerable<ClipEntry> CurrentEntries()
        {
            foreach (var entry in entries) yield return entry;
            foreach (var (id, record) in preferences.Object("pinnedEntries"))
                if (!liveIds.Contains(id) && record is JsonObject)
                    yield return new ClipEntry(id, record.Text("source"), record.Text("text"), record.Text("time"), record.Flag("local"));
        }

        void DrawHistory()
        {
            var pins = Keys(preferences, "pinned");
            var hidden = Keys(preferences, "hidden");
            var search = _clipboardQuery.Trim();
            var visible = CurrentEntries().Where(entry => !hidden.Contains(entry.Id))
                .Where(entry => _clipboardFilter switch
                {
                    "local" => entry.Local, "remote" => !entry.Local, "pinned" => pins.Contains(entry.Id),
                    var key when key.StartsWith("device:", StringComparison.Ordinal) => !entry.Local && entry.Source == key[7..],
                    _ => true
                })
                .Where(entry => search.Length == 0 || entry.Text.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    entry.Source.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(entry => pins.Contains(entry.Id)).ToArray();
            if (!visible.Any(entry => entry.Id == _clipboardSelection)) _clipboardSelection = visible.FirstOrDefault()?.Id;
            list.Children.Clear();
            foreach (var entry in visible)
            {
                var preview = entry.Text.Length > 240 ? entry.Text[..(char.IsHighSurrogate(entry.Text[239]) ? 239 : 240)] + "…" : entry.Text;
                var summary = new StackPanel { Spacing = 4, Children =
                {
                    Muted(ClipSource(entry, pins.Contains(entry.Id))),
                    new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis }
                }};
                var open = Action(L("查看剪贴板内容", "View clipboard text"), () =>
                {
                    _clipboardSelection = entry.Id;
                    if (_compact)
                    {
                        _editing = true;
                        Header(L("剪贴板", "Clipboard"), Action(L("返回", "Back"), LoadAsync));
                        _contents.Children.Clear();
                        _viewport.Offset = default;
                        _contents.Children.Add(detailPanel);
                        DrawDetail(entry);
                    }
                    else DrawHistory();
                    return Task.CompletedTask;
                });
                open.Content = summary;
                open.Classes.Add("workbench-list-row");
                open.Classes.Set("selected", _clipboardSelection == entry.Id);
                AutomationProperties.SetName(open, preview);
                list.Children.Add(open);
            }
            if (visible.Length == 0) list.Children.Add(Muted(L("没有符合条件的记录", "No matching entries")));
            DrawDetail(visible.FirstOrDefault(entry => entry.Id == _clipboardSelection));
        }

        string ClipSource(ClipEntry entry, bool pinned) =>
            string.Join(" · ", new[] { pinned ? L("置顶", "Pinned") : "", entry.Source, entry.Time }.Where(part => part.Length > 0));

        void DrawDetail(ClipEntry? entry)
        {
            inspector.Children.Clear();
            if (entry is null)
            {
                inspector.Children.Add(Muted(L("请选择记录", "Select an entry")));
                return;
            }
            bool pinned = Keys(preferences, "pinned").Contains(entry.Id);
            var text = Body(entry.Text);
            var input = new Border { Child = new ScrollViewer { Content = text, MaxHeight = 300 }, Padding = new Thickness(10),
                MinHeight = 140 };
            input.Classes.Add("detail-text");
            inspector.Children.Add(TitleRow(Muted(ClipSource(entry, false)),
                Action(pinned ? L("取消置顶", "Unpin") : L("置顶", "Pin"), () =>
                    SetPreference(entry, new() { ["pin"] = !pinned }), glyph: "pin")));
            inspector.Children.Add(input);
            inspector.Children.Add(TitleRow(PrimaryAction(L("复制", "Copy"), async () =>
            {
                await Execute("clipboard_local_write", new() { ["text"] = entry.Text }, token);
                Report(L("已复制到本机剪贴板", "Copied to this device"));
            }, "copy"), Action(L("在本机隐藏", "Hide on this device"), () =>
                SetPreference(entry, new() { ["hide"] = true }))));
        }

        async Task SetPreference(ClipEntry entry, JsonObject change)
        {
            change["id"] = entry.Id;
            change["includeEntries"] = true;
            if (change.Flag("pin"))
                change["pinnedEntries"] = new JsonObject { [entry.Id] = Snapshot(entry) };
            preferences = await Execute("clipboard_preferences", change, token);
            if (_compact) await LoadAsync();
            else DrawHistory();
        }

        static HashSet<string> Keys(JsonNode? value, string key) => value.Array(key)
            .OfType<JsonValue>().Select(node => node.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        static JsonObject Snapshot(ClipEntry entry) => new()
        {
            ["source"] = entry.Source, ["text"] = entry.Text, ["time"] = entry.Time, ["local"] = entry.Local
        };
        static string ClipId(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private string ClipboardDirectionLabel(string direction) => direction switch
    {
        "send" => L("仅发送", "Send only"),
        "receive" => L("仅接收", "Receive only"),
        _ => L("双向同步", "Two-way sync")
    };
}
