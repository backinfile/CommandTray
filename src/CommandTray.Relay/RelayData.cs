using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandTray.Relay;

internal static class RelayData
{
    internal const int WorkspaceLimit = 16 * 1024 * 1024;
    internal const long FileLimit = 128L * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static RelayState Empty()
    {
        var document = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, activeSpaceId = (string?)null, spaces = Array.Empty<object>(),
            sshHosts = Array.Empty<object>()
        }, Json);
        return new(new(0, document), new(0, "", null, DateTimeOffset.UnixEpoch), [], []);
    }

    internal static bool IsId(string? value) => Guid.TryParseExact(value, "N", out _);

    internal static void ValidateState(RelayState state)
    {
        if (state.Workspace.Revision < 0 || !IsWorkspace(state.Workspace.Document) ||
            state.Clipboard.Revision < 0 || state.Clipboard.Text.Length > 1_000_000 ||
            state.Devices.Any(device => !IsId(device.Id) || !IsHash(device.TokenHash)) ||
            state.Devices.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count() != state.Devices.Length ||
            state.Files.Any(file => !IsId(file.Id) || !IsFileName(file.Name) || !IsFolder(file.Folder) ||
                                    file.Size is < 0 or > FileLimit || !IsHash(file.Sha256)) ||
            state.Files.Select(file => file.Id).Distinct(StringComparer.Ordinal).Count() != state.Files.Length)
            throw new InvalidDataException("The relay state does not conform to the version 1 format.");
    }

    internal static bool IsWorkspace(JsonElement document) => document.ValueKind == JsonValueKind.Object &&
        document.TryGetProperty("schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number &&
        version.TryGetInt32(out var number) && number == 1 &&
        document.TryGetProperty("spaces", out var spaces) && spaces.ValueKind == JsonValueKind.Array;

    internal static bool IsFileName(string? name) => name is { Length: > 0 and <= 240 } &&
        name is not ("." or "..") && !name.Any(ch => ch is '/' or '\\' or '\0' || char.IsControl(ch));

    internal static bool IsFolder(string? folder) => folder is { Length: <= 800 } &&
        (folder.Length == 0 || folder.Split('/').All(IsFileName));

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}

internal sealed record RelayState(WorkspaceRecord Workspace, ClipboardRecord Clipboard, DeviceRecord[] Devices, StoredFile[] Files);
internal sealed record WorkspaceRecord(long Revision, JsonElement Document);
internal sealed record ClipboardRecord(long Revision, string Text, string? DeviceId, DateTimeOffset UpdatedAt);
internal sealed record DeviceRecord(string Id, string Name, string TokenHash, DateTimeOffset PairedAt);
internal sealed record StoredFile(string Id, string Name, string Folder, long Size, string Sha256, DateTimeOffset ModifiedAt, string DeviceId);
internal sealed record PairInput(string? DeviceName, string? PairingCode);
internal sealed record WorkspaceInput(long BaseRevision, JsonElement Document);
internal sealed record ClipboardInput(string? Text);
internal sealed record WorkspaceWrite(bool Accepted, WorkspaceRecord Value);
