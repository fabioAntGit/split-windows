using System.Runtime.InteropServices;
using ImageMagick;
using SkiaSharp;

namespace Split;

/// <summary>
/// Pictures in and out of the editors. They are edited as Skia bitmaps (upright, in sRGB, premultiplied)
/// so the preview and the saved file go through the same drawing code; the library that reads and writes
/// the formats stays behind this door, with its security policy.
/// </summary>
public static class EditorImages
{
    /// <summary>The size an editor works on: editing stays fluid, and the full resolution is used only when saving.</summary>
    public const int PreviewSide = 1800;

    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>The picture upright and in sRGB; with <paramref name="maxSide"/> its longest side is reduced to that.</summary>
    public static SKBitmap Load(string path, int maxSide = 0)
    {
        var format = Formats.Detect(path) ?? Format.Png;
        Imaging.Initialize();
        IMagickImage<ushort> image;
        if (format == Format.Svg)
        {
            image = ImageConverter.RenderSvg(path);
        }
        else
        {
            using var frames = ImageConverter.Open(path, format);
            image = frames.Images[frames.IsIcon ? frames.Largest : 0].Clone();
        }
        using (image)
        {
            try
            {
                image.AutoOrient();
                try { if (image.GetColorProfile() is not null || image.ColorSpace != ColorSpace.sRGB) image.TransformColorSpace(ColorProfiles.SRGB); }
                catch (MagickException) { }
                if (maxSide > 0 && Math.Max(image.Width, image.Height) > maxSide)
                    image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
                var (width, height) = ((int)image.Width, (int)image.Height);
                using var pixels = image.GetPixels();
                var bytes = pixels.ToByteArray(PixelMapping.RGBA) ?? throw OutOfMemory(width, height);
                return FromStraight(bytes, width, height);
            }
            catch (MagickResourceLimitErrorException)
            {
                throw OutOfMemory((int)image.Width, (int)image.Height);
            }
        }
    }

    public static Problem OutOfMemory(int width, int height) =>
        new(L("There isn’t enough memory for a %@ × %@ pixel image.", width.ToString(), height.ToString()), L("Close other apps or make the image smaller first."));

