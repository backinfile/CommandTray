using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommandTray.Sync;
using Avalonia.Threading;

namespace CommandTray.Services;

internal sealed class DesktopRuntime : IDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _terminals = new();
    private readonly object _terminalGate = new();
    private readonly string _directory;
    private readonly string _clipboardPath;
    private Control? _ui;
    private WindowsDesktopShell? _shell;
    private string? _sensitiveClipboardText;

    public DesktopRuntime(string directory)
    {
        _directory = directory;
        Workspace = new WorkspaceStore(directory);
        Relay = new DesktopRelay(directory, Workspace);
        Media = new DesktopMedia(Path.Combine(directory, "captures"));
        McpCredential = DesktopRelay.ReadOrCreateLocalCredential(directory);
        _clipboardPath = Path.Combine(directory, "clipboard.json");
    }

    public WorkspaceStore Workspace { get; }
    public DesktopRelay Relay { get; }
    public DesktopMedia Media { get; }
    public string McpCredential { get; }
    public event EventHandler<DesktopNotice>? Notification;
    public void AttachWindow(Control window) => _ui = window;
    public WindowsClientBackend? Backend { get; private set; }

    public void AttachShell(WindowsDesktopShell shell, WindowsClientBackend backend)
    {
        _shell = shell;
        Backend = backend;
    }

    public Task<T> OnUiAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        if (_shell is not null)
            return Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).GetTask();
        var window = _ui ?? throw new InvalidOperationException("The Windows application is unavailable.");
        return window.InvokeAsync(action, cancellationToken);
    }

    public void Navigate(DesktopPage page, string? spaceId = null, string? itemId = null) =>
        (_shell ?? throw new InvalidOperationException("The Windows application is unavailable.")).Navigate(page, spaceId, itemId);

    public string? PromptSecret(string title) =>
        (_shell ?? throw new InvalidOperationException("The Windows application is unavailable.")).PromptSecret(title);

    public Rectangle? SelectScreenshotRegion() =>
        DesktopCapturesPanel.SelectRegion(_shell?.Owner);

    public string? AnnotateScreenshot(string path) =>
        DesktopCapturesPanel.Annotate(Media.ResolveCapture(path), _shell?.Owner);

    public bool OpenSsh(string itemId, string hostId, string? suppliedPassphrase = null)
    {
        var host = Workspace.ReadHosts().OfType<JsonObject>().FirstOrDefault(item => WorkspaceStore.Text(item["id"]) == hostId)
            ?? throw new KeyNotFoundException("Unknown SSH host.");
        string? identityFile = null;
        if (WorkspaceStore.Text(host["keyId"]) is { Length: > 0 } keyId)
        {
            var passphrase = string.IsNullOrEmpty(suppliedPassphrase)
                ? PromptSecret(Localizer.T("SSH vault passphrase", "SSH 密钥库口令"))
                : suppliedPassphrase;
            if (passphrase is null) return false;
            identityFile = CreateIdentityFile(keyId, passphrase);
        }
        try
        {
            var session = OpenTerminal(itemId, "ssh", null, SshArguments.ForHost(host, identityFile).ToArray());
            if (identityFile is not null)
            {
                var path = identityFile;
                session.SetCleanup(() => File.Delete(path));
                identityFile = null;
            }
            return true;
        }
        finally { if (identityFile is not null) File.Delete(identityFile); }
    }
    public void Notify(string message, string? spaceId = null, string? itemId = null) =>
        Notification?.Invoke(this, new DesktopNotice(message, spaceId, itemId));
    public Func<bool, bool>? SetPetVisible { get; set; }
    public SettingsForm Window => _ui as SettingsForm ?? throw new InvalidOperationException("The Windows application is unavailable.");

    public TerminalSession OpenTerminal(string itemId, string shell, string? directory, string[]? arguments = null)
    {
        lock (_terminalGate) return OpenTerminalCore(itemId, shell, directory, arguments);
    }

    private TerminalSession OpenTerminalCore(string itemId, string shell, string? directory, string[]? arguments)
    {
        if (_terminals.TryGetValue(itemId, out var existing) && existing.Active)
            throw new InvalidOperationException("There is already a terminal running in this component.");
        if (_terminals.TryRemove(itemId, out existing)) existing.Dispose();
        var terminal = TerminalSession.Start(shell, directory, arguments);
        if (!_terminals.TryAdd(itemId, terminal))
        {
            terminal.Dispose();
            throw new InvalidOperationException("Another terminal was started for this component.");
        }
        return terminal;
    }

    public TerminalSession? Terminal(string itemId) => _terminals.GetValueOrDefault(itemId);

    public void CloseTerminal(string itemId)
    {
        lock (_terminalGate)
            if (_terminals.TryRemove(itemId, out var session)) session.Dispose();
    }

    public void ReapRemovedTerminals()
    {
        var ids = Workspace.Snapshot().Spaces.SelectMany(x => x.Items).Where(x => x.Kind == "terminal").Select(x => x.Id).ToHashSet();
        foreach (var id in _terminals.Keys)
            if (!ids.Contains(id)) CloseTerminal(id);
    }

    public string ReadClipboard()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (text != _sensitiveClipboardText)
        {
            _sensitiveClipboardText = null;
            RememberClipboard(text);
        }
        return text;
    }

    public void WriteClipboard(string text, bool sensitive = false)
    {
        if (text.Length == 0) Clipboard.Clear();
        else Clipboard.SetText(text);
        _sensitiveClipboardText = sensitive ? text : null;
        if (!sensitive) RememberClipboard(text);
    }

    internal string? ReadClipboardForSync()
    {
        var data = Clipboard.GetDataObject();
        if (data is null || data.GetFormats(autoConvert: false).Length == 0) return "";
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", autoConvert: false) ||
            ClipboardFlagDisabled(data, "CanUploadToCloudClipboard") ||
            ClipboardFlagDisabled(data, "CanIncludeInClipboardHistory")) return null;
        var text = data.GetData(DataFormats.UnicodeText) as string;
        if (text == _sensitiveClipboardText) return null;
        _sensitiveClipboardText = null;
        return text;
    }

    private static bool ClipboardFlagDisabled(IDataObject data, string name)
    {
        if (!data.GetDataPresent(name, autoConvert: false)) return false;
        return data.GetData(name, autoConvert: false) switch
        {
            MemoryStream { Length: >= 4 } stream => BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()) != 1,
            byte[] { Length: >= 4 } bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes) != 1,
            int value => value != 1,
            _ => true
        };
    }

    public string[] ClipboardHistory() =>
        !File.Exists(_clipboardPath) ? [] :
            JsonSerializer.Deserialize<string[]>(DesktopRelay.Unprotect(JsonNode.Parse(File.ReadAllText(_clipboardPath))!.GetValue<string>())) ?? [];

    private void RememberClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 262144) return;
        var saved = ClipboardHistory();
        if (saved.FirstOrDefault() == text) return;
        var current = new[] { text }.Concat(saved.Where(entry => entry != text)).Take(25);
        WorkspaceStore.WriteAtomic(_clipboardPath, JsonValue.Create(DesktopRelay.Protect(JsonSerializer.Serialize(current)))!);
    }

    public string ImportSshKey(string path, string name, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ArgumentException("Enter a key name with up to 128 characters.");
        var info = new FileInfo(path);
        if (info.Length is < 32 or > 131072) throw new InvalidDataException("Invalid SSH key length.");
        var bytes = File.ReadAllBytes(path);
        try
        {
            if (!System.Text.Encoding.UTF8.GetString(bytes).Contains("PRIVATE KEY-----", StringComparison.Ordinal))
                throw new InvalidDataException("Choose a PEM or OpenSSH private key.");
            var id = Guid.NewGuid().ToString("N");
            var encrypted = SecretEnvelope.Encrypt(bytes, passphrase, id);
            Workspace.StoreSshKey(id, name, JsonSerializer.SerializeToNode(encrypted, JsonSerializerOptions.Web)!.AsObject());
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string CreateIdentityFile(string keyId, string passphrase)
    {
        var encrypted = Workspace.ReadSshKey(keyId).Deserialize<SecretEnvelope>(JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid encrypted SSH key.");
        var bytes = encrypted.Decrypt(passphrase, keyId);
        var path = Path.Combine(_directory, ".ssh-" + Guid.NewGuid().ToString("N") + ".key");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes);
            var acl = new System.Security.AccessControl.FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity.User!,
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
            return path;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose()
    {
        foreach (var id in _terminals.Keys) CloseTerminal(id);
        Media.Dispose();
    }
}

internal sealed record DesktopNotice(string Message, string? SpaceId = null, string? ItemId = null);
