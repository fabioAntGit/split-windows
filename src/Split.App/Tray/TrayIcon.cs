using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using Split.Windows;

namespace Split.App;

/// <summary>The icon in the notification area: the logo, drawn at the size and colour the taskbar asks for, with the menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon icon;
    private readonly Action quit;

    public TrayIcon(Action quit)
    {
        this.quit = quit;
        icon = new TaskbarIcon
        {
            ToolTipText = "Split",
            NoLeftClickDelay = true,
            MenuActivation = PopupActivationMode.LeftOrRightClick,
            ContextMenu = new ContextMenu(),
        };
        Redraw();
        icon.PreviewTrayContextMenuOpen += (_, _) => Rebuild();
        icon.ForceCreate();
        // The taskbar's colour and scale can change while the program runs.
        SystemEvents.UserPreferenceChanged += Changed;
        SystemEvents.DisplaySettingsChanged += Changed;
    }

    private void Changed(object? sender, EventArgs e) => icon.Dispatcher.BeginInvoke(Redraw);

    private void Redraw()
    {
        var pixels = (int)Math.Round(16 * Desktop.PrimaryScale());
        using var stream = new MemoryStream(Logo.TrayIcon(pixels, Desktop.SystemUsesLightTheme));
        icon.Icon = new Icon(stream);
    }

    private void Rebuild()
    {
        var menu = icon.ContextMenu!;
        menu.Items.Clear();
        foreach (var entry in TrayMenu.Build(quit))
        {
            if (entry.IsSeparator) { menu.Items.Add(new Separator()); continue; }
            var item = new MenuItem { Header = entry.Title, IsEnabled = entry.Enabled };
            if (entry.Symbol is { } symbol) item.Icon = Icons.Block(symbol, 16);
            if (entry.Run is { } run) item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= Changed;
        SystemEvents.DisplaySettingsChanged -= Changed;
        icon.Dispose();
    }
}
