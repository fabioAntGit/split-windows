using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Split.App;

/// <summary>Inter for text and the Fluent icon font for symbols, both loaded from the files next to the program.</summary>
public static class Fonts
{
    private static readonly Uri Folder = new(Path.Combine(AppContext.BaseDirectory, "fonts") + Path.DirectorySeparatorChar);

    public static readonly FontFamily Inter = new(Folder, "./#Inter");
    public static readonly FontFamily IconsRegular = new(Folder, "./#FluentSystemIcons-Regular");
    public static readonly FontFamily IconsFilled = new(Folder, "./#FluentSystemIcons-Filled");

    public static TextBlock Text(string text, double size, FontWeight? weight = null, Color? color = null) => new()
    {
        Text = text,
        FontFamily = Inter,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = Palette.Brush(color ?? Palette.Primary),
    };

    /// <summary>Makes a one-line text that doesn't fit lose its middle, so a file name keeps its start and its extension.</summary>
    public static void TrimMiddle(TextBlock block)
    {
        var full = block.Text;
        // Without trimming of its own a block is as wide as its whole text, clipped; with it, as wide as its place.
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        System.Windows.Automation.AutomationProperties.SetName(block, full);
        block.SizeChanged += (_, _) => block.Text = Middle(full, block.ActualWidth, text => new FormattedText(text, CultureInfo.CurrentUICulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black, null,
            TextOptions.GetTextFormattingMode(block), VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace);
    }

    /// <summary>The longest cut of the text with "…" in its middle that is no wider than <paramref name="width"/>.</summary>
    public static string Middle(string text, double width, Func<string, double> measure)
    {
        if (measure(text) <= width) return text;
        int low = 0, high = text.Length - 1;
        while (low < high)
        {
            var keep = (low + high + 1) / 2;
            if (measure(Cut(text, keep)) <= width) low = keep; else high = keep - 1;
        }
        return Cut(text, low);
    }

    private static string Cut(string text, int keep)
    {
        int head = (keep + 1) / 2, tail = text.Length - keep / 2;
        // Never split a character written as two halves.
        if (head > 0 && char.IsHighSurrogate(text[head - 1])) head--;
        if (tail < text.Length && char.IsLowSurrogate(text[tail])) tail++;
        return text[..head] + "…" + text[tail..];
    }
}
