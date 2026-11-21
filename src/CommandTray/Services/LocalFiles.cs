using System.Text;

namespace CommandTray.Services;

public sealed record LocalFileEntry(string Name, string Path, bool Directory, long Size);

internal static class LocalFiles
{
    public static LocalFileEntry[] List(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        return directory.EnumerateFileSystemInfos().Take(1000)
            .Select(item => new LocalFileEntry(item.Name, item.FullName, item is DirectoryInfo, item is FileInfo file ? file.Length : 0))
            .OrderByDescending(item => item.Directory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static async Task<string> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[65536];
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var read = await stream.ReadAsync(buffer, cancellationToken);
        if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0) throw new InvalidDataException("Binary files cannot be previewed as text.");
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
