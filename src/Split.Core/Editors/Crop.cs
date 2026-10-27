using SkiaSharp;

namespace Split;

public enum CropAspect
{
    Free, Original, Square, R4x3, R3x4, R4x5, R5x4, R16x9, R9x16, R3x2, R2x3,
    InstagramPost, InstagramPortrait, InstagramLandscape, Story, Youtube, LinkPost, XHeader, FacebookCover, LinkedInBanner,
}

public static class CropAspects
{
    public static readonly IReadOnlyList<CropAspect> All = Enum.GetValues<CropAspect>();

    /// <summary>The formats of the social networks, shown apart in the picker. Only their shape is kept:
    /// the networks resize pictures themselves, and enlarging a small one would only soften it.</summary>
    public static readonly IReadOnlyList<CropAspect> Social =
        [CropAspect.InstagramPost, CropAspect.InstagramPortrait, CropAspect.InstagramLandscape, CropAspect.Story, CropAspect.Youtube,
         CropAspect.LinkPost, CropAspect.XHeader, CropAspect.FacebookCover, CropAspect.LinkedInBanner];

    public static readonly IReadOnlyList<CropAspect> General = All.Where(a => !Social.Contains(a)).ToList();

    public static string Title(this CropAspect aspect) => aspect switch
    {
        CropAspect.Free => L("Free"),
        CropAspect.Original => L("Original"),
        CropAspect.Square => L("Square"),
        CropAspect.R4x3 => "4:3",
        CropAspect.R3x4 => "3:4",
        CropAspect.R4x5 => "4:5",
        CropAspect.R5x4 => "5:4",
        CropAspect.R16x9 => "16:9",
        CropAspect.R9x16 => "9:16",
        CropAspect.R3x2 => "3:2",
        CropAspect.R2x3 => "2:3",
        CropAspect.InstagramPost => $"{L("Instagram Post")} 1:1",
        CropAspect.InstagramPortrait => $"{L("Instagram Portrait")} 4:5",
        CropAspect.InstagramLandscape => $"{L("Instagram Landscape")} 1.91:1",
        CropAspect.Story => $"{L("Story, Reel or TikTok")} 9:16",
        CropAspect.Youtube => "YouTube 16:9",
        CropAspect.LinkPost => $"{L("Link Post (Facebook, LinkedIn)")} 1.91:1",
        CropAspect.XHeader => $"{L("X Header")} 3:1",
        CropAspect.FacebookCover => $"{L("Facebook Cover")} 2.7:1",
        _ => $"{L("LinkedIn Banner")} 4:1",
    };

    /// <summary>Width ÷ height in pixels; null when free.</summary>
    public static double? Ratio(this CropAspect aspect, NSz original) => aspect switch
    {
        CropAspect.Free => null,
        CropAspect.Original => original.Width / Math.Max(original.Height, 1),
        CropAspect.Square => 1,
        CropAspect.R4x3 => 4.0 / 3,
        CropAspect.R3x4 => 3.0 / 4,
        CropAspect.R4x5 => 4.0 / 5,
        CropAspect.R5x4 => 5.0 / 4,
        CropAspect.R16x9 => 16.0 / 9,
        CropAspect.R9x16 => 9.0 / 16,
        CropAspect.R3x2 => 3.0 / 2,
        CropAspect.R2x3 => 2.0 / 3,
        CropAspect.InstagramPost => 1,
        CropAspect.InstagramPortrait => 4.0 / 5,
        CropAspect.InstagramLandscape or CropAspect.LinkPost => 1.91,
        CropAspect.Story => 9.0 / 16,
        CropAspect.Youtube => 16.0 / 9,
        CropAspect.XHeader => 3,
        CropAspect.FacebookCover => 851.0 / 315,
        _ => 4,
    };
}

/// <summary>A fixed crop format: its pixel width ÷ height, and how large the crop is next to the largest crop of that shape the picture allows.</summary>
public readonly record struct CropShape(double Ratio, double Scale);

/// <summary>Crop in normalised coordinates (top-left origin) of the rotated and flipped picture.</summary>
public sealed record CropSettings
{
    public int Turns { get; init; }
    public bool FlipH { get; init; }
    public bool FlipV { get; init; }
    public NBox Rect { get; init; } = NBox.Full;

