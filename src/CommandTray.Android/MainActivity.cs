using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using CommandTray.Ui;

namespace CommandTray.Android;

[Application(Name = "dev.commandtray.android.CommandTrayApp", AllowBackup = false)]
public sealed class AndroidApplication(nint javaReference, JniHandleOwnership transfer)
    : AvaloniaAndroidApplication<ClientApplication>(javaReference, transfer)
{
    internal AndroidBackend Backend { get; private set; } = null!;

    public override void OnCreate()
    {
        // The common file picker stages content streams in Path.GetTempPath().
        if (CacheDir?.AbsolutePath is { } cache)
            System.Environment.SetEnvironmentVariable("TMPDIR", cache);
        Backend = new AndroidBackend(this);
        base.OnCreate();
        _ = Backend.StartConfiguredMcpAsync();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
        // Some Android system fonts lose CJK glyphs when Avalonia mixes font weights.
        .With(new FontManagerOptions
        {
            DefaultFamilyName = "avares://CommandTray.Android/Assets/Fonts#Noto Sans SC"
        })
        .AfterSetup(configured =>
        {
            if (configured.Instance?.ApplicationLifetime is IActivityApplicationLifetime lifetime)
                lifetime.MainViewFactory = () => new ClientView(Backend);
        });
}

[Activity(Name = "dev.commandtray.android.MainActivity", Label = "CommandTray", Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private CancellationTokenSource? _foreground;
    private AndroidBackend Backend => ((AndroidApplication)Application!).Backend;

    protected override void OnResume()
    {
        base.OnResume();
        _foreground = new CancellationTokenSource();
        _ = Backend.SyncWhileResumedAsync(_foreground.Token);
    }

    protected override void OnPause()
    {
        Backend.UpdateClipboardFocus(this, false);
        _foreground?.Cancel();
        _foreground?.Dispose();
        _foreground = null;
        base.OnPause();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        Backend.UpdateClipboardFocus(this, hasFocus);
    }
}
