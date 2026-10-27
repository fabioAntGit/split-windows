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
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);

        using var icon = new MemoryStream();
        using var writer = new BinaryWriter(icon);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((byte)(pixels >= 256 ? 0 : pixels)); writer.Write((byte)(pixels >= 256 ? 0 : pixels));
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write((uint)png.Length); writer.Write(22u);
        writer.Write(png.ToArray());
        return icon.ToArray();
    }
}
