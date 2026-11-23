using Avalonia;
using CommandTray.Services;
using CommandTray.Sync;

namespace CommandTray;

internal static class Program
{
    private const string MutexName = @"Local\CommandTraySuite.SingleInstance";
    private const string ShowEventName = @"Local\CommandTraySuite.ShowSettings";
    private const string ShutdownEventName = @"Local\CommandTraySuite.Shutdown";

    [STAThread]
    private static void Main(string[] args)
    {
        if (SuiteDesktopUpdate.HandleArguments(args, SuiteUpdates.AssemblyVersion(typeof(Program).Assembly), out int updaterExit))
        {
            Environment.ExitCode = updaterExit;
            return;
        }
        if (args is ["--command-host", var gateName, var encodedCommand])
        {
            Environment.ExitCode = CommandHost.Run(gateName, encodedCommand);
            return;
        }

        using var mutex = new Mutex(true, MutexName + AppPaths.InstanceSuffix, out var firstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + AppPaths.InstanceSuffix);
        using var shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName + AppPaths.InstanceSuffix);
        if (args is ["--shutdown"])
        {
            shutdownEvent.Set();
            return;
        }
        if (!firstInstance)
        {
            showEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        // Native auxiliaries use Avalonia's Win32 dispatcher on the same STA.
        WindowsFormsSynchronizationContext.AutoInstall = false;
        // Keep native inputs consistent with the light workspace; automatic
        // dark controls otherwise mix dark text surfaces with custom white UI.
        System.Windows.Forms.Application.SetColorMode(SystemColorMode.Classic);
        var store = new ConfigurationStore();
        AppConfigurationSafeLoad(store, out var configuration);
        Localizer.Configure(configuration.Language);
        using var context = new WindowsAppStartup(store, configuration, showEvent, shutdownEvent);
        AppBuilder.Configure(() => new WindowsClientApplication(context))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    private static void AppConfigurationSafeLoad(ConfigurationStore store, out Models.AppConfiguration configuration)
    {
        try { configuration = store.Load(); }
        catch (Exception ex)
        {
            MessageBox.Show(Localizer.T($"Configuration could not be loaded:\n{ex.Message}", $"无法加载配置：\n{ex.Message}"), "CommandTray",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            configuration = new Models.AppConfiguration();
        }
    }
}
