using SkiaSharp;

namespace Split;

public sealed record GradientPreset(string Name, string From, string To)
{
    public static readonly IReadOnlyList<GradientPreset> All =
    [
        new(L("Citrine"), "#FF9A3D", "#FF4E7A"),
        new(L("Ocean"), "#2E8BFF", "#36D1DC"),
        new(L("Mint"), "#43E97B", "#38F9D7"),
        new(L("Sunset"), "#FA709A", "#FEE140"),
        new(L("Lavender"), "#A18CD1", "#FBC2EB"),
        new(L("Sky"), "#89F7FE", "#66A6FF"),
        new(L("Peach"), "#FFECD2", "#FCB69F"),
        new(L("Paper"), "#FFFFFF", "#E6E6E6"),
        new(L("Graphite"), "#434343", "#000000"),
        new(L("Charcoal"), "#2B2B2B", "#121212"),
    ];

    public SKColor[] Colors => [EditorImages.Color(From), EditorImages.Color(To)];
}

public enum BackdropKind { Gradient, Solid, Blur, Transparent }

public static class BackdropKinds
{
    public static readonly IReadOnlyList<BackdropKind> All = Enum.GetValues<BackdropKind>();

    public static string Title(this BackdropKind kind) => kind switch
    {
        BackdropKind.Gradient => L("Gradient"),
        BackdropKind.Solid => L("Color"),
        BackdropKind.Blur => L("Blurred"),
        _ => L("None"),
    };
}

public enum BackdropRatio { Auto, Square, R4x5, R4x3, R16x9, R9x16 }

public static class BackdropRatios
{
    public static readonly IReadOnlyList<BackdropRatio> All = Enum.GetValues<BackdropRatio>();

    public static string Title(this BackdropRatio ratio) => ratio switch
    {
        BackdropRatio.Auto => L("Automatic"),
        BackdropRatio.Square => L("Square"),
        BackdropRatio.R4x5 => L("4:5 (Instagram)"),
        BackdropRatio.R4x3 => "4:3",
        BackdropRatio.R16x9 => "16:9",
        _ => L("9:16 (Stories)"),
    };

    public static double? Value(this BackdropRatio ratio) => ratio switch
    {
        BackdropRatio.Auto => null,
        BackdropRatio.Square => 1,
        BackdropRatio.R4x5 => 4.0 / 5,
        BackdropRatio.R4x3 => 4.0 / 3,
        BackdropRatio.R16x9 => 16.0 / 9,
        _ => 9.0 / 16,
    };
}

/// <summary>Everything is relative to the picture's size, so the preview and the full-size output match.</summary>
public sealed record BackdropStyle
{
    public BackdropKind Kind { get; init; } = BackdropKind.Gradient;
    public int Gradient { get; init; }
    public SKColor Solid { get; init; } = SKColors.White;
    public double Padding { get; init; } = 0.08;
    public double Corner { get; init; } = 0.03;
    public double Shadow { get; init; } = 0.5;
    public BackdropRatio Ratio { get; init; } = BackdropRatio.Auto;

    public bool ProducesTransparency => Kind == BackdropKind.Transparent;
}

public static class BackdropRenderer
{
    public static NSz BackdropSize(NSz image, BackdropStyle style)
    {
        var longest = Math.Max(image.Width, image.Height);
        var pad = style.Padding * longest;
        var (width, height) = (image.Width + pad * 2, image.Height + pad * 2);
        if (style.Ratio.Value() is { } ratio)
        {
            if (width / height < ratio) width = height * ratio; else height = width / ratio;
        }
        return new NSz(Math.Round(width, MidpointRounding.AwayFromZero), Math.Round(height, MidpointRounding.AwayFromZero));
    }

