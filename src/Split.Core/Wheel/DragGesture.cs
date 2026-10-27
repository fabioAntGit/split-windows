namespace Split;

[Flags]
public enum ModKeys { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }

/// <summary>What one look at the mouse and the keys found.</summary>
public readonly record struct GestureStep(bool DragEnded, WheelKind? Trigger);

/// <summary>
/// "Something is being dragged + a key was tapped", from what can be read without hooks: the primary button,
/// how far the pointer has gone since it went down, and the modifier keys. Files can't be read from
/// outside the drag, so a pointer dragged past the system's threshold counts as a possible file drag; the
/// wheel's window finds out whether it really is one.
/// </summary>
public sealed class DragGesture
{
    /// <summary>A tap is a key (or combination) pressed and released within this time.</summary>
    public const double TapWindow = 0.8;

    private bool buttonWasDown;
    private (int X, int Y) downAt;
    private ModKeys last, candidate;
    private double tapStarted;

    public bool IsDragging { get; private set; }

    public static ModKeys KeysOf(ModifierChoice choice) => choice switch
    {
        ModifierChoice.Shift => ModKeys.Shift,
        ModifierChoice.Ctrl => ModKeys.Ctrl,
        ModifierChoice.Alt => ModKeys.Alt,
        ModifierChoice.CtrlAlt => ModKeys.Ctrl | ModKeys.Alt,
        _ => ModKeys.None,
    };

    public GestureStep Update(bool primaryDown, int x, int y, int dragWidth, int dragHeight, ModKeys keys, double now, Func<WheelKind, ModKeys>? keyOf = null)
    {
        var ended = false;
        if (primaryDown)
        {
            if (!buttonWasDown)
            {
                downAt = (x, y);
                IsDragging = false;
            }
            if (!IsDragging && (Math.Abs(x - downAt.X) > dragWidth || Math.Abs(y - downAt.Y) > dragHeight)) IsDragging = true;
        }
        else if (IsDragging)
        {
            IsDragging = false;
            ended = true;
        }
        buttonWasDown = primaryDown;
        return new GestureStep(ended, Track(keys, now, keyOf ?? (kind => KeysOf(Settings.Modifier(kind)))));
    }

    /// <summary>A tap is pressing and releasing a modifier (or combination) quickly while dragging.</summary>
    private WheelKind? Track(ModKeys keys, double now, Func<WheelKind, ModKeys> keyOf)
    {
        var before = last;
        last = keys;
        if (keys == before) return null;
        if (before == ModKeys.None)
        {
            candidate = keys;
            tapStarted = now;
        }
        else if (keys != ModKeys.None)
        {
            candidate |= keys;
        }
        else
        {
            var tapped = candidate;
            candidate = ModKeys.None;
            if (!IsDragging || now - tapStarted > TapWindow) return null;
            foreach (var kind in WheelKinds.All)
            {
                if (keyOf(kind) == tapped) return kind;
            }
        }
        return null;
    }
}

/// <summary>Where the wheel's square goes on a screen.</summary>
public static class WheelPlacement
{
    /// <summary>The top-left corner of a square of <paramref name="side"/> centred on a point and kept inside the work area.</summary>
    public static (double X, double Y) Origin(Box area, double centreX, double centreY, double side) =>
        (Math.Min(Math.Max(centreX - side / 2, area.X), area.Right - side), Math.Min(Math.Max(centreY - side / 2, area.Y), area.Bottom - side));

    /// <summary>
    /// The drag left the wheel only if the pointer is outside its window: moving between the window's own
    /// parts also reports leaving and entering, and that is not leaving.
    /// </summary>
    public static bool DragLeft(Box window, double pointerX, double pointerY) =>
        pointerX < window.X || pointerX >= window.Right || pointerY < window.Y || pointerY >= window.Bottom;
}
