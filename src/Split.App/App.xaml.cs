using System.Windows;
using Split.Windows;

namespace Split.App;

public partial class App : Application
{
    private SingleInstance? instance;
    private TrayIcon? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Platform.Current = new WindowsPlatform();

        instance = new SingleInstance();
        if (!instance.IsPrimary)
        {
            // Another Split is running: give it the files and leave.
            SingleInstance.Send(e.Args);
            Shutdown();
            return;
        }
        instance.Listen(paths => Dispatcher.BeginInvoke(() => Received(paths)));

        NotificationHud.Shared.Attach(Dispatcher);
        tray = new TrayIcon(Quit);

        if (!Settings.DidShowWelcome)
        {
            Settings.DidShowWelcome = true;
            AppWindows.ShowHelp();
        }
        if (e.Args.Length > 0) WheelLauncher.Open(e.Args);
    }

    /// <summary>Files opened with Split, or sent by a second start. A start without files brings up the settings, like opening an app again.</summary>
    private static void Received(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) AppWindows.ShowSettings();
        else WheelLauncher.Open(paths);
    }

    private void Quit()
    {
        Shelf.Shared.Quitting = true;
        tray?.Dispose();
        instance?.Dispose();
        Shutdown();
    }
}
