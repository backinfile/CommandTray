using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace CommandTray.Services;

public sealed record DesktopEntityId(string Id);

internal sealed class DesktopMcpTools(DesktopRuntime desktop)
{
    [McpServerTool(Name = "get_desktop_settings", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read local desktop settings without returning credentials.")]
    public Task<object> GetSettings(CancellationToken cancellationToken) =>
        Task.FromResult<object>(RequireBackend().ReadSettings());

    [McpServerTool(Name = "set_desktop_settings", UseStructuredContent = true)]
    [Description("Set the language (auto, en or zh), sign-in launch, or local MCP preference.")]
    public Task<object> SetSettings(CancellationToken cancellationToken, string? language = null, bool? runAtLogin = null, bool? mcpEnabled = null) =>
        desktop.OnUiAsync<object>(() => RequireBackend().SaveSettings(language, runAtLogin, mcpEnabled), cancellationToken);

    [McpServerTool(Name = "open_desktop_page", UseStructuredContent = true)]
    [Description("Open the native commands, workspace, sharedFiles, clipboard, captures or settings page.")]
    public Task<object> OpenPage(string page, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<DesktopPage>(page, ignoreCase: true, out var destination) || !Enum.IsDefined(destination))
            throw new ArgumentException("Unknown desktop page.");
        return desktop.OnUiAsync<object>(() =>
        {
            desktop.Navigate(destination);
            return new { page };
        }, cancellationToken);
    }

    [McpServerTool(Name = "list_workspaces", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read the spaces, active space, components, geometry and editable component values.")]
    public WorkspaceSnapshot ListSpaces() => desktop.Workspace.Snapshot();

    [McpServerTool(Name = "create_workspace", UseStructuredContent = true)]
    [Description("Create and activate a named workspace.")]
    public DesktopEntityId AddSpace(string name) => new(desktop.Workspace.AddSpace(name));

    [McpServerTool(Name = "rename_workspace", UseStructuredContent = true)]
    [Description("Change the name of a workspace.")]
    public OperationResult RenameSpace(string spaceId, string name)
    {
        desktop.Workspace.RenameSpace(spaceId, name); return Ok();
    }

    [McpServerTool(Name = "delete_workspace", Destructive = true, UseStructuredContent = true)]
    [Description("Delete a workspace and close its terminal sessions.")]
    public OperationResult DeleteSpace(string spaceId)
    {
        desktop.Workspace.DeleteSpace(spaceId); desktop.ReapRemovedTerminals(); return Ok();
    }

    [McpServerTool(Name = "activate_workspace", UseStructuredContent = true)]
    [Description("Select and show the space in the Windows workspace.")]
    public Task<OperationResult> ActivateSpace(string spaceId, CancellationToken cancellationToken) =>
        desktop.OnUiAsync(() =>
        {
            desktop.Workspace.ActivateSpace(spaceId);
            desktop.Navigate(DesktopPage.Workspace, spaceId);
            return Ok();
        }, cancellationToken);

    [McpServerTool(Name = "arrange_workspace", UseStructuredContent = true)]
    [Description("Tile a workspace within the requested pixel width.")]
    public OperationResult Arrange(string spaceId, int width)
    {
        desktop.Workspace.Arrange(spaceId, Math.Clamp(width, 450, 12000)); return Ok();
    }

    [McpServerTool(Name = "add_workspace_component", UseStructuredContent = true)]
    [Description("Add files, terminal, note, todo, board, preview, service, log, commands, clipboard, captures, ssh or sharedFiles.")]
    public DesktopEntityId AddItem(string spaceId, string kind, string title, JsonObject? data = null) =>
        new(desktop.Workspace.AddItem(spaceId, kind, title, data));

    [McpServerTool(Name = "rename_workspace_component", UseStructuredContent = true)]
    [Description("Rename an existing component.")]
    public OperationResult RenameItem(string spaceId, string itemId, string title)
    {
        desktop.Workspace.RenameItem(spaceId, itemId, title); return Ok();
    }

    [McpServerTool(Name = "move_resize_workspace_component", UseStructuredContent = true)]
    [Description("Set component position and size in canvas pixels.")]
    public OperationResult MoveItem(string spaceId, string itemId, int x, int y, int width, int height)
    {
        desktop.Workspace.MoveItem(spaceId, itemId, new Rectangle(x, y, width, height)); return Ok();
    }

    [McpServerTool(Name = "set_workspace_component_data", UseStructuredContent = true)]
    [Description("Update component values, for example note text, to-do tasks or whiteboard strokes.")]
    public OperationResult SetItemData(string spaceId, string itemId, JsonObject data)
    {
        desktop.Workspace.EditData(spaceId, itemId, target =>
        {
            foreach (var field in data) target[field.Key] = field.Value?.DeepClone();
        });
        return Ok();
    }

    [McpServerTool(Name = "remove_workspace_component", Destructive = true, UseStructuredContent = true)]
    [Description("Remove a component and close its terminal session if one exists.")]
    public OperationResult RemoveItem(string spaceId, string itemId)
    {
        desktop.Workspace.RemoveItem(spaceId, itemId); desktop.CloseTerminal(itemId); return Ok();
    }

    [McpServerTool(Name = "list_local_files", ReadOnly = true, UseStructuredContent = true)]
    [Description("List the content of a local directory on the Windows computer.")]
    public LocalFileEntry[] ListLocalFiles(string path) => LocalFiles.List(path);

    [McpServerTool(Name = "preview_local_file", ReadOnly = true)]
    [Description("Preview up to 64 KiB of a local UTF-8 text file.")]
    public Task<string> Preview(string path, CancellationToken cancellationToken) => LocalFiles.PreviewAsync(path, cancellationToken);

    [McpServerTool(Name = "copy_local_file", Destructive = true, UseStructuredContent = true)]
    [Description("Copy a file to a destination, preserving any existing file unless overwrite is true.")]
    public OperationResult Copy(string source, string destination, bool overwrite = false)
    {
        File.Copy(Path.GetFullPath(source), Path.GetFullPath(destination), overwrite); return Ok();
    }

    [McpServerTool(Name = "create_local_folder", Idempotent = true, UseStructuredContent = true)]
    [Description("Create a local folder.")]
    public OperationResult MakeFolder(string path) { Directory.CreateDirectory(Path.GetFullPath(path)); return Ok(); }

    [McpServerTool(Name = "start_terminal_session", UseStructuredContent = true)]
    [Description("Start cmd or powershell in an existing terminal component.")]
    public OperationResult StartTerminal(string spaceId, string itemId, string shell = "cmd", string? directory = null)
    {
        var item = FindTerminal(spaceId, itemId);
        directory ??= WorkspaceStore.Text(item.Data["workingDirectory"]) ?? WorkspaceStore.Text(item.Data["directory"]);
        desktop.OpenTerminal(itemId, shell is "cmd" or "powershell" ? shell : throw new ArgumentException("Use cmd or powershell."), directory);
        try
        {
            desktop.Workspace.EditData(spaceId, itemId, data =>
            {
                data["shell"] = shell;
                data.Remove("hostId");
                if (directory is not null)
                {
                    data["directory"] = directory;
                    data["workingDirectory"] = directory;
                }
            });
        }
        catch { desktop.CloseTerminal(itemId); throw; }
        return Ok();
    }

    [McpServerTool(Name = "send_terminal_input", OpenWorld = true, UseStructuredContent = true)]
    [Description("Send keystrokes or user-requested commands to a running terminal.")]
    public OperationResult SendTerminal(string itemId, string input)
    {
        var terminal = desktop.Terminal(itemId) ?? throw new InvalidOperationException("Start a terminal first.");
        terminal.Send(input); return Ok();
    }

    [McpServerTool(Name = "read_terminal_output", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read the terminal transcript and whether its session is still running.")]
    public object ReadTerminal(string itemId)
    {
        var terminal = desktop.Terminal(itemId);
        return new { active = terminal?.Active ?? false, output = terminal?.Output ?? "" };
    }

    [McpServerTool(Name = "resize_terminal_session", UseStructuredContent = true)]
    [Description("Resize a native terminal in columns and rows.")]
    public OperationResult ResizeTerminal(string itemId, int columns, int rows)
    {
        var terminal = desktop.Terminal(itemId) ?? throw new InvalidOperationException("Start a terminal first.");
        terminal.Resize(columns, rows);
        return Ok();
    }

    [McpServerTool(Name = "close_terminal_session", UseStructuredContent = true)]
    [Description("Close an owned terminal session and save its transcript in the workspace.")]
    public OperationResult StopTerminal(string spaceId, string itemId)
    {
        _ = FindTerminal(spaceId, itemId);
        var transcript = desktop.Terminal(itemId)?.Output;
        desktop.CloseTerminal(itemId);
        if (transcript is not null) desktop.Workspace.EditData(spaceId, itemId, data => data["history"] = transcript);
        return Ok();
    }

    [McpServerTool(Name = "get_relay_status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read device pairing and workspace sync state without returning a token.")]
    public object RelayStatus() => desktop.Relay.Status();

    [McpServerTool(Name = "pair_relay", OpenWorld = true, UseStructuredContent = true)]
    [Description("Pair the current Windows device to a self-hosted relay.")]
    public async Task<object> Pair(string url, string deviceName, string pairingCode, CancellationToken cancellationToken)
    {
        await desktop.Relay.PairAsync(url, deviceName, pairingCode, cancellationToken);
        return desktop.Relay.Status();
    }

    [McpServerTool(Name = "disconnect_relay", UseStructuredContent = true)]
    [Description("Remove this application's local relay connection credentials.")]
    public async Task<OperationResult> Disconnect(CancellationToken cancellationToken)
    {
        await desktop.Relay.DisconnectAsync(cancellationToken); return Ok();
    }

    [McpServerTool(Name = "list_paired_devices", ReadOnly = true, UseStructuredContent = true)]
    [Description("List the currently paired devices.")]
    public Task<JsonArray> Devices(CancellationToken cancellationToken) => desktop.Relay.ListDevicesAsync(cancellationToken);

    [McpServerTool(Name = "push_workspace", OpenWorld = true, UseStructuredContent = true)]
    [Description("Send the current workspace to the relay. Stops on a revision conflict.")]
    public async Task<object> Push(CancellationToken cancellationToken) => new { revision = await desktop.Relay.PushWorkspaceAsync(cancellationToken) };

    [McpServerTool(Name = "pull_workspace", Destructive = true, UseStructuredContent = true)]
    [Description("Merge the latest relay changes into the local workspace.")]
    public async Task<object> Pull(CancellationToken cancellationToken)
    {
        var revision = await desktop.Relay.PullWorkspaceAsync(cancellationToken);
        desktop.ReapRemovedTerminals();
        return new { revision };
    }

    [McpServerTool(Name = "resolve_workspace_sync_conflict", UseStructuredContent = true)]
    [Description("Resolve a workspace sync conflict by choosing local or remote.")]
    public async Task<object> Resolve(string choice, CancellationToken cancellationToken)
    {
        var revision = await desktop.Relay.ResolveWorkspaceAsync(choice, cancellationToken);
        desktop.ReapRemovedTerminals();
        return new { revision };
    }

    [McpServerTool(Name = "list_shared_files", ReadOnly = true, UseStructuredContent = true)]
    [Description("List files available through the paired relay.")]
    public Task<JsonArray> SharedFiles(CancellationToken cancellationToken) => desktop.Relay.ListFilesAsync(cancellationToken);

    [McpServerTool(Name = "upload_shared_file", OpenWorld = true, UseStructuredContent = true)]
    [Description("Upload a specific local file to an optional relay folder.")]
    public Task<JsonObject> Upload(string path, CancellationToken cancellationToken, string? folder = null) =>
        desktop.Relay.UploadAsync(path, folder, cancellationToken);

    [McpServerTool(Name = "download_shared_file", UseStructuredContent = true)]
    [Description("Download a shared file to a local path. Existing files are preserved unless overwrite is true.")]
    public async Task<OperationResult> Download(string id, string destination, CancellationToken cancellationToken, bool overwrite = false)
    {
        await desktop.Relay.DownloadAsync(id, destination, overwrite, cancellationToken); return Ok();
    }

    [McpServerTool(Name = "delete_shared_file", Destructive = true, UseStructuredContent = true)]
    [Description("Delete a file from the paired relay.")]
    public async Task<OperationResult> DeleteShared(string id, CancellationToken cancellationToken)
    {
        await desktop.Relay.DeleteFileAsync(id, cancellationToken); return Ok();
    }

    [McpServerTool(Name = "read_local_clipboard", ReadOnly = true)]
    [Description("Read the current local text clipboard.")]
    public Task<string> ReadClipboard(CancellationToken cancellationToken) => desktop.OnUiAsync(desktop.ReadClipboard, cancellationToken);

    [McpServerTool(Name = "write_local_clipboard", UseStructuredContent = true)]
    [Description("Write local clipboard text.")]
    public Task<OperationResult> WriteClipboard(string text, CancellationToken cancellationToken) =>
        desktop.OnUiAsync(() => { desktop.WriteClipboard(text); return Ok(); }, cancellationToken);

    [McpServerTool(Name = "list_clipboard_history", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read the application's local text clipboard history.")]
    public string[] ClipboardHistory() => desktop.ClipboardHistory();

    [McpServerTool(Name = "send_clipboard_to_relay", OpenWorld = true, UseStructuredContent = true)]
    [Description("Publish explicitly supplied clipboard text to the paired relay.")]
    public Task<JsonObject> SendClipboard(string text, CancellationToken cancellationToken) =>
        desktop.Relay.PutClipboardAsync(text, cancellationToken);

    [McpServerTool(Name = "read_relay_clipboard", ReadOnly = true, UseStructuredContent = true)]
    [Description("Fetch the latest text clipboard value from the relay.")]
    public Task<JsonObject> ReceiveClipboard(CancellationToken cancellationToken) => desktop.Relay.GetClipboardAsync(cancellationToken);

    [McpServerTool(Name = "receive_relay_clipboard_to_local", OpenWorld = true, UseStructuredContent = true)]
    [Description("Fetch the latest relay text and apply it to the local Windows clipboard.")]
    public async Task<JsonObject> ReceiveToLocalClipboard(CancellationToken cancellationToken)
    {
        var clipboard = await desktop.Relay.GetClipboardAsync(cancellationToken);
        await WriteClipboard(WorkspaceStore.Text(clipboard["text"]) ?? "", cancellationToken);
        return clipboard;
    }

    [McpServerTool(Name = "list_capture_history", ReadOnly = true, UseStructuredContent = true)]
    [Description("List screenshots and recordings made by CommandTray.")]
    public string[] Captures() => desktop.Media.Captures();

    [McpServerTool(Name = "take_screenshot", UseStructuredContent = true)]
    [Description("Capture the Windows desktop or a supplied screen rectangle.")]
    public Task<object> Screenshot(CancellationToken cancellationToken, int? x = null, int? y = null, int? width = null, int? height = null)
    {
        Rectangle? region = null;
        if (x is not null || y is not null || width is not null || height is not null)
            region = new Rectangle(x ?? throw new ArgumentException("Specify x."), y ?? throw new ArgumentException("Specify y."),
                width ?? throw new ArgumentException("Specify width."), height ?? throw new ArgumentException("Specify height."));
        return desktop.OnUiAsync<object>(() => new { path = desktop.Media.Screenshot(region) }, cancellationToken);
    }

    [McpServerTool(Name = "select_screenshot_region", UseStructuredContent = true)]
    [Description("Open the local desktop area selector and save the selected region.")]
    public Task<object> SelectScreenshot(CancellationToken cancellationToken) =>
        desktop.OnUiAsync<object>(() =>
        {
            var rectangle = desktop.SelectScreenshotRegion();
            if (rectangle is null) return new { status = "cancelled", path = (string?)null };
            var path = desktop.Media.Screenshot(rectangle);
            using var image = Image.FromFile(path);
            Clipboard.SetImage(image);
            return new { status = "saved", path = (string?)path };
        }, cancellationToken);

    [McpServerTool(Name = "pin_screenshot", UseStructuredContent = true)]
    [Description("Show a saved screenshot in a local topmost window.")]
    public Task<OperationResult> Pin(string capture, CancellationToken cancellationToken) =>
        desktop.OnUiAsync(() => { DesktopCapturesPanel.ShowPinned(desktop.Media.ResolveCapture(capture)); return Ok(); }, cancellationToken);

    [McpServerTool(Name = "annotate_screenshot", UseStructuredContent = true)]
    [Description("Open the saved screenshot in the local annotation editor.")]
    public Task<object> AnnotateScreenshot(string capture, CancellationToken cancellationToken) =>
        desktop.OnUiAsync<object>(() =>
        {
            var path = desktop.AnnotateScreenshot(capture);
            return new { status = path is null ? "cancelled" : "saved", path };
        }, cancellationToken);

    [McpServerTool(Name = "open_capture", UseStructuredContent = true)]
    [Description("Open a saved screenshot or recording in its Windows viewer.")]
    public OperationResult OpenCapture(string capture)
    {
        using var process = Process.Start(new ProcessStartInfo(desktop.Media.ResolveCapture(capture)) { UseShellExecute = true });
        return Ok();
    }

    [McpServerTool(Name = "start_screen_recording", UseStructuredContent = true)]
    [Description("Begin recording the main Windows screen with FFmpeg.")]
    public async Task<object> StartRecording(CancellationToken cancellationToken, int frameRate = 24) =>
        new { path = await desktop.Media.StartRecordingAsync(frameRate, cancellationToken) };

    [McpServerTool(Name = "stop_screen_recording", UseStructuredContent = true)]
    [Description("Finish the current screen recording and return the saved MP4 path.")]
    public async Task<object> StopRecording(CancellationToken cancellationToken) =>
        new { path = await desktop.Media.StopRecordingAsync(cancellationToken) };

    [McpServerTool(Name = "get_screen_recording_status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Check whether CommandTray is recording.")]
    public object RecordingStatus() => desktop.Media.RecordingState;

    [McpServerTool(Name = "recognize_screenshot_text")]
    [Description("Read text from a saved screenshot using local Tesseract.")]
    public Task<string> Ocr(string capture, CancellationToken cancellationToken, string language = "eng") =>
        desktop.Media.RecognizeTextAsync(capture, language, cancellationToken);

    [McpServerTool(Name = "list_ssh_hosts", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read synchronized SSH host definitions and their key IDs.")]
    public JsonArray Hosts() => desktop.Workspace.ReadHosts();

    [McpServerTool(Name = "save_ssh_host", UseStructuredContent = true)]
    [Description("Create or edit an SSH host by ID without starting a connection.")]
    public DesktopEntityId SaveHost(string name, string address, string username, int port = 22, string? id = null, string? keyId = null) =>
        new(desktop.Workspace.SetHost(id, name, address, username, port, keyId));

    [McpServerTool(Name = "delete_ssh_host", Destructive = true, UseStructuredContent = true)]
    [Description("Remove a saved SSH host definition.")]
    public OperationResult DeleteHost(string id) { desktop.Workspace.DeleteHost(id); return Ok(); }

    [McpServerTool(Name = "list_ssh_key_names", ReadOnly = true, UseStructuredContent = true)]
    [Description("List SSH key IDs and names. No private key material is returned.")]
    public IReadOnlyList<SshKeyView> Keys() => desktop.Workspace.ListSshKeys();

    [McpServerTool(Name = "import_encrypted_ssh_key", UseStructuredContent = true)]
    [Description("Import a local SSH key after the user supplies an encryption passphrase in the native dialog.")]
    public Task<object> ImportKey(string path, string name, CancellationToken cancellationToken) =>
        desktop.OnUiAsync<object>(() =>
        {
            var passphrase = desktop.PromptSecret("Encrypt SSH key");
            return passphrase is null ? new { status = "cancelled", id = (string?)null } :
                new { status = "imported", id = (string?)desktop.ImportSshKey(path, name, passphrase) };
        }, cancellationToken);

    [McpServerTool(Name = "delete_encrypted_ssh_key", Destructive = true, UseStructuredContent = true)]
    [Description("Remove an encrypted SSH key and clear its host references.")]
    public OperationResult DeleteKey(string id) { desktop.Workspace.RemoveSshKey(id); return Ok(); }

    [McpServerTool(Name = "connect_ssh_host", OpenWorld = true, UseStructuredContent = true)]
    [Description("Open a saved SSH host in a terminal. An encrypted key is unlocked through a native prompt.")]
    public Task<object> ConnectHost(string spaceId, string hostId, CancellationToken cancellationToken) =>
        desktop.OnUiAsync<object>(() =>
        {
            var host = desktop.Workspace.ReadHosts().OfType<JsonObject>().FirstOrDefault(item => WorkspaceStore.Text(item["id"]) == hostId)
                ?? throw new KeyNotFoundException("Unknown SSH host.");
            var created = desktop.Workspace.AddItem(spaceId, "terminal", WorkspaceStore.Text(host["name"]) ?? "SSH",
                new JsonObject { ["hostId"] = hostId });
            try
            {
                if (desktop.OpenSsh(created, hostId))
                {
                    desktop.Navigate(DesktopPage.Workspace, spaceId, created);
                    return new { itemId = (string?)created, status = "started" };
                }
            }
            catch
            {
                desktop.CloseTerminal(created);
                desktop.Workspace.RemoveItem(spaceId, created);
                throw;
            }
            desktop.Workspace.RemoveItem(spaceId, created);
            return new { itemId = (string?)null, status = "cancelled" };
        }, cancellationToken);

    [McpServerTool(Name = "set_desktop_pet_visible", UseStructuredContent = true)]
    [Description("Show or hide the desktop pet.")]
    public Task<object> Pet(bool visible, CancellationToken cancellationToken) =>
        desktop.OnUiAsync<object>(() => new { visible = desktop.SetPetVisible?.Invoke(visible) ?? false }, cancellationToken);

    [McpServerTool(Name = "show_component_notification", UseStructuredContent = true)]
    [Description("Show a local notification on the desktop pet or in the tray.")]
    public Task<OperationResult> ShowNotice(string message, CancellationToken cancellationToken, string? spaceId = null, string? itemId = null)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 240) throw new ArgumentException("Enter up to 240 notification characters.");
        return desktop.OnUiAsync(() => { desktop.Notify(message, spaceId, itemId); return Ok(); }, cancellationToken);
    }

    private WorkspaceItem FindTerminal(string spaceId, string itemId) => desktop.Workspace.Snapshot().Spaces
        .FirstOrDefault(space => space.Id == spaceId)?.Items.SingleOrDefault(item => item.Id == itemId && item.Kind == "terminal")
        ?? throw new KeyNotFoundException("Unknown terminal component.");

    private WindowsClientBackend RequireBackend() => desktop.Backend
        ?? throw new InvalidOperationException("The Windows application is unavailable.");

    private static OperationResult Ok() => new(true, "ok");
}
