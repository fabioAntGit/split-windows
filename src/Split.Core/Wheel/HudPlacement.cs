namespace Split;

public enum ScreenEdge { Left, Top, Right, Bottom }

public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>Where the notification card sits: in the corner of the work area next to the tray.</summary>
public static class HudPlacement
{
    /// <summary>How far the card's edge stays from the screen's edges (the card inside its transparent margin).</summary>
    public const double SideOffset = 12, EdgeOffset = 8;

    /// <summary>How far the card slides in from the side, and out again.</summary>
    public const double Slide = 28;

    /// <summary>
    /// The tray sits at the right of a taskbar at the top or the bottom, and at the bottom of one on a side,
    /// so the card takes the top-right corner under a top taskbar, the bottom-left beside a left one, and the bottom-right otherwise.
    /// <paramref name="inset"/> is the transparent margin round the card, which the window includes.
    /// </summary>
    public static Box Frame(Box workArea, ScreenEdge taskbar, double width, double height, double inset)
    {
        var left = taskbar == ScreenEdge.Left;
        var x = left ? workArea.X + SideOffset - inset : workArea.Right - width - SideOffset + inset;
        var y = taskbar == ScreenEdge.Top ? workArea.Y + EdgeOffset - inset : workArea.Bottom - height - EdgeOffset + inset;
        return new Box(x, y, width, height);
    }

    /// <summary>The side the card comes in from.</summary>
    public static double SlideDirection(ScreenEdge taskbar) => taskbar == ScreenEdge.Left ? -1 : 1;
}
