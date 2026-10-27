using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Split.Windows;

/// <summary>What the desktop tells us: where the taskbar and work area are, and how the user wants things to move and look.</summary>
public static class Desktop
{
    /// <summary>The work area of the main monitor, in pixels.</summary>
    public static Box PrimaryWorkArea()
    {
        SystemParametersInfo(0x30, 0, out Rect area, 0);
        return new Box(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);
    }

    /// <summary>The taskbar's edge of the main monitor; the bottom when it can't be told.</summary>
    public static ScreenEdge TaskbarEdge()
    {
        var data = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        return SHAppBarMessage(5, ref data) != 0 && data.Edge is >= 0 and <= 3 ? (ScreenEdge)data.Edge : ScreenEdge.Bottom;
    }

    /// <summary>Pixels per 96 of the main monitor's scale (1 at 100%).</summary>
    public static double PrimaryScale()
    {
        var monitor = MonitorFromPoint(default, 1);
        return GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x / 96.0 : 1;
    }

    /// <summary>The work area (in pixels) and scale of the monitor at a point in pixels, or of the main monitor.</summary>
    public static (Box Work, double Scale) MonitorAt(int x, int y, bool primary = false)
    {
        var monitor = MonitorFromPoint(new Point { X = x, Y = y }, primary ? 1u : 2u);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        var work = info.Work;
        return (new Box(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), scale);
    }

    /// <summary>The monitor under the pointer.</summary>
    public static (Box Work, double Scale) MonitorAtCursor()
    {
        GetCursorPos(out var cursor);
        return MonitorAt(cursor.X, cursor.Y);
    }

    /// <summary>"Animation effects" in Windows' accessibility settings, turned off.</summary>
    public static bool ReduceMotion => SystemParametersInfo(0x1042, 0, out int on, 0) && on == 0;

    /// <summary>Whether the taskbar is light, which decides the colour of the tray icon.</summary>
    public static bool SystemUsesLightTheme
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
    }

    /// <summary>The window's title bar follows the colour of the app, dark or light.</summary>
    public static void ColorTitleBar(IntPtr window, bool dark, int rgb)
    {
        var immersive = dark ? 1 : 0;
        DwmSetWindowAttribute(window, 20, ref immersive, 4);
        var colorref = (rgb & 0xFF) << 16 | (rgb >> 8 & 0xFF) << 8 | rgb >> 16 & 0xFF;
        DwmSetWindowAttribute(window, 35, ref colorref, 4);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData { public int Size; public IntPtr Window; public uint Callback; public int Edge; public Rect Bounds; public IntPtr Param; }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, int param, out Rect value, uint flags);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, int param, out int value, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
}
