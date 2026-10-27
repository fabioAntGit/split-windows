using SkiaSharp;

namespace Split;

public enum MarkupTool { Select, Arrow, Line, Rectangle, Ellipse, Pen, Highlighter, Text, Counter, Blur, Pixelate, Redact }

public static class MarkupTools
{
    public static readonly IReadOnlyList<MarkupTool> All = Enum.GetValues<MarkupTool>();

    public static string Title(this MarkupTool tool) => tool switch
    {
        MarkupTool.Select => L("Select and Move"),
        MarkupTool.Arrow => L("Arrow"),
        MarkupTool.Line => L("Line"),
        MarkupTool.Rectangle => L("Rectangle"),
        MarkupTool.Ellipse => L("Ellipse"),
        MarkupTool.Pen => L("Pen"),
        MarkupTool.Highlighter => L("Highlighter"),
        MarkupTool.Text => L("Text"),
        MarkupTool.Counter => L("Number"),
        MarkupTool.Blur => L("Blur Area"),
        MarkupTool.Pixelate => L("Pixelate Area"),
        _ => L("Cover Area"),
    };

    public static string Symbol(this MarkupTool tool) => tool switch
    {
        MarkupTool.Select => "cursorarrow",
        MarkupTool.Arrow => "arrow.up.right",
        MarkupTool.Line => "line.diagonal",
        MarkupTool.Rectangle => "rectangle",
        MarkupTool.Ellipse => "circle",
        MarkupTool.Pen => "scribble",
        MarkupTool.Highlighter => "highlighter",
        MarkupTool.Text => "textformat",
        MarkupTool.Counter => "1.circle",
        MarkupTool.Blur => "drop",
        MarkupTool.Pixelate => "square.grid.3x3.fill",
        _ => "rectangle.fill",
    };

    /// <summary>Tools that define an area (drawn from corner to corner).</summary>
    public static bool IsArea(this MarkupTool tool) => tool is MarkupTool.Rectangle or MarkupTool.Ellipse or MarkupTool.Blur or MarkupTool.Pixelate or MarkupTool.Redact;
}

/// <summary>One annotation. Points are normalised to the picture (0…1, top-left origin).</summary>
public sealed record Mark(Guid Id, MarkupTool Tool, IReadOnlyList<NPt> Points, SKColor Color, double Width)
{
    public string Text { get; init; } = "";
    public int Number { get; init; }

    public static Mark Create(MarkupTool tool, IReadOnlyList<NPt> points, SKColor color, double width) => new(Guid.NewGuid(), tool, points, color, width);

    public NBox Bounds => NBox.Around(Points);

    public Mark Translated(NSz delta) => this with { Points = Points.Select(p => new NPt(p.X + delta.Width, p.Y + delta.Height)).ToList() };

    // The points are compared by value: a mark that was moved and put back is the mark it was.
    public bool Equals(Mark? other) =>
        other is not null && Id == other.Id && Tool == other.Tool && Color == other.Color && Width == other.Width && Text == other.Text
        && Number == other.Number && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Id, Tool, Color, Width, Text, Number);
}

public static class MarkupRenderer
{
    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private static readonly Lazy<SKTypeface> SemiBold = new(() => SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default);
    private static readonly Lazy<SKTypeface> Bold = new(() => SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default);

    /// <summary>Blurred and pixelated copies of the whole picture; privacy marks reveal them inside their area.</summary>
    public static (SKBitmap Blurred, SKBitmap Pixelated) PrivacyLayers(SKBitmap image)
    {
        var longest = Math.Max(image.Width, image.Height);
        return (Blur(image, longest * 0.02f), Pixelate(image, Math.Max(8, longest / 55.0)));
    }

