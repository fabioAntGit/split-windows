using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Split.Windows;

namespace Split.App;

public enum WheelState { Hidden, Probing, Shown }

/// <summary>
/// The window the wheel lives in: clear, square, always on top. It is a drop target from the moment it
/// appears, so a file drag in progress finds it; it takes the keyboard only once the wheel is clicked.
/// </summary>
public sealed class WheelWindow : Window
{
    public const int NoActivate = 0x08000000, ToolWindow = 0x80, ExStyle = -20;

    public WheelWindow(WheelController controller)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowDrop = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = Placeholder();
        SourceInitialized += (_, _) => SetExStyle(NoActivate | ToolWindow, true);

        // Everything is decided here, from the window and the real pointer, never from a part of it: moving
        // between the wheel's parts also reports leaving and entering, and that isn't the drag leaving.
        DragEnter += (_, e) =>
        {
            var paths = Paths(e);
            if (paths is null) controller.NotAFileDrag();
            else controller.FileDragEntered(paths);
            Over(e, controller, paths is not null);
        };
        DragOver += (_, e) => Over(e, controller, true);
        DragLeave += (_, _) =>
        {
            if (controller.PointerLeft()) controller.View?.DragExited();
        };
        Drop += (_, e) =>
        {
            e.Effects = controller.View?.Dropped(Paths(e)) == true ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Activated += (_, _) => WasActivated = true;
        Deactivated += (_, _) =>
        {
            if (WasActivated) controller.Deactivated();
        };
    }

    /// <summary>The window has had the keyboard since it last appeared.</summary>
    public bool WasActivated { get; set; }

