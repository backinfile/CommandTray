using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace CommandTray.Ui;

public class ClientApplication : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        RequestedThemeVariant = ThemeVariant.Dark;
    }
}