    /// <summary>Set for a fixed format (4:5, 16:9…), so every picture of a batch gets that shape even when the pictures differ in size.</summary>
    public CropShape? Shape { get; init; }

    public bool IsIdentity => Turns % 4 == 0 && !FlipH && !FlipV && Rect == NBox.Full;
}

public enum CropHandle { Move, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right }

public static class CropRenderer
{
    private static readonly SKSamplingOptions Exact = new(SKFilterMode.Nearest);

    /// <summary>Quarter turns clockwise, then mirroring as seen on screen.</summary>
    public static SKBitmap Transform(SKBitmap image, int turns, bool flipH, bool flipV)
    {
        var quarter = ((turns % 4) + 4) % 4;
        if (quarter == 0 && !flipH && !flipV) return image;
        float w = image.Width, h = image.Height;
        var swap = quarter % 2 == 1;
        var result = EditorImages.Blank(swap ? image.Height : image.Width, swap ? image.Width : image.Height);
        using var canvas = new SKCanvas(result);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.Scale(flipH ? -1 : 1, flipV ? -1 : 1);
        canvas.RotateDegrees(quarter * 90);
        using var source = SKImage.FromBitmap(image);
        canvas.DrawImage(source, -w / 2, -h / 2, Exact);
        return result;
    }

    public static SKBitmap Render(SKBitmap image, CropSettings settings)
    {
        var transformed = Transform(image, settings.Turns, settings.FlipH, settings.FlipV);
        var size = new NSz(transformed.Width, transformed.Height);
        var rect = RectFor(settings, size);
        if (rect == NBox.Full) return transformed;
        var pixels = rect.In(size).Integral().Intersect(new NBox(0, 0, size.Width, size.Height));
        if (pixels.Width < 1 || pixels.Height < 1)
            throw new Problem(L("The crop area is empty."), L("Make the selection bigger and try again."));
        using var subset = new SKBitmap();
        transformed.ExtractSubset(subset, new SKRectI((int)pixels.MinX, (int)pixels.MinY, (int)pixels.MaxX, (int)pixels.MaxY));
        return subset.Copy();
    }

    /// <summary>The crop for a picture of <paramref name="size"/>: the drawn rect, or for a fixed format the same shape, relative size and centre on this picture.</summary>
    public static NBox RectFor(CropSettings settings, NSz size)
    {
        if (settings.Shape is not { } shape) return settings.Rect;
        var center = new NPt(settings.Rect.MidX, settings.Rect.MidY);
        var largest = Fitted(shape.Ratio, size, center);
        var width = Math.Min(1, largest.Width * shape.Scale);
        var height = Math.Min(1, largest.Height * shape.Scale);
        var x = Math.Min(Math.Max(center.X - width / 2, 0), 1 - width);
        var y = Math.Min(Math.Max(center.Y - height / 2, 0), 1 - height);
        return new NBox(x, y, width, height);
    }

    /// <summary>Largest rect with <paramref name="ratio"/> (pixel width ÷ height) inside the picture, centred on <paramref name="center"/>.</summary>
    public static NBox Fitted(double ratio, NSz imageSize, NPt center)
    {
        var normalised = ratio * imageSize.Height / Math.Max(imageSize.Width, 1);
        var width = normalised >= 1 ? 1 : normalised;
        var height = normalised >= 1 ? 1 / normalised : 1;
        var x = Math.Min(Math.Max(center.X - width / 2, 0), 1 - width);
        var y = Math.Min(Math.Max(center.Y - height / 2, 0), 1 - height);
        return new NBox(x, y, width, height);
    }

