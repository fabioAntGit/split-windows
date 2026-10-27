using SkiaSharp;

namespace Split;

public enum CollageLayout { Grid, Row, Column }

public static class CollageLayouts
{
    public static readonly IReadOnlyList<CollageLayout> All = Enum.GetValues<CollageLayout>();

    public static string Title(this CollageLayout layout) => layout switch
    {
        CollageLayout.Grid => L("Grid"),
        CollageLayout.Row => L("Row"),
        _ => L("Column"),
    };

    public static string Symbol(this CollageLayout layout) => layout switch
    {
        CollageLayout.Grid => "square.grid.2x2",
        CollageLayout.Row => "rectangle.split.3x1",
        _ => "rectangle.split.1x2",
    };
}

public enum CollageFill { White, Black, Transparent, Gradient }

public static class CollageFills
{
    public static readonly IReadOnlyList<CollageFill> All = Enum.GetValues<CollageFill>();

    public static string Title(this CollageFill fill) => fill switch
    {
        CollageFill.White => L("White"),
        CollageFill.Black => L("Black"),
        CollageFill.Transparent => L("Transparent"),
        _ => L("Gradient"),
    };
}

public sealed record CollageSpec
{
    public CollageLayout Layout { get; init; } = CollageLayout.Grid;

    /// <summary>A fraction of the output width.</summary>
    public double Spacing { get; init; } = 0.02;

    /// <summary>A fraction of the smaller side of a cell.</summary>
    public double Corner { get; init; }

    /// <summary>Grid cells cropped to fill (true) or whole images (false).</summary>
    public bool Fill { get; init; } = true;

    public CollageFill Background { get; init; } = CollageFill.White;
    public int Gradient { get; init; }
    public double Width { get; init; } = 2400;
}

public static class CollageRenderer
{
    /// <summary>Cell rectangles (from the top-left corner) and the canvas size for pictures with these aspect ratios.</summary>
    public static (NSz Canvas, NBox[] Cells) Layout(IReadOnlyList<double> aspects, CollageSpec style)
    {
        var count = aspects.Count;
        if (count == 0) return (new NSz(0, 0), []);
        var width = style.Width;
        var gap = Math.Round(style.Spacing * width, MidpointRounding.AwayFromZero);
        var cells = new NBox[count];
        switch (style.Layout)
        {
            case CollageLayout.Row:
            {
                var height = (width - gap * (count + 1)) / aspects.Sum();
                var x = gap;
                for (var i = 0; i < count; i++)
                {
                    cells[i] = new NBox(x, gap, height * aspects[i], height);
                    x += height * aspects[i] + gap;
                }
                return (new NSz(width, Math.Round(height + gap * 2, MidpointRounding.AwayFromZero)), cells);
            }
            case CollageLayout.Column:
            {
                var cellWidth = width - gap * 2;
                var y = gap;
                for (var i = 0; i < count; i++)
                {
                    var height = cellWidth / aspects[i];
                    cells[i] = new NBox(gap, y, cellWidth, height);
                    y += height + gap;
                }
                return (new NSz(width, Math.Round(y, MidpointRounding.AwayFromZero)), cells);
            }
            default:
            {
                var columns = (int)Math.Ceiling(Math.Sqrt(count));
                var rows = (int)Math.Ceiling((double)count / columns);
                var cellWidth = (width - gap * (columns + 1)) / columns;
                var cellAspect = style.Fill ? 1 : aspects.Order().ToArray()[count / 2];
                var cellHeight = cellWidth / cellAspect;
                for (var i = 0; i < count; i++)
                {
                    var row = i / columns;
                    var column = i % columns;
                    // The last row is centred when it isn't full.
                    var inRow = row == rows - 1 ? count - row * columns : columns;
                    var offset = (columns - inRow) * (cellWidth + gap) / 2;
                    cells[i] = new NBox(gap + offset + column * (cellWidth + gap), gap + row * (cellHeight + gap), cellWidth, cellHeight);
                }
                return (new NSz(width, Math.Round(rows * (cellHeight + gap) + gap, MidpointRounding.AwayFromZero)), cells);
            }
        }
    }

