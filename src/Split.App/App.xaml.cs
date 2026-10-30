using System.Windows;
using Split.Windows;

namespace Split.App;

public partial class App : Application
{
    private SingleInstance? instance;
    private TrayIcon? tray;
    private readonly DragMonitor monitor = new();

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
        if (Settings.PlaySounds || Settings.PlayWheelSounds) _ = SoundOut.Prepare();

        WheelHost.Current = new AppWheelHost();
        WheelController.Install();
        monitor.Triggered = WheelController.Shared.BeginDrag;
        monitor.DragEnded = WheelController.Shared.DragEnded;
        monitor.Cancelled = WheelController.Shared.Escape;
        monitor.Start();

        AppWindows.ShowSettings = () => SettingsWindow.Open();
        AppWindows.ShowActivity = () => SettingsWindow.Open(SettingsTab.Activity);
        AppWindows.ShowHelp = () => HelpWindow.Open();
        AppWindows.ShowAbout = () => AboutWindow.Open();

        if (!Settings.DidShowWelcome)
        {
            Settings.DidShowWelcome = true;
            AppWindows.ShowHelp();
        }
        if (e.Args.Length > 0) instance.Receive(e.Args);
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
        monitor.Stop();
        WheelController.Shared.Hide(animated: false);
        tray?.Dispose();
        instance?.Dispose();
        Shutdown();
    }
}
