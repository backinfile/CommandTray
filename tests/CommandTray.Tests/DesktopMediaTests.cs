using System.Diagnostics;
using System.Text;
using CommandTray.Services;

namespace CommandTray.Tests;

public sealed class DesktopMediaTests
{
    [Fact]
    public void RenameCapturePreservesFormatAndRefusesExistingName()
    {
        var folder = Directory.CreateTempSubdirectory("CommandTrayMediaTest-");
        try
        {
            using var media = new DesktopMedia(folder.FullName);
            var source = Path.Combine(folder.FullName, "old.png");
            var existing = Path.Combine(folder.FullName, "existing.png");
            File.WriteAllText(source, "screenshot");
            File.WriteAllText(existing, "existing screenshot");

            Assert.Throws<ArgumentException>(() => media.RenameCapture(source, "../outside"));
            Assert.Throws<IOException>(() => media.RenameCapture(source, "existing"));
            var renamed = media.RenameCapture(source, "new");
            Assert.Equal(Path.Combine(folder.FullName, "new.png"), renamed);
            Assert.Equal("screenshot", File.ReadAllText(renamed));
            Assert.Equal("existing screenshot", File.ReadAllText(existing));
            Assert.False(File.Exists(source));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task EncoderFailureClearsSessionAndNextRecordingCanBeSaved()
    {
        var folder = Directory.CreateTempSubdirectory("CommandTrayMediaTest-");
        try
        {
            using var media = new DesktopMedia(folder.FullName);
            var failure = Path.Combine(folder.FullName, "failed.mp4");
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => media.StartEncoderAsync(
                Encoder("[Console]::Error.WriteLine('fixture encoder error'); exit 1"), failure));
            Assert.Contains("fixture encoder error", exception.Message);
            Assert.False(media.IsRecording);
            Assert.Empty(media.Captures());

            var path = Path.Combine(folder.FullName, "retry.mp4");
            var partial = (path + ".partial").Replace("'", "''");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await media.StartEncoderAsync(Encoder($$"""
                [IO.File]::WriteAllText('{{partial}}', 'fixture output')
                [Console]::WriteLine('frame=1')
                if ([Console]::ReadLine() -ne 'q') { exit 1 }
                """), path, timeout.Token);
            Assert.True(media.IsRecording);
            Assert.Empty(media.Captures());
            Assert.Equal(path, await media.StopRecordingAsync(timeout.Token));
            Assert.Equal(new[] { path }, media.Captures());
            Assert.False(File.Exists(path + ".partial"));
        }
        finally { folder.Delete(recursive: true); }
    }

    private static ProcessStartInfo Encoder(string script)
    {
        var program = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            info.ArgumentList.Add(argument);
        return info;
    }
}