    /// <summary>The bitmap drawn pixel for pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static void Draw(SKCanvas canvas, SKBitmap bitmap, float x, float y, SKPaint? paint = null)
    {
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, x, y, new SKSamplingOptions(SKFilterMode.Nearest), paint);
    }

    /// <summary>A bitmap made of 8-bit RGBA pixels that are not premultiplied.</summary>
    public static SKBitmap FromStraight(byte[] rgba, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height));
        using var source = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), rgba);
        if (!source.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) throw OutOfMemory(width, height);
        return bitmap;
    }

    /// <summary>The pixels as 8-bit RGBA that is not premultiplied, row after row.</summary>
    public static byte[] ToStraight(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.Width * bitmap.Height * 4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            using var image = SKImage.FromBitmap(bitmap);
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            if (!image.ReadPixels(info, handle.AddrOfPinnedObject(), bitmap.Width * 4, 0, 0)) throw OutOfMemory(bitmap.Width, bitmap.Height);
        }
        finally { handle.Free(); }
        return bytes;
    }

    /// <summary>A new, empty (transparent) bitmap, or the "not enough memory" explanation.</summary>
    public static SKBitmap Blank(int width, int height)
    {
        var bitmap = new SKBitmap();
        if (width < 1 || height < 1 || !bitmap.TryAllocPixels(new SKImageInfo(width, height))) throw OutOfMemory(width, height);
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    /// <summary>A copy with the longest side at most <paramref name="maxSide"/>; the same bitmap when it is small enough.</summary>
    public static SKBitmap Scaled(SKBitmap bitmap, int maxSide)
    {
        var longest = Math.Max(bitmap.Width, bitmap.Height);
        if (longest <= maxSide) return bitmap;
        var scale = (double)maxSide / longest;
        var result = Blank(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)));
        using var canvas = new SKCanvas(result);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, new SKRect(0, 0, result.Width, result.Height), Smooth);
        return result;
    }

    /// <summary>True when some pixel is not fully opaque: only then does a checkerboard show behind the picture.</summary>
    public static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque) return false;
        var pixels = bitmap.GetPixelSpan();
        // Alpha is the fourth byte of both byte orders the bitmap can have.
        for (var i = 3; i < pixels.Length; i += 4 * 3) if (pixels[i] < 255) return true;
        return false;
    }

    public static SKColor Color(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return new SKColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    // MARK: Saving

    /// <summary>
    /// Saves an edited picture next to <paramref name="source"/>, in the source's format when it can hold the
    /// result (PNG when transparency must survive, or when the format can't be written), and returns its path.
    /// </summary>
    public static string Save(SKBitmap image, string source, string suffix, bool needsAlpha = false)
    {
        Imaging.Initialize();
        var format = Formats.Detect(source) ?? Format.Png;
        // HEIC can be read but not written: a photograph goes to JPG.
        if (format is Format.Svg or Format.Ico or Format.Pdf or Format.Gif or Format.Bmp) format = Format.Png;
        if (format == Format.Heic) format = needsAlpha ? Format.Png : Format.Jpg;
        if (needsAlpha && format == Format.Jpg) format = Format.Png;
        var output = Output.Url(source, format.Ext(), suffix);
        var temp = Output.TempSibling(output);
        try
        {
            using var picture = new MagickImage(ToStraight(image), new PixelReadSettings((uint)image.Width, (uint)image.Height, StorageType.Char, PixelMapping.RGBA));
            if (format == Format.Jpg)
            {
                picture.BackgroundColor = MagickColors.White;
                picture.Alpha(AlphaAction.Remove);
            }
            if (Settings.KeepsMetadata) CopyMetadata(source, picture);
            Define(picture, format);
            try
            {
                using (var file = File.Create(temp)) picture.Write(file, Coder(format));
            }
            catch (MagickResourceLimitErrorException)
            {
                throw OutOfMemory(image.Width, image.Height);
            }
            catch (MagickException e)
            {
                throw new Problem(L("The %@ encoder refused this image (%@ × %@ px).", format.Label(), image.Width.ToString(), image.Height.ToString()),
                                  format == Format.Avif ? L("%@ has limits on size and color format. Try PNG, JPG or WebP.", format.Label()) : L("Try another format, such as PNG."),
                                  e.Message);
            }
        }
        catch
        {
            Output.Discard(temp);
            Output.Discard(output);
            throw;
        }
        Output.Commit(temp, output);
        return output;
    }

    /// <summary>The camera data of the original travels with the edited picture, except what the edit made untrue: the orientation (the pixels are upright now) and the pixel size.</summary>
    private static void CopyMetadata(string source, MagickImage target)
    {
        try
        {
            var format = Formats.Detect(source) ?? Format.Png;
            if (format == Format.Svg) return;
            using var frames = ImageConverter.Open(source, format);
            var original = frames.Images[0];
            if (original.GetExifProfile() is { } exif)
            {
                exif.SetValue(ExifTag.Orientation, (ushort)1);
                exif.RemoveValue(ExifTag.PixelXDimension);
                exif.RemoveValue(ExifTag.PixelYDimension);
                target.SetProfile(exif);
            }
            if (original.GetXmpProfile() is { } xmp) target.SetProfile(xmp);
            if (original.GetIptcProfile() is { } iptc) target.SetProfile(iptc);
        }
        catch (Exception e) when (e is MagickException or Problem)
        {
            // The picture itself is what matters; a damaged tag block is left behind.
        }
    }

    private static MagickFormat Coder(Format format) => format switch
    {
        Format.Jpg => MagickFormat.Jpeg, Format.Png => MagickFormat.Png, Format.Webp => MagickFormat.WebP, Format.Jxl => MagickFormat.Jxl,
        Format.Avif => MagickFormat.Avif, Format.Tiff => MagickFormat.Tiff, _ => MagickFormat.Png,
    };

    private static void Define(MagickImage image, Format format)
    {
        switch (format)
        {
            case Format.Jpg or Format.Avif or Format.Jxl:
                image.Quality = (uint)Settings.Quality.Webp();
                break;
            case Format.Webp:
                image.Quality = (uint)Settings.Quality.Webp();
                image.Settings.SetDefine(MagickFormat.WebP, "alpha-quality", "100");
                break;
            case Format.Png:
                image.Settings.SetDefine(MagickFormat.Png, "exclude-chunk", "date,time");
                break;
        }
    }
}