    public static SKBitmap Render(IReadOnlyList<SKBitmap> images, CollageSpec style)
    {
        var (canvasSize, cells) = Layout(images.Select(i => i.Width / (double)Math.Max(i.Height, 1)).ToList(), style);
        var result = EditorImages.Blank((int)canvasSize.Width, (int)canvasSize.Height);
        using var canvas = new SKCanvas(result);
        var full = new SKRect(0, 0, result.Width, result.Height);
        switch (style.Background)
        {
            case CollageFill.White: canvas.Clear(SKColors.White); break;
            case CollageFill.Black: canvas.Clear(SKColors.Black); break;
            case CollageFill.Gradient:
            {
                var preset = GradientPreset.All[Math.Min(Math.Max(style.Gradient, 0), GradientPreset.All.Count - 1)];
                using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(full.Right, full.Bottom), preset.Colors, [0f, 1f], SKShaderTileMode.Clamp) };
                canvas.DrawRect(full, paint);
                break;
            }
        }
        var fills = style.Layout == CollageLayout.Grid && style.Fill;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        for (var i = 0; i < images.Count; i++)
        {
            var cell = cells[i];
            var (w, h) = (images[i].Width, images[i].Height);
            var scale = fills ? Math.Max(cell.Width / w, cell.Height / h) : Math.Min(cell.Width / w, cell.Height / h);
            var drawn = new SKRect((float)(cell.MidX - w * scale / 2), (float)(cell.MidY - h * scale / 2), (float)(cell.MidX + w * scale / 2), (float)(cell.MidY + h * scale / 2));
            var clip = fills ? new SKRect((float)cell.MinX, (float)cell.MinY, (float)cell.MaxX, (float)cell.MaxY) : drawn;
            var radius = (float)(style.Corner * Math.Min(clip.Width, clip.Height));
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(clip, radius, radius), antialias: true);
            using (var picture = SKImage.FromBitmap(images[i])) canvas.DrawImage(picture, drawn, sampling);
            canvas.Restore();
        }
        return result;
    }
}

/// <summary>The state of the Collage editor: the pictures in order, the style, and a preview rendered from reduced copies.</summary>
public sealed class CollageModel
{
    /// <summary>The preview is the same drawing at this width, from pictures reduced to <see cref="ThumbnailSide"/>.</summary>
    private const int PreviewWidth = 1200, ThumbnailSide = 700;

    private readonly List<string> paths;
    private readonly Dictionary<string, SKBitmap> thumbnails = [];
    private CollageSpec style = new();
    private int version;

    public CollageModel(IReadOnlyList<string> paths)
    {
        this.paths = [.. paths];
        foreach (var path in paths)
        {
            try { thumbnails[path] = EditorImages.Load(path, ThumbnailSide); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        Render();
    }

    /// <summary>Raised from the thread that finished a preview.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> Paths => paths;
    public IReadOnlyDictionary<string, SKBitmap> Thumbnails => thumbnails;
    public SKBitmap? Rendered { get; private set; }
    public Task Pending { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<string> Unreadable => paths.Where(p => !thumbnails.ContainsKey(p)).ToList();

    public CollageSpec Style
    {
        get => style;
        set
        {
            style = value;
            Render();
        }
    }

    public void Move(int from, int to)
    {
        if (from < 0 || from >= paths.Count || to < 0 || to >= paths.Count || from == to) return;
        var path = paths[from];
        paths.RemoveAt(from);
        paths.Insert(to, path);
        Render();
    }

    public void Remove(string path)
    {
        paths.Remove(path);
        Render();
    }

    private void Render()
    {
        var mine = Interlocked.Increment(ref version);
        var chosen = style with { Width = PreviewWidth };
        var images = paths.Where(thumbnails.ContainsKey).Select(p => thumbnails[p]).ToList();
        Pending = Task.Run(() =>
        {
            if (mine != Volatile.Read(ref version)) return;
            SKBitmap? image;
            try { image = images.Count > 0 ? CollageRenderer.Render(images, chosen) : null; }
            catch (Problem) { image = null; }
            if (mine != Volatile.Read(ref version)) return;
            Rendered = image;
            Changed?.Invoke();
        });
    }

    public void Save()
    {
        var chosen = paths.ToList();
        var spec = style;
        if (chosen.Count == 0) return;
        JobRunner.Shared.Single(L("Creating the collage…"), L("%@ images", chosen.Count), chosen, () =>
        {
            var images = new List<SKBitmap>();
            try
            {
                for (var i = 0; i < chosen.Count; i++)
                {
                    // Each picture is read at the size its cell needs, not at full resolution.
                    images.Add(EditorImages.Load(chosen[i], (int)spec.Width));
                    JobProgress.Report((i + 1.0) / (chosen.Count + 1));
                }
                using var result = CollageRenderer.Render(images, spec);
                var format = spec.Background == CollageFill.Transparent ? Format.Png : Format.Jpg;
                var output = Output.Unique(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(chosen[0]))!, L("Collage") + "." + format.Ext()));
                EditorImages.Write(result, output, format, Quality.High);
                return [output];
            }
            finally { foreach (var image in images) image.Dispose(); }
        });
    }
}
