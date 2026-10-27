using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Split.App;

/// <summary>Text and symbols of the wheel in Inter and the Fluent icon font, measured and drawn the same way.</summary>
public sealed class WpfTextMeasurer : ITextMeasurer
{
    private static readonly ConcurrentDictionary<FontWeight, Typeface> Faces = new();
    private static readonly ConcurrentDictionary<(string, double), Geometry?> Outlines = new();

    /// <summary>Names are medium; the ones standing alone, and the centre's title, semibold.</summary>
    public static FontWeight Weight(bool bold) => bold ? FontWeights.SemiBold : FontWeights.Medium;

    public static FormattedText Format(string text, double size, FontWeight weight, Color color, double width, int maxLines = 0, double pixelsPerDip = 1)
    {
        var face = Faces.GetOrAdd(weight, w => new Typeface(Fonts.Inter, FontStyles.Normal, w, FontStretches.Normal));
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Palette.Brush(color), pixelsPerDip)
        {
            MaxTextWidth = width,
            TextAlignment = TextAlignment.Center,
        };
        // Cut short, a name ends in "…" on its last line.
        if (maxLines > 0)
        {
            formatted.MaxLineCount = maxLines;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }
        return formatted;
    }

    public double Height(int lines, double fontSize, bool bold) =>
        Format(string.Join('\n', Enumerable.Repeat("Hg", lines)), fontSize, Weight(bold), Colors.Black, 1000).Height;

    public (double Width, double Height) Measure(string text, double fontSize, bool bold, double width)
    {
        var formatted = Format(text, fontSize, Weight(bold), Colors.Black, width);
        formatted.TextAlignment = TextAlignment.Left;
        return (formatted.Width, formatted.Height);
    }

    /// <summary>How many lines fit in a box of this height.</summary>
    public static int Lines(double height, double size, FontWeight weight) =>
        Math.Max(1, (int)Math.Floor(height / Format("Hg", size, weight, Colors.Black, 1000).Height + 0.05));

    /// <summary>The outline of a symbol at a size, with its top-left at the origin.</summary>
    public static Geometry? Symbol(string name, double size) => Outlines.GetOrAdd((name, size), key =>
    {
        if (Icons.Find(key.Item1) is not { } found) return null;
        var face = new Typeface(found.Filled ? Fonts.IconsFilled : Fonts.IconsRegular, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var geometry = new FormattedText(found.Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, key.Item2, Brushes.Black, 1).BuildGeometry(default);
        geometry.Freeze();
        return geometry;
    });
}