    /// <summary>A Gaussian blur whose edges keep the colour of the picture's border instead of fading.</summary>
    public static SKBitmap Blur(SKBitmap image, float sigma)
    {
        var result = EditorImages.Blank(image.Width, image.Height);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) };
        EditorImages.Draw(canvas, image, 0, 0, paint);
        return result;
    }

    /// <summary>Squares of <paramref name="cell"/> pixels, each the average of what it covers, counted from the bottom-left corner.</summary>
    public static SKBitmap Pixelate(SKBitmap image, double cell)
    {
        var (width, height) = (image.Width, image.Height);
        var result = EditorImages.Blank(width, height);
        var source = image.Pixels;
        var target = new SKColor[source.Length];
        var side = Math.Max(1, (int)Math.Round(cell));
        var rows = (height + side - 1) / side;
        Parallel.For(0, rows, row =>
        {
            var bottom = height - row * side;
            var top = Math.Max(0, bottom - side);
            for (var left = 0; left < width; left += side)
            {
                var right = Math.Min(width, left + side);
                long a = 0, r = 0, g = 0, b = 0;
                for (var y = top; y < bottom; y++)
                {
                    for (var x = left; x < right; x++)
                    {
                        var pixel = source[y * width + x];
                        a += pixel.Alpha; r += pixel.Red; g += pixel.Green; b += pixel.Blue;
                    }
                }
                var count = (bottom - top) * (right - left);
                var average = new SKColor((byte)(r / count), (byte)(g / count), (byte)(b / count), (byte)(a / count));
                for (var y = top; y < bottom; y++) for (var x = left; x < right; x++) target[y * width + x] = average;
            }
        });
        result.Pixels = target;
        return result;
    }

    public static bool NeedsPrivacyLayers(IEnumerable<Mark> marks) => marks.Any(m => m.Tool is MarkupTool.Blur or MarkupTool.Pixelate);

    /// <summary>A copy of <paramref name="baseImage"/> with the marks drawn on it; <paramref name="hiding"/> is a mark that is being edited elsewhere.</summary>
    public static SKBitmap Render(SKBitmap baseImage, IEnumerable<Mark> marks, SKBitmap? blurred = null, SKBitmap? pixelated = null, Guid? hiding = null)
    {
        var size = new NSz(baseImage.Width, baseImage.Height);
        var result = EditorImages.Blank(baseImage.Width, baseImage.Height);
        using var canvas = new SKCanvas(result);
        EditorImages.Draw(canvas, baseImage, 0, 0);
        var scale = Math.Max(size.Width, size.Height) / 1000;
        foreach (var mark in marks.Where(m => m.Id != hiding)) Draw(mark, canvas, size, scale, blurred, pixelated);
        return result;
    }

    private static SKPoint ToPixels(NPt p, NSz size) => new((float)(p.X * size.Width), (float)(p.Y * size.Height));

    public static void Draw(Mark mark, SKCanvas canvas, NSz size, double scale, SKBitmap? blurred, SKBitmap? pixelated)
    {
        if (mark.Points.Count == 0) return;
        var points = mark.Points.Select(p => ToPixels(p, size)).ToList();
        var first = points[0];
        var last = points[^1];
        var lineWidth = (float)(mark.Width * scale);
        var area = new SKRect(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
        canvas.Save();
        using var stroke = new SKPaint
        {
            Color = mark.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = lineWidth, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };
        using var fill = new SKPaint { Color = mark.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        switch (mark.Tool)
        {
            case MarkupTool.Rectangle:
                canvas.DrawRect(Inset(area, lineWidth / 2), stroke);
                break;
            case MarkupTool.Ellipse:
                canvas.DrawOval(Inset(area, lineWidth / 2), stroke);
                break;
            case MarkupTool.Line:
                canvas.DrawLine(first, last, stroke);
                break;
            case MarkupTool.Arrow:
            {
                var length = Math.Sqrt(Math.Pow(last.X - first.X, 2) + Math.Pow(last.Y - first.Y, 2));
                if (length <= 1) break;
                var head = Math.Min(Math.Max(lineWidth * 4.2, 14 * scale), length * 0.6);
                var angle = Math.Atan2(last.Y - first.Y, last.X - first.X);
                var tail = new SKPoint((float)(last.X - head * 0.8 * Math.Cos(angle)), (float)(last.Y - head * 0.8 * Math.Sin(angle)));
                canvas.DrawLine(first, tail, stroke);
                const double spread = Math.PI / 7;
                var left = new SKPoint((float)(last.X - head * Math.Cos(angle - spread)), (float)(last.Y - head * Math.Sin(angle - spread)));
                var right = new SKPoint((float)(last.X - head * Math.Cos(angle + spread)), (float)(last.Y - head * Math.Sin(angle + spread)));
                var builder = new SKPathBuilder();
                builder.MoveTo(last);
                builder.LineTo(left);
                builder.LineTo(right);
                builder.Close();
                using var path = builder.Detach();
                canvas.DrawPath(path, fill);
                break;
            }
            case MarkupTool.Pen or MarkupTool.Highlighter:
            {
                if (mark.Tool == MarkupTool.Highlighter)
                {
                    stroke.StrokeWidth = lineWidth * 3.5f;
                    stroke.BlendMode = SKBlendMode.Multiply;
                    stroke.Color = mark.Color.WithAlpha(102);
                    stroke.StrokeCap = SKStrokeCap.Butt;
                }
                var builder = new SKPathBuilder();
                builder.MoveTo(first);
                foreach (var p in points.Skip(1)) builder.LineTo(p);
                if (points.Count == 1) builder.LineTo(new SKPoint(first.X + 0.1f, first.Y));
                using var path = builder.Detach();
                canvas.DrawPath(path, stroke);
                break;
            }
            case MarkupTool.Text:
            {
                using var font = new SKFont(SemiBold.Value, (float)((16 + mark.Width * 2.2) * scale));
                // The text hangs from the point, as a text box does.
                canvas.DrawText(mark.Text, first.X, first.Y - font.Metrics.Ascent, SKTextAlign.Left, font, fill);
                break;
            }
            case MarkupTool.Counter:
            {
                var radius = (float)((12 + mark.Width * 1.4) * scale);
                canvas.DrawCircle(first, radius, fill);
                using var font = new SKFont(Bold.Value, radius * 1.15f);
                using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
                var label = mark.Number.ToString();
                var metrics = font.Metrics;
                var height = metrics.CapHeight != 0 ? metrics.CapHeight : -metrics.Ascent * 0.7f;
                canvas.DrawText(label, first.X, first.Y + height / 2, SKTextAlign.Center, font, white);
                break;
            }
            case MarkupTool.Blur or MarkupTool.Pixelate:
            {
                var layer = mark.Tool == MarkupTool.Blur ? blurred : pixelated;
                if (layer is null) break;
                canvas.ClipRect(area);
                EditorImages.Draw(canvas, layer, 0, 0);
                break;
            }
            case MarkupTool.Redact:
                { using var black = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill }; canvas.DrawRect(area, black); }
                break;
        }
        canvas.Restore();
    }

    private static SKRect Inset(SKRect rect, float by) => new(rect.Left + by, rect.Top + by, rect.Right - by, rect.Bottom - by);

    /// <summary>Faces as blur areas, slightly enlarged so hair and chin are covered.</summary>
    public static IReadOnlyList<NBox> FaceAreas(IEnumerable<PixelBox> faces, int width, int height) =>
        faces.Select(f => new NBox((double)f.X / width, (double)f.Y / height, (double)f.Width / width, (double)f.Height / height))
             .Select(r => r.Inset(-r.Width * 0.15, -r.Height * 0.2).Intersect(NBox.Full))
             .Where(r => r.Width > 0 && r.Height > 0).ToList();

    /// <summary>The faces the system finds in the picture, as areas to blur.</summary>
    public static IReadOnlyList<NBox> FaceAreas(SKBitmap image)
    {
        // Detection works on a reduced grey copy: faces are large, and small pictures are found faster.
        using var small = EditorImages.Scaled(image, 1280);
        var rgba = EditorImages.ToStraight(small);
        var gray = new byte[small.Width * small.Height];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)(0.299 * rgba[i * 4] + 0.587 * rgba[i * 4 + 1] + 0.114 * rgba[i * 4 + 2] + 0.5);
        return FaceAreas(Platform.Current.FindFaces(gray, small.Width, small.Height), small.Width, small.Height);
    }
}

