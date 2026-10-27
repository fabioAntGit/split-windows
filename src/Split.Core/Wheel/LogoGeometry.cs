namespace Split;

public readonly record struct LogoPoint(double X, double Y);

/// <summary>One edge of a slice: a straight line, or a clockwise arc of <see cref="Radius"/> (0 for a line) ending at <see cref="End"/>.</summary>
public readonly record struct LogoSegment(LogoPoint End, double Radius = 0, bool LargeArc = false);

/// <summary>A closed outline, drawn with y pointing up: flip y to draw it on a screen.</summary>
public sealed record LogoOutline(LogoPoint Start, IReadOnlyList<LogoSegment> Segments);

/// <summary>
/// Split's mark: six rounded slices of a wheel, the one from twelve to two o'clock chosen. The
/// tray icon draws it from here, and so does the application icon.
/// </summary>
public static class LogoGeometry
{
    public const int Slices = 6;

    /// <summary>The slices that are not chosen: 70/255 grey.</summary>
    public const double Grey = 70.0 / 255;

    // The wheel's proportions, against its outer radius: where the slices' round inner ends
    // reach, the space between slices and the rounding of their outer corners.
    private const double Tip = 0.336;
    private const double DefaultGap = 0.083;
    private const double Rounding = 0.117;

    /// <summary>
    /// Slice <paramref name="index"/>, counted clockwise from the chosen one, in a wheel of
    /// <paramref name="radius"/> around the centre. Small wheels ask for a wider <paramref name="gap"/>
    /// (a share of the radius), so the slices stay apart.
    /// </summary>
    public static LogoOutline Slice(int index, LogoPoint center, double radius, double? gap = null)
    {
        var step = 2 * Math.PI / Slices;
        var upper = Math.PI / 2 - index * step;
        var lower = upper - step;
        var half = (gap ?? DefaultGap) * radius / 2;
        var corner = Rounding * radius;
        var spread = Math.Sin(step / 2);

        // Each side runs parallel to its slice's border, `half` inside it.
        static LogoPoint Ray(double angle) => new(Math.Cos(angle), Math.Sin(angle));
        var upperNormal = new LogoPoint(Math.Sin(upper), -Math.Cos(upper));
        var lowerNormal = new LogoPoint(-Math.Sin(lower), Math.Cos(lower));
        static LogoPoint Point(LogoPoint normal, double across, double angle, double along)
        {
            var direction = Ray(angle);
            return new(normal.X * across + direction.X * along, normal.Y * across + direction.Y * along);
        }

        // The rounding of an outer corner: its centre, and where it touches the side and the rim.
        (LogoPoint Center, LogoPoint OnSide, LogoPoint OnRim) OuterCorner(double angle, LogoPoint normal)
        {
            var along = Math.Sqrt((radius - corner) * (radius - corner) - (half + corner) * (half + corner));
            var at = Point(normal, half + corner, angle, along);
            var onRim = new LogoPoint(at.X / (radius - corner) * radius, at.Y / (radius - corner) * radius);
            return (at, Point(normal, half, angle, along), onRim);
        }

        // The inner end is one round arc touching both sides, its nearest point at `Tip`.
        var end = (Tip * radius - half / spread) / (1 / spread - 1);
        var endAlong = (half + end) / spread * Math.Cos(step / 2);
        var middle = Ray((upper + lower) / 2);
        var endCenter = new LogoPoint(middle.X * (half + end) / spread, middle.Y * (half + end) / spread);
        var upperOuter = OuterCorner(upper, upperNormal);
        var lowerOuter = OuterCorner(lower, lowerNormal);
        var upperInner = Point(upperNormal, half, upper, endAlong);
        var lowerInner = Point(lowerNormal, half, lower, endAlong);

        LogoPoint Shifted(LogoPoint p) => new(center.X + p.X, center.Y + p.Y);
        // Clockwise from one point to the other around `around`: "large" when it goes the long way.
        LogoSegment Arc(LogoPoint around, double arcRadius, LogoPoint from, LogoPoint to)
        {
            var start = Math.Atan2(from.Y - around.Y, from.X - around.X);
            var finish = Math.Atan2(to.Y - around.Y, to.X - around.X);
            var sweep = start - finish;
            while (sweep < 0) sweep += 2 * Math.PI;
            return new(Shifted(to), arcRadius, sweep > Math.PI);
        }

        return new LogoOutline(Shifted(upperInner),
        [
            new(Shifted(upperOuter.OnSide)),
            Arc(upperOuter.Center, corner, upperOuter.OnSide, upperOuter.OnRim),
            Arc(new(0, 0), radius, upperOuter.OnRim, lowerOuter.OnRim),
            Arc(lowerOuter.Center, corner, lowerOuter.OnRim, lowerOuter.OnSide),
            new(Shifted(lowerInner)),
            Arc(endCenter, end, lowerInner, upperInner),
        ]);
    }
}
