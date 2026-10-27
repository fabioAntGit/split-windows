using SkiaSharp;

namespace Split;

public enum PhotoFilter { None, Mono, Noir, Tonal, Fade, Chrome, Instant, Process, Transfer }

public static class PhotoFilters
{
    public static readonly IReadOnlyList<PhotoFilter> All = Enum.GetValues<PhotoFilter>();

    public static string Title(this PhotoFilter filter) => filter switch
    {
        PhotoFilter.None => L("None"),
        PhotoFilter.Mono => L("Mono"),
        PhotoFilter.Noir => L("Noir"),
        PhotoFilter.Tonal => L("Tonal"),
        PhotoFilter.Fade => L("Fade"),
        PhotoFilter.Chrome => L("Chrome"),
        PhotoFilter.Instant => L("Instant"),
        PhotoFilter.Process => L("Process"),
        _ => L("Transfer"),
    };
}

/// <summary>Neutral values leave the picture untouched.</summary>
public sealed record Adjustments
{
    public bool Auto { get; init; }
    public double Exposure { get; init; }       // EV, −2…2
    public double Brightness { get; init; }     // −0.5…0.5
    public double Contrast { get; init; } = 1;  // 0.5…1.5
    public double Saturation { get; init; } = 1; // 0…2
    public double Vibrance { get; init; }       // −1…1
    public double Warmth { get; init; }         // −1 (cool) … 1 (warm)
    public double Tint { get; init; }           // −1…1
    public double Highlights { get; init; }     // 0…1 recovers highlights
    public double Shadows { get; init; }        // −1…1
    public double Sharpness { get; init; }      // 0…1
    public double Noise { get; init; }          // 0…1
    public double Vignette { get; init; }       // 0…1
    public PhotoFilter Filter { get; init; }

    public bool IsNeutral => this == new Adjustments();
}

/// <summary>
/// The adjustments, applied pixel by pixel on the CPU so the preview and the saved file go through the same maths.
/// Light and colour work on sRGB values, except exposure and temperature, which work in linear light as a lens does.
/// Radii that are measured in pixels grow with the picture, so a reduced preview looks like the full-size result.
/// </summary>
public static class AdjustRenderer
{
    /// <summary>The picture the radii below are tuned for.</summary>
    private const double ReferenceSide = 1400;

