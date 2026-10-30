using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Split.App;

/// <summary>Split's mark drawn on a screen: the shapes come from <see cref="LogoGeometry"/>.</summary>
public static class Logo
{
    /// <summary>Slice <paramref name="index"/> of a wheel of <paramref name="radius"/> around (cx, cy); y points down here.</summary>
    public static Geometry Slice(int index, double cx, double cy, double radius, double? gap = null)
    {
        var outline = LogoGeometry.Slice(index, new LogoPoint(cx, cy), radius, gap);
        // The outline is drawn with y up: mirror it about the centre.
        Point At(LogoPoint p) => new(p.X, 2 * cy - p.Y);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(At(outline.Start), isFilled: true, isClosed: true);
            foreach (var segment in outline.Segments)
            {
                if (segment.Radius == 0) context.LineTo(At(segment.End), true, false);
                else context.ArcTo(At(segment.End), new Size(segment.Radius, segment.Radius), 0, segment.LargeArc, SweepDirection.Clockwise, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The sizes of the application icon: what Windows asks for at 100% to 250% scale in lists, the taskbar and the title bar.</summary>
    public static readonly int[] AppIconSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    /// <summary>
    /// The tray's wheel as an .ico holding one PNG: the chosen slice solid, the others faint. White on a dark
    /// taskbar, black on a light one.
    /// </summary>
    public static byte[] TrayIcon(int pixels, bool lightTaskbar)
    {
        var ink = lightTaskbar ? Colors.Black : Colors.White;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            for (var index = 0; index < LogoGeometry.Slices; index++)
            {
                var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round((index == 0 ? 1 : 0.38) * 255), ink.R, ink.G, ink.B));
                context.DrawGeometry(brush, null, Slice(index, pixels / 2.0, pixels / 2.0, pixels / 2.0 - 0.5 * pixels / 18, 0.16));
            }
        }
        return Ico([(pixels, Png(visual, pixels))]);
    }

    /// <summary>The application icon: the wheel, the chosen slice white and the others grey, on a black rounded square. Small sizes are lighter and have wider gaps so the slices stay apart.</summary>
    public static byte[] AppIcon(IEnumerable<int> sizes) => Ico([.. sizes.Select(pixels => (pixels, Png(AppIconVisual(pixels), pixels)))]);

    public static DrawingVisual AppIconVisual(int pixels)
    {
        var visual = new DrawingVisual();
        using var context = visual.RenderOpen();
        var margin = pixels <= 24 ? 0.5 : pixels / 32.0;
        var body = pixels - 2 * margin;
        context.DrawRoundedRectangle(Brushes.Black, null, new Rect(margin, margin, body, body), body * 0.22, body * 0.22);
        // Small sizes get lighter slices and wider gaps: at 16 pixels the dark grey would blend into the black.
        var small = pixels <= 32;
        var grey = (byte)Math.Round((small ? 0.5 : LogoGeometry.Grey) * 255);
        for (var index = 0; index < LogoGeometry.Slices; index++)
        {
            var brush = index == 0 ? Brushes.White : new SolidColorBrush(Color.FromRgb(grey, grey, grey));
            context.DrawGeometry(brush, null, Slice(index, pixels / 2.0, pixels / 2.0, body * (small ? 0.4 : 0.36), small ? 0.2 : pixels < 64 ? 0.14 : null));
        }
        return visual;
    }

    public static byte[] Png(Visual visual, int pixels)
    {
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        return png.ToArray();
    }

    /// <summary>An .ico of PNG images (Windows has read them since Vista).</summary>
    private static byte[] Ico(IReadOnlyList<(int Pixels, byte[] Png)> images)
    {
        using var icon = new MemoryStream();
        using var writer = new BinaryWriter(icon);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)images.Count);
        var offset = 6u + 16u * (uint)images.Count;
        foreach (var (pixels, png) in images)
        {
            writer.Write((byte)(pixels >= 256 ? 0 : pixels)); writer.Write((byte)(pixels >= 256 ? 0 : pixels));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write((uint)png.Length); writer.Write(offset);
            offset += (uint)png.Length;
        }
        foreach (var (_, png) in images) writer.Write(png);
        return icon.ToArray();
    }
}
