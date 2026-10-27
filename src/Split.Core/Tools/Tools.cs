using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Split;

/// <summary>The work of the one-click tools.</summary>
public static class Tools
{
    /// <summary>Removing a background needs the model that ships with the app.</summary>
    public static bool CanRemoveBackground => Platform.Current.CanRemoveBackground;

    /// <summary>File names as people sort them: "page 2" before "page 10".</summary>
    public static int NaturalCompare(string a, string b)
    {
        var left = Regex.Split(a, @"(\d+)");
        var right = Regex.Split(b, @"(\d+)");
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var order = i % 2 == 1
                ? CompareDigits(left[i], right[i])
                : string.Compare(left[i], right[i], CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            if (order != 0) return order;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static int CompareDigits(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    }

    private static List<string> InNameOrder(IReadOnlyList<string> paths) =>
        paths.Order(Comparer<string>.Create((a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)))).ToList();

    private static string Name(string path) => Path.GetFileName(path);

    private static string Folder(string path) => Path.GetDirectoryName(Path.GetFullPath(path))!;

    /// <summary>Size of a file, or of everything inside a folder (split results are measured by their contents).</summary>
    public static long FileSize(string path) =>
        File.Exists(path) ? new FileInfo(path).Length
        : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;

    // MARK: Compress

    public static string Compress(string path)
    {
        var format = Formats.Detect(path) ?? throw Explain.NoTargets([path], tools: true);
        var originalSize = FileSize(path);
        var suffix = " (" + L("compressed") + ")";
        string output;
        switch (format.Group())
        {
            case FormatGroup.Image:
                output = CompressImage(path, format);
                break;
            case FormatGroup.Video:
                output = Output.Url(path, "mp4", suffix);
                MediaConverter.Encode(path, ["-map", "0:v:0", "-map", "0:a:0?", .. MediaConverter.H264Arguments(1080, Quality.Compact),
                                             "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-map_metadata", "0"],
                                      output, L("Compressing the video"));
                break;
            case FormatGroup.Audio:
                // Lossless sources become AAC; lossy ones are re-encoded at a lower bitrate.
                var target = format is Format.Wav or Format.Aiff or Format.Flac ? Format.M4a : format;
                output = Output.Url(path, target.Ext(), suffix);
                MediaConverter.Encode(path, ["-vn", .. MediaConverter.AudioArguments(target, "128k"), "-map_metadata", "0"],
                                      output, L("Compressing the audio"));
                break;
            default:
                output = PdfCompression.Compress(path);
                break;
        }
        if (FileSize(output) >= originalSize)
        {
            Output.Discard(output);
            throw new Problem(L("“%@” is already well compressed: the new version wouldn’t be smaller, so it wasn’t saved.", Name(path)),
                              L("To make it smaller, convert to a more efficient format (for example WebP or AVIF for images, MP4 for videos)."));
        }
        return output;
    }

    private static string CompressImage(string path, Format format)
    {
        var target = format is Format.Svg or Format.Ico ? Format.Png : format;
        if (target == Format.Heic)
        {
            // HEIC can be read but not written.
            target = Format.Jpg;
            Fidelity.Note(L("HEIC can’t be written on Windows, so the compressed copy is a JPG."));
        }
        var output = Output.Url(path, target.Ext(), " (" + L("compressed") + ")");
        try
        {
            if (format == Format.Svg)
            {
                using var drawn = ImageConverter.RenderSvg(path);
                ImageConverter.WriteSingle(drawn, null, output, target, format, faithful: false, Quality.Compact);
                return output;
            }
            using var frames = ImageConverter.Open(path, format);
            var image = frames.Images[frames.IsIcon ? frames.Largest : 0];
            if (Math.Max(image.Width, image.Height) > 2560) image.Resize(new MagickGeometry(2560, 2560));
            ImageConverter.WriteSingle(image, path, output, target, format, faithful: true, Quality.Compact);
        }
        catch
        {
            Output.Discard(output);
            throw;
        }
        return output;
    }

    // MARK: Metadata

    public static string StripMetadata(string path)
    {
        var format = Formats.Detect(path) ?? throw Explain.NoTargets([path], tools: true);
        var output = Output.Url(path, format.Ext(), " (" + L("no metadata") + ")");
        try
        {
            switch (format.Group())
            {
                case FormatGroup.Image:
                    StripImage(path, format, output);
                    break;
                case FormatGroup.Audio or FormatGroup.Video:
                    MediaConverter.Encode(path, ["-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-c", "copy",
                                                 "-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact"],
                                          output, L("Removing the metadata"));
                    break;
                default:
                    PdfCompression.StripMetadata(path, output);
                    break;
            }
        }
        catch
        {
            Output.Discard(output);
            throw;
        }
        return output;
    }

    /// <summary>
    /// exiftool works on plain names in a work folder of its own: Windows builds of it read arguments in the system's code page,
    /// and a file name may hold anything.
    /// </summary>
    private static void StripImage(string path, Format format, string output)
    {
        var exiftool = Engines.Require("exiftool");
        var folder = Output.ScratchDirectory();
        try
        {
            var source = Path.Combine(folder, "in." + format.Ext());
            var clean = Path.Combine(folder, "out." + format.Ext());
            File.Copy(path, source);
            // Drop everything but keep orientation and colour profile so the photo looks the same.
            ProcessRunner.RunChecked(exiftool, ["-q", "-all=", "-tagsFromFile", "@", "-Orientation", "-ICC_Profile", "-o", clean, source],
                                     L("Removing the metadata"), cwd: folder);
            var temp = Output.TempSibling(output);
            File.Move(clean, temp);
            Output.Commit(temp, output);
        }
        finally { Output.Discard(folder); }
    }

    // MARK: PDF

    public static string CombinePdf(IReadOnlyList<string> paths)
    {
        var sorted = InNameOrder(paths);
        var scratch = Output.ScratchDirectory();
        var name = sorted.Count == 1 ? Output.BaseName(sorted[0]) : Output.BaseName(sorted[0]) + " (" + L("combined") + ")";
        var output = Output.Unique(Path.Combine(Folder(sorted[0]), name + ".pdf"));
        var temp = Output.TempSibling(output);
        try
        {
            using var merged = new PdfDocument();
            foreach (var path in sorted)
            {
                JobProgress.CheckCancelled();
                var source = path;
                if (Formats.Detect(path) == Format.Pdf) PdfTools.Open(path).Dispose();
                else source = PictureAsPdf(path, scratch);
                using var document = PdfReader.Open(source, PdfDocumentOpenMode.Import);
                foreach (var page in document.Pages) merged.AddPage(page);
            }
            merged.Save(temp);
        }
        catch (Exception e) when (e is not Problem and not IOException and not UnauthorizedAccessException)
        {
            Output.Discard(temp);
            Output.Discard(output);
            throw new Problem(L("Split couldn’t save the combined PDF."), FileCheck.PrivacyHint(Folder(output)), e.Message);
        }
        catch
        {
            Output.Discard(temp);
            Output.Discard(output);
            throw;
        }
        finally { Output.Discard(scratch); }
        Output.Commit(temp, output);
        return output;
    }

    /// <summary>A one-page PDF of the picture, so it can join the others; the first picture of files that hold several.</summary>
    private static string PictureAsPdf(string path, string scratch)
    {
        var format = Formats.Detect(path) ?? Format.Png;
        var page = Path.Combine(scratch, $"{Guid.NewGuid():N}.pdf");
        if (format == Format.Svg)
        {
            using var drawn = ImageConverter.RenderSvg(path);
            ImageConverter.WriteSingle(drawn, null, page, Format.Pdf, format, faithful: false);
        }
        else
        {
            using var frames = ImageConverter.Open(path, format);
            ImageConverter.WriteSingle(frames.Images[0], path, page, Format.Pdf, format, faithful: false);
        }
        return page;
    }

    public static string SplitPages(string path)
    {
        using var viewer = PdfTools.Open(path);
        var count = viewer.PageCount;
        var folder = Output.Folder(path, " (" + L("pages") + ")");
        Directory.CreateDirectory(folder);
        try
        {
            using var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var digits = count.ToString().Length;
            for (var index = 0; index < count; index++)
            {
                JobProgress.CheckCancelled();
                var number = (index + 1).ToString("D" + digits);
                var target = Path.Combine(folder, Output.BaseName(path) + " - " + L("page %@", number) + ".pdf");
                try
                {
                    using var single = new PdfDocument();
                    single.AddPage(source.Pages[index]);
                    single.Save(target);
                }
                catch (Exception e) when (e is not Problem and not UnauthorizedAccessException)
                {
                    throw new Problem(L("Page %@ couldn’t be saved as a separate PDF.", index + 1),
                                      L("Check that there is disk space and permission to write in the folder."), e.Message);
                }
                JobProgress.Report((index + 1.0) / count);
            }
        }
        catch
        {
            Output.Discard(folder);
            throw;
        }
        return folder;
    }

    // MARK: Archives

    public static string Zip(IReadOnlyList<string> paths)
    {
        var name = paths.Count == 1 ? Name(paths[0]) : L("Archive");
        var output = Output.Unique(Path.Combine(Folder(paths[0]), name + ".zip"));
        try { ArchiveTools.Pack(paths, Format.Zip, output); }
        catch
        {
            Output.Discard(output);
            throw;
        }
        return output;
    }

    // MARK: Audio and video

    public static string Normalize(string path)
    {
        var format = Formats.Detect(path) ?? throw Explain.NoTargets([path], tools: true);
        var info = MediaConverter.ProbeFile(path);
        if (!info.ContainsAudio) throw new Problem(L("“%@” has no sound, so there is no volume to normalize.", Name(path)));
        var output = Output.Url(path, format.Ext(), " (" + L("normalized") + ")");
        List<string> args = ["-map", "0:v?", "-map", "0:a:0", "-af", "loudnorm=I=-16:LRA=11:TP=-1.5", "-ar", "48000"];
        args.AddRange(format.Group() == FormatGroup.Video ? ["-c:v", "copy", .. MediaConverter.AudioArgumentsForContainer(format)] : MediaConverter.AudioArguments(format));
        MediaConverter.Encode(path, [.. args, "-map_metadata", "0"], output, L("Normalizing the volume"));
        return output;
    }

    public static string Mute(string path)
    {
        var format = Formats.Detect(path) ?? throw Explain.NoTargets([path], tools: true);
        var output = Output.Url(path, format.Ext(), " (" + L("no audio") + ")");
        MediaConverter.Encode(path, ["-map", "0:v", "-c", "copy", "-an", "-map_metadata", "0"], output, L("Removing the audio"));
        return output;
    }

    public static string ConcatVideos(IReadOnlyList<string> paths)
    {
        var sorted = InNameOrder(paths);
        var probes = sorted.Select(MediaConverter.ProbeFile).ToList();
        if (probes[0].Width <= 0 || probes[0].Height <= 0)
        {
            throw new Problem(L("The first video (“%@”) has no picture or its dimensions are invalid.", Name(sorted[0])),
                              L("Videos are joined in name order and at the size of the first one. Check that the first one opens in a video player."));
        }
        var width = probes[0].Width / 2 * 2;
        var height = probes[0].Height / 2 * 2;
        var inputs = new List<string>();
        foreach (var path in sorted) inputs.AddRange(MediaConverter.LocalInput(path));
        var filter = new StringBuilder();
        var concatInputs = new StringBuilder();
        var silentIndex = sorted.Count;
        for (var index = 0; index < probes.Count; index++)
        {
            filter.Append($"[{index}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,")
                  .Append($"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{index}];");
            if (probes[index].ContainsAudio) filter.Append($"[{index}:a:0]aresample=48000,aformat=channel_layouts=stereo[a{index}];");
            else
            {
                // Silent clips get generated silence so concat keeps audio in sync.
                inputs.AddRange(["-f", "lavfi", "-t", Math.Max(probes[index].Duration, 0.1).ToString("0.000", CultureInfo.InvariantCulture), "-i", "anullsrc=r=48000:cl=stereo"]);
                filter.Append($"[{silentIndex}:a:0]anull[a{index}];");
                silentIndex++;
            }
            concatInputs.Append($"[v{index}][a{index}]");
        }
        filter.Append($"{concatInputs}concat=n={sorted.Count}:v=1:a=1[v][a]");
        var output = Output.Unique(Path.Combine(Folder(sorted[0]), Output.BaseName(sorted[0]) + " (" + L("joined") + ").mp4"));
        var temp = Output.TempSibling(output);
        try
        {
            ProcessRunner.RunChecked(MediaConverter.Ffmpeg(),
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. inputs, "-filter_complex", filter.ToString(), "-map", "[v]", "-map", "[a]",
                 "-c:v", "libx264", "-preset", "veryfast", "-crf", Settings.Quality.X264Crf(),
                 "-c:a", "aac", "-b:a", Settings.Quality.AudioKbps(), "-movflags", "+faststart", temp],
                L("Joining the videos"));
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

    public static string Mono(string path)
    {
        var format = Formats.Detect(path) ?? throw Explain.NoTargets([path], tools: true);
        var info = MediaConverter.ProbeFile(path);
        if (!info.ContainsAudio) throw new Problem(L("“%@” has no sound to convert to mono.", Name(path)));
        var output = Output.Url(path, format.Ext(), " (" + L("mono") + ")");
        // The built-in Vorbis encoder only writes stereo: the same mono signal goes to both sides.
        string[] downmix = format == Format.Ogg ? ["-af", "pan=stereo|c0=0.5*c0+0.5*c1|c1=0.5*c0+0.5*c1"] : [];
        List<string> args = ["-map", "0:v?", "-map", "0:a:0", .. downmix];
        args.AddRange(format.Group() == FormatGroup.Video ? ["-c:v", "copy", .. MediaConverter.AudioArgumentsForContainer(format)] : MediaConverter.AudioArguments(format));
        if (format != Format.Ogg) args.AddRange(["-ac", "1"]);
        MediaConverter.Encode(path, [.. args, "-map_metadata", "0"], output, L("Converting to mono"));
        return output;
    }

    public static string TrimSilence(string path)
    {
        var format = Formats.Detect(path) ?? throw Explain.NoTargets([path], tools: true);
        var info = MediaConverter.ProbeFile(path);
        if (!info.ContainsAudio) throw new Problem(L("“%@” has no sound.", Name(path)));
        if (info.Duration >= 3 * 3600)
        {
            throw new Problem(L("“%@” is longer than 3 hours, too long to trim silence in one go.", Name(path)),
                              L("Cut it into parts with the audio editor first."));
        }
        // start_duration must be 0: in ffmpeg 9 it also trims that much of the sound itself.
        // start_silence keeps 50 ms so the cut is not abrupt and a second pass finds nothing.
        const string edge = "silenceremove=start_periods=1:start_duration=0:start_silence=0.05:start_threshold=-50dB:detection=peak";
        var output = Output.Url(path, format.Ext(), " (" + L("silence trimmed") + ")");
        MediaConverter.Encode(path, ["-vn", "-af", $"{edge},areverse,{edge},areverse", .. MediaConverter.AudioArguments(format), "-map_metadata", "0"],
                              output, L("Trimming the silence"));
        double result;
        try { result = MediaConverter.ProbeFile(output).Duration; }
        catch (Problem problem) when (!problem.IsCancellation) { result = info.Duration; }
        if (info.Duration - result < 0.05)
        {
            Output.Discard(output);
            throw new Problem(L("“%@” has no silence at the start or the end, so there was nothing to trim.", Name(path)),
                              L("Only sound below −50 dB counts as silence."));
        }
        return output;
    }

    /// <summary>Twelve evenly spaced frames, full resolution, in a folder next to the video.</summary>
    public static string Snapshots(string path)
    {
        var info = MediaConverter.ProbeFile(path);
        if (!info.HasVideo || info.Duration <= 0) throw new Problem(L("“%@” has no picture to take frames from.", Name(path)));
        var folder = Output.Folder(path, " (" + L("frames") + ")");
        Directory.CreateDirectory(folder);
        try
        {
            const int count = 12;
            for (var index = 0; index < count; index++)
            {
                var time = info.Duration * (index + 0.5) / count;
                var frame = Path.Combine(folder, $"{L("Video frame")} {index + 1:00}.png");
                MediaConverter.Encode(path, ["-frames:v", "1", "-update", "1"], frame, L("Capturing frame %@", index + 1),
                                      ["-ss", time.ToString("0.000", CultureInfo.InvariantCulture)], duration: 1);
                JobProgress.Report((index + 1.0) / count);
            }
        }
        catch
        {
            Output.Discard(folder);
            throw;
        }
        return folder;
    }

    /// <summary>A shareable video of the sound wave: white on black, like the app.</summary>
    public static string WaveformVideo(string path)
    {
        var info = MediaConverter.ProbeFile(path);
        if (!info.ContainsAudio) throw new Problem(L("“%@” has no sound.", Name(path)));
        var output = Output.Url(path, "mp4", " (" + L("video") + ")");
        const string filter = "[0:a]aformat=channel_layouts=mono,showwaves=s=1280x720:mode=cline:rate=30:colors=0xF2F2F2,format=yuv420p[v]";
        MediaConverter.Encode(path, ["-filter_complex", filter, "-map", "[v]", "-map", "0:a:0", "-c:v", "libx264", "-preset", "veryfast",
                                     "-crf", "23", "-c:a", "aac", "-b:a", Settings.Quality.AudioKbps(), "-shortest", "-movflags", "+faststart"],
                              output, L("Creating the video"), duration: info.Duration);
        return output;
    }

    // MARK: Pictures

    public static string RemoveBackground(string path) => BackgroundRemoval.Remove(path);

    public static IReadOnlyList<string> ReadQr(string path) => QrCodes.Read(path);
}
