namespace Split;

/// <summary>A point, in whatever unit the caller works in; the editors keep their marks in 0…1 of the picture, from its top-left corner.</summary>
public readonly record struct NPt(double X, double Y);

public readonly record struct NSz(double Width, double Height);

/// <summary>A rectangle from its top-left corner, in doubles so that the editors' maths comes out the same in the preview and in the saved file.</summary>
public readonly record struct NBox(double X, double Y, double Width, double Height)
{
    /// <summary>The whole picture, in 0…1.</summary>
    public static readonly NBox Full = new(0, 0, 1, 1);

    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;

    public bool Contains(NPt point) => point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;

    /// <summary>Grown by <paramref name="dx"/> and <paramref name="dy"/> on every side (shrunk when negative).</summary>
    public NBox Inset(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public NBox Intersect(NBox other)
    {
        var left = Math.Max(MinX, other.MinX);
        var top = Math.Max(MinY, other.MinY);
        var right = Math.Min(MaxX, other.MaxX);
        var bottom = Math.Min(MaxY, other.MaxY);
        return right <= left || bottom <= top ? new NBox(0, 0, 0, 0) : new NBox(left, top, right - left, bottom - top);
    }

    /// <summary>The box around a set of points; empty when there are none.</summary>
    public static NBox Around(IReadOnlyList<NPt> points)
    {
        if (points.Count == 0) return new NBox(0, 0, 0, 0);
        double left = points[0].X, right = left, top = points[0].Y, bottom = top;
        foreach (var p in points)
        {
            left = Math.Min(left, p.X);
            right = Math.Max(right, p.X);
            top = Math.Min(top, p.Y);
            bottom = Math.Max(bottom, p.Y);
        }
        return new NBox(left, top, right - left, bottom - top);
    }

    /// <summary>Scaled from a 0…1 box to the pixels of a picture of <paramref name="size"/>.</summary>
    public NBox In(NSz size) => new(X * size.Width, Y * size.Height, Width * size.Width, Height * size.Height);

    /// <summary>Expanded outwards to whole pixels, as an image crop needs.</summary>
    public NBox Integral()
    {
        var left = Math.Floor(MinX);
        var top = Math.Floor(MinY);
        return new NBox(left, top, Math.Ceiling(MaxX) - left, Math.Ceiling(MaxY) - top);
    }
}

public static class Geometry
{
    /// <summary>The rectangle that fits a picture of <paramref name="size"/> into <paramref name="container"/>, centred, with a margin.</summary>
    public static NBox Fit(NSz size, NSz container, double margin = 24)
    {
        if (size.Width <= 0 || size.Height <= 0) return new NBox(0, 0, 0, 0);
        var availableWidth = Math.Max(1, container.Width - margin * 2);
        var availableHeight = Math.Max(1, container.Height - margin * 2);
        var scale = Math.Min(availableWidth / size.Width, availableHeight / size.Height);
        var (width, height) = (size.Width * scale, size.Height * scale);
        return new NBox((container.Width - width) / 2, (container.Height - height) / 2, width, height);
    }
}