/// <summary>The state of the Annotate and Cover editor for one picture: the marks, the tool and the history of changes.</summary>
public sealed class AnnotateModel
{
    private IReadOnlyList<Mark> marks = [];
    private Guid? editingText;
    private readonly Stack<IReadOnlyList<Mark>> undo = new(), redo = new();

    public AnnotateModel(string path, SKBitmap preview)
    {
        Path = path;
        Preview = preview;
        (Blurred, Pixelated) = MarkupRenderer.PrivacyLayers(preview);
        Rendered = preview;
        HasTransparency = EditorImages.HasTransparency(preview);
    }

    public event Action? Changed;

    public string Path { get; }
    public SKBitmap Preview { get; }
    public SKBitmap Blurred { get; }
    public SKBitmap Pixelated { get; }
    public bool HasTransparency { get; }

    /// <summary>The picture with the marks drawn on it.</summary>
    public SKBitmap Rendered { get; private set; }

    public MarkupTool Tool { get; set; } = MarkupTool.Arrow;
    public SKColor Color { get; set; } = new(255, 59, 48);
    public double Width { get; set; } = 4;
    public Guid? Selection { get; set; }
    public string? Note { get; set; }

    public IReadOnlyList<Mark> Marks
    {
        get => marks;
        set { marks = value; Refresh(); }
    }