    /// <summary>Which handle a point (in view coordinates) grabs; corners win over edges, edges over moving.</summary>
    public static CropHandle? HandleAt(NPt point, NBox crop, NBox frame)
    {
        var r = new NBox(frame.MinX + crop.MinX * frame.Width, frame.MinY + crop.MinY * frame.Height, crop.Width * frame.Width, crop.Height * frame.Height);
        const double corner = 18, edge = 10;
        bool Near(double x, double y) => Math.Abs(point.X - x) <= corner && Math.Abs(point.Y - y) <= corner;
        if (Near(r.MinX, r.MinY)) return CropHandle.TopLeft;
        if (Near(r.MaxX, r.MinY)) return CropHandle.TopRight;
        if (Near(r.MinX, r.MaxY)) return CropHandle.BottomLeft;
        if (Near(r.MaxX, r.MaxY)) return CropHandle.BottomRight;
        var insideX = point.X >= r.MinX && point.X <= r.MaxX;
        var insideY = point.Y >= r.MinY && point.Y <= r.MaxY;
        if (insideX && Math.Abs(point.Y - r.MinY) <= edge) return CropHandle.Top;
        if (insideX && Math.Abs(point.Y - r.MaxY) <= edge) return CropHandle.Bottom;
        if (insideY && Math.Abs(point.X - r.MinX) <= edge) return CropHandle.Left;
        if (insideY && Math.Abs(point.X - r.MaxX) <= edge) return CropHandle.Right;
        return r.Contains(point) ? CropHandle.Move : null;
    }

    /// <summary>New crop after dragging <paramref name="handle"/> by <paramref name="delta"/> (normalised), keeping <paramref name="ratio"/> (normalised w÷h) if set.</summary>
    public static NBox Drag(CropHandle handle, NBox start, NSz delta, double? ratio)
    {
        const double minimum = 0.03;
        if (handle == CropHandle.Move)
        {
            var moveX = Math.Min(Math.Max(start.MinX + delta.Width, 0), 1 - start.Width);
            var moveY = Math.Min(Math.Max(start.MinY + delta.Height, 0), 1 - start.Height);
            return new NBox(moveX, moveY, start.Width, start.Height);
        }
        double minX = start.MinX, minY = start.MinY, maxX = start.MaxX, maxY = start.MaxY;
        switch (handle)
        {
            case CropHandle.TopLeft: minX += delta.Width; minY += delta.Height; break;
            case CropHandle.TopRight: maxX += delta.Width; minY += delta.Height; break;
            case CropHandle.BottomLeft: minX += delta.Width; maxY += delta.Height; break;
            case CropHandle.BottomRight: maxX += delta.Width; maxY += delta.Height; break;
            case CropHandle.Top: minY += delta.Height; break;
            case CropHandle.Bottom: maxY += delta.Height; break;
            case CropHandle.Left: minX += delta.Width; break;
            case CropHandle.Right: maxX += delta.Width; break;
        }
        minX = Math.Min(Math.Max(minX, 0), start.MaxX - minimum);
        maxX = Math.Max(Math.Min(maxX, 1), start.MinX + minimum);
        minY = Math.Min(Math.Max(minY, 0), start.MaxY - minimum);
        maxY = Math.Max(Math.Min(maxY, 1), start.MinY + minimum);
        if (ratio is not { } fixedRatio) return new NBox(minX, minY, maxX - minX, maxY - minY);

        double width, height;
        switch (handle)
        {
            case CropHandle.Left or CropHandle.Right:
            {
                // Width drives height, centred on the original middle.
                width = maxX - minX;
                height = width / fixedRatio;
                var available = Math.Min(start.MidY, 1 - start.MidY) * 2;
                if (height > available) { height = available; width = height * fixedRatio; }
                var x = handle == CropHandle.Left ? start.MaxX - width : start.MinX;
                return new NBox(x, start.MidY - height / 2, width, height);
            }
            case CropHandle.Top or CropHandle.Bottom:
            {
                height = maxY - minY;
                width = height * fixedRatio;
                var available = Math.Min(start.MidX, 1 - start.MidX) * 2;
                if (width > available) { width = available; height = width / fixedRatio; }
                var y = handle == CropHandle.Top ? start.MaxY - height : start.MinY;
                return new NBox(start.MidX - width / 2, y, width, height);
            }
            default:
            {
                // Corners: the opposite corner stays put.
                var left = handle is CropHandle.TopLeft or CropHandle.BottomLeft;
                var top = handle is CropHandle.TopLeft or CropHandle.TopRight;
                var anchorX = left ? start.MaxX : start.MinX;
                var anchorY = top ? start.MaxY : start.MinY;
                width = maxX - minX;
                height = maxY - minY;
                if (width / height > fixedRatio) width = height * fixedRatio; else height = width / fixedRatio;
                var roomX = left ? anchorX : 1 - anchorX;
                var roomY = top ? anchorY : 1 - anchorY;
                if (width > roomX) { width = roomX; height = width / fixedRatio; }
                if (height > roomY) { height = roomY; width = height * fixedRatio; }
                return new NBox(left ? anchorX - width : anchorX, top ? anchorY - height : anchorY, width, height);
            }
        }
    }
}

