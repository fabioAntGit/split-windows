using System.Globalization;
using ImageMagick;

namespace Split;

/// <summary>
/// "Compress To…": the smallest loss that still fits a size the user picks (for e-mail limits, upload forms…).
/// Images search for the best quality and only shrink if they must; audio and video get the exact bitrate
/// the size allows (video in two passes).
/// </summary>
public static class TargetSize
{
    /// <summary>Quick choices in the prompt, in bytes.</summary>
    public static readonly IReadOnlyList<long> Presets = [500_000, 1_000_000, 2_000_000, 5_000_000, 10_000_000, 25_000_000];

    /// <summary>Common upload limits, a little under the real ones so the file passes with room to spare (e-mail grows attachments by about a third when sending them).</summary>
    public static IReadOnlyList<(string Title, long Bytes)> Uses =>
        [(L("E-mail attachment"), 18_000_000), (L("Discord, free account"), 10_000_000)];

    /// <summary>The smallest size the prompt accepts.</summary>
    public const long Minimum = 10_000;

    /// <summary>"2MB", "1,5 MB", "800kb", "2 Mo", "300000" → bytes (decimal units; French writes octets: Ko, Mo, Go).</summary>
    public static long? Parse(string text)
    {
        var cleaned = string.Concat(text.ToLowerInvariant().Replace(',', '.').Where(c => !char.IsWhiteSpace(c)));
        (string Suffix, double Factor)[] units =
            [("gb", 1e9), ("mb", 1e6), ("kb", 1e3), ("go", 1e9), ("mo", 1e6), ("ko", 1e3), ("g", 1e9), ("m", 1e6), ("k", 1e3), ("b", 1), ("o", 1)];
        foreach (var (suffix, factor) in units)
        {
            if (!cleaned.EndsWith(suffix, StringComparison.Ordinal)) continue;
            return double.TryParse(cleaned[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
                ? (long)(value * factor) : null;
        }
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) && plain >= 1 ? (long)plain : null;
    }

    /// <summary>"500 KB", "10 MB": what goes in the size field, readable back by <see cref="Parse"/> in any language.</summary>
    public static string Plain(long bytes)
    {
        if (bytes >= 1_000_000)
        {
            var megabytes = bytes / 1e6;
            return (megabytes == Math.Round(megabytes) ? ((long)megabytes).ToString(CultureInfo.InvariantCulture) : megabytes.ToString("0.0", CultureInfo.InvariantCulture)) + " MB";
        }
        return $"{bytes / 1000} KB";
    }

    public static string Describe(long bytes) => ByteCount.Format(bytes);

    public static string Compress(string path, long limit)
    {
        var format = Formats.Detect(path) ?? throw Explain.NoTargets([path], tools: true);
        var size = Tools.FileSize(path);
        if (size <= limit)
        {
            throw new Problem(L("“%@” is already %@, within the %@ you asked for.", Path.GetFileName(path), Describe(size), Describe(limit)),
                              L("Nothing was changed."));
        }
        return format.Group() switch
        {
            FormatGroup.Image => Image(path, format, limit),
            FormatGroup.Audio => Audio(path, format, limit),
            FormatGroup.Video => Video(path, limit),
            _ => throw Explain.NoTargets([path], tools: true),
        };
    }

    // MARK: Images

    private static string Image(string path, Format format, long limit)
    {
        using var frames = ImageConverter.Open(path, format);
        var full = frames.Images[0];
        // Lossy formats stay as they are; lossless ones need a lossy format to get small.
        Format[] lossy = [Format.Jpg, Format.Avif, Format.Webp, Format.Jxl];
        var target = lossy.Contains(format) ? format : ImageConverter.HasTransparency(full) ? Format.Webp : Format.Jpg;
        if (target != format)
            Fidelity.Note(L("%@ can only get this small as a photo format, so the result is %@.", format.Label(), target.Label()));
        var scale = 1.0;
        for (var attempt = 0; attempt < 14; attempt++)
        {
            JobProgress.CheckCancelled();
            using var scaled = scale < 1 ? Shrunk(full, scale) : null;
            var image = scaled ?? full;
            if (BestEncoding(image, target, limit) is { } data)
            {
                var output = Output.Url(path, target.Ext(), " (" + Describe(limit) + ")");
                var temp = Output.TempSibling(output);
                try { File.WriteAllBytes(temp, data); }
                catch
                {
                    Output.Discard(temp);
                    throw;
                }
                Output.Commit(temp, output);
                if (scale < 1)
                {
                    var (width, height) = image.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom ? (image.Height, image.Width) : (image.Width, image.Height);
                    Fidelity.Note(L("To fit %@ the image was also reduced to %@×%@ pixels.", Describe(limit), width.ToString(), height.ToString()));
                }
                return output;
            }
            scale *= 0.8;
            JobProgress.Report(1 - scale);
        }
        throw new Problem(L("“%@” can’t get down to %@ without becoming unrecognizable.", Path.GetFileName(path), Describe(limit)), L("Choose a larger size."));
    }

    private static IMagickImage<ushort> Shrunk(IMagickImage<ushort> full, double scale)
    {
        var side = (uint)Math.Max(1, Math.Max(full.Width, full.Height) * scale);
        var copy = full.Clone();
        copy.Resize(new MagickGeometry(side, side));
        return copy;
    }

    /// <summary>The highest quality whose file fits the limit (binary search), or null when even the lowest is too big.</summary>
    private static byte[]? BestEncoding(IMagickImage<ushort> image, Format format, long limit)
    {
        double low = 0.25, high = 0.95;
        var best = ImageConverter.EncodeAt(image, format, (int)(low * 100));
        if (best.Length > limit) return null;
        for (var step = 0; step < 7; step++)
        {
            var middle = (low + high) / 2;
            var data = ImageConverter.EncodeAt(image, format, (int)(middle * 100));
            if (data.Length <= limit)
            {
                best = data;
                low = middle;
            }
            else high = middle;
        }
        return best;
    }

    // MARK: Audio and video

    /// <summary>AAC below this sounds broken; video below this is a slideshow.</summary>
    public const int MinimumAudioKbps = 24;
    public const int MinimumVideoKbps = 90;

    private static string Audio(string path, Format format, long limit)
    {
        var info = MediaConverter.ProbeFile(path);
        if (info.Duration <= 0) throw UnknownLength(path);
        var kbps = (int)(limit * 8 * 0.97 / info.Duration / 1000);
        if (kbps < MinimumAudioKbps) throw TooSmall(path, (long)(MinimumAudioKbps * 1000 * info.Duration / 8 / 0.97));
        var target = format is Format.Mp3 or Format.Ogg or Format.Opus ? format : Format.M4a;
        var output = Output.Url(path, target.Ext(), " (" + Describe(limit) + ")");
        MediaConverter.Encode(path, ["-vn", .. MediaConverter.AudioArguments(target, $"{Math.Min(kbps, 320)}k"), "-map_metadata", "0"],
                              output, L("Compressing the audio"), duration: info.Duration);
        return output;
    }

    private static string Video(string path, long limit)
    {
        var info = MediaConverter.ProbeFile(path);
        if (info.Duration <= 0) throw UnknownLength(path);
        var totalKbps = limit * 8 * 0.96 / info.Duration / 1000;
        var audioKbps = info.ContainsAudio ? (totalKbps > 600 ? 128 : totalKbps > 250 ? 96 : 48) : 0;
        var videoKbps = (int)totalKbps - audioKbps;
        if (videoKbps < MinimumVideoKbps) throw TooSmall(path, (long)((MinimumVideoKbps + 48) * 1000 * info.Duration / 8 / 0.96));
        // The lower the bitrate, the smaller the picture that still looks clean.
        var height = videoKbps > 2500 ? 1080 : videoKbps > 1200 ? 720 : videoKbps > 500 ? 540 : 360;
        var output = Output.Url(path, "mp4", " (" + Describe(limit) + ")");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            TwoPass(path, info, videoKbps, audioKbps, height, output);
            var size = Tools.FileSize(output);
            if (size <= limit) break;
            // Container overhead on very short clips can overshoot: one corrective pass.
            if (attempt == 1) break;
            File.Delete(output);
            videoKbps = (int)(videoKbps * (double)limit / size * 0.95);
        }
        if (info.DisplaySize.Height > height)
            Fidelity.Note(L("To fit %@ the video was reduced to %@p.", Describe(limit), height.ToString()));
        return output;
    }