    public static Border Placeholder() => new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };

    private static string[]? Paths(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void Over(DragEventArgs e, WheelController controller, bool files)
    {
        // Before the wheel is up the window only has to stay the target.
        e.Effects = !files ? DragDropEffects.None
            : controller.View is { } view ? (view.DragMoved(e.GetPosition(view)) ? DragDropEffects.Copy : DragDropEffects.None) : DragDropEffects.Copy;
        e.Handled = true;
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    public void SetExStyle(int flags, bool on)
    {
        var style = GetWindowLong(Handle, ExStyle);
        SetWindowLong(Handle, ExStyle, on ? style | flags : style & ~flags);
    }

    /// <summary>The extended style of the real window, for the tests.</summary>
    public int ExtendedStyle => GetWindowLong(Handle, ExStyle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}

/// <summary>
/// Opens, moves and closes the wheel. A drag begins as a probe: the clear window is put under the pointer
/// at once, and when files arrive in it the wheel appears; if nothing arrives within a moment it was not a
/// file drag (text, a moving window) and the window leaves without a trace.
/// </summary>
public sealed class WheelController
{
    /// <summary>How long a probe waits for files before it goes: they arrive within a few milliseconds when it is a file drag.</summary>
    public const double NotAFileDragAfter = 0.3;

    /// <summary>After the button comes up, a drag's wheel stays this long before it closes.</summary>
    public const double CloseAfterDrag = 0.35;

    public static WheelController Shared { get; } = new();

    /// <summary>Opens no window; only the state moves (tests).</summary>
    public bool Headless { get; set; }

    public WheelState State { get; private set; }

    public WheelView? View { get; private set; }

    /// <summary>The wheel a probe will open once files arrive.</summary>
    public WheelKind? ProbeKind { get; private set; }

    /// <summary>A probe is waiting for files.</summary>
    public bool WaitingForFiles => enterTimer is not null;

    /// <summary>The window, in pixels as last placed, and the scale of the screen it is on; the windows' state for the tests.</summary>
    public (double X, double Y, double Width, double Height, double Scale) Placed => placed;

    public WheelWindow? Window => window;

    private WheelWindow? window;
    private DispatcherTimer? enterTimer, closeTimer, clickWatch;
    private int generation;
    private readonly System.Diagnostics.Stopwatch shownAt = new();
    private bool buttonWasDown;
    private (double X, double Y, double Width, double Height, double Scale) placed;

    /// <summary>Makes the wheel appear wherever the app asks for one.</summary>
    public static void Install()
    {
        WheelLauncher.Present = Shared.Present;
        // Made now, so a drag that is already under way doesn't wait for a window to be built.
        _ = Shared.EnsureWindow().Handle;
        Look.Changed += () => Shared.window?.Dispatcher.BeginInvoke(() => Shared.View?.Redraw());
    }

    // Starting from a drag

    /// <summary>A key was tapped during what may be a file drag.</summary>
    public void BeginDrag(WheelKind kind)
    {
        // The files are already known and the drag is inside the wheel, so the window is not entered again: switch in place.
        if (State == WheelState.Shown && View is { ClickMode: false } open)
        {
            WheelLauncher.Show(kind, open.Paths, clickMode: false);
            return;
        }
        if (State == WheelState.Probing)
        {
            ProbeKind = kind;
            return;
        }
        HideNow();
        State = WheelState.Probing;
        ProbeKind = kind;
        if (!Headless)
        {
            var win = EnsureWindow();
            win.Content = WheelWindow.Placeholder();
            Place(win, ProbeSide(), atCursor: true);
            Reveal(win, activate: false);
        }
        enterTimer = new DispatcherTimer(TimeSpan.FromSeconds(NotAFileDragAfter), DispatcherPriority.Normal, (_, _) => NotAFileDrag(), Dispatcher.CurrentDispatcher);
    }

    private static double ProbeSide() => WheelLayout.WindowSize(Settings.WheelSize) + 2 * WheelView.ShadowMargin;

    /// <summary>Files came into the probe's window.</summary>
    public void FileDragEntered(IReadOnlyList<string> paths)
    {
        // The drag entering again (it also does when only a key changes) while the wheel is up changes nothing.
        if (State != WheelState.Probing || ProbeKind is not { } kind) return;
        StopEnterTimer();
        WheelLauncher.Show(kind, paths, clickMode: false);
        // Nothing to offer: Show has said why, and the probe goes.
        if (State == WheelState.Probing) HideNow();
    }

    /// <summary>What came in is not files, or no files came: the window goes without a trace.</summary>
    public void NotAFileDrag()
    {
        if (State == WheelState.Probing) HideNow();
    }

    /// <summary>The button of the drag came up: the wheel closes shortly after, unless it is being clicked.</summary>
    public void DragEnded()
    {
        closeTimer?.Stop();
        closeTimer = new DispatcherTimer(TimeSpan.FromSeconds(CloseAfterDrag), DispatcherPriority.Normal, (_, _) =>
        {
            closeTimer?.Stop();
            closeTimer = null;
            if (State != WheelState.Hidden && View is not { ClickMode: true }) Hide(animated: true);
        }, Dispatcher.CurrentDispatcher);
    }

    /// <summary>Esc cancels the drag where it started; the wheel goes with it.</summary>
    public void Escape()
    {
        if (State != WheelState.Hidden && View is not { ClickMode: true }) Hide(animated: true);
    }

    /// <summary>Whether the real pointer is outside the window: the only sign that the drag left.</summary>
    public bool PointerLeft()
    {
        if (window is null || Headless) return true;
        GetCursorPos(out var cursor);
        GetWindowRect(window.Handle, out var rect);
        return WheelPlacement.DragLeft(new Box(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), cursor.X, cursor.Y);
    }

    /// <summary>
    /// The window lost the keyboard (another app was chosen): a clicked wheel goes. Windows may hand the focus
    /// elsewhere right after giving it, as the app asking was starting up, so that first moment doesn't count.
    /// </summary>
    public void Deactivated()
    {
        if (State == WheelState.Shown && View is { ClickMode: true } && shownAt.Elapsed.TotalSeconds > 0.5) Hide(animated: true);
    }

    // Showing

    /// <summary>Opens a wheel for the request: around the pointer for a drag, in the middle of the main screen when clicked.</summary>
    public void Present(WheelRequest request)
    {
        generation++;
        // A drag keeps its window (the files are not offered to a new one); anything else starts clean.
        var probing = State == WheelState.Probing;
        if (request.ClickMode || (!probing && View is not { ClickMode: false })) HideNow();
        StopEnterTimer();
        StopClickWatch();

        var view = new WheelView(request.Items, request.Title, request.Subtitle, request.Paths, request.ClickMode, forWindow: true) { IsLive = true };
        view.OnPerform = (item, chosen) =>
        {
            Hide(animated: true);
            item.Action(chosen);
        };
        view.OnCancel = () => Hide(animated: true);
        view.OnNeedsClicks = AcceptClicks;
        View?.StopAnimating();
        View = view;
        State = WheelState.Shown;
        shownAt.Restart();
        var anchor = view.Center;
        if (!Headless)
        {
            var win = EnsureWindow();
            win.WasActivated = false;
            win.Content = view;
            // A probe is already under the pointer; a wheel replaced in place moves to where the pointer is now.
            anchor = probing && !request.ClickMode ? Anchor() : Place(win, view.Side, atCursor: !request.ClickMode);
            Reveal(win, activate: request.ClickMode);
        }
        view.AnimateIn(anchor);
        if (request.ClickMode) AcceptClicks();
    }

    /// <summary>From here on the wheel is clicked: it takes the keyboard and closes on a click elsewhere.</summary>
    private void AcceptClicks()
    {
        if (Headless || window is null || View is null) return;
        shownAt.Restart();
        window.SetExStyle(WheelWindow.NoActivate, false);
        window.Activate();
        SetForegroundWindow(window.Handle);
        View.Focus();
        Keyboard.Focus(View);
        buttonWasDown = AnyButtonDown();
        clickWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, (_, _) => WatchClicks(), window.Dispatcher);
    }

    /// <summary>A click outside the window closes the wheel, whether or not Windows let it have the focus.</summary>
    private void WatchClicks()
    {
        var down = AnyButtonDown();
        var pressed = down && !buttonWasDown;
        buttonWasDown = down;
        if (pressed && PointerLeft()) Hide(animated: true);
    }

    private static bool AnyButtonDown() => (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0;

    public void Hide(bool animated)
    {
        StopClickWatch();
        StopEnterTimer();
        if (State == WheelState.Hidden) return;
        var view = View;
        State = WheelState.Hidden;
        View = null;
        if (!animated || view is null)
        {
            window?.Hide();
            view?.StopAnimating();
            return;
        }
        var mine = generation;
        view.AnimateOut(() =>
        {
            // A wheel shown meanwhile owns the window now.
            if (mine == generation && State == WheelState.Hidden) window?.Hide();
        });
    }

    private void HideNow() => Hide(animated: false);

    private void StopEnterTimer()
    {
        enterTimer?.Stop();
        enterTimer = null;
    }

    private void StopClickWatch()
    {
        clickWatch?.Stop();
        clickWatch = null;
    }

    private WheelWindow EnsureWindow() => window ??= MakeWindow();

    private WheelWindow MakeWindow()
    {
        var made = new WheelWindow(this);
        // The window is moved by pixels; when Windows hands it to a screen of another scale, it keeps its place.
        made.DpiChanged += (_, _) => PlaceAgain(made);
        return made;
    }

    // Placement (in pixels: the screen the point is on has its own scale)

    private Point Place(WheelWindow win, double side, bool atCursor)
    {
        POINT point;
        if (atCursor) GetCursorPos(out point);
        else point = new POINT();
        var (work, scale) = atCursor ? MonitorAt(point.X, point.Y) : MonitorAt(0, 0, primary: true);
        if (!atCursor)
        {
            point.X = (int)(work.X + work.Width / 2);
            point.Y = (int)(work.Y + work.Height / 2);
        }
        var wheel = (side - 2 * WheelView.ShadowMargin) * scale;
        var (x, y) = WheelPlacement.Origin(work, point.X, point.Y, wheel);
        var margin = WheelView.ShadowMargin * scale;
        placed = (Math.Round(x - margin), Math.Round(y - margin), Math.Round(side * scale), Math.Round(side * scale), scale);
        win.Width = win.Height = side;
        PlaceAgain(win);
        return atCursor ? Anchor() : new Point(side / 2, side / 2);
    }

    /// <summary>Where the pointer is inside the window, for the wheel to grow out of.</summary>
    private Point Anchor()
    {
        GetCursorPos(out var cursor);
        return new Point((cursor.X - placed.X) / placed.Scale, (cursor.Y - placed.Y) / placed.Scale);
    }

    private void PlaceAgain(WheelWindow win) =>
        SetWindowPos(win.Handle, new IntPtr(-1), (int)placed.X, (int)placed.Y, (int)placed.Width, (int)placed.Height, 0x10 /* SWP_NOACTIVATE */);

    private void Reveal(WheelWindow win, bool activate)
    {
        win.ShowActivated = activate;
        win.SetExStyle(WheelWindow.NoActivate, !activate);
        if (!win.IsVisible) win.Show();
        PlaceAgain(win);
    }

    private static (Box Work, double Scale) MonitorAt(int x, int y, bool primary = false)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, primary ? 1u : 2u);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        var work = info.Work;
        return (new Box(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), scale);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