    /// <summary>The mark whose text is being typed: it is drawn by the text box, not by the picture.</summary>
    public Guid? EditingText
    {
        get => editingText;
        set { editingText = value; Refresh(); }
    }

    public NSz Size => new(Preview.Width, Preview.Height);

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    private void Refresh()
    {
        Rendered = MarkupRenderer.Render(Preview, marks, Blurred, Pixelated, editingText);
        Changed?.Invoke();
    }

    /// <summary>Every change goes through here so it can be undone (Ctrl+Z).</summary>
    public void Commit(IReadOnlyList<Mark> changed)
    {
        if (marks.SequenceEqual(changed)) return;
        undo.Push(marks);
        redo.Clear();
        Marks = changed;
    }

    public void Undo() => Step(undo, redo);

    public void Redo() => Step(redo, undo);

    private void Step(Stack<IReadOnlyList<Mark>> from, Stack<IReadOnlyList<Mark>> to)
    {
        if (from.Count == 0) return;
        to.Push(marks);
        Marks = from.Pop();
        if (Selection is { } id && marks.All(m => m.Id != id)) Selection = null;
    }

    public Mark? MarkAt(NPt point)
    {
        const double tolerance = 0.015;
        var longest = Math.Max(Size.Width, Size.Height);
        foreach (var mark in marks.Reverse())
        {
            var box = mark.Bounds;
            if (mark.Tool == MarkupTool.Text)
            {
                var fontSize = (16 + mark.Width * 2.2) / 1000 * longest;
                box = box with { Width = mark.Text.Length * fontSize * 0.6 / Size.Width, Height = fontSize * 1.3 / Size.Height };
            }
            if (mark.Tool == MarkupTool.Counter)
            {
                var radius = (12 + mark.Width * 1.4) / 1000;
                box = box.Inset(-radius, -radius);
            }
            if (box.Inset(-tolerance, -tolerance).Contains(point)) return mark;
        }
        return null;
    }

    public void RemoveSelected()
    {
        if (Selection is not { } id) return;
        Commit(marks.Where(m => m.Id != id).ToList());
        Selection = null;
    }

    public void BlurFaces()
    {
        try
        {
            var faces = MarkupRenderer.FaceAreas(Preview);
            if (faces.Count == 0)
            {
                Note = L("No faces were found in this image. Use “Blur Area” to mark them by hand.");
                Changed?.Invoke();
                return;
            }
            var added = faces.Select(f => Mark.Create(MarkupTool.Blur, [new NPt(f.MinX, f.MinY), new NPt(f.MaxX, f.MaxY)], Color, 4));
            Commit([.. marks, .. added]);
            Note = faces.Count == 1 ? L("1 face blurred.") : L("%@ faces blurred.", faces.Count);
        }
        catch (Exception e) when (e is not Problem)
        {
            Note = L("Face detection failed: %@", e.Message);
        }
        Changed?.Invoke();
    }

    public void Save()
    {
        var chosen = marks;
        var path = Path;
        JobRunner.Shared.Single(L("Saving annotations…"), System.IO.Path.GetFileName(path), [path], () =>
        {
            using var image = EditorImages.Load(path);
            var layers = MarkupRenderer.NeedsPrivacyLayers(chosen) ? MarkupRenderer.PrivacyLayers(image) : (null, null);
            using var result = MarkupRenderer.Render(image, chosen, layers.Item1, layers.Item2);
            return [EditorImages.Save(result, path, " (" + L("annotated") + ")")];
        });
    }
}
