using ImageMagick;
using SkiaSharp;

namespace Split;

/// <summary>Pictures. WebP and JPEG XL are written by the app itself; HEIC can be read but not written.</summary>
public sealed class ImageConverter : IConversionFamily
{
    public static readonly Format[] StillTargets =
        [Format.Jpg, Format.Png, Format.Webp, Format.Jxl, Format.Avif, Format.Tiff, Format.Bmp, Format.Gif, Format.Ico, Format.Pdf];

    public IReadOnlyList<Format> Targets(Format source) =>
        source.Group() != FormatGroup.Image ? [] : source == Format.Gif ? [.. StillTargets, Format.Mp4, Format.Webm] : StillTargets;

    private static MagickFormat Coder(Format format) => format switch
    {
        Format.Jpg => MagickFormat.Jpeg, Format.Png => MagickFormat.Png, Format.Webp => MagickFormat.WebP, Format.Jxl => MagickFormat.Jxl,
        Format.Avif => MagickFormat.Avif, Format.Tiff => MagickFormat.Tiff, Format.Bmp => MagickFormat.Bmp, Format.Gif => MagickFormat.Gif,
        Format.Ico => MagickFormat.Ico, _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public IReadOnlyList<string> Convert(string path, Format source, Format target)
    {
        if (source == Format.Gif && target is Format.Mp4 or Format.Webm) return new MediaConverter().Convert(path, source, target);
        Imaging.Initialize();
        if (source == Format.Svg)
        {
            var drawn = Output.Url(path, target.Ext());
            using var render = RenderSvg(path);
            WriteSingle(render, null, drawn, target, source, faithful: false);
            return [drawn];
        }
        using var frames = Open(path, source);
        if (frames.Images.Count > 1) return ConvertFrames(path, frames, source, target);
        var output = Output.Url(path, target.Ext());
        WriteSingle(frames.Images[0], path, output, target, source, faithful: true);
        return [output];
    }

    /// <summary>What a file with several images holds: an animation (frames with timing), the sizes of an icon, or pages (multi-page TIFF, HEIC collections).</summary>
    public sealed class Frames(List<IMagickImage<ushort>> images, List<double> delays, int loops, bool animated, bool isIcon) : IDisposable
    {
        public List<IMagickImage<ushort>> Images { get; } = images;

        /// <summary>Seconds each frame stays on screen.</summary>
        public List<double> Delays { get; } = delays;

        /// <summary>0 means "forever", as in the formats themselves.</summary>
        public int Loops { get; } = loops;

        public bool Animated { get; } = animated;
        public bool IsIcon { get; } = isIcon;

        /// <summary>The biggest image: what a single picture of an icon should show.</summary>
        public int Largest => Enumerable.Range(0, Images.Count).MaxBy(i => (long)Images[i].Width * Images[i].Height);

        public void Dispose()
        {
            foreach (var image in Images) image.Dispose();
        }
    }

    /// <summary>Formats that hold pages.</summary>
    private static readonly HashSet<Format> PageTargets = [Format.Tiff, Format.Pdf];

    private static IReadOnlyList<string> ConvertFrames(string path, Frames frames, Format source, Format target)
    {
        var output = Output.Url(path, target.Ext());
        if (frames.IsIcon)
        {
            // An icon's images are one picture at several sizes: keep the biggest.
            WriteSingle(frames.Images[frames.Largest], null, output, target, source, faithful: false);
            return [output];
        }
        if (frames.Animated)
        {
            switch (target)
            {
                case Format.Gif or Format.Png or Format.Webp or Format.Jxl:
                    WriteAnimation(frames, output, target);
                    break;
                case Format.Tiff or Format.Pdf:
                    WritePages(frames, output, target);
                    Fidelity.Note(L("%@ keeps every frame of the animation, but not its timing: it doesn’t play.", target.Label()));
                    break;
                default:
                    WriteSingle(frames.Images[0], path, output, target, source, faithful: true);
                    Fidelity.Note(L("%@ holds a single picture, so only the first of the %@ frames was kept. GIF, PNG, WebP, JPEG XL or MP4 keep the animation.",
                                    target.Label(), frames.Images.Count.ToString()));
                    break;
            }
            return [output];
        }
        if (PageTargets.Contains(target))
        {
            WritePages(frames, output, target);
            return [output];
        }
        // Pages into a one-picture format: one file per page, in a folder, so none is lost.
        Output.Discard(output);
        var folder = Output.Folder(path, " (" + L("pages") + ")");
        Directory.CreateDirectory(folder);
        for (var index = 0; index < frames.Images.Count; index++)
        {
            JobProgress.CheckCancelled();
            var page = Path.Combine(folder, $"{Output.BaseName(path)} {index + 1}.{target.Ext()}");
            WriteSingle(frames.Images[index], path, page, target, source, faithful: target is not (Format.Webp or Format.Jxl));
            JobProgress.Report((index + 1.0) / frames.Images.Count);
        }
        return [folder];
    }

    // MARK: Reading

    private static Problem Unreadable(string path) =>
        new(L("Split couldn’t read “%@” as an image.", Path.GetFileName(path)),
            FileCheck.Mismatch(path) ?? L("The file seems damaged or incomplete (for example, a download that didn’t finish). Open it in Photos to check."));

    public static Frames Open(string path, Format source)
    {
        Imaging.Initialize();
        var format = Imaging.FormatOf(path, source) ?? throw Unreadable(path);
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (format == MagickFormat.Png && PngChunks.Disassemble(bytes) is { } animation)
                return new Frames(animation.Frames, animation.Delays, animation.Loops, animated: true, isIcon: false);
            using var collection = new MagickImageCollection(bytes, new MagickReadSettings { Format = format });
            if (collection.Count == 0) throw Unreadable(path);
            var animated = collection.Count > 1 && format is MagickFormat.Gif or MagickFormat.WebP or MagickFormat.Jxl;
            if (animated) collection.Coalesce();
            var loops = (int)collection[0].AnimationIterations;
            var delays = collection.Select(image =>
            {
                var seconds = (double)image.AnimationDelay / Math.Max(1, image.AnimationTicksPerSecond);
                return seconds > 0.01 ? seconds : 0.1;
            }).ToList();
            // The collection is disposed here; the frames own their own copies from now on.
            var images = collection.Select(image => image.Clone()).ToList();
            return new Frames(images, delays, loops, animated, isIcon: format == MagickFormat.Ico);
        }
        catch (MagickException)
        {
            throw Unreadable(path);
        }
    }

