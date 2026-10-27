using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Split;

public enum MaskKind { Blur, Black }

/// <summary>An area of the picture covered for the whole clip.</summary>
public sealed record MaskRegion(NBox Rect, MaskKind Kind)
{
    public Guid Id { get; } = Guid.NewGuid();
}

public enum MediaOutput { Same, Mp4, Gif, M4a, Mp3, Wav }

/// <summary>Every edit, independent of the window, so the ffmpeg command can be built and tested on its own.</summary>
public sealed class MediaEdit
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Duration { get; set; }
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Mute { get; set; }
    public bool FadeIn { get; set; }
    public bool FadeOut { get; set; }
    public int Turns { get; set; }
    public bool FlipH { get; set; }

    /// <summary>In the rotated frame, 0…1 from its top-left corner.</summary>
    public NBox? Crop { get; set; }

    public List<MaskRegion> Masks { get; set; } = [];
    public MediaOutput Output { get; set; } = MediaOutput.Same;
    public bool FastTrim { get; set; }

    public bool Trimmed => Start > 0.01 || (Duration > 0 && End < Duration - 0.01);
    public double Length => Math.Max(0.05, End - Start);
    public double ResultDuration => Length / Speed;
    public bool HasVideoChanges => Turns % 4 != 0 || FlipH || Crop is not null || Masks.Count > 0 || Speed != 1;
    public bool HasAudioChanges => Speed != 1 || Volume != 1 || Mute || FadeIn || FadeOut;
    public bool IsIdentity => !Trimmed && !HasVideoChanges && !HasAudioChanges && Output == MediaOutput.Same;

    /// <summary>Stream copy is only possible when nothing but the cut changes.</summary>
    public bool CanCopy => Trimmed && !HasVideoChanges && !HasAudioChanges && Output == MediaOutput.Same;

    public MediaEdit Clone()
    {
        var copy = (MediaEdit)MemberwiseClone();
        copy.Masks = [.. Masks];
        return copy;
    }
}

