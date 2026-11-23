using Microsoft.Win32;

namespace CommandTray.Services;

internal static class WindowsUpdateInstall
{
    private const string InstallerEntry = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{31824658-E8C1-41FA-B6F9-0CF816286536}_is1";

    public static string Platform()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallerEntry);
        var location = key?.GetValue("InstallLocation") as string;
        return !string.IsNullOrWhiteSpace(location) &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase)
            ? "windows-setup" : "windows-portable";
    }
}