    /// <summary>The SVG as pixels. It is drawn from a copy without remote references, so it can't load anything from the Internet.</summary>
    public static IMagickImage<ushort> RenderSvg(string path)
    {
        Imaging.Initialize();
        var failure = new Problem(L("Split couldn’t draw the SVG “%@”.", Path.GetFileName(path)),
                                  L("The file may have syntax errors or use SVG features that aren’t supported. Open it in a web browser to check."));
        try
        {
            using var svg = new Svg.Skia.SKSvg();
            svg.FromSvg(LocalOnly.Svg(DocumentPipeline.ReadText(path)));
            if (svg.Picture is not { } picture) throw failure;
            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0) throw failure;
            var longest = Math.Max(bounds.Width, bounds.Height);
            // Small drawings are enlarged to about 2048 px; a huge declared size is held to what memory can take.
            var scale = Math.Min(Math.Max(1, 2048 / longest), 16384 / longest);
            var width = (int)Math.Max(1, Math.Round(bounds.Width * scale));
            var height = (int)Math.Max(1, Math.Round(bounds.Height * scale));
            using var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(width / bounds.Width, height / bounds.Height);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return new MagickImage(encoded.ToArray(), new MagickReadSettings { Format = MagickFormat.Png });
        }
        catch (Exception e) when (e is not Problem and not IOException and not UnauthorizedAccessException)
        {
            throw new Problem(failure.Message, failure.Suggestion, e.Message);
        }
    }

    // MARK: Writing

    /// <summary>Formats that keep the orientation as a tag; the others (and a PNG, which many viewers read without it) get the pixels turned.</summary>
    private static readonly HashSet<Format> OrientationTagTargets = [Format.Jpg, Format.Tiff];

    /// <summary>
    /// One image ready for a format: upright pixels (or, where the format keeps the tag, the tag), the colour
    /// profile, and the rest of the metadata only when "Keep metadata" is on and the pixels come from a file.
    /// </summary>
    private static IMagickImage<ushort> Prepare(IMagickImage<ushort> source, Format format, bool faithful)
    {
        var image = source.Clone();
        var keep = Settings.KeepsMetadata && faithful;
        if (!(keep && OrientationTagTargets.Contains(format))) Upright(image);
        if (!keep)
        {
            var profile = image.GetColorProfile();
            image.Strip();
            if (profile is not null) image.SetProfile(profile);
        }
        else if (image.GetExifProfile() is { } exif)
        {
            // Pixel dimensions in EXIF describe the original; the picture may differ now.
            exif.RemoveValue(ExifTag.PixelXDimension);
            exif.RemoveValue(ExifTag.PixelYDimension);
            image.SetProfile(exif);
        }
        if (format == Format.Jpg && HasTransparency(image))
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
            Fidelity.Note(L("JPG has no transparency, so transparent areas became white. PNG, WebP or JPEG XL keep it."));
        }
        return image;
    }

    /// <summary>Turns the pixels to the orientation the picture is meant to be seen in and says so in its EXIF, if it has any.</summary>
    private static void Upright(IMagickImage<ushort> image)
    {
        if (image.Orientation is Orientation.Undefined or Orientation.TopLeft) return;
        image.AutoOrient();
        if (image.GetExifProfile() is { } exif)
        {
            exif.SetValue(ExifTag.Orientation, (ushort)1);
            image.SetProfile(exif);
        }
    }

    /// <summary>An alpha channel that is actually used (many opaque PNGs carry one anyway).</summary>
    public static bool HasTransparency(IMagickImage<ushort> image) =>
        image.HasAlpha && image.Statistics().GetChannel(PixelChannel.Alpha) is { } alpha && alpha.Minimum < Quantum.Max;

    private static Problem Refused(Format format, IMagickImage<ushort> image, Exception error) =>
        error is MagickResourceLimitErrorException
            ? new Problem(L("There isn’t enough memory for a %@ × %@ pixel image.", image.Width.ToString(), image.Height.ToString()),
                          L("Close other apps or make the image smaller first."), error.Message)
            : new Problem(L("The %@ encoder refused this image (%@ × %@ px).", format.Label(), image.Width.ToString(), image.Height.ToString()),
                          format == Format.Avif ? L("%@ has limits on size and color format. Try PNG, JPG or WebP.", format.Label()) : L("Try another format, such as PNG."),
                          error.Message);

    /// <summary>
    /// One picture in any format. <paramref name="faithful"/> says the pixels come from a file whose metadata
    /// should travel with them; <paramref name="path"/> is that file, for a JPEG that goes into a PDF untouched.
    /// </summary>
    private static void WriteSingle(IMagickImage<ushort> source, string? path, string output, Format target, Format sourceFormat, bool faithful)
    {
        if (target == Format.Ico)
        {
            using var upright = Prepare(source, target, faithful: false);
            WriteIcon(upright, output);
            return;
        }
        if (target == Format.Pdf)
        {
            using var upright = Prepare(source, target, faithful: false);
            // A camera JPEG that is already upright goes in as it is: no second round of compression.
            var original = path is not null && sourceFormat == Format.Jpg && (source.Orientation is Orientation.TopLeft or Orientation.Undefined) && !HasTransparency(source)
                ? File.ReadAllBytes(path) : null;
            ImagePdf.Write([(upright, original)], output);
            return;
        }
        using var image = Prepare(source, target, faithful);
        Save(image, output, target, JxlLossless(sourceFormat));
    }

    /// <summary>JPEG XL keeps lossless sources lossless; the rest uses the chosen quality.</summary>
    private static bool JxlLossless(Format source) => source is Format.Png or Format.Tiff or Format.Bmp or Format.Gif or Format.Ico or Format.Svg;

    private static void Define(IMagickImage<ushort> image, Format format, bool jxlLossless)
    {
        var quality = Settings.Quality;
        switch (format)
        {
            case Format.Jpg or Format.Avif:
                image.Quality = (uint)quality.Webp();
                break;
            case Format.Webp:
                image.Quality = (uint)quality.Webp();
                image.Settings.SetDefine(MagickFormat.WebP, "alpha-quality", "100");
                break;
            case Format.Jxl:
                image.Quality = jxlLossless ? 100u : (uint)quality.Webp();
                break;
            case Format.Png:
                image.Settings.SetDefine(MagickFormat.Png, "exclude-chunk", "date,time");
                break;
            case Format.Bmp when image.HasAlpha:
                image.Settings.SetDefine(MagickFormat.Bmp, "format", "bmp4");
                break;
        }
    }

    /// <summary>Encodes into a hidden temp next to the result and renames it when the encoder is done.</summary>
    private static void Save(IMagickImage<ushort> image, string output, Format format, bool jxlLossless = false)
    {
        var temp = Output.TempSibling(output);
        try
        {
            Define(image, format, jxlLossless);
            var profile = image.GetColorProfile()?.ToByteArray();
            using (var file = File.Create(temp)) image.Write(file, Coder(format));
            // The encoder swaps a standard sRGB profile for a bare marker; the profile itself is kept in the file.
            if (format == Format.Png && profile is not null && File.ReadAllBytes(temp) is var bytes && !PngChunks.HasProfile(bytes))
                File.WriteAllBytes(temp, PngChunks.WithProfile(bytes, profile));
        }
        catch (MagickException e)
        {
            Output.Discard(temp);
            throw Refused(format, image, e);
        }
        catch
        {
            Output.Discard(temp);
            throw;
        }
        Output.Commit(temp, output);
    }

    /// <summary>Every image of the source as a page (PDF) or an image of a multi-page TIFF.</summary>
    private static void WritePages(Frames frames, string output, Format format)
    {
        var prepared = new List<IMagickImage<ushort>>();
        try
        {
            if (format == Format.Pdf)
            {
                foreach (var image in frames.Images) prepared.Add(Prepare(image, format, faithful: false));
                ImagePdf.Write(prepared.Select(p => (p, (byte[]?)null)).ToList(), output);
                return;
            }
            var temp = Output.TempSibling(output);
            try
            {
                foreach (var image in frames.Images)
                {
                    JobProgress.CheckCancelled();
                    prepared.Add(Prepare(image, format, faithful: true));
                }
                using var collection = new MagickImageCollection();
                foreach (var image in prepared) collection.Add(image.Clone());
                using (var file = File.Create(temp)) collection.Write(file, MagickFormat.Tiff);
            }
            catch (MagickException e)
            {
                Output.Discard(temp);
                throw new Problem(L("Split could not put the %@ images together into one %@.", frames.Images.Count.ToString(), format.Label()), technical: e.Message);
            }
            catch
            {
                Output.Discard(temp);
                throw;
            }
            Output.Commit(temp, output);
        }
        finally
        {
            foreach (var image in prepared) image.Dispose();
        }
    }

    /// <summary>Animated GIF, PNG (APNG), WebP or JPEG XL with each frame's own delay and the original loop count.</summary>
    private static void WriteAnimation(Frames frames, string output, Format format)
    {
        var temp = Output.TempSibling(output);
        try
        {
            if (format == Format.Png)
            {
                var pictures = new List<(byte[] Png, double Delay)>();
                for (var index = 0; index < frames.Images.Count; index++)
                {
                    JobProgress.CheckCancelled();
                    using var frame = frames.Images[index].Clone();
                    // Every frame in the same layout (RGBA, 8 bits), so they share one header.
                    frame.Settings.SetDefine(MagickFormat.Png, "color-type", "6");
                    frame.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8");
                    frame.Settings.SetDefine(MagickFormat.Png, "exclude-chunk", "date,time");
                    frame.Alpha(AlphaAction.Set);
                    pictures.Add((frame.ToByteArray(MagickFormat.Png), frames.Delays[index]));
                }
                File.WriteAllBytes(temp, PngChunks.Assemble(pictures, frames.Loops));
            }
            else
            {
                using var collection = new MagickImageCollection();
                for (var index = 0; index < frames.Images.Count; index++)
                {
                    JobProgress.CheckCancelled();
                    var frame = frames.Images[index].Clone();
                    frame.AnimationDelay = (uint)Math.Max(1, Math.Round(frames.Delays[index] * 100));
                    frame.AnimationTicksPerSecond = 100;
                    frame.AnimationIterations = (uint)frames.Loops;
                    frame.Quality = format == Format.Jxl ? 100u : (uint)Settings.Quality.Webp();
                    if (format == Format.Webp) frame.Settings.SetDefine(MagickFormat.WebP, "alpha-quality", "100");
                    collection.Add(frame);
                }
                using var file = File.Create(temp);
                collection.Write(file, Coder(format));
            }
        }
        catch (Exception e) when (e is MagickException or InvalidDataException)
        {
            Output.Discard(temp);
            throw new Problem(L("Split could not build the animated %@ (%@ frames).", format.Label(), frames.Images.Count.ToString()),
                              L("Very long or large animations can fail. Convert to MP4 or WebP, which handle animation better."), e.Message);
        }
        catch
        {
            Output.Discard(temp);
            throw;
        }
        Output.Commit(temp, output);
    }

    /// <summary>An icon with the standard sizes up to the image's own (ICO stops at 256 px).</summary>
    private static void WriteIcon(IMagickImage<ushort> image, string output)
    {
        var longest = (int)Math.Max(image.Width, image.Height);
        if (longest > 256) Fidelity.Note(L("ICO holds at most 256×256 pixels, so the image was reduced to that size."));
        var sides = new[] { 16, 32, 48, 64, 128, 256 }.Where(side => side <= Math.Max(16, longest)).ToList();
        var temp = Output.TempSibling(output);
        try
        {
            using var collection = new MagickImageCollection();
            // The biggest first.
            foreach (var side in Enumerable.Reverse(sides)) collection.Add(SquareIcon(image, side));
            using (var file = File.Create(temp)) collection.Write(file, MagickFormat.Ico);
        }
        catch (MagickException e)
        {
            Output.Discard(temp);
            throw Refused(Format.Ico, image, e);
        }
        catch
        {
            Output.Discard(temp);
            throw;
        }
        Output.Commit(temp, output);
    }

    /// <summary>ICO entries must be square and at most 256 px: fit the image on a transparent square.</summary>
    private static IMagickImage<ushort> SquareIcon(IMagickImage<ushort> image, int side)
    {
        var square = image.Clone();
        square.Alpha(AlphaAction.Set);
        square.Resize(new MagickGeometry((uint)side, (uint)side));
        square.Extent((uint)side, (uint)side, Gravity.Center, MagickColors.Transparent);
        return square;
    }
}