public static class MediaEditRenderer
{
    private static string F(double value, int decimals) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);

    /// <summary>Output format: the source's own when it can be re-encoded, else MP4 / M4A.</summary>
    public static Format OutputFormat(Format source, MediaEdit edit, bool isVideo) => edit.Output switch
    {
        MediaOutput.Mp4 => Format.Mp4,
        MediaOutput.Gif => Format.Gif,
        MediaOutput.M4a => Format.M4a,
        MediaOutput.Mp3 => Format.Mp3,
        MediaOutput.Wav => Format.Wav,
        _ => isVideo ? (new[] { Format.Mp4, Format.Mov, Format.M4v, Format.Mkv, Format.Webm }.Contains(source) ? source : Format.Mp4) : source,
    };

    public static List<string> Atempo(double speed)
    {
        var remaining = speed;
        var filters = new List<string>();
        while (remaining > 2.0) { filters.Add("atempo=2.0"); remaining /= 2; }
        while (remaining < 0.5) { filters.Add("atempo=0.5"); remaining /= 0.5; }
        if (Math.Abs(remaining - 1) > 0.0001) filters.Add("atempo=" + F(remaining, 4));
        return filters;
    }

    /// <summary>Even sizes (yuv420p needs them), at least 2 px.</summary>
    public static int Even(double value) => Math.Max(2, (int)(value / 2) * 2);

    /// <summary>Even positions; unlike sizes these may be 0.</summary>
    public static int EvenOffset(double value) => Math.Max(0, (int)(value / 2) * 2);

    /// <summary>ffmpeg arguments (input options, output options) for an edit.</summary>
    public static (List<string> Input, List<string> Output) Arguments(MediaEdit edit, MediaConverter.Probe probe, Format target)
    {
        var input = new List<string>();
        if (edit.Trimmed) input.AddRange(["-ss", F(edit.Start, 3), "-t", F(edit.Length, 3)]);
        if (edit.CanCopy && edit.FastTrim) return (input, ["-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero"]);
        var output = new List<string>();
        var isVideoOutput = probe.HasVideo && target.Group() != FormatGroup.Audio;

        if (isVideoOutput)
        {
            var (displayWidth, displayHeight) = probe.DisplaySize;
            var (width, height) = edit.Turns % 2 == 1 ? ((double)displayHeight, (double)displayWidth) : (displayWidth, displayHeight);
            var graph = new List<string>();
            var chain = new List<string>();
            switch (((edit.Turns % 4) + 4) % 4)
            {
                case 1: chain.Add("transpose=1"); break;
                case 2: chain.AddRange(["hflip", "vflip"]); break;
                case 3: chain.Add("transpose=2"); break;
            }
            if (edit.FlipH) chain.Add("hflip");
            var label = "0:v";
            if (chain.Count > 0)
            {
                graph.Add("[0:v]" + string.Join(",", chain) + "[rot]");
                label = "rot";
            }
            for (var index = 0; index < edit.Masks.Count; index++)
            {
                var mask = edit.Masks[index];
                var x = EvenOffset(mask.Rect.MinX * width);
                var y = EvenOffset(mask.Rect.MinY * height);
                var w = Math.Min(Even(mask.Rect.Width * width), Even(width) - x);
                var h = Math.Min(Even(mask.Rect.Height * height), Even(height) - y);
                if (w < 2 || h < 2) continue;
                if (mask.Kind == MaskKind.Black)
                {
                    graph.Add($"[{label}]drawbox=x={x}:y={y}:w={w}:h={h}:color=black:t=fill[r{index}]");
                }
                else
                {
                    var sigma = Math.Max(8, Math.Min(w, h) / 5);
                    graph.Add($"[{label}]split=2[a{index}][b{index}];[b{index}]crop={w}:{h}:{x}:{y},gblur=sigma={sigma}:steps=3[c{index}];"
                              + $"[a{index}][c{index}]overlay={x}:{y}[r{index}]");
                }
                label = $"r{index}";
            }
            var tail = new List<string>();
            if (edit.Crop is { } crop)
            {
                var x = EvenOffset(crop.MinX * width);
                var y = EvenOffset(crop.MinY * height);
                var w = Math.Min(Even(crop.Width * width), Even(width) - x);
                var h = Math.Min(Even(crop.Height * height), Even(height) - y);
                tail.Add($"crop={w}:{h}:{x}:{y}");
            }
            if (edit.Speed != 1) tail.Add("setpts=PTS/" + F(edit.Speed, 4));
            tail.Add(target == Format.Gif
                ? "fps=15,scale='min(720,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse"
                : MediaConverter.EvenDimensions);
            graph.Add($"[{label}]" + string.Join(",", tail) + "[vout]");
            output.AddRange(["-filter_complex", string.Join(";", graph), "-map", "[vout]"]);
            if (target == Format.Gif)
            {
                output.AddRange(["-an", "-loop", "0"]);
                return (input, output);
            }
            if (!edit.Mute && probe.ContainsAudio) output.AddRange(["-map", "0:a:0?"]);
        }
        else
        {
            output.AddRange(["-vn", "-map", "0:a:0"]);
        }

        var wantsAudio = probe.ContainsAudio && !edit.Mute;
        if (wantsAudio)
        {
            var audio = Atempo(edit.Speed);
            if (edit.Volume != 1) audio.Add("volume=" + F(edit.Volume, 3));
            var duration = edit.ResultDuration;
            var fade = Math.Min(1.5, duration / 3);
            if (edit.FadeIn) audio.Add("afade=t=in:st=0:d=" + F(fade, 2));
            if (edit.FadeOut) audio.Add($"afade=t=out:st={F(Math.Max(0, duration - fade), 3)}:d={F(fade, 2)}");
            if (audio.Count > 0) output.AddRange(["-af", string.Join(",", audio)]);
        }
        else if (isVideoOutput)
        {
            output.Add("-an");
        }

        if (isVideoOutput)
        {
            if (target == Format.Webm)
            {
                output.AddRange(["-c:v", "libvpx-vp9", "-crf", Settings.Quality.Vp9Crf(), "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p"]);
                if (wantsAudio) output.AddRange(["-c:a", "libopus", "-b:a", "128k"]);
            }
            else
            {
                output.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", Settings.Quality.X264Crf(), "-pix_fmt", "yuv420p"]);
                if (wantsAudio) output.AddRange(["-c:a", "aac", "-b:a", Settings.Quality.AudioKbps()]);
                if (target is Format.Mp4 or Format.Mov or Format.M4v) output.AddRange(["-movflags", "+faststart"]);
            }
        }
        else
        {
            output.AddRange(MediaConverter.AudioArguments(target));
        }
        output.AddRange(["-map_metadata", Settings.KeepsMetadata ? "0" : "-1"]);
        return (input, output);
    }

    public static string Export(string path, MediaEdit edit)
    {
        var probe = MediaConverter.ProbeFile(path);
        var source = Formats.Detect(path) ?? Format.Mp4;
        var target = OutputFormat(source, edit, probe.HasVideo);
        if (edit.Mute && !probe.HasVideo)
            throw new Problem(L("Removing the sound from an audio file would leave an empty file."), L("Turn off “Mute”."));
        var output = Output.Url(path, target.Ext(), " (" + L("edited") + ")");
        var (input, arguments) = Arguments(edit, probe, target);
        MediaConverter.Encode(path, arguments, output, L("Exporting"), input, edit.ResultDuration);
        return output;
    }

    /// <summary>One frame at full resolution, exactly at <paramref name="time"/>.</summary>
    public static string SaveFrame(string path, double time)
    {
        var seconds = time.ToString("F1", Localizer.Shared.Culture);
        var output = Output.Url(path, "png", " (" + L("frame at %@ s", seconds) + ")");
        MediaConverter.Encode(path, ["-frames:v", "1", "-update", "1"], output, L("Capturing the frame"), ["-ss", F(time, 3)], 1);
        return output;
    }

    /// <summary>Peak levels for drawing an audio waveform (0…1).</summary>
    public static float[] Waveform(string path, int bins = 320)
    {
        var result = ProcessRunner.RunChecked(MediaConverter.Ffmpeg(),
            ["-hide_banner", "-loglevel", "error", "-nostdin", .. MediaConverter.LocalInput(path), "-vn", "-ac", "1", "-ar", "800", "-f", "f32le", "-"], L("Reading the sound"));
        var samples = MemoryMarshal.Cast<byte, float>(result.StdoutData.AsSpan(0, result.StdoutData.Length / 4 * 4)).ToArray();
        if (samples.Length == 0) return [];
        var per = Math.Max(1, samples.Length / bins);
        var peaks = new List<float>();
        for (var index = 0; index < samples.Length; index += per)
        {
            var peak = 0f;
            for (var i = index; i < Math.Min(index + per, samples.Length); i++) peak = Math.Max(peak, Math.Abs(samples[i]));
            peaks.Add(peak);
        }
        var top = Math.Max(peaks.Max(), 0.0001f);
        return peaks.Select(p => p / top).ToArray();
    }

    /// <summary>Frames for the filmstrip: one at the middle of each of <paramref name="count"/> equal parts, at most 240 px.</summary>
    public static List<SKBitmap> Filmstrip(string path, double duration, int count = 12)
    {
        var frames = new List<SKBitmap>();
        for (var i = 0; i < count; i++)
        {
            JobProgress.CheckCancelled();
            var time = duration * (i + 0.5) / count;
            var result = ProcessRunner.Run(MediaConverter.Ffmpeg(),
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", F(time, 3), .. MediaConverter.LocalInput(path), "-frames:v", "1",
                 "-vf", "scale=240:240:force_original_aspect_ratio=decrease", "-f", "image2pipe", "-c:v", "png", "-"]);
            if (result.Status != 0 || result.StdoutData.Length == 0) continue;
            if (SKBitmap.Decode(result.StdoutData) is { } frame) frames.Add(frame);
        }
        return frames;
    }
}


