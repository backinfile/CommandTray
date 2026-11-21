using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CommandTray.Relay;

internal sealed class RelayAdmin(RelayDirectory directory)
{
    [McpServerTool(Name = "get_relay_status", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Read persisted relay counts and sync revisions from the deployment host.")]
    public RelayOverview Status()
    {
        var state = directory.Read();
        return new(state.Devices.Length, state.Files.Length, state.Workspace.Revision, state.Clipboard.Revision);
    }

    [McpServerTool(Name = "list_relay_devices", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("List paired devices. Does not return device tokens or their hashes.")]
    public RelayDeviceView[] Devices() => directory.Read().Devices
        .Select(device => new RelayDeviceView(device.Id, device.Name, device.PairedAt)).ToArray();

    [McpServerTool(Name = "revoke_relay_device", Destructive = true, UseStructuredContent = true)]
    [Description("Revoke a paired device while the relay is stopped.")]
    public async Task<RelayRevocation> Revoke(string deviceId, CancellationToken cancellationToken)
    {
        using var store = new RelayStore(directory);
        return new(await store.DeleteDeviceAsync(deviceId, cancellationToken));
    }

    [McpServerTool(Name = "list_relay_backups", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("List backup IDs in the relay deployment's data directory.")]
    public RelayBackupList Backups() => new(directory.ListBackups());

    [McpServerTool(Name = "backup_relay", UseStructuredContent = true)]
    [Description("Create a local state and file backup. The relay server must be stopped for a consistent snapshot.")]
    public RelayBackup Backup() => new(directory.Backup());

    [McpServerTool(Name = "restore_relay_backup", Destructive = true, UseStructuredContent = true)]
    [Description("Restore a validated local backup while the relay is stopped. The previous store is retained.")]
    public RelayRestore Restore(string backupId) => new(backupId, directory.Restore(backupId));
}

public sealed record RelayOverview(int Devices, int Files, long WorkspaceRevision, long ClipboardRevision);
public sealed record RelayDeviceView(string Id, string Name, DateTimeOffset PairedAt);
public sealed record RelayBackupList(string[] Ids);
public sealed record RelayBackup(string Id);
public sealed record RelayRestore(string Id, string? PreviousStore);
public sealed record RelayRevocation(bool Revoked);
