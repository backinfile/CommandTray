using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private readonly List<Transfer> _transfers = [];
    private StackPanel? _transferRows;
    private bool _processingTransfers;
    private string _fileQuery = "";
    private string? _fileFolder;

    private enum TransferState { Queued, Running, Saving, Complete, Cancelled, Failed }

    private sealed class Transfer(string name, bool upload, Func<Transfer, IProgress<long>, CancellationToken, Task<bool>> run)
    {
        public string Name => name;
        public bool Upload => upload;
        public Func<Transfer, IProgress<long>, CancellationToken, Task<bool>> Run => run;
        public TransferState State { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public long Bytes { get; set; }
        public long? Total { get; set; }
        public string? Error { get; set; }
        public TextBlock? Label { get; set; }
        public ProgressBar? Progress { get; set; }
    }

    private async Task RenderFilesAsync(CancellationToken token)
    {
        Header(L("共享文件", "Shared files"));
        var relay = await ReadPageContent("relay_status", token);
        var queue = new StackPanel();
        _transferRows = queue;
        var transferPanel = WorkbenchPanel(L("传输队列", "Transfer queue"), "history", queue);
        transferPanel.IsVisible = _transfers.Count > 0;
        if (!relay.Flag("paired"))
        {
            PairPrompt(relay);
            _contents.Children.Add(transferPanel);
            DrawTransfers();
            return;
        }

        var upload = PrimaryAction(L("上传文件", "Upload files"), ChooseUploadAsync, "plus");
        var count = Muted("");
        _contents.Children.Add(PageActions(count, upload,
            MoreActions(L("共享文件列表操作", "Shared file list actions"), (L("刷新列表", "Refresh list"), LoadAsync))));
        _contents.Children.Add(transferPanel);
        DrawTransfers();

        var files = (await ReadPageContent("files_list", token) as JsonArray ?? []).OfType<JsonObject>().ToArray();
        count.Text = L($"{files.Length} 个文件", $"{files.Length} files");
        var search = new TextBox { Text = _fileQuery, PlaceholderText = L("搜索文件", "Search files"), MinWidth = 154 };
        AutomationProperties.SetName(search, L("搜索文件", "Search files"));
        var names = files.Select(file => file.Text("folder")).Distinct(StringComparer.Ordinal).Order().ToArray();
        var folders = new ComboBox { ItemsSource = new[] { L("所有目录", "All folders") }.Concat(
            names.Select(name => name.Length == 0 ? L("根目录", "Root") : name)) };
        folders.SelectedIndex = _fileFolder is null ? 0 : Array.IndexOf(names, _fileFolder) + 1;
        if (folders.SelectedIndex <= 0) _fileFolder = null;
        AutomationProperties.SetName(folders, L("目录", "Folder"));
        _contents.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { search, folders } });
        var rows = new StackPanel();
        _contents.Children.Add(WorkbenchPanel(L("共享文件", "Shared files"), "files", rows));
        search.TextChanged += (_, _) => { _fileQuery = search.Text ?? ""; DrawFiles(); };
        folders.SelectionChanged += (_, _) =>
        {
            _fileFolder = folders.SelectedIndex <= 0 ? null : names[folders.SelectedIndex - 1];
            DrawFiles();
        };
        DrawFiles();

        void DrawFiles()
        {
            rows.Children.Clear();
            var query = _fileQuery.Trim();
            var displayed = files.Where(file => (_fileFolder is null || _fileFolder == file.Text("folder")) &&
                new[] { file.Text("name"), file.Text("folder"), file.Text("deviceName") }
                    .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase)));
            foreach (var file in displayed)
            {
                var id = file.Text("id");
                var name = file.Text("name");
                var metadata = new[] { file.Text("folder"), FileSize(file["size"]), file.Text("deviceName"),
                    DateTimeOffset.TryParse(file.Text("modifiedAt"), out var modified) ? modified.ToLocalTime().ToString("g") : "" };
                var actions = Row(
                    Action(L("下载", "Download"), () => QueueDownloadAsync(file, relay.Text("deviceId")), glyph: "download"),
                    MoreActions(L("文件操作", "File actions"), (L("删除", "Delete"), () => Confirm(L("删除共享文件？", "Delete shared file?"),
                        () => Change("file_delete", new() { ["id"] = id }), L("删除文件", "Delete file")))));
                rows.Children.Add(SettingRow(name, actions, string.Join(" · ", metadata.Where(field => field.Length > 0))));
            }
            if (rows.Children.Count == 0)
                rows.Children.Add(SettingRow(files.Length == 0 ? L("暂无共享文件", "No shared files") :
                    L("没有符合条件的文件", "No matching files"), new Border()));
        }

        Task ChooseUploadAsync() => Form(L("上传文件", "Upload files"), L("选择文件", "Choose files"), async values =>
        {
            var inputs = await RequireTopLevel().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
            if (inputs.Count == 0) return;
            foreach (var file in inputs)
                Enqueue(new Transfer(file.Name, true, (job, progress, cancellation) =>
                    UploadOneAsync(file, values.Text("folder"), relay.Text("deviceId"), job, progress, cancellation)));
            _ = ProcessTransfersAsync();
            await LoadAsync();
        }, new Field("folder", L("共享目录", "Shared folder"), _fileFolder ?? ""));
    }

    private async Task<bool> UploadOneAsync(IStorageFile file, string folder, string deviceId, Transfer job,
        IProgress<long> progress, CancellationToken cancellation)
    {
        var path = TemporaryFile(file.Name);
        try
        {
            await using (var original = await file.OpenReadAsync())
            await using (var staged = File.Create(path))
            {
                if (original.CanSeek && original.Length > 128L * 1024 * 1024)
                    throw new InvalidDataException(L("文件大小上限为 128 MiB。", "The file limit is 128 MiB."));
                var buffer = new byte[64 * 1024];
                for (int n; (n = await original.ReadAsync(buffer, cancellation)) > 0;)
                {
                    if (staged.Length + n > 128L * 1024 * 1024)
                        throw new InvalidDataException(L("文件大小上限为 128 MiB。", "The file limit is 128 MiB."));
                    await staged.WriteAsync(buffer.AsMemory(0, n), cancellation);
                }
                job.Total = staged.Length;
            }
            await _backend.ExecuteAsync("file_upload", new()
            {
                ["name"] = file.Name, ["folder"] = folder, ["path"] = path, ["deviceId"] = deviceId
            }, progress, cancellation);
            return true;
        }
        finally { RemoveTemporary(path); }
    }

    private Task QueueDownloadAsync(JsonObject file, string deviceId)
    {
        var name = file.Text("name");
        Enqueue(new Transfer(name, false, async (job, progress, cancellation) =>
        {
            job.Total = file["size"]?.GetValue<long>();
            var path = TemporaryFile(name);
            try
            {
                await _backend.ExecuteAsync("file_download",
                    new() { ["id"] = file.Text("id"), ["path"] = path, ["deviceId"] = deviceId }, progress, cancellation);
                await using var source = File.OpenRead(path);
                if (job.Total is long total && source.Length != total)
                    throw new InvalidDataException(L("文件大小校验失败。", "The downloaded size does not match."));
                if (file.Text("sha256") is { Length: > 0 } expected)
                {
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellation));
                    if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(L("文件校验失败。", "The file checksum does not match."));
                    source.Position = 0;
                }
                cancellation.ThrowIfCancellationRequested();
                job.State = TransferState.Saving;
                DrawTransfers();
                if (TopLevel.GetTopLevel(this) is not { } top) return false;
                var destination = await top.StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions { SuggestedFileName = name, ShowOverwritePrompt = true });
                if (destination is null) return false;
                await using var output = await destination.OpenWriteAsync();
                if (output.CanSeek) output.SetLength(0);
                await source.CopyToAsync(output);
                return true;
            }
            finally { RemoveTemporary(path); }
        }));
        _ = ProcessTransfersAsync();
        return Task.CompletedTask;
    }

    private void Enqueue(Transfer job)
    {
        if (_transfers.Count >= 40)
            _transfers.RemoveAll(transfer => transfer.State is TransferState.Complete or TransferState.Cancelled);
        _transfers.Add(job);
        DrawTransfers();
    }

    private async Task ProcessTransfersAsync()
    {
        if (_processingTransfers) return;
        _processingTransfers = true;
        bool uploaded = false;
        try
        {
            while (_transfers.FirstOrDefault(item => item.State == TransferState.Queued) is { } job)
            {
                using var cancellation = new CancellationTokenSource();
                job.Cancellation = cancellation;
                job.State = TransferState.Running;
                job.Bytes = 0;
                job.Error = null;
                DrawTransfers();
                var progress = new Progress<long>(bytes =>
                {
                    if (job.Cancellation != cancellation || job.State != TransferState.Running) return;
                    job.Bytes = bytes;
                    UpdateTransfer(job);
                });
                try
                {
                    var completed = await job.Run(job, progress, cancellation.Token);
                    job.State = completed ? TransferState.Complete : TransferState.Cancelled;
                    uploaded |= completed && job.Upload;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { job.State = TransferState.Cancelled; }
                catch (Exception error) { job.State = TransferState.Failed; job.Error = error.Message; }
                finally { job.Cancellation = null; DrawTransfers(); }
            }
        }
        finally { _processingTransfers = false; }
        if (uploaded && _page == "files" && !_editing) await LoadAsync();
    }

    private void DrawTransfers()
    {
        if (_page != "files" || _transferRows is not { } rows) return;
        if (rows.Parent is StackPanel { Parent: Border panel }) panel.IsVisible = _transfers.Count > 0;
        rows.Children.Clear();
        foreach (var job in _transfers)
        {
            job.Label = Muted("");
            job.Progress = new ProgressBar { Height = 5, Maximum = 100 };
            var actions = job.State switch
            {
                TransferState.Queued or TransferState.Running => Action(L("取消", "Cancel"), () =>
                {
                    if (job.Cancellation is { } cancellation) cancellation.Cancel();
                    else { job.State = TransferState.Cancelled; DrawTransfers(); }
                    return Task.CompletedTask;
                }),
                TransferState.Cancelled or TransferState.Failed => SecondaryAction(L("重试", "Retry"), () =>
                {
                    job.State = TransferState.Queued;
                    DrawTransfers();
                    _ = ProcessTransfersAsync();
                    return Task.CompletedTask;
                }),
                TransferState.Complete => IconButton("close", L("关闭", "Dismiss"), () =>
                {
                    _transfers.Remove(job);
                    DrawTransfers();
                    return Task.CompletedTask;
                }),
                _ => new Button { IsVisible = false }
            };
            var copy = new StackPanel { Spacing = 4, Children = { Heading(job.Name), job.Label, job.Progress } };
            var row = Section(TitleRow(copy, actions));
            row.Padding = new Thickness(13, 9);
            rows.Children.Add(row);
            UpdateTransfer(job);
        }
    }

    private void UpdateTransfer(Transfer job)
    {
        if (job.Label is not { } label || job.Progress is not { } progress) return;
        label.Text = job.State switch
        {
            TransferState.Running => job.Upload ? L("正在上传…", "Uploading…") : L("正在下载…", "Downloading…"),
            TransferState.Saving => L("选择保存位置", "Choose where to save"),
            TransferState.Complete => L("已完成", "Complete"),
            TransferState.Cancelled => L("已取消", "Cancelled"),
            TransferState.Failed => job.Error,
            _ => L("等待传输", "Queued")
        };
        SetStatusTone(label, job.State switch
        {
            TransferState.Complete => "success", TransferState.Saving => "warning", TransferState.Failed => "error", _ => ""
        });
        progress.IsVisible = job.State == TransferState.Running;
        progress.IsIndeterminate = job.Total is not > 0;
        if (job.Total is > 0 and var total)
        {
            progress.Value = Math.Clamp(100d * job.Bytes / total, 0, 100);
            if (progress.IsVisible) label.Text += $" {progress.Value:N0}%";
        }
    }
}
