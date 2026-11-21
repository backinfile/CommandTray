using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CommandTray.Relay;

internal sealed class RelayDirectory(string directory)
{
    private static readonly UnixFileMode DirectoryAccess = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public string Root { get; } = Path.GetFullPath(directory);
    private string Active => Path.Combine(Root, "store");
    public string Blobs => Path.Combine(Active, "blobs");
    public string StatePath => Path.Combine(Active, "state.json");
    private string Backups => Path.Combine(Root, "backups");

    public static RelayDirectory FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("COMMANDTRAY_RELAY_DATA") ?? "relay-data");

    public string Blob(string id) => Path.Combine(Blobs, id + ".blob");

    public FileStream Acquire()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Root);
        else Directory.CreateDirectory(Root, DirectoryAccess);
        try
        {
            return new FileStream(Path.Combine(Root, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("The relay data directory is already in use. Stop the relay before backup or restore.", error);
        }
    }

    public RelayState Read()
    {
        if (!File.Exists(StatePath)) return RelayData.Empty();
        using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadState(stream);
    }

    public string Backup()
    {
        using var lease = Acquire();
        Directory.CreateDirectory(Backups);
        string id = Guid.NewGuid().ToString("N");
        string archive = Path.Combine(Backups, id + ".zip");
        string partial = archive + ".partial";
        try
        {
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                var state = Read();
                using (var manifest = zip.CreateEntry("state.json", CompressionLevel.Fastest).Open())
                    JsonSerializer.Serialize(manifest, state, RelayData.Json);
                foreach (var file in state.Files)
                    zip.CreateEntryFromFile(Blob(file.Id), "blobs/" + file.Id + ".blob", CompressionLevel.NoCompression);
            }
            File.Move(partial, archive);
            return id;
        }
        finally { File.Delete(partial); }
    }

    public string[] ListBackups() => Directory.Exists(Backups)
        ? Directory.GetFiles(Backups, "*.zip").Select(Path.GetFileNameWithoutExtension)
            .OfType<string>().Where(RelayData.IsId).OrderDescending().ToArray()
        : [];

    public string? Restore(string backupId)
    {
        if (!RelayData.IsId(backupId)) throw new ArgumentException("Use a backup ID returned by list_relay_backups.", nameof(backupId));

        using var lease = Acquire();
        using var zip = ZipFile.OpenRead(Path.Combine(Backups, backupId + ".zip"));
        string stage = Path.Combine(Root, "restore-" + Guid.NewGuid().ToString("N"));
        string? previous = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(stage, "blobs"));
            var manifest = zip.GetEntry("state.json") ?? throw new InvalidDataException("Backup does not contain state.json.");
            if (manifest.Length > 64L * 1024 * 1024) throw new InvalidDataException("Backup state file is too large.");
            RelayState state;
            using (var stream = manifest.Open()) state = ReadState(stream);
            using (var stream = File.Create(Path.Combine(stage, "state.json")))
                JsonSerializer.Serialize(stream, state, RelayData.Json);

            foreach (var file in state.Files)
            {
                string name = file.Id + ".blob";
                var entry = zip.GetEntry("blobs/" + name) ?? throw new InvalidDataException($"Backup is missing blob {file.Id}.");
                if (entry.Length != file.Size) throw new InvalidDataException($"Backup size differs for blob {file.Id}.");
                string destination = Path.Combine(stage, "blobs", name);
                entry.ExtractToFile(destination);
                using var blob = File.OpenRead(destination);
                if (!Convert.ToHexStringLower(SHA256.HashData(blob)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Backup checksum differs for blob {file.Id}.");
            }

            if (Directory.Exists(Active))
            {
                previous = Path.Combine(Root, "previous-" + Guid.NewGuid().ToString("N"));
                Directory.Move(Active, previous);
            }

            try { Directory.Move(stage, Active); }
            catch
            {
                if (previous is not null) Directory.Move(previous, Active);
                throw;
            }
            return previous is null ? null : Path.GetFileName(previous);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static RelayState ReadState(Stream stream)
    {
        var state = JsonSerializer.Deserialize<RelayState>(stream, RelayData.Json)
                    ?? throw new InvalidDataException("Relay state is empty.");
        RelayData.ValidateState(state);
        return state;
    }
}