/// <summary>The state of the Crop editor: the picture as shown (turned and flipped), the crop and its format.</summary>
public sealed class CropModel
{
    private CropAspect aspect = CropAspect.Free;
    private CropSettings settings = new();

    public CropModel(IReadOnlyList<string> paths, SKBitmap preview)
    {
        Paths = paths;
        Preview = preview;
        Display = preview;
        HasTransparency = EditorImages.HasTransparency(preview);
    }

    public event Action? Changed;

    public IReadOnlyList<string> Paths { get; }
    public SKBitmap Preview { get; }
    public SKBitmap Display { get; private set; }
    public bool HasTransparency { get; }

    public CropSettings Settings
    {
        get => settings;
        set { settings = value; Changed?.Invoke(); }
    }

    public CropAspect Aspect
    {
        get => aspect;
        set { aspect = value; ApplyAspect(); }
    }

    public NSz DisplaySize => new(Display.Width, Display.Height);

    /// <summary>The crop's width ÷ height in 0…1 units, which is what dragging keeps.</summary>
    public double? NormalisedRatio => aspect.Ratio(DisplaySize) is { } ratio ? ratio * DisplaySize.Height / Math.Max(DisplaySize.Width, 1) : null;

    public NSz ResultSize => new(Math.Round(settings.Rect.Width * DisplaySize.Width), Math.Round(settings.Rect.Height * DisplaySize.Height));

    private void RefreshDisplay()
    {
        Display = CropRenderer.Transform(Preview, settings.Turns, settings.FlipH, settings.FlipV);
    }

    private void ApplyAspect()
    {
        if (aspect.Ratio(DisplaySize) is not { } ratio)
        {
            Changed?.Invoke();
            return;
        }
        Settings = settings with { Rect = CropRenderer.Fitted(ratio, DisplaySize, new NPt(settings.Rect.MidX, settings.Rect.MidY)) };
    }

    public void Rotate(bool clockwise)
    {
        var r = settings.Rect;
        settings = settings with
        {
            Turns = (settings.Turns + (clockwise ? 1 : 3)) % 4,
            Rect = clockwise ? new NBox(1 - r.MaxY, r.MinX, r.Height, r.Width) : new NBox(r.MinY, 1 - r.MaxX, r.Height, r.Width),
        };
        RefreshDisplay();
        ApplyAspect();
    }

    public void Flip(bool horizontal)
    {
        var r = settings.Rect;
        settings = horizontal
            ? settings with { FlipH = !settings.FlipH, Rect = r with { X = 1 - r.MaxX } }
            : settings with { FlipV = !settings.FlipV, Rect = r with { Y = 1 - r.MaxY } };
        RefreshDisplay();
        Changed?.Invoke();
    }

    public void Reset()
    {
        settings = new CropSettings();
        aspect = CropAspect.Free;
        RefreshDisplay();
        Changed?.Invoke();
    }

    /// <summary>The settings to apply to every picture; "Original" keeps each picture's own shape.</summary>
    public CropSettings BatchSettings
    {
        get
        {
            if (aspect == CropAspect.Original || aspect.Ratio(DisplaySize) is not { } ratio) return settings;
            var largest = CropRenderer.Fitted(ratio, DisplaySize, new NPt(0.5, 0.5));
            return settings with { Shape = new CropShape(ratio, settings.Rect.Width / Math.Max(largest.Width, 0.0001)) };
        }
    }

    public void Save()
    {
        var batch = BatchSettings;
        JobRunner.Shared.Each(L("Cropping…"), (L("1 image cropped"), L("%@ images cropped")), Paths, 3, path =>
        {
            using var image = EditorImages.Load(path);
            using var result = CropRenderer.Render(image, batch);
            return [EditorImages.Save(result, path, " (" + L("cropped") + ")")];
        });
    }
}