    private static void TwoPass(string path, MediaConverter.Probe info, int videoKbps, int audioKbps, int maxHeight, string output)
    {
        var folder = Output.ScratchDirectory();
        try
        {
            var log = Path.Combine(folder, "pass");
            string[] picture =
            [
                "-vf", $"scale=-2:'min({maxHeight},ih)'," + MediaConverter.EvenDimensions, "-c:v", "libx264", "-preset", "medium",
                "-b:v", $"{videoKbps}k", "-pix_fmt", "yuv420p", "-passlogfile", log,
            ];
            ProcessRunner.RunChecked(MediaConverter.Ffmpeg(),
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. MediaConverter.LocalInput(path), "-map", "0:v:0", .. picture, "-pass", "1", "-an", "-f", "null", "NUL"],
                L("Analyzing the video"), cwd: folder);
            JobProgress.Report(0.4);
            string[] sound = audioKbps > 0 ? ["-map", "0:a:0", "-c:a", "aac", "-b:a", $"{audioKbps}k"] : [];
            MediaConverter.Encode(path, ["-map", "0:v:0", .. picture, "-pass", "2", .. sound, "-map_metadata", "0", "-movflags", "+faststart"],
                                  output, L("Compressing the video"), duration: info.Duration);
        }
        finally { Output.Discard(folder); }
    }

    private static Problem UnknownLength(string path) =>
        new(L("The length of “%@” is unknown, so the bitrate can’t be worked out.", Path.GetFileName(path)));

    private static Problem TooSmall(string path, long minimum) =>
        new(L("“%@” is too long to fit that size without falling apart.", Path.GetFileName(path)),
            L("Choose at least %@, or trim it first in the editor.", Describe(minimum)));
}
