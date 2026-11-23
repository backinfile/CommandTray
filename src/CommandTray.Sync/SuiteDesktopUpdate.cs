using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;

namespace CommandTray.Sync;

public static class SuiteDesktopUpdate
{
    private const string Switch = "--apply-suite-update";

    public static void Launch(string readyDirectory, string platform, bool restart)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
        var directory = Path.Combine(readyDirectory, "helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var helper = Path.Combine(directory, Path.GetFileName(executable));
        File.Copy(executable, helper);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, File.GetUnixFileMode(executable));

        using var parent = Process.GetCurrentProcess();
        var arguments = new[] { Switch, readyDirectory, platform, executable,
            parent.Id.ToString(CultureInfo.InvariantCulture), parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            restart ? "restart" : "exit" };
        LaunchProcess(helper, arguments)?.Dispose();
    }

    public static bool HandleArguments(string[] arguments, Version current, out int result)
    {
        result = 0;
        if (arguments.FirstOrDefault() != Switch) return false;
        if (arguments is not [Switch, var ready, var platform, var executable, var pid, var ticks, var mode] ||
            !int.TryParse(pid, out int parent) || !long.TryParse(ticks, out long started) || mode is not ("restart" or "exit"))
        {
            result = 2;
            return true;
        }
        try
        {
            WaitForParent(parent, started);
            var asset = SuiteUpdates.ReadReadyAsset(ready, platform, current)
                ?? throw new InvalidDataException("There is no verified newer update.");
            var package = SuiteUpdates.PackagePath(ready);
            if (!SuiteUpdates.VerifyFileAsync(package, asset).GetAwaiter().GetResult())
                throw new InvalidDataException("Update file failed verification.");
            if (platform == "windows-setup")
                RunInstaller(package, asset.FileName);
            else
                ReplaceFiles(package, executable, platform);
            File.Delete(package);
            File.Delete(Path.Combine(ready, "latest.json"));
        }
        catch (Exception error)
        {
            result = 1;
            try { File.WriteAllText(Path.Combine(ready, "install-error.txt"), error.Message); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (mode == "restart")
        {
            try { LaunchProcess(executable, OperatingSystem.IsWindows() ? ["--show"] : [])?.Dispose(); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { result = 1; }
        }
        return true;
    }

    private static void WaitForParent(int id, long started)
    {
        try
        {
            using var parent = Process.GetProcessById(id);
            if (parent.StartTime.ToUniversalTime().Ticks == started && !parent.WaitForExit(30_000))
                throw new TimeoutException("CommandTray did not exit before the update.");
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static void RunInstaller(string package, string assetName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var setup = Path.Combine(AppContext.BaseDirectory, assetName);
        File.Copy(package, setup, overwrite: true);
        using var process = LaunchProcess(setup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
            "/NORESTARTAPPLICATIONS", "/SP-"]) ?? throw new IOException("Setup could not start.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"Setup exited with code {process.ExitCode}.");
    }

    private static void ReplaceFiles(string package, string executable, string platform)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var work = Path.Combine(root, ".commandtray-update-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(work, "stage");
        var backup = Path.Combine(work, "backup");
        Directory.CreateDirectory(stage);
        var applied = new List<(string Destination, string Saved, bool Existed)>();
        var recovered = true;
        try
        {
            if (platform == "windows-portable") ExtractZip(package, stage);
            else if (platform.StartsWith("linux-", StringComparison.Ordinal)) ExtractTar(package, stage);
            else throw new InvalidDataException("Unsupported desktop archive.");

            if (!File.Exists(Path.Combine(stage, Path.GetFileName(executable))))
                throw new InvalidDataException("The update does not contain this executable.");
            foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stage, file);
                var destination = Path.Combine(root, relative);
                var saved = Path.Combine(backup, relative);
                EnsureRealParents(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                bool existed = File.Exists(destination);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(destination, saved);
                }
                File.Move(file, destination, overwrite: true);
                applied.Add((destination, saved, existed));
            }
        }
        catch
        {
            foreach (var entry in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (entry.Existed) File.Move(entry.Saved, entry.Destination, overwrite: true);
                    else File.Delete(entry.Destination);
                }
                catch (IOException) { recovered = false; }
                catch (UnauthorizedAccessException) { recovered = false; }
            }
            throw;
        }
        finally
        {
            if (recovered)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void ExtractZip(string package, string stage)
    {
        using var zip = ZipFile.OpenRead(package);
        foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
        {
            var destination = EntryPath(stage, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
        }
    }

    private static void ExtractTar(string package, string stage)
    {
        using var input = File.OpenRead(package);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType == TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("Unsupported update archive entry.");
            var destination = EntryPath(stage, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination,
                entry.Mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute));
        }
    }

    private static string EntryPath(string root, string name)
    {
        var segments = name.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.Contains(':') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Invalid update archive path.");
        return Path.Combine(root, Path.Combine(segments));
    }

    private static void EnsureRealParents(string root, string relative)
    {
        var path = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            path = Path.Combine(path, segment);
            if (new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null)
                throw new IOException("An update target is a symbolic link.");
        }
    }

    private static Process? LaunchProcess(string file, string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(file)!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start);
    }
}
