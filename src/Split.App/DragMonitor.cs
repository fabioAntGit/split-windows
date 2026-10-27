using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Split.App;

/// <summary>
/// Notices "something is being dragged + a key was tapped" without hooks or administrator rights: a look
/// at the pointer, the primary button and the modifier keys every 16 ms (the keys of a quick tap are
/// gone sooner than a slower look would see). What is being dragged is not known from here; the wheel's
/// window is put under the pointer and finds out whether files arrive.
/// </summary>
public sealed class DragMonitor
{
    private const int Shift = 0x10, Control = 0x11, Menu = 0x12, Escape = 0x1B, LeftButton = 1, RightButton = 2;
    private const int DragWidth = 68, DragHeight = 69, SwappedButtons = 23;

    private readonly DragGesture gesture = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private DispatcherTimer? timer;
    private bool escapeWasDown;

    /// <summary>A key was tapped while something may be dragged: open this wheel.</summary>
    public Action<WheelKind>? Triggered { get; set; }

    public Action? DragEnded { get; set; }

    /// <summary>Esc was pressed during a drag, which cancels it where it began.</summary>
    public Action? Cancelled { get; set; }

    public void Start()
    {
        timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
    }

    public void Stop()
    {
        timer?.Stop();
        timer = null;
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private void Tick()
    {
        var primary = GetSystemMetrics(SwappedButtons) != 0 ? RightButton : LeftButton;
        GetCursorPos(out var cursor);
        var keys = (Down(Shift) ? ModKeys.Shift : 0) | (Down(Control) ? ModKeys.Ctrl : 0) | (Down(Menu) ? ModKeys.Alt : 0);
        var step = gesture.Update(Down(primary), cursor.X, cursor.Y, GetSystemMetrics(DragWidth), GetSystemMetrics(DragHeight), keys, clock.Elapsed.TotalSeconds);
        if (step.DragEnded) DragEnded?.Invoke();
        if (step.Trigger is { } kind) Triggered?.Invoke(kind);
        var escape = Down(Escape);
        if (escape && !escapeWasDown && gesture.IsDragging) Cancelled?.Invoke();
        escapeWasDown = escape;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
}