/// <summary>The state of the Edit Media editor: the edit, what was read from the file, and the pieces the timeline draws.</summary>
public sealed class MediaEditorModel : IDisposable
{
    public enum EditMode { View, Crop, Redact }

    /// <summary>Formats the player takes as they are; anything else, or a file it refuses, gets a light preview copy.</summary>
    private static readonly Format[] Native = [Format.Mp4, Format.M4v, Format.M4a, Format.Mp3, Format.Wav];

    private readonly CancellationTokenSource cancel = new();
    private string? scratch;

    public MediaEditorModel(string path)
    {
        Path = path;
        Loaded = Task.Run(Load);
    }

    public string Path { get; }
    public MediaEdit Edit { get; private set; } = new();
    public MediaConverter.Probe Probe { get; private set; } = new();

    /// <summary>What the window shows instead of the stage until the file is ready.</summary>
    public string? Loading { get; private set; } = L("Opening…");

    public double? LoadingFraction { get; private set; }
    public Problem? Failure { get; private set; }

    /// <summary>The file the player plays: the original, or a preview copy.</summary>
    public string? PlaybackSource { get; private set; }

    public bool IsProxy { get; private set; }
    public IReadOnlyList<SKBitmap> Thumbnails { get; private set; } = [];
    public float[] Waveform { get; private set; } = [];
    public EditMode Mode { get; set; } = EditMode.View;
    public MaskKind MaskKind { get; set; } = MaskKind.Blur;
    public Guid? SelectedRedaction { get; set; }
    public double Current { get; set; }
    public bool IsPlaying { get; set; }