    public static SKBitmap Render(SKBitmap image, Adjustments adjustments)
    {
        if (adjustments.IsNeutral) return image;
        var (width, height) = (image.Width, image.Height);
        var data = EditorImages.ToStraight(image);
        var plan = new Plan(adjustments, data, width, height);

        if (plan.HasTone) ForRows(height, y => { for (var x = 0; x < width; x++) Pixel(data, (y * width + x) * 4, plan.Tone); });

        if (adjustments.Sharpness > 0 || adjustments.Noise > 0)
        {
            var sigma = (float)(Math.Max(width, height) / ReferenceSide * 1.2);
            byte[] soft;
            using (var toned = EditorImages.FromStraight(data, width, height))
            using (var blurred = MarkupRenderer.Blur(toned, sigma)) soft = EditorImages.ToStraight(blurred);
            var amount = (float)(adjustments.Sharpness * 1.5);
            var noise = (float)adjustments.Noise;
            ForRows(height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var at = (y * width + x) * 4;
                    float r = data[at] / 255f, g = data[at + 1] / 255f, b = data[at + 2] / 255f;
                    float sr = soft[at] / 255f, sg = soft[at + 1] / 255f, sb = soft[at + 2] / 255f;
                    var delta = Lum(r, g, b) - Lum(sr, sg, sb);
                    if (noise > 0)
                    {
                        // Small differences are noise and melt into the blur; big ones are edges and stay.
                        var keep = SmoothStep(0, 0.02f + 0.1f * noise, Math.Abs(delta));
                        var mix = noise * (1 - keep);
                        r += (sr - r) * mix; g += (sg - g) * mix; b += (sb - b) * mix;
                    }
                    if (amount > 0) { r += amount * delta; g += amount * delta; b += amount * delta; }
                    Store(data, at, r, g, b);
                }
            });
        }

        if (adjustments.Filter != PhotoFilter.None || adjustments.Vignette > 0)
        {
            var vignette = (float)Math.Min(1, adjustments.Vignette * 0.9);
            ForRows(height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var at = (y * width + x) * 4;
                    float r = data[at] / 255f, g = data[at + 1] / 255f, b = data[at + 2] / 255f;
                    Filter(adjustments.Filter, ref r, ref g, ref b);
                    if (vignette > 0)
                    {
                        var dx = (x + 0.5f) / width * 2 - 1;
                        var dy = (y + 0.5f) / height * 2 - 1;
                        var distance = MathF.Sqrt(dx * dx + dy * dy) / MathF.Sqrt(2);
                        var factor = 1 - vignette * SmoothStep(0.3f, 1, distance);
                        r *= factor; g *= factor; b *= factor;
                    }
                    Store(data, at, r, g, b);
                }
            });
        }
        return EditorImages.FromStraight(data, width, height);
    }

    // MARK: Plan

    private sealed class Plan
    {
        public readonly Tone Tone;
        public bool HasTone => Tone.Any;

        public Plan(Adjustments a, byte[] data, int width, int height)
        {
            var (low, scale) = a.Auto ? Levels(data) : (0f, 1f);
            var gains = a.Warmth != 0 || a.Tint != 0 ? TemperatureGains(a.Warmth, a.Tint) : null;
            Tone = new Tone
            {
                Low = low, Scale = scale, Exposure = (float)Math.Pow(2, a.Exposure), Exposed = a.Exposure != 0,
                Brightness = (float)a.Brightness, Contrast = (float)a.Contrast, Saturation = (float)a.Saturation,
                // Auto enhance also gives colours a gentle lift, as a photo app's "auto" does.
                Vibrance = (float)a.Vibrance + (a.Auto ? 0.2f : 0), Gains = gains,
                Highlights = (float)a.Highlights, Shadows = (float)a.Shadows,
            };
        }
    }

    private sealed class Tone
    {
        public float Low, Scale = 1, Exposure = 1, Brightness, Contrast = 1, Saturation = 1, Vibrance, Highlights, Shadows;
        public bool Exposed;
        public float[]? Gains;

        public bool Any => Low != 0 || Scale != 1 || Exposed || Brightness != 0 || Contrast != 1 || Saturation != 1 || Vibrance != 0 || Gains is not null || Highlights != 0 || Shadows != 0;
    }

    /// <summary>The black point and the stretch that fill the range from the 0.5% darkest to the 0.5% lightest pixels.</summary>
    private static (float Low, float Scale) Levels(byte[] data)
    {
        var histogram = new long[256];
        for (var i = 0; i < data.Length; i += 4) histogram[(int)(Lum(data[i] / 255f, data[i + 1] / 255f, data[i + 2] / 255f) * 255 + 0.5f)]++;
        var total = data.Length / 4;
        long seen = 0;
        int low = 0, high = 255;
        for (var v = 0; v < 256; v++) { seen += histogram[v]; if (seen >= total * 0.005) { low = v; break; } }
        seen = 0;
        for (var v = 255; v >= 0; v--) { seen += histogram[v]; if (seen >= total * 0.005) { high = v; break; } }
        if (high - low < 13) return (0, 1);
        return (low / 255f, Math.Min(1.6f, 255f / (high - low)));
    }

    // MARK: Pixel maths

    private static void Pixel(byte[] data, int at, Tone t)
    {
        float r = data[at] / 255f, g = data[at + 1] / 255f, b = data[at + 2] / 255f;
        if (t.Low != 0 || t.Scale != 1) { r = (r - t.Low) * t.Scale; g = (g - t.Low) * t.Scale; b = (b - t.Low) * t.Scale; }
        if (t.Exposed) { r = Exposed(r, t.Exposure); g = Exposed(g, t.Exposure); b = Exposed(b, t.Exposure); }
        if (t.Saturation != 1)
        {
            var l = Lum(r, g, b);
            r = l + (r - l) * t.Saturation; g = l + (g - l) * t.Saturation; b = l + (b - l) * t.Saturation;
        }
        if (t.Brightness != 0) { r += t.Brightness; g += t.Brightness; b += t.Brightness; }
        if (t.Contrast != 1)
        {
            r = (r - 0.5f) * t.Contrast + 0.5f; g = (g - 0.5f) * t.Contrast + 0.5f; b = (b - 0.5f) * t.Contrast + 0.5f;
        }
        if (t.Vibrance != 0)
        {
            // Dull colours move more than vivid ones.
            r = Clamp(r); g = Clamp(g); b = Clamp(b);
            var saturation = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            var k = Math.Max(0, 1 + t.Vibrance * (1 - saturation));
            var l = Lum(r, g, b);
            r = l + (r - l) * k; g = l + (g - l) * k; b = l + (b - l) * k;
        }
        if (t.Gains is { } gains)
        {
            r = Srgb.FromLinear(Srgb.ToLinear(Clamp(r)) * gains[0]);
            g = Srgb.FromLinear(Srgb.ToLinear(Clamp(g)) * gains[1]);
            b = Srgb.FromLinear(Srgb.ToLinear(Clamp(b)) * gains[2]);
        }
        if (t.Highlights != 0 || t.Shadows != 0)
        {
            var l = Clamp(Lum(r, g, b));
            var shift = t.Shadows * 0.4f * MathF.Pow(1 - l, 3) - t.Highlights * 0.4f * MathF.Pow(l, 3);
            r += shift; g += shift; b += shift;
        }
        Store(data, at, r, g, b);
    }

    /// <summary>Doubling the light per EV, in linear light.</summary>
    public static float Exposed(float value, float factor) => Srgb.FromLinear(Srgb.ToLinear(Clamp(value)) * factor);

    /// <summary>
    /// Gains per channel (in linear light) that move the white of 6500 K to the one chosen: lower temperatures
    /// make the picture warmer. The overall brightness stays the same; tint trades green for magenta.
    /// </summary>
    public static float[] TemperatureGains(double warmth, double tint)
    {
        var neutral = Kelvin(6500);
        var target = Kelvin(6500 - warmth * 2500);
        var gains = new float[] { target[0] / neutral[0], target[1] / neutral[1] * (float)(1 - 0.2 * tint), target[2] / neutral[2] };
        var scale = 0.2126f * gains[0] + 0.7152f * gains[1] + 0.0722f * gains[2];
        return [gains[0] / scale, gains[1] / scale, gains[2] / scale];
    }

    /// <summary>The colour of a black body at a temperature, 0…1 per channel.</summary>
    private static float[] Kelvin(double kelvin)
    {
        var t = kelvin / 100;
        var r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        var g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        var b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        return [(float)Math.Clamp(r / 255, 0.02, 1), (float)Math.Clamp(g / 255, 0.02, 1), (float)Math.Clamp(b / 255, 0.02, 1)];
    }

    /// <summary>The looks: a colour grade each (saturation, channel multipliers, contrast, lifted blacks), the black-and-white ones through a tone curve.</summary>
    public static void Filter(PhotoFilter filter, ref float r, ref float g, ref float b)
    {
        switch (filter)
        {
            case PhotoFilter.Mono: Grey(ref r, ref g, ref b, 0.92f, 0.03f, 1); break;
            case PhotoFilter.Noir: Grey(ref r, ref g, ref b, 1.45f, -0.04f, 1.15f); break;
            case PhotoFilter.Tonal: Grey(ref r, ref g, ref b, 0.78f, 0.07f, 1); break;
            case PhotoFilter.Fade: Grade(ref r, ref g, ref b, 0.7f, 1, 1, 1, 0.88f, 0.07f); break;
            case PhotoFilter.Chrome: Grade(ref r, ref g, ref b, 1.25f, 1.03f, 1, 0.97f, 1.12f, 0); break;
            case PhotoFilter.Process: Grade(ref r, ref g, ref b, 0.95f, 0.92f, 1, 1.12f, 1.12f, -0.01f); break;
            case PhotoFilter.Transfer: Grade(ref r, ref g, ref b, 1.1f, 1.08f, 1, 0.88f, 1.05f, 0.02f); break;
            case PhotoFilter.Instant: Grade(ref r, ref g, ref b, 0.9f, 1.05f, 1, 0.92f, 0.9f, 0.05f); break;
        }
    }

    private static void Grey(ref float r, ref float g, ref float b, float contrast, float lift, float gamma)
    {
        var l = Math.Max(0, 0.299f * r + 0.587f * g + 0.114f * b);
        l = MathF.Pow(Clamp(l), gamma);
        l = Clamp((l - 0.5f) * contrast + 0.5f + lift);
        r = g = b = l;
    }

    private static void Grade(ref float r, ref float g, ref float b, float saturation, float mr, float mg, float mb, float contrast, float lift)
    {
        var l = Lum(r, g, b);
        r = (l + (r - l) * saturation) * mr;
        g = (l + (g - l) * saturation) * mg;
        b = (l + (b - l) * saturation) * mb;
        r = (r - 0.5f) * contrast + 0.5f + lift;
        g = (g - 0.5f) * contrast + 0.5f + lift;
        b = (b - 0.5f) * contrast + 0.5f + lift;
    }

    private static float Lum(float r, float g, float b) => 0.2125f * r + 0.7154f * g + 0.0721f * b;

    private static float Clamp(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Clamp((x - edge0) / (edge1 - edge0));
        return t * t * (3 - 2 * t);
    }

    private static void Store(byte[] data, int at, float r, float g, float b)
    {
        data[at] = (byte)(Clamp(r) * 255 + 0.5f);
        data[at + 1] = (byte)(Clamp(g) * 255 + 0.5f);
        data[at + 2] = (byte)(Clamp(b) * 255 + 0.5f);
    }

    private static void ForRows(int height, Action<int> row) => Parallel.For(0, height, row);
}

