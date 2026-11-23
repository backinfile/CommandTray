using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Runtime;
using Android.Util;
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
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden |
        ConfigChanges.UiMode | ConfigChanges.FontScale)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private static readonly (string Resource, float Sp)[] TextSizes =
    [
        ("TrayFontSmall", 11), ("TrayFontCaption", 12), ("TrayFontDetail", 13), ("TrayFontBody", 14),
        ("TrayFontSection", 15), ("TrayFontLarge", 16), ("TrayFontTitle", 17), ("TrayReadingLineHeight", 22)
    ];

    private CancellationTokenSource? _foreground;
    private AndroidBackend Backend => ((AndroidApplication)Application!).Backend;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        UpdateTextSizes();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        UpdateTextSizes();
    }

    private void UpdateTextSizes()
    {
        if (Avalonia.Application.Current is not { } app || Resources?.DisplayMetrics is not { } metrics) return;
        foreach (var (resource, sp) in TextSizes)
        {
            double size = TypedValue.ApplyDimension(ComplexUnitType.Sp, sp, metrics) / metrics.Density;
            if (!Equals(app.Resources[resource], size)) app.Resources[resource] = size;
        }
    }

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
