using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Split;

/// <summary>
/// Audio, video and subtitles through ffmpeg. Every track, subtitle, chapter and tag the target can
/// hold goes along; streams that already fit the container are copied untouched, and what the
/// container can't hold is told to the user (see <see cref="Fidelity"/>).
/// </summary>
public sealed class MediaConverter : IConversionFamily
{
    public static readonly Format[] AudioTargets = [Format.Mp3, Format.M4a, Format.Wav, Format.Flac, Format.Ogg, Format.Opus, Format.Aiff, Format.Wma];
    public static readonly Format[] VideoTargets = [Format.Mp4, Format.Mov, Format.Mkv, Format.Webm, Format.Avi, Format.Wmv, Format.Gif];

    public IReadOnlyList<Format> Targets(Format source) => source.Group() switch
    {
        FormatGroup.Audio => AudioTargets,
        FormatGroup.Video => [.. VideoTargets, .. AudioTargets],
        FormatGroup.Subtitle => [Format.Srt, Format.Vtt],
        _ => [],
    };

    public IReadOnlyList<string> Convert(string path, Format source, Format target) =>
        source.Group() == FormatGroup.Subtitle ? Subtitle(path, target) : Transcode(path, target);

    /// <summary>One stream of a media file, as ffprobe describes it.</summary>
    public sealed class StreamInfo
    {
        public int Index { get; set; }
        public string Type { get; set; } = "";
        public string Codec { get; set; } = "";
        public bool IsCover { get; set; }
        public int Channels { get; set; }
        public string SampleFormat { get; set; } = "";
        public int Bits { get; set; }
        public string Transfer { get; set; } = "";
    }

    public sealed class Probe
    {
        public string? VideoEncoder { get; set; }
        public string? AudioEncoder { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Rotation { get; set; }
        public double Duration { get; set; }
        public int Chapters { get; set; }
        public List<StreamInfo> Streams { get; } = [];

        public bool HasVideo => VideoEncoder is not null;
        public bool ContainsAudio => AudioEncoder is not null;

        /// <summary>Size as played (phones store portrait video as rotated landscape).</summary>
        public (int Width, int Height) DisplaySize => Math.Abs(Rotation) % 180 == 90 ? (Height, Width) : (Width, Height);

        public List<StreamInfo> Videos => Streams.Where(s => s.Type == "video" && !s.IsCover).ToList();
        public List<StreamInfo> Audios => Streams.Where(s => s.Type == "audio").ToList();
        public List<StreamInfo> Covers => Streams.Where(s => s.Type == "video" && s.IsCover).ToList();
        public List<StreamInfo> Subtitles => Streams.Where(s => s.Type == "subtitle").ToList();
        public List<StreamInfo> Attachments => Streams.Where(s => s.Type is "attachment" or "data").ToList();

        /// <summary>PQ (HDR10, Dolby Vision) or HLG: needs 10 bits and its colour tags to look right.</summary>
        public bool IsHdr => Videos.FirstOrDefault() is { } video && video.Transfer is "smpte2084" or "arib-std-b67";
    }

    public static string Ffmpeg() => Engines.Require("ffmpeg");

