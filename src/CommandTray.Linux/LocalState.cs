using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandTray.Linux;

internal static class LocalState
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static readonly UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static string? RootOverride => Environment.GetEnvironmentVariable("COMMANDTRAY_HOME");

    public static string Root => Path.GetFullPath(RootOverride ??
        Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "commandtray"));

    public static string Runtime => string.IsNullOrEmpty(RootOverride) && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
        ? Path.Combine(runtime, "commandtray") : Path.Combine(Root, "run");

    public static string Socket => Path.Combine(Runtime, "control.sock");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Profiles => Path.Combine(Root, "profiles.json");
    public static string Workspace => Path.Combine(Root, "workspace.json");

    public static void EnsureDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path, PrivateDirectory);
        if (directory.LinkTarget is not null)
            throw new IOException($"CommandTray state directory cannot be a symbolic link: {path}");
        if (File.GetUnixFileMode(path) != PrivateDirectory)
            File.SetUnixFileMode(path, PrivateDirectory);
    }

    public static T Read<T>(string path, T fallback)
    {
        try
        {
            using var file = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(file, Json) ?? fallback;
        }
        catch (FileNotFoundException) { return fallback; }
        catch (DirectoryNotFoundException) { return fallback; }
    }

    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        EnsureDirectory(directory);
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Access = FileAccess.Write, Mode = FileMode.CreateNew, UnixCreateMode = PrivateFile
            }))
            {
                JsonSerializer.Serialize(file, value, Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static FileStream Lock(string path)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
                UnixCreateMode = PrivateFile
            });
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"CommandTray state is already in use: {path}", ex);
        }
    }
}

internal sealed record LocalSettings(string? Server = null, string? DeviceId = null, string? Token = null, int McpPort = 58629, string? McpToken = null)
{
    public static LocalSettings Load() => LocalState.Read(LocalState.Settings, new LocalSettings());
}

internal sealed record WorkspaceState(long BaseRevision, bool Dirty, JsonObject Document)
{
    public static WorkspaceState Load() => LocalState.Read(LocalState.Workspace, Empty());

    public static WorkspaceState Empty() => new(0, false, new JsonObject
    {
        ["schemaVersion"] = 1,
        ["activeSpaceId"] = null,
        ["spaces"] = new JsonArray(),
        ["sshHosts"] = new JsonArray()
    });
}

internal sealed record CommandProfile(string Id, string Name, string Executable, string[] Arguments, string WorkingDirectory,
    int StopTimeoutSeconds = 3, bool AutoStart = false);
internal sealed record RunStatus(bool Started, int? ExitCode = null, string? Error = null);
