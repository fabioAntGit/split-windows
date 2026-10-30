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
}