/// <summary>The sRGB transfer curve, from tables so that whole pictures can be converted quickly.</summary>
public static class Srgb
{
    private const int Steps = 4096;
    private static readonly float[] Decoded = Enumerable.Range(0, Steps + 1).Select(i => Decode(i / (float)Steps)).ToArray();
    private static readonly float[] Encoded = Enumerable.Range(0, Steps + 1).Select(i => Encode(i / (float)Steps)).ToArray();

    private static float Decode(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static float Encode(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;

    public static float ToLinear(float v) => Decoded[(int)(Math.Clamp(v, 0, 1) * Steps + 0.5f)];

    public static float FromLinear(float v) => Encoded[(int)(Math.Clamp(v, 0, 1) * Steps + 0.5f)];
}

/// <summary>The state of the Adjust editor: the settings, a reduced picture, and the preview that follows them.</summary>
public sealed class AdjustModel
{
    private Adjustments adjustments = new();
    private int version;

    public AdjustModel(IReadOnlyList<string> paths, SKBitmap preview)
    {
        Paths = paths;
        Preview = EditorImages.Scaled(preview, 1400);
        Rendered = Preview;
        HasTransparency = EditorImages.HasTransparency(Preview);
    }

    /// <summary>Raised from the thread that finished a preview.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> Paths { get; }
    public SKBitmap Preview { get; }
    public bool HasTransparency { get; }
    public SKBitmap Rendered { get; private set; }
    public bool ShowOriginal { get; set; }

    /// <summary>The newest preview being drawn, for whoever needs to wait for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public Adjustments Adjustments
    {
        get => adjustments;
        set
        {
            adjustments = value;
            var mine = Interlocked.Increment(ref version);
            Pending = Task.Run(() =>
            {
                // Dragging a slider asks for many previews; only the newest is drawn.
                if (mine != Volatile.Read(ref version)) return;
                SKBitmap image;
                try { image = AdjustRenderer.Render(Preview, value); }
                catch (Problem) { image = Preview; }
                if (mine != Volatile.Read(ref version)) return;
                Rendered = image;
                Changed?.Invoke();
            });
        }
    }

    public void Save()
    {
        var chosen = adjustments;
        JobRunner.Shared.Each(L("Applying adjustments…"), (L("1 image adjusted"), L("%@ images adjusted")), Paths, 2, path =>
        {
            using var image = EditorImages.Load(path);
            using var result = AdjustRenderer.Render(image, chosen);
            return [EditorImages.Save(result, path, " (" + L("adjusted") + ")")];
        });
    }
}