    /// <summary>Ends when the file was read and the first thing to play is set (the thumbnails and the waveform follow).</summary>
    public Task Loaded { get; }

    /// <summary>The filmstrip and the waveform are done.</summary>
    public Task Details { get; private set; } = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>Raised from the thread that changed it.</summary>
    public event Action? Changed;

    public bool IsVideo => Probe.HasVideo;
    public bool Ready => PlaybackSource is not null && Failure is null;

    /// <summary>The picture's size as shown, after the turns.</summary>
    public (double Width, double Height) DisplaySize
    {
        get
        {
            var (w, h) = Probe.DisplaySize;
            return Edit.Turns % 2 == 1 ? (h, w) : (w, h);
        }
    }

    private void Load()
    {
        try
        {
            FileCheck.Preflight(Path);
            var probe = MediaConverter.ProbeFile(Path);
            if (!probe.ContainsAudio && !probe.HasVideo)
                throw new Problem(L("“%@” has no sound or picture that can be edited.", System.IO.Path.GetFileName(Path)));
            Probe = probe;
            Edit.Duration = probe.Duration;
            Edit.End = probe.Duration;
            if (Native.Contains(Formats.Detect(Path) ?? Format.Mp4))
            {
                PlaybackSource = Path;
                Loading = null;
            }
            else
            {
                MakePreview();
            }
            Changed?.Invoke();
            Details = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!probe.HasVideo) Waveform = MediaEditRenderer.Waveform(Path);
                    else Thumbnails = MediaEditRenderer.Filmstrip(Path, probe.Duration);
                }
                catch (Exception e) when (e is Problem or IOException) { }
                Changed?.Invoke();
            }, cancel.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failure = Explain.Error(e, Path);
            Loading = null;
            Changed?.Invoke();
        }
    }

    /// <summary>A light H.264/AAC copy for the player, for formats it can't play itself.</summary>
    public void MakePreview()
    {
        if (IsProxy) return;
        Loading = L("Preparing the preview…");
        Changed?.Invoke();
        try
        {
            scratch = Output.ScratchDirectory();
            var proxy = System.IO.Path.Combine(scratch, Probe.HasVideo ? "preview.mp4" : "preview.m4a");
            string[] arguments = Probe.HasVideo
                ? ["-map", "0:v:0", "-map", "0:a:0?", "-vf", "scale=-2:'min(720,ih)'," + MediaConverter.EvenDimensions,
                   "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k"]
                : ["-vn", "-c:a", "aac", "-b:a", "160k"];
            JobProgress.Run(fraction => { LoadingFraction = fraction; Changed?.Invoke(); }, null,
                            () => { MediaConverter.Encode(Path, arguments, proxy, L("Preparing the preview"), duration: Probe.Duration); return 0; });
            IsProxy = true;
            PlaybackSource = proxy;
            Loading = null;
            LoadingFraction = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failure = Explain.Error(e, Path);
            Loading = null;
        }
        Changed?.Invoke();
    }

    /// <summary>The player refused the file as it is: use a preview copy, or give up if that is what it refused.</summary>
    public void PlayerFailed()
    {
        if (IsProxy)
        {
            Failure = new Problem(L("“%@” has no sound or picture that can be edited.", System.IO.Path.GetFileName(Path)));
            Changed?.Invoke();
            return;
        }
        PlaybackSource = null;
        Task.Run(MakePreview);
    }

    public void SetStart() => Edit.Start = Math.Min(Current, Edit.End - 0.1);

    public void SetEnd() => Edit.End = Math.Max(Current, Edit.Start + 0.1);

    /// <summary>Back to the untouched edit, keeping what the file is.</summary>
    public void Reset()
    {
        var duration = Edit.Duration;
        Edit = new MediaEdit { Duration = duration, End = duration };
    }

    public void Save()
    {
        var edit = Edit.Clone();
        JobRunner.Shared.Single(L("Exporting…"), System.IO.Path.GetFileName(Path), [Path], () => [MediaEditRenderer.Export(Path, edit)]);
    }

    public void SaveFrame()
    {
        var time = Current;
        JobRunner.Shared.Single(L("Saving the frame…"), System.IO.Path.GetFileName(Path), [Path], () => [MediaEditRenderer.SaveFrame(Path, time)]);
    }

    public void Dispose()
    {
        cancel.Cancel();
        try { Loaded.Wait(); Details.Wait(); } catch (AggregateException) { }
        if (scratch is not null) Output.Discard(scratch);
        foreach (var frame in Thumbnails) frame.Dispose();
        Thumbnails = [];
    }
}
