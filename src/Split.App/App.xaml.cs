using System.Windows;

namespace Split.App;

public partial class App : Application
{
    // No window and no tray yet: start, then leave cleanly. Later phases keep it alive.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown();
    }
}