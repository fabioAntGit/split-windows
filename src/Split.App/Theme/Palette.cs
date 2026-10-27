using System.Windows;
using System.Windows.Media;
using Split.Windows;

namespace Split.App;

/// <summary>
/// Whatever is selected takes the strongest contrast the window allows: white with black text on
/// black windows, black with white text on white ones. The wheel, the editors and the other windows
/// share one colour, black or white, so the selection is the same everywhere it appears.
/// The wheel's highlighted slice is the one gentler exception: it covers a large part of the wheel.
/// </summary>
public static class Palette
{
    public static bool IsWhite => Settings.LookColor == LookColor.White;

    private static Color Gray(double white) => Color.FromRgb((byte)Math.Round(white * 255), (byte)Math.Round(white * 255), (byte)Math.Round(white * 255));

    /// <summary>The selection: primary buttons and chosen tools.</summary>
    public static Color Selection => IsWhite ? Gray(0.08) : Colors.White;

    public static Color OnSelection => IsWhite ? Colors.White : Gray(0.04);

    /// <summary>Strokes, rings and selected outlines.</summary>
    public static Color Accent => IsWhite ? Gray(0.1) : Gray(0.96);

    /// <summary>Tint for switches and sliders: graphite, so the knob stays visible on the track.</summary>
    public static Color ControlTint => IsWhite ? Gray(0.42) : Gray(0.62);

    /// <summary>The backdrop of the wheel and of every window: a soft black, or white.</summary>
    public static Color WheelSurface => IsWhite ? Gray(0.96) : Gray(0.165);

    /// <summary>
    /// The wheel, the notices and the menus float over the desktop and let it show faintly through. There is no
    /// blur, which is what costs time on every frame. Increase contrast keeps them solid.
    /// </summary>
    public static Color Floating => Motion.IncreaseContrast ? WheelSurface : With(WheelSurface, 0.86);

    /// <summary>The wheel's lines, names and icons.</summary>
    public static Color WheelInk => IsWhite ? Gray(0.1) : Colors.White;

    /// <summary>
    /// The highlighted slice: a light veil of the ink, so it is seen on the surface without covering it,
    /// and the names and icons on it keep the ink. At 0.18 the ink holds 4.5:1 on it.
    /// </summary>
    public static Color WheelSelection => With(WheelInk, 0.18);

    /// <summary>Text, and the base of hairlines and fills (a share of it): black on white windows, white on black ones.</summary>
    public static Color Primary => IsWhite ? Colors.Black : Colors.White;

    public static Color Secondary => With(Primary, 0.55);

    /// <summary>The colour at a share of its opacity.</summary>
    public static Color With(Color color, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>WCAG relative luminance.</summary>
    public static double Luminance(Color color)
    {
        static double Linear(byte value) { var v = value / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    public static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>A colour laid over another, as the eye sees it.</summary>
    public static Color Over(Color top, Color under)
    {
        var weight = top.A / 255.0;
        byte Mix(byte over, byte below) => (byte)Math.Round(below * (1 - weight) + over * weight);
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }
}

/// <summary>The look chosen in Settings. Open windows listen to <see cref="Changed"/> and redraw in the new colour.</summary>
public static class Look
{
    public static event Action? Changed;

    public static LookColor Color
    {
        get => Settings.LookColor;
        set
        {
            Settings.LookColor = value;
            Changed?.Invoke();
        }
    }
}

public static class Motion
{
    public static bool ReduceMotion => Desktop.ReduceMotion;

    public static bool IncreaseContrast => SystemParameters.HighContrast;
}

/// <summary>Sizes of the text styles, in device-independent pixels.</summary>
public static class TextSize
{
    public const double LargeTitle = 26, Title = 22, Title2 = 17, Title3 = 15, Headline = 13, Body = 13, Callout = 12, Subheadline = 11, Footnote = 10;
}