    public static Probe ProbeFile(string path)
    {
        var ffprobe = Engines.Require("ffprobe", "Gyan.FFmpeg");
        var result = ProcessRunner.RunChecked(ffprobe, [
            "-v", "error", "-show_entries",
            "stream=index,codec_type,codec_name,width,height,channels,sample_fmt,bits_per_raw_sample,color_transfer"
                + ":stream_disposition=attached_pic:stream_side_data=rotation:stream_tags=rotate:format=duration",
            "-show_chapters", "-of", "json", "-protocol_whitelist", "file", path,
        ], L("Reading the file"));
        JsonDocument document;
        try { document = JsonDocument.Parse(result.StdoutData); }
        catch (JsonException)
        {
            throw new Problem(L("ffprobe could not describe “%@”.", Path.GetFileName(path)), L("The file may be damaged or not be audio or video."));
        }
        using (document)
        {
            var root = document.RootElement;
            var probe = new Probe();
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var info = new StreamInfo
                    {
                        Index = Int(stream, "index") ?? probe.Streams.Count,
                        Type = Text(stream, "codec_type") ?? "",
                        Codec = Text(stream, "codec_name") ?? "unknown",
                        IsCover = stream.TryGetProperty("disposition", out var disposition) && Int(disposition, "attached_pic") == 1,
                        Channels = Int(stream, "channels") ?? 0,
                        SampleFormat = Text(stream, "sample_fmt") ?? "",
                        Bits = int.TryParse(Text(stream, "bits_per_raw_sample"), out var bits) ? bits : 0,
                        Transfer = Text(stream, "color_transfer") ?? "",
                    };
                    probe.Streams.Add(info);
                    if (info.Type == "video" && !info.IsCover && probe.VideoEncoder is null)
                    {
                        probe.VideoEncoder = info.Codec;
                        probe.Width = Int(stream, "width") ?? 0;
                        probe.Height = Int(stream, "height") ?? 0;
                        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.EnumerateArray().Select(d => Int(d, "rotation")).FirstOrDefault(r => r is not null) is { } rotation)
                            probe.Rotation = rotation;
                        else if (stream.TryGetProperty("tags", out var tags) && int.TryParse(Text(tags, "rotate"), out var rotate))
                            probe.Rotation = rotate;
                    }
                    else if (info.Type == "audio" && probe.AudioEncoder is null) probe.AudioEncoder = info.Codec;
                }
            }
            if (root.TryGetProperty("format", out var format) && double.TryParse(Text(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                probe.Duration = duration;
            probe.Chapters = root.TryGetProperty("chapters", out var chapters) ? chapters.GetArrayLength() : 0;
            return probe;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (int)Math.Round(value.GetDouble()) : null;

    /// <summary>Checks the result against the source and says what didn't make it across.</summary>
    private static void Compare(Probe source, string output, Format target)
    {
        Probe result;
        try { result = ProbeFile(output); }
        catch (Problem problem) when (!problem.IsCancellation) { return; }
        if (Settings.KeepsMetadata && source.Chapters > 0 && result.Chapters == 0)
            Fidelity.Note(L("%@ doesn’t keep chapters, so the %@ chapter marks were left out.", target.Label(), source.Chapters.ToString()));
        if (target.Group() == FormatGroup.Video && result.Audios.Count < source.Audios.Count)
            Fidelity.Note(L("Only %@ of the %@ audio tracks fit in %@.", result.Audios.Count.ToString(), source.Audios.Count.ToString(), target.Label()));
    }

    private static IReadOnlyList<string> Transcode(string path, Format target)
    {
        var info = ProbeFile(path);
        var name = Path.GetFileName(path);
        if (target.Group() == FormatGroup.Audio && !info.ContainsAudio)
            throw new Problem(L("“%@” has no sound, so a %@ can’t be made from it.", name, target.Label()), L("The video was recorded or exported without an audio track."));
        if ((target.Group() == FormatGroup.Video || target == Format.Gif) && !info.HasVideo)
            throw new Problem(L("“%@” has no picture, so a %@ can’t be made from it.", name, target.Label()),
                              info.ContainsAudio ? L("It is audio only. To make a video from sound, use “Audio to Video” on the tools wheel.") : null);
        var output = Output.Url(path, target.Ext());
        string[] metadata = Settings.KeepsMetadata ? ["-map_metadata", "0", "-map_chapters", "0"] : ["-map_metadata", "-1", "-map_chapters", "-1"];
        var what = L("Converting to %@", target.Label());

        if (target.Group() == FormatGroup.Audio)
        {
            Encode(path, [.. AudioOnlyArguments(info, target), .. metadata], output, what);
            Compare(info, output, target);
            return [output];
        }
        if (target == Format.Gif)
        {
            if (info.ContainsAudio) Fidelity.Note(L("GIF has no sound, so the audio was left out. MP4 or WebM keep it."));
            Encode(path, VideoArguments(target, info), output, what);
            return [output];
        }
        // Copy what already fits the new container and re-encode only the rest, so the
        // picture isn't touched when only the audio needs changing.
        var plan = new StreamPlan(info, target);
        try
        {
            Encode(path, [.. plan.Arguments(plan.CanCopyVideo, plan.CanCopyAudio), .. metadata], output, what);
        }
        catch (Problem problem) when (!problem.IsCancellation && (plan.CanCopyVideo || plan.CanCopyAudio))
        {
            Encode(path, [.. plan.Arguments(false, false), .. metadata], output, what);
        }
        plan.ReportLosses();
        Compare(info, output, target);
        return [output];
    }

    /// <summary>Audio files keep the first track, its tags and chapters, the cover where the format has room for one, the bit depth of lossless sources and every channel the format can hold.</summary>
    public static List<string> AudioOnlyArguments(Probe info, Format target)
    {
        if (info.Audios.FirstOrDefault() is not { } first) return ["-vn", .. AudioArguments(target)];
        var args = new List<string> { "-map", $"0:{first.Index}" };
        if (info.Audios.Count > 1)
            Fidelity.Note(L("%@ holds one audio track, so only the first of %@ was kept.", target.Label(), info.Audios.Count.ToString()));
        Format[] coverTargets = [Format.Mp3, Format.M4a, Format.Flac, Format.Aiff];
        if (info.Covers.FirstOrDefault() is { } cover && Settings.KeepsMetadata)
        {
            if (coverTargets.Contains(target))
            {
                // MP4 covers must be JPEG or PNG; the others take the picture as it is.
                var codec = target == Format.M4a && cover.Codec is not ("mjpeg" or "png") ? "png" : "copy";
                args.AddRange(["-map", $"0:{cover.Index}", "-c:v", codec, "-disposition:v:0", "attached_pic"]);
            }
            else Fidelity.Note(L("%@ has no place for cover art, so the cover was left out. MP3, M4A, FLAC or AIFF keep it.", target.Label()));
        }
        else args.Add("-vn");
        if (target == Format.Aiff) args.AddRange(["-write_id3v2", "1"]);
        if (target is Format.Mp3 or Format.Wma && first.Channels > 2)
        {
            args.AddRange(["-ac", "2"]);
            Fidelity.Note(L("%@ holds at most two channels, so the surround sound was mixed down to stereo.", target.Label()));
        }
        // The built-in Vorbis encoder is stereo only; Opus in an Ogg file keeps every channel.
        if (target == Format.Ogg && first.Channels > 2) return [.. args, "-c:a", "libopus", "-b:a", Settings.Quality.AudioKbps()];
        return [.. args, .. AudioArguments(target, depthOf: first)];
    }

    /// <summary>Which streams go into a video container and which of them can be copied as they are.</summary>
    public sealed class StreamPlan(Probe info, Format target)
    {
        private static readonly HashSet<string> TextSubtitles = ["subrip", "ass", "ssa", "webvtt", "mov_text", "text"];

        public bool CanCopyVideo => info.Videos.FirstOrDefault()?.Codec is { } video && target switch
        {
            Format.Mp4 => video is "h264" or "hevc" or "av1" or "mpeg4",
            Format.Mov => video is "h264" or "hevc" or "prores" or "mpeg4" or "mjpeg",
            Format.Mkv => video != "gif",
            Format.Webm => video is "vp8" or "vp9" or "av1",
            _ => false,
        };

        public bool CanCopyAudio
        {
            get
            {
                var codecs = info.Audios.Select(a => a.Codec).ToList();
                return target switch
                {
                    Format.Mp4 or Format.Mov => codecs.All(c => c is "aac" or "mp3" or "alac" or "ac3" or "eac3"),
                    Format.Mkv => true,
                    Format.Webm => codecs.All(c => c is "opus" or "vorbis"),
                    _ => false,
                };
            }
        }

        /// <summary>Subtitle tracks the container can hold, and the codec to write them with.</summary>
        public (List<StreamInfo> Kept, string? Codec) Subtitles => target switch
        {
            Format.Mkv => (info.Subtitles, "copy"),
            Format.Mp4 or Format.Mov => (info.Subtitles.Where(s => TextSubtitles.Contains(s.Codec)).ToList(), "mov_text"),
            Format.Webm => (info.Subtitles.Where(s => TextSubtitles.Contains(s.Codec)).ToList(), "webvtt"),
            _ => ([], null),
        };

        public List<string> Arguments(bool copyVideo, bool copyAudio)
        {
            if (info.Videos.FirstOrDefault() is not { } video) return [];
            var args = new List<string> { "-map", $"0:{video.Index}" };
            foreach (var audio in info.Audios) args.AddRange(["-map", $"0:{audio.Index}"]);
            var (kept, codec) = Subtitles;
            foreach (var subtitle in kept) args.AddRange(["-map", $"0:{subtitle.Index}"]);
            if (target == Format.Mkv)
                foreach (var attachment in info.Attachments.Where(a => a.Type == "attachment")) args.AddRange(["-map", $"0:{attachment.Index}"]);

            if (copyVideo)
            {
                args.AddRange(["-c:v", "copy"]);
                if (video.Codec == "hevc" && target is Format.Mp4 or Format.Mov) args.AddRange(["-tag:v", "hvc1"]);
            }
            else args.AddRange(EncodeVideo(target, info));
            if (info.Audios.Count > 0) args.AddRange(copyAudio ? ["-c:a", "copy"] : AudioArgumentsForContainer(target));
            if (codec is not null && kept.Count > 0) args.AddRange(["-c:s", codec]);
            if (target == Format.Mkv && info.Attachments.Count > 0) args.AddRange(["-c:t", "copy"]);
            if (target is Format.Mp4 or Format.Mov) args.AddRange(["-movflags", "+faststart"]);
            return args;
        }

        public void ReportLosses()
        {
            var dropped = info.Subtitles.Count - Subtitles.Kept.Count;
            if (dropped > 0)
                Fidelity.Note(L("%@ of the subtitle tracks were left out: %@ can’t hold them. MKV keeps every track.", dropped.ToString(), target.Label()));
            if (target != Format.Mkv && info.Attachments.Any(a => a.Type == "attachment"))
                Fidelity.Note(L("Files attached to the video (such as subtitle fonts) were left out: only MKV holds them."));
            if (info.Videos.Count > 1) Fidelity.Note(L("Only the first of the %@ video tracks was kept.", info.Videos.Count.ToString()));
            if (info.IsHdr && !CanCopyVideo && target is Format.Avi or Format.Wmv)
                Fidelity.Note(L("The video is HDR and %@ only holds standard range, so the colors may look flatter.", target.Label()));
        }
    }

    private static IReadOnlyList<string> Subtitle(string path, Format target)
    {
        var output = Output.Url(path, target.Ext());
        if (target == Format.Srt && TryReadText(path) is { } text
            && (text.Contains("\nSTYLE") || Regex.IsMatch(text, @"-->[^\n]*\b(line|position|align|size|vertical):")))
            Fidelity.Note(L("SRT has no place for WebVTT positions and styles, so the subtitles show in the default place and style."));
        // ffmpeg reads subtitles as UTF-8, so files in an older encoding go through a UTF-8 copy.
        var utf8 = TextDecoding.Utf8(File.ReadAllBytes(path));
        if (utf8.WasUtf8)
        {
            Encode(path, [], output, L("Converting subtitles"));
            return [output];
        }
        var folder = Output.ScratchDirectory();
        try
        {
            var copy = Path.Combine(folder, Path.GetFileName(path));
            File.WriteAllBytes(copy, utf8.Data);
            Encode(copy, [], output, L("Converting subtitles"));
        }
        finally { Output.Discard(folder); }
        return [output];
    }

    private static string? TryReadText(string path)
    {
        try { return DocumentPipeline.ReadText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Runs ffmpeg into a hidden temp file and renames it when it succeeds. <paramref name="duration"/> is
    /// the length of the result in seconds (for the progress bar); probed when omitted.
    /// </summary>
    public static void Encode(string input, IEnumerable<string> arguments, string output, string what, IEnumerable<string>? inputArguments = null, double? duration = null)
    {
        var temp = Output.TempSibling(output);
        var progress = Path.Combine(Path.GetTempPath(), $"split-progress-{Guid.NewGuid()}.txt");
        try
        {
            double total;
            try { total = duration ?? ProbeFile(input).Duration; }
            catch (Problem problem) when (!problem.IsCancellation) { total = 0; }
            string[] fixedArguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", progress];
            var args = fixedArguments.Concat(inputArguments ?? []).Concat(LocalInput(input)).Concat(arguments).Append(temp).ToList();
            ProcessRunner.RunChecked(Ffmpeg(), args, what, monitor: () => ReportProgress(progress, total));
        }
        catch
        {
            Output.Discard(temp);
            throw;
        }
        finally { File.Delete(progress); }
        Output.Commit(temp, output);
    }

    /// <summary>ffmpeg's <c>-progress</c> file reports the encoded position as out_time_us.</summary>
    public static void ReportProgress(string file, double total)
    {
        if (total <= 0) return;
        string text;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException) { return; }
        var line = text.Split('\n').LastOrDefault(l => l.StartsWith("out_time_us=", StringComparison.Ordinal));
        if (line is not null && double.TryParse(line["out_time_us=".Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var micro))
            JobProgress.Report(micro / 1_000_000 / total);
    }

    /// <summary>Input restricted to local files: a crafted playlist inside a "video" can't make ffmpeg reach the network.</summary>
    public static string[] LocalInput(string path) => ["-protocol_whitelist", "file", "-i", path];

    /// <summary>One line of a concat list: forward slashes and escaped quotes, so any Windows path reads back as itself.</summary>
    public static string ConcatEntry(string path) => "file '" + path.Replace('\\', '/').Replace("'", "'\\''") + "'";

    // MARK: Argument builders

    /// <summary><paramref name="depthOf"/> keeps 24- and 32-bit lossless audio at its depth in WAV and AIFF.</summary>
    public static List<string> AudioArguments(Format target, string? bitrate = null, StreamInfo? depthOf = null)
    {
        bitrate ??= Settings.Quality.AudioKbps();
        string[] lossless = ["flac", "alac", "wavpack", "ape", "tta", "truehd", "mlp"];
        var isLossless = depthOf is not null && (lossless.Contains(depthOf.Codec) || depthOf.Codec.StartsWith("pcm_", StringComparison.Ordinal));
        var floating = isLossless && (depthOf!.SampleFormat.StartsWith("flt", StringComparison.Ordinal) || depthOf.SampleFormat.StartsWith("dbl", StringComparison.Ordinal));
        var depth = !isLossless ? 16 : floating ? 32 : depthOf!.Bits > 16 || depthOf.SampleFormat.StartsWith("s32", StringComparison.Ordinal) ? 24 : 16;
        return target switch
        {
            Format.Mp3 => ["-c:a", "libmp3lame", "-b:a", bitrate],
            Format.M4a => ["-c:a", "aac", "-b:a", bitrate],
            Format.Wav => ["-c:a", floating ? "pcm_f32le" : depth == 24 ? "pcm_s24le" : "pcm_s16le"],
            Format.Aiff => ["-c:a", floating ? "pcm_f32be" : depth == 24 ? "pcm_s24be" : "pcm_s16be"],
            Format.Flac => ["-c:a", "flac"],
            Format.Ogg => ["-c:a", "vorbis", "-strict", "experimental", "-ac", "2", "-b:a", bitrate],
            Format.Opus => ["-c:a", "libopus", "-b:a", bitrate],
            Format.Wma => ["-c:a", "wmav2", "-b:a", bitrate],
            _ => [],
        };
    }

    public const string EvenDimensions = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    public static List<string> H264Arguments(int? maxHeight = null, Quality? quality = null)
    {
        var filter = maxHeight is { } height ? $"scale=-2:'min({height},ih)'," + EvenDimensions : EvenDimensions;
        return ["-vf", filter, "-c:v", "libx264", "-preset", "veryfast", "-crf", (quality ?? Settings.Quality).X264Crf(), "-pix_fmt", "yuv420p"];
    }

    /// <summary>The picture re-encoded for a container (all mapping is done by <see cref="StreamPlan"/>).</summary>
    public static List<string> EncodeVideo(Format target, Probe info)
    {
        switch (target)
        {
            case Format.Mp4 or Format.Mov or Format.Mkv:
                if (info.IsHdr)
                {
                    // 10-bit HEVC with the source's colour tags, so HDR stays HDR.
                    return ["-vf", EvenDimensions, "-c:v", "libx265", "-preset", "fast", "-crf", Settings.Quality.X264Crf(), "-pix_fmt", "yuv420p10le",
                            "-tag:v", "hvc1", "-color_primaries", "bt2020", "-colorspace", "bt2020nc",
                            "-color_trc", info.Videos.FirstOrDefault()?.Transfer ?? "smpte2084", "-x265-params", "log-level=error"];
                }
                return ["-vf", EvenDimensions, "-c:v", "libx264", "-preset", "veryfast", "-crf", Settings.Quality.X264Crf(), "-pix_fmt", "yuv420p"];
            case Format.Webm:
                List<string> args = ["-vf", EvenDimensions, "-c:v", "libvpx-vp9", "-crf", Settings.Quality.Vp9Crf(), "-b:v", "0",
                                     "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"];
                if (info.IsHdr)
                    args.AddRange(["-pix_fmt", "yuv420p10le", "-color_primaries", "bt2020", "-colorspace", "bt2020nc", "-color_trc", info.Videos.FirstOrDefault()?.Transfer ?? "smpte2084"]);
                else
                    args.AddRange(["-pix_fmt", info.VideoEncoder == "gif" ? "yuva420p" : "yuv420p"]);
                return args;
            case Format.Avi:
                return ["-vf", EvenDimensions, "-c:v", "mpeg4", "-q:v", "3"];
            case Format.Wmv:
                return ["-vf", EvenDimensions, "-c:v", "wmv2", "-q:v", "3"];
            default:
                return [];
        }
    }

    public static List<string> VideoArguments(Format target, Probe info) => target == Format.Gif
        ? ["-an", "-vf", "fps=15,scale='min(720,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-loop", "0"]
        : new StreamPlan(info, target).Arguments(false, false);

    /// <summary>Audio settings that keep the container valid when only the audio is re-encoded.</summary>
    public static List<string> AudioArgumentsForContainer(Format format) => format switch
    {
        Format.Webm => ["-c:a", "libopus", "-b:a", "128k"],
        Format.Avi => ["-c:a", "libmp3lame", "-b:a", Settings.Quality.AudioKbps()],
        Format.Wmv => ["-c:a", "wmav2", "-b:a", Settings.Quality.AudioKbps()],
        Format.Mp4 or Format.Mov or Format.M4v or Format.Mkv => ["-c:a", "aac", "-b:a", Settings.Quality.AudioKbps()],
        _ => AudioArguments(format),
    };
}
