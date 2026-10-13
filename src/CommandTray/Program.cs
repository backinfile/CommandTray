using CommandTray.Services;

namespace CommandTray;

internal static class Program
{
    private const string MutexName = @"Local\CommandTray.SingleInstance";
    private const string ShowEventName = @"Local\CommandTray.ShowSettings";
    private const string ShutdownEventName = @"Local\CommandTray.Shutdown";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--command-host", var gateName, var encodedCommand])
        {
            Environment.ExitCode = CommandHost.Run(gateName, encodedCommand);
            return;
        }

        using var mutex = new Mutex(true, MutexName, out var firstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
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
        // Keep native inputs consistent with the light workspace; automatic
        // dark controls otherwise mix dark text surfaces with custom white UI.
        Application.SetColorMode(SystemColorMode.Classic);
        var store = new ConfigurationStore();
        AppConfigurationSafeLoad(store, out var configuration);
        Localizer.Configure(configuration.Language);
        using var context = new TrayApplicationContext(store, configuration, showEvent, shutdownEvent);
        Application.Run(context);
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
