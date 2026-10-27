namespace Split;

/// <summary>A point of the wheel, relative to its centre with y pointing up.</summary>
public readonly record struct Pt(double X, double Y);

/// <summary>A rectangle of the wheel, relative to its centre with y pointing up (so <see cref="MinY"/> is its bottom).</summary>
public readonly record struct Rc(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MaxX => X + Width;
    public double MinY => Y;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;

    public Rc OffsetBy(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public Rc InsetBy(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public Rc Union(Rc other)
    {
        var (x, y) = (Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY));
        return new Rc(x, y, Math.Max(MaxX, other.MaxX) - x, Math.Max(MaxY, other.MaxY) - y);
    }

    public bool Intersects(Rc other) => MinX < other.MaxX && other.MinX < MaxX && MinY < other.MaxY && other.MinY < MaxY;
}

/// <summary>What the wheel looks like: its size, and whether names show under the icons.</summary>
public readonly record struct WheelStyle(WheelSize Size = WheelSize.Medium, bool Names = true)
{
    public static WheelStyle Current => new(Settings.WheelSize, Settings.WheelNames);
}

/// <summary>How wide and tall names are when drawn. The app measures with the real font; the tests with a stand-in.</summary>
public interface ITextMeasurer
{
    /// <summary>The height of <paramref name="lines"/> lines of text.</summary>
    double Height(int lines, double fontSize, bool bold);

    /// <summary>The size of <paramref name="text"/> centred and wrapped between words at <paramref name="width"/>.</summary>
    (double Width, double Height) Measure(string text, double fontSize, bool bold, double width);
}

/// <summary>Caches text sizes: the same name is measured at the same widths again and again.</summary>
public sealed class Measurer(ITextMeasurer text)
{
    private readonly Dictionary<string, (double Width, double Height)> sizes = [];
    private readonly Dictionary<string, double> heights = [];

    public double Height(int lines, double fontSize, bool bold)
    {
        var key = $"{lines}|{fontSize}|{bold}";
        if (!heights.TryGetValue(key, out var height)) heights[key] = height = Math.Ceiling(text.Height(lines, fontSize, bold));
        return height;
    }

    public (double Width, double Height) Size(string title, double fontSize, bool bold, double width)
    {
        var key = $"{fontSize}|{bold}|{width}|{title}";
        if (!sizes.TryGetValue(key, out var size))
        {
            var measured = text.Measure(title, fontSize, bold, width);
            sizes[key] = size = (Math.Ceiling(measured.Width), Math.Ceiling(measured.Height));
        }
        return size;
    }

    /// <summary>On one line.</summary>
    public double Width(string title, double fontSize, bool bold) => Size(title, fontSize, bold, 10_000).Width + 2;
}

/// <summary>
/// Geometry of the wheel and where each slice's icon and name go. The size is the user's, so a
/// wheel never changes size with what it shows. Every icon sits on one ring and every name right
/// under its icon, all in one font, on up to two lines; a name too long for its slice ends in
/// "…" (the centre shows the whole name of the slice under the pointer). The ring runs through the
/// middle of the icon and its two lines of name together, not through the icon alone, so the pair
/// is centred in its slice. A slice without an icon centres its name on the middle of the ring.
/// Nothing comes closer than <see cref="Clearance"/> to the lines around its slice, so it stays inside the
/// selection when the slice pops out.
/// </summary>
public sealed class WheelLayout
{
    public sealed record Slice
    {
        /// <summary>Centre of the icon, relative to the wheel's centre (y up); null without one.</summary>
        public Pt? Icon { get; init; }

        /// <summary>The name's box, relative to the wheel's centre; null when the name isn't shown.</summary>
        public Rc? Text { get; init; }

        /// <summary>The name doesn't fit whole and ends in "…".</summary>
        public bool Truncated { get; init; }
    }

    public const double RimMargin = 12, WindowMargin = 10;

    /// <summary>How far the chosen slice moves outwards.</summary>
    public const double Pop = 2.5;

    public const double Clearance = 6, SymbolGap = 3;
    public const int MaxLines = 2;

    public int Count { get; private init; }
    public WheelStyle Style { get; private init; }
    public double Inner { get; private init; }
    public double Outer { get; private init; }

    /// <summary>The names under icons; <see cref="LabelFontSize"/> is for slices that have only a name.</summary>
    public double FontSize { get; private init; }

    public double LabelFontSize { get; private init; }
    public double SymbolSize { get; private init; }

    /// <summary>The room an icon takes, the same for every slice so the names line up.</summary>
    public (double Width, double Height) IconBox { get; private init; }

    /// <summary>Distance from the centre to the middle of every icon and its name together.</summary>
    public double IconRadius { get; private init; }

    /// <summary>
    /// How far an icon, and its name with it, sits above that ring, which is the middle of the pair:
    /// the same for all of them, so the names still line up. None without names under the icons, and
    /// none on a wheel so crowded that the pair no longer fits between its lines.
    /// </summary>
    public double IconLift { get; private init; }

    public IReadOnlyList<Slice> Slices { get; private set; } = [];

    public double Disc => Outer + RimMargin;
    public double Size => 2 * (Disc + WindowMargin);
    public double Step => 2 * Math.PI / Math.Max(Count, 1);

    /// <summary>1 at the medium size: text and icons grow and shrink with the wheel.</summary>
    public double Scale => Outer / OuterRadius(WheelSize.Medium);

    /// <summary>Text and icons grow more slowly than the wheel, so a bigger wheel also has more room for names.</summary>
    public double TextScale => Math.Sqrt(Scale);

    public static double OuterRadius(WheelSize size) => size switch
    {
        WheelSize.ExtraSmall => 146,
        WheelSize.Small => 166,
        WheelSize.Medium => 186,
        WheelSize.Large => 210,
        _ => 236,
    };

    public static double InnerRadius(WheelSize size) => Round(OuterRadius(size) * 0.36);

    /// <summary>The side of the window a wheel of this size needs.</summary>
    public static double WindowSize(WheelSize size) => 2 * (OuterRadius(size) + RimMargin + WindowMargin);

    public double AngleOf(int index) => (index + 0.5) * Step;

    public int? SliceAt(Pt offset) => SliceAt(offset, Count, Style.Size);

    public bool IsOnHub(Pt offset) => IsOnHub(offset, Style.Size);

    /// <summary>
    /// The slice under a point given relative to the centre (y up), measured clockwise from
    /// 12 o'clock. A generous margin outside the ring still counts; the hub doesn't. It needs
    /// only the size, so a picture of the wheel can answer without laying out its names.
    /// </summary>
    public static int? SliceAt(Pt offset, int count, WheelSize size)
    {
        if (count <= 0) return null;
        var distance = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
        if (distance < InnerRadius(size) - 4 || distance > OuterRadius(size) + RimMargin + 24) return null;
        var angle = Math.PI / 2 - Math.Atan2(offset.Y, offset.X);
        if (angle < 0) angle += 2 * Math.PI;
        return (int)(angle / (2 * Math.PI / count)) % count;
    }

    public static bool IsOnHub(Pt offset, WheelSize size) => Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y) < InnerRadius(size) - 4;

    /// <summary>The icon and name of slice <paramref name="index"/> together, relative to the wheel's centre, moved outwards by <paramref name="popped"/>.</summary>
    public Rc Frame(int index, double popped = 0)
    {
        var slice = Slices[index];
        Rc? frame = slice.Text;
        if (slice.Icon is { } icon)
        {
            var box = new Rc(icon.X - IconBox.Width / 2, icon.Y - IconBox.Height / 2, IconBox.Width, IconBox.Height);
            frame = frame is { } text ? text.Union(box) : box;
        }
        return (frame ?? default).OffsetBy(popped * Math.Sin(AngleOf(index)), popped * Math.Cos(AngleOf(index)));
    }

    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    private static double Half(double value) => Round(value * 2) / 2;

    // Building

    /// <summary>
    /// When there isn't room for a name under every icon, not even shortened, the wheel shows
    /// icons only (<see cref="Style"/>.Names is then false); the centre still names the slice under the pointer.
    /// </summary>
    public static WheelLayout Make(IReadOnlyList<string> titles, IReadOnlyList<string?> symbols, WheelStyle style, ITextMeasurer text)
    {
        var layout = Fitted(titles, symbols, style, text);
        if (!style.Names || !layout.Slices.Any(s => s.Icon is not null && s.Text is null)) return layout;
        return Fitted(titles, symbols, new WheelStyle(style.Size, false), text);
    }

    private static IEnumerable<double> Stride(double from, double through, double by)
    {
        for (var n = 0; from + n * by <= through + 1e-9; n++) yield return from + n * by;
    }

    private static IEnumerable<double> StrideDown(double from, double through, double by)
    {
        for (var n = 0; from - n * by >= through - 1e-9; n++) yield return from - n * by;
    }

    /// <summary>
    /// Every icon has the same size and sits on the same ring: the one closest to the preferred radius
    /// where the most names fit whole under their icons. A crowded wheel gets smaller icons,
    /// never icons out of line.
    /// </summary>
    private static WheelLayout Fitted(IReadOnlyList<string> titles, IReadOnlyList<string?> symbols, WheelStyle style, ITextMeasurer text)
    {
        var outer = OuterRadius(style.Size);
        var inner = InnerRadius(style.Size);
        var scale = outer / OuterRadius(WheelSize.Medium);
        var textScale = Math.Sqrt(scale);
        var fontSize = Math.Max(9.5, Half(11 * textScale));
        var labelFontSize = Math.Max(10.5, Half(13 * textScale));
        var measurer = new Measurer(text);
        var names = measurer.Height(MaxLines, fontSize, false);
        var middle = (inner + outer) / 2;
        var named = style.Names && symbols.Any(s => s is not null);
        var pairLift = (SymbolGap + names) / 2;
        var symbolSize = Round((style.Names ? 19 : 24) * textScale);
        WheelLayout Make(double size, (double, double) box, double radius, double lift) => new()
        {
            Count = titles.Count, Style = style, Inner = inner, Outer = outer, FontSize = fontSize, LabelFontSize = labelFontSize,
            SymbolSize = size, IconBox = box, IconRadius = radius, IconLift = lift,
        };
        while (true)
        {
            var box = (Width: Round(symbolSize * 1.35), Height: Round(symbolSize * 1.2));
            // As far out as two lines of name allow, where the slices are widest.
            var preferred = named ? Math.Max(middle, outer - Clearance - box.Height / 2 - SymbolGap - names) : middle;
            var radii = named
                ? Stride(inner + Clearance + box.Height / 2, outer - Clearance - box.Height / 2, 2).OrderBy(r => Math.Abs(r - preferred)).ToList()
                : [preferred];
            (WheelLayout Layout, int Missing, int Truncated)? best = null;
            // Centred as a pair when there is room for that, else with the icon itself on the ring.
            foreach (var lift in named ? new[] { pairLift, 0 } : [0])
            {
                if (best is { } done && done.Missing + done.Truncated == 0) break;
                foreach (var radius in radii)
                {
                    var layout = Make(symbolSize, box, radius, lift);
                    if (!layout.IconsFit(symbols)) continue;
                    layout.FitSlices(titles, symbols, measurer);
                    var missing = layout.Slices.Count(s => s.Text is null && (s.Icon is null || named));
                    var truncated = layout.Slices.Count(s => s.Truncated);
                    if (best is null || (missing, truncated).CompareTo((best.Value.Missing, best.Value.Truncated)) < 0) best = (layout, missing, truncated);
                    if (missing == 0 && truncated == 0) break;
                }
            }
            if (best is { } found) return found.Layout;
            if (symbolSize <= 8)
            {
                var last = Make(symbolSize, box, preferred, 0);
                last.FitSlices(titles, symbols, measurer);
                return last;
            }
            symbolSize -= 1;
        }
    }

    private bool IconsFit(IReadOnlyList<string?> symbols) => symbols.Select((symbol, index) => (symbol, index)).All(item =>
    {
        if (item.symbol is null) return true;
        var icon = IconCentre(item.index);
        return Clear(new Rc(icon.X - IconBox.Width / 2, icon.Y - IconBox.Height / 2, IconBox.Width, IconBox.Height), item.index);
    });

    private Pt IconCentre(int index) => new(IconRadius * Math.Sin(AngleOf(index)), IconRadius * Math.Cos(AngleOf(index)) + IconLift);

    private void FitSlices(IReadOnlyList<string> titles, IReadOnlyList<string?> symbols, Measurer measurer)
    {
        var named = Enumerable.Range(0, titles.Count).Where(i => symbols[i] is null).ToList();
        var nameRadius = named.Count == 0 ? 0 : RoomiestRadius(named, measurer);
        var slices = new List<Slice>();
        for (var index = 0; index < Count; index++)
        {
            if (symbols[index] is not null)
            {
                var icon = IconCentre(index);
                var top = icon.Y - IconBox.Height / 2 - SymbolGap;
                var (text, truncated) = Style.Names
                    ? FitName(titles[index], index, false, measurer, (width, height) => new Rc(icon.X - width / 2, top - height, width, height))
                    : (null, false);
                slices.Add(new Slice { Icon = icon, Text = text, Truncated = truncated });
            }
            else
            {
                var anchor = new Pt(nameRadius * Math.Sin(AngleOf(index)), nameRadius * Math.Cos(AngleOf(index)));
                var (text, truncated) = FitName(titles[index], index, true, measurer, (width, height) => new Rc(anchor.X - width / 2, anchor.Y - height / 2, width, height));
                slices.Add(new Slice { Text = text, Truncated = truncated });
            }
        }
        Slices = slices;
    }

    /// <summary>
    /// The one radius for names without icons where the most crowded of them has the most room:
    /// the middle of the ring, unless a crowded wheel needs them further out, where slices are wider.
    /// </summary>
    private double RoomiestRadius(List<int> indices, Measurer measurer)
    {
        var middle = (Inner + Outer) / 2;
        var lineHeight = measurer.Height(1, LabelFontSize, true);
        var (radius, room) = (middle, -1.0);
        foreach (var candidate in Stride(middle, Outer - Clearance - lineHeight / 2, 2))
        {
            var found = indices.Select(index =>
            {
                var anchor = new Pt(candidate * Math.Sin(AngleOf(index)), candidate * Math.Cos(AngleOf(index)));
                return WidestClear(index, true, 1, measurer, (width, height) => new Rc(anchor.X - width / 2, anchor.Y - height / 2, width, height)) ?? 0;
            }).Min();
            if (found > room + 0.5) (radius, room) = (candidate, found);
            if (found >= Round(120 * Scale)) break;
        }
        return radius;
    }

    /// <summary>The widest one-line box <paramref name="place"/> makes that stays clear of slice <paramref name="index"/>'s lines.</summary>
    private double? WidestClear(int index, bool bold, int lines, Measurer measurer, Func<double, double, Rc> place)
    {
        var size = bold ? LabelFontSize : FontSize;
        var height = measurer.Height(lines, size, bold);
        var narrowest = Round(size * 2);
        foreach (var width in StrideDown(Round(120 * Scale), narrowest, 2))
        {
            if (Clear(place(width, height), index)) return width;
        }
        return null;
    }

    /// <summary>
    /// The widest box <paramref name="place"/> makes for one line and for two that stays clear of the slice's
    /// lines, and the name in the one that shows the most of it.
    /// </summary>
    private (Rc?, bool) FitName(string title, int index, bool bold, Measurer measurer, Func<double, double, Rc> place)
    {
        var size = bold ? LabelFontSize : FontSize;
        var oneHeight = measurer.Height(1, size, bold);
        var twoHeight = measurer.Height(2, size, bold);
        var oneLine = measurer.Width(title, size, bold);
        var one = WidestClear(index, bold, 1, measurer, place);
        if (one is { } fits && oneLine <= fits) return (place(oneLine, oneHeight), false);
        // Two lines break between words only: a word wider than the box would be split in two.
        var words = title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => measurer.Width(w, size, bold)).ToList();
        if (MaxLines > 1 && WidestClear(index, bold, 2, measurer, place) is { } two)
        {
            var wrapped = measurer.Size(title, size, bold, two);
            if (wrapped.Height <= twoHeight + 0.5 && words.All(w => w <= two)) return (place(Math.Min(two, wrapped.Width + 2), twoHeight), false);
            if ((words.Count > 0 ? words[0] : 0) <= two && two * 2 >= (one ?? 0)) return (place(two, twoHeight), true);
        }
        return (one is { } width ? place(width, oneHeight) : null, true);
    }

    // Fitting

    /// <summary>Inside the ring, away from the hub and the rim, and not touching either line of slice <paramref name="index"/>.</summary>
    public bool Clear(Rc rect, int index)
    {
        var margin = Clearance;
        Pt[] corners = [new(rect.MinX, rect.MinY), new(rect.MaxX, rect.MinY), new(rect.MinX, rect.MaxY), new(rect.MaxX, rect.MaxY)];
        if (!corners.All(c => Math.Sqrt(c.X * c.X + c.Y * c.Y) <= Outer - margin)) return false;
        var (nx, ny) = (Math.Min(Math.Max(0, rect.MinX), rect.MaxX), Math.Min(Math.Max(0, rect.MinY), rect.MaxY));
        if (Math.Sqrt(nx * nx + ny * ny) < Inner + margin) return false;
        if (Count <= 1) return true;
        var padded = rect.InsetBy(-margin, -margin);
        foreach (var edge in new[] { index * Step, (index + 1) * Step })
        {
            var start = new Pt(Inner * Math.Sin(edge), Inner * Math.Cos(edge));
            var end = new Pt(Disc * Math.Sin(edge), Disc * Math.Cos(edge));
            if (SegmentIntersects(start, end, padded)) return false;
        }
        return true;
    }

    /// <summary>Liang–Barsky: does the segment pass through the rectangle?</summary>
    public static bool SegmentIntersects(Pt a, Pt b, Rc rect)
    {
        double t0 = 0, t1 = 1;
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        foreach (var (p, q) in new[] { (-dx, a.X - rect.MinX), (dx, rect.MaxX - a.X), (-dy, a.Y - rect.MinY), (dy, rect.MaxY - a.Y) })
        {
            if (p == 0)
            {
                if (q < 0) return false;
            }
            else
            {
                var t = q / p;
                if (p < 0) t0 = Math.Max(t0, t); else t1 = Math.Min(t1, t);
                if (t0 > t1) return false;
            }
        }
        return true;
    }
}
