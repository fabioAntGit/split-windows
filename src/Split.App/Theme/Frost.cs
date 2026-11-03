using System.Windows.Media;
using System.Windows.Media.Imaging;
using Split.Windows;

namespace Split.App;

/// <summary>
/// What shows through the glass: the picture behind it, so blurred that only its colours and shapes remain. It is made
/// small, an average of blocks of about a thirty-second of its side, softened once more, and drawn stretched with smooth
/// scaling, which finishes the blur for nothing.
/// </summary>
public static class Frost
{
    /// <summary>The screen behind a rectangle of pixels, frosted; null when the screen can't be read.</summary>
    public static BitmapSource? Behind(int x, int y, int width, int height) =>
        Desktop.Capture(x, y, width, height) is { } pixels ? Blur(pixels, width, height) : null;

    /// <summary>A picture frosted, cut to the middle square first (it fills a square the way the desktop picture does).</summary>
    public static BitmapSource Square(BitmapSource picture)
    {
        var side = Math.Min(picture.PixelWidth, picture.PixelHeight);
        var square = new CroppedBitmap(new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0),
                                       new System.Windows.Int32Rect((picture.PixelWidth - side) / 2, (picture.PixelHeight - side) / 2, side, side));
        var pixels = new byte[side * side * 4];
        square.CopyPixels(pixels, side * 4, 0);
        return Blur(pixels, side, side);
    }

    /// <summary>BGRA rows from the top, averaged in blocks and softened; the result is opaque.</summary>
    public static BitmapSource Blur(byte[] bgra, int width, int height)
    {
        var block = Math.Max(1, Math.Max(width, height) / 32);
        var (w, h) = ((width + block - 1) / block, (height + block - 1) / block);
        var sums = new double[w * h * 3];
        var counts = new int[w * h];
        for (var y = 0; y < height; y++)
        {
            var row = y / block * w;
            for (var x = 0; x < width; x++)
            {
                var (cell, at) = (row + x / block, (y * width + x) * 4);
                sums[cell * 3] += bgra[at];
                sums[cell * 3 + 1] += bgra[at + 1];
                sums[cell * 3 + 2] += bgra[at + 2];
                counts[cell]++;
            }
        }
        for (var cell = 0; cell < counts.Length; cell++)
            for (var c = 0; c < 3; c++) sums[cell * 3 + c] /= counts[cell];
        // Twice 1-2-1 across and down, so the blocks don't show once stretched.
        for (var pass = 0; pass < 2; pass++)
        {
            sums = Soften(sums, w, h, 1, 0);
            sums = Soften(sums, w, h, 0, 1);
        }
        var small = new byte[w * h * 4];
        for (var cell = 0; cell < w * h; cell++)
        {
            for (var c = 0; c < 3; c++) small[cell * 4 + c] = (byte)Math.Round(sums[cell * 3 + c]);
            small[cell * 4 + 3] = 255;
        }
        var frosted = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, small, w * 4);
        frosted.Freeze();
        return frosted;
    }

    private static double[] Soften(double[] values, int w, int h, int dx, int dy)
    {
        var result = new double[values.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (before, after) = ((Math.Max(0, y - dy) * w + Math.Max(0, x - dx)) * 3, (Math.Min(h - 1, y + dy) * w + Math.Min(w - 1, x + dx)) * 3);
                var at = (y * w + x) * 3;
                for (var c = 0; c < 3; c++) result[at + c] = (values[before + c] + 2 * values[at + c] + values[after + c]) / 4;
            }
        return result;
    }
}
