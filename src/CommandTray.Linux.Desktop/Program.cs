using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommandTray.Ui;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("linux")]

namespace CommandTray.Linux.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("CommandTray Desktop for Linux requires Linux.");
            return 1;
        }

        try
        {
            return AppBuilder.Configure<LinuxApplication>()
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            LinuxApplication.Backend?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

internal sealed class LinuxApplication : ClientApplication
{
    public static LinuxClientBackend? Backend { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            var window = new Window
            {
                Title = "CommandTray",
                Width = 1180, Height = 810, MinWidth = 550, MinHeight = 450
            };

            IClipboard Clipboard() => window.Clipboard ??
                throw new InvalidOperationException("The Linux desktop did not provide a clipboard.");

            Backend = new LinuxClientBackend(
                async cancellation => await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return await Clipboard().TryGetTextAsync();
                }),
                async (text, cancellation) => await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    await Clipboard().SetTextAsync(text);
                }),
                startLocalDaemon: true);

            window.Content = new ClientView(Backend);
            window.Opened += async (_, _) =>
            {
                try { await Backend.StartConfiguredMcpAsync(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    Console.Error.WriteLine("CommandTray MCP: " + ex.Message);
                }
            };
            lifetime.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
