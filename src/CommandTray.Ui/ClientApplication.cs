using Avalonia;
using Avalonia.Markup.Xaml;

namespace CommandTray.Ui;

public class ClientApplication : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