    public static SKBitmap Render(SKBitmap image, BackdropStyle style)
    {
        var size = new NSz(image.Width, image.Height);
        var canvasSize = BackdropSize(size, style);
        var (cw, ch) = ((int)canvasSize.Width, (int)canvasSize.Height);
        var result = EditorImages.Blank(cw, ch);
        using var canvas = new SKCanvas(result);
        switch (style.Kind)
        {
            case BackdropKind.Gradient:
            {
                var preset = GradientPreset.All[Math.Min(Math.Max(style.Gradient, 0), GradientPreset.All.Count - 1)];
                using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(cw, ch), preset.Colors, [0f, 1f], SKShaderTileMode.Clamp) };
                canvas.DrawRect(0, 0, cw, ch, paint);
                break;
            }
            case BackdropKind.Solid:
                canvas.Clear(style.Solid);
                break;
            case BackdropKind.Blur:
                using (var blurred = BlurredFill(image, cw, ch)) EditorImages.Draw(canvas, blurred, 0, 0);
                break;
        }

        var longest = Math.Max(size.Width, size.Height);
        // Rounded to whole pixels; the extra pixel of an odd margin goes to the top, as in the first design.
        var left = (float)Math.Round((canvasSize.Width - size.Width) / 2, MidpointRounding.AwayFromZero);
        var top = (float)((canvasSize.Height - size.Height) - Math.Round((canvasSize.Height - size.Height) / 2, MidpointRounding.AwayFromZero));
        var frame = new SKRect(left, top, left + image.Width, top + image.Height);
        var radius = (float)(style.Corner * Math.Min(size.Width, size.Height));

        // The shadow is cast by the rounded picture as a whole: draw it in a layer that carries the shadow.
        using var layer = new SKPaint();
        if (style.Shadow > 0)
        {
            var sigma = (float)(longest * 0.05 * style.Shadow / 2);
            layer.ImageFilter = SKImageFilter.CreateDropShadow(0, (float)(longest * 0.012 * style.Shadow), sigma, sigma,
                                                               new SKColor(0, 0, 0, (byte)Math.Round(0.38 * style.Shadow * 255)));
        }
        canvas.SaveLayer(layer);
        canvas.ClipRoundRect(new SKRoundRect(frame, radius, radius), antialias: true);
        EditorImages.Draw(canvas, image, frame.Left, frame.Top);
        canvas.Restore();
        return result;
    }

    /// <summary>The picture itself, scaled to cover the canvas and heavily blurred, a little brighter in colour than it was.</summary>
    public static SKBitmap BlurredFill(SKBitmap image, int width, int height)
    {
        var scale = Math.Max(width / (double)image.Width, height / (double)image.Height);
        var (w, h) = (image.Width * scale, image.Height * scale);
        using var covered = EditorImages.Blank(width, height);
        using (var canvas = new SKCanvas(covered))
        {
            using var picture = SKImage.FromBitmap(image);
            canvas.DrawImage(picture, new SKRect((float)((width - w) / 2), (float)((height - h) / 2), (float)((width + w) / 2), (float)((height + h) / 2)),
                             new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        var result = EditorImages.Blank(width, height);
        using var target = new SKCanvas(result);
        // Saturation 1.15 and brightness −0.04, applied after the blur.
        const float s = 1.15f, lr = 0.2125f, lg = 0.7154f, lb = 0.0721f, brightness = -0.04f;
        float[] matrix =
        [
            lr * (1 - s) + s, lg * (1 - s), lb * (1 - s), 0, brightness,
            lr * (1 - s), lg * (1 - s) + s, lb * (1 - s), 0, brightness,
            lr * (1 - s), lg * (1 - s), lb * (1 - s) + s, 0, brightness,
            0, 0, 0, 1, 0,
        ];
        var sigma = (float)(Math.Max(width, height) * 0.04);
        using var paint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateColorFilter(SKColorFilter.CreateColorMatrix(matrix), SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp)),
        };
        EditorImages.Draw(target, covered, 0, 0, paint);
        return result;
    }
}

/// <summary>The state of the Add Background editor: the style and a reduced picture to preview it on.</summary>
public sealed class BackgroundModel
{
    private BackdropStyle style = new();
    private int version;

    public BackgroundModel(IReadOnlyList<string> paths, SKBitmap preview)
    {
        Paths = paths;
        // Smaller preview: the blur and shadow are recomputed on every change.
        Preview = EditorImages.Scaled(preview, 1100);
        Rendered = BackdropRenderer.Render(Preview, style);
    }

    /// <summary>Raised from the thread that finished a preview.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> Paths { get; }
    public SKBitmap Preview { get; }
    public SKBitmap? Rendered { get; private set; }
    public Task Pending { get; private set; } = Task.CompletedTask;

    public BackdropStyle Style
    {
        get => style;
        set
        {
            style = value;
            var mine = Interlocked.Increment(ref version);
            Pending = Task.Run(() =>
            {
                if (mine != Volatile.Read(ref version)) return;
                SKBitmap? image;
                try { image = BackdropRenderer.Render(Preview, value); }
                catch (Problem) { image = null; }
                if (mine != Volatile.Read(ref version)) return;
                Rendered = image;
                Changed?.Invoke();
            });
        }
    }

    public void Save()
    {
        var chosen = style;
        JobRunner.Shared.Each(L("Adding a background…"), (L("1 image with a background"), L("%@ images with a background")), Paths, 2, path =>
        {
            using var image = EditorImages.Load(path);
            using var result = BackdropRenderer.Render(image, chosen);
            return [EditorImages.Save(result, path, " (" + L("with background") + ")", needsAlpha: chosen.ProducesTransparency)];
        });
    }
}
