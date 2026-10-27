using System.IO;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>Edit Media: the ffmpeg commands an edit makes, what comes out of them, and the editor's model.</summary>
public static class MediaEditTests
{
    private static string[] Args(MediaEdit edit, MediaConverter.Probe probe, Format target, out string[] input)
    {
        var (i, o) = MediaEditRenderer.Arguments(edit, probe, target);
        input = [.. i];
        return [.. o];
    }

    public static void Register()
    {
        Test("Video and audio", "Edits: the trim, what changes the picture and what changes the sound", () =>
        {
            var edit = new MediaEdit { Duration = 10, End = 10 };
            Check(edit.IsIdentity && !edit.Trimmed && edit.Length == 10 && edit.ResultDuration == 10, "untouched");
            edit.Start = 2;
            edit.End = 5;
            Check(edit.Trimmed && edit.Length == 3 && edit.CanCopy && !edit.IsIdentity, "a cut alone can be copied");
            edit.Speed = 2;
            Check(edit.ResultDuration == 1.5 && edit.HasVideoChanges && edit.HasAudioChanges && !edit.CanCopy, "speed changes both and needs a re-encode");
            edit.Speed = 1;
            edit.Volume = 0.5;
            Check(edit.HasAudioChanges && !edit.HasVideoChanges && !edit.CanCopy, "volume is the sound only");
            edit.Volume = 1;
            edit.Turns = 4;
            Check(!edit.HasVideoChanges, "four quarter turns are no turn");
            edit.Turns = 1;
            Check(edit.HasVideoChanges, "one is");
            edit.Turns = 0;
            edit.Output = MediaOutput.Mp3;
            Check(!edit.CanCopy && !edit.IsIdentity, "another format is a change");
            Check(new MediaEdit { Start = 0.005, Duration = 4, End = 4 }.Trimmed == false && new MediaEdit { Duration = 4, End = 3.5 }.Trimmed, "the trim has a margin of a hundredth of a second");
            Check(new MediaEdit { Start = 1, End = 1 }.Length == 0.05, "never shorter than 0.05 s");
        });

        Test("Video and audio", "Output formats: the source's own when it can be written, MP4 or M4A otherwise", () =>
        {
            Check(MediaEditRenderer.OutputFormat(Format.Webm, new MediaEdit(), true) == Format.Webm, "WebM stays WebM");
            Check(MediaEditRenderer.OutputFormat(Format.Avi, new MediaEdit(), true) == Format.Mp4, "AVI becomes MP4");
            Check(MediaEditRenderer.OutputFormat(Format.Flac, new MediaEdit(), false) == Format.Flac, "a sound keeps its format");
            Check(MediaEditRenderer.OutputFormat(Format.Mkv, new MediaEdit { Output = MediaOutput.Gif }, true) == Format.Gif, "a chosen format wins");
        });

        Test("Video and audio", "ffmpeg commands: atempo, crops with even values, quick trim", () =>
        {
            // ffmpeg's atempo accepts 0.5…2 per stage; faster or slower speeds are chained.
            foreach (var speed in new[] { 4, 0.25, 3, 0.3, 1.5 })
            {
                var factors = MediaEditRenderer.Atempo(speed).Select(f => double.Parse(f["atempo=".Length..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
                Check(factors.All(f => f is >= 0.5 and <= 2), $"{speed}×: a factor outside 0.5…2 in {string.Join(",", factors)}");
                Check(Near(factors.Aggregate(1.0, (a, b) => a * b), speed, 0.001), $"{speed}×: the product is {factors.Aggregate(1.0, (a, b) => a * b)}");
            }
            Check(MediaEditRenderer.EvenOffset(0) == 0 && MediaEditRenderer.Even(0) == 2 && MediaEditRenderer.Even(241) == 240, "even values");
            Check(MediaEditRenderer.Atempo(1).Count == 0, "1× needs no filter");
            var probe = new MediaConverter.Probe { VideoEncoder = "h264", AudioEncoder = "aac", Width = 321, Height = 241, Duration = 10 };
            var edit = new MediaEdit { Duration = 10, Start = 2, End = 5, Crop = new NBox(0.1, 0.1, 0.5, 0.5) };
            var arguments = Args(edit, probe, Format.Mp4, out var input);
            Check(input.SequenceEqual(["-ss", "2.000", "-t", "3.000"]), $"trim: {string.Join(" ", input)}");
            var graph = arguments[Array.IndexOf(arguments, "-filter_complex") + 1];
            Check(graph.Contains("crop=160:120:32:24"), $"crop with even values: {graph}");
            edit.Crop = null;
            edit.FastTrim = true;
            Check(Args(edit, probe, Format.Mp4, out _).Contains("copy"), "a quick trim copies the streams");
        });

        Test("Video and audio", "ffmpeg commands: order of rotation, flip, covers, crop, speed; fades, volume and the audio of each target", () =>
        {
            var probe = new MediaConverter.Probe { VideoEncoder = "h264", AudioEncoder = "aac", Width = 320, Height = 240, Duration = 10 };
            var edit = new MediaEdit
            {
                Duration = 10, End = 10, Turns = 1, FlipH = true, Speed = 2, Volume = 0.5, FadeIn = true, FadeOut = true,
                Masks = [new MaskRegion(new NBox(0, 0, 0.5, 0.5), MaskKind.Black), new MaskRegion(new NBox(0.5, 0.5, 0.5, 0.5), MaskKind.Blur)],
            };
            var arguments = Args(edit, probe, Format.Mp4, out var input);
            Check(input.Length == 0, "no trim, no input options");
            var graph = arguments[Array.IndexOf(arguments, "-filter_complex") + 1];
            // Turned 90° right it is 240×320: the covers are in those coordinates.
            Check(graph.StartsWith("[0:v]transpose=1,hflip[rot];"), $"turn, then flip: {graph}");
            Check(graph.Contains("[rot]drawbox=x=0:y=0:w=120:h=160:color=black:t=fill[r0]"), $"a black cover: {graph}");
            Check(graph.Contains("[r0]split=2[a1][b1];[b1]crop=120:160:120:160,gblur=sigma=24:steps=3[c1];[a1][c1]overlay=120:160[r1]"), $"a blurred cover: {graph}");
            Check(graph.Contains("[r1]setpts=PTS/2.0000,scale=trunc(iw/2)*2:trunc(ih/2)*2[vout]"), $"speed and even sizes last: {graph}");
            var audio = arguments[Array.IndexOf(arguments, "-af") + 1];
            Check(audio == "atempo=2.0000,volume=0.500,afade=t=in:st=0:d=1.50,afade=t=out:st=3.500:d=1.50", $"audio filters: {audio}");
            Check(arguments.Contains("libx264") && arguments.Contains("+faststart") && arguments.Contains("aac"), "H.264, AAC and a fast-start MP4");
            Check(Args(edit, probe, Format.Webm, out _) is var web && web.Contains("libvpx-vp9") && web.Contains("libopus") && !web.Contains("+faststart"), "WebM takes VP9 and Opus");
            var gif = Args(edit, probe, Format.Gif, out _);
            Check(gif.Contains("-an") && gif.Contains("-loop") && gif[Array.IndexOf(gif, "-filter_complex") + 1].Contains("palettegen"), "a GIF has no sound and a palette");
            edit.Mute = true;
            var muted = Args(edit, probe, Format.Mp4, out _);
            Check(muted.Contains("-an") && !muted.Contains("-af") && !muted.Contains("aac"), "muted: no audio at all");
            edit = new MediaEdit { Duration = 10, End = 10, Output = MediaOutput.M4a };
            var sound = Args(edit, probe, Format.M4a, out _);
            Check(sound.Contains("-vn") && sound.Contains("0:a:0") && !sound.Contains("-filter_complex"), "audio only drops the picture");
            var tiny = new MediaEdit { Duration = 1, End = 1, FadeIn = true, FadeOut = true };
            var fades = Args(tiny, probe, Format.Mp4, out _);
            Check(fades[Array.IndexOf(fades, "-af") + 1] == "afade=t=in:st=0:d=0.33,afade=t=out:st=0.667:d=0.33", $"a short clip fades for a third of its length: {fades[Array.IndexOf(fades, "-af") + 1]}");
        });

        Test("Video and audio", "Export: trim, speed, crop, rotate, cover, GIF, audio only", () =>
        {
            var clip = Fixtures.Isolated("clip.mp4", "media-edit");
            var edit = new MediaEdit { Duration = 2, Start = 0.5, End = 1.5 };
            Check(Near(Probe.Duration(MediaEditRenderer.Export(clip, edit)), 1, 0.15), "a 1 s trim");
            edit = new MediaEdit { Duration = 2, End = 2, Speed = 2 };
            var fast = MediaEditRenderer.Export(clip, edit);
            Check(Near(Probe.Duration(fast), 1, 0.15), $"2× → {Probe.Duration(fast)} s");
            Check(Probe.Codecs(fast, "audio").Count > 0, "speed keeps the sound");
            edit.Speed = 1;
            edit.Crop = new NBox(0, 0, 0.5, 0.5);
            Check(Probe.VideoSize(MediaEditRenderer.Export(clip, edit)) == (160, 120), "crop 160×120");
            edit.Crop = null;
            edit.Turns = 1;
            Check(Probe.VideoSize(MediaEditRenderer.Export(clip, edit)) == (240, 320), "rotated 240×320");
            edit.Turns = 0;
            edit.Masks = [new MaskRegion(new NBox(0, 0, 0.5, 0.5), MaskKind.Black), new MaskRegion(new NBox(0.5, 0.5, 0.5, 0.5), MaskKind.Blur)];
            var redacted = MediaEditRenderer.Export(clip, edit);
            var frame = MediaEditRenderer.SaveFrame(redacted, 1);
            using var image = Pixels.Load(frame) ?? throw new TestFailure("the saved frame can't be read");
            Check(Pixels.Close(Pixels.At(image, 40, 30), [0, 0, 0], 20), $"the covered area should be black: {string.Join(",", Pixels.At(image, 40, 30))}");
            edit.Masks = [];
            edit.Output = MediaOutput.Gif;
            Check((Pixels.Info(MediaEditRenderer.Export(clip, edit))?.Frames ?? 0) > 5, "animated GIF");
            edit.Output = MediaOutput.M4a;
            var audioOnly = MediaEditRenderer.Export(clip, edit);
            Check(Probe.Codecs(audioOnly, "video").Count == 0 && Probe.Codecs(audioOnly, "audio").SequenceEqual(["aac"]), "audio only, as M4A");

            var wav = Fixtures.Isolated("tone.wav", "audio-edit");
            var sound = new MediaEdit { Duration = 3, Start = 0.5, End = 2.5, Volume = 0.5, FadeIn = true, FadeOut = true };
            Check(Near(Probe.Duration(MediaEditRenderer.Export(wav, sound)), 2, 0.1), "trimmed audio with fades");
            sound.Mute = true;
            ExpectError("muting an audio file", ["empty"], () => MediaEditRenderer.Export(wav, sound));
            var peaks = MediaEditRenderer.Waveform(wav);
            Check(peaks.Length > 100 && peaks.Max() == 1, $"waveform: {peaks.Length} points");
        });

        Test("Video and audio", "Quick trim copies the streams, and the result is where the cut says", () =>
        {
            var clip = Fixtures.Isolated("clip.mp4", "media-fast");
            var edit = new MediaEdit { Duration = 2, Start = 0.5, End = 1.5, FastTrim = true };
            var output = MediaEditRenderer.Export(clip, edit);
            Check(Near(Probe.Duration(output), 1, 0.5) && Probe.Codecs(output, "video").SequenceEqual(["h264"]), "a stream copy keeps the codecs");
            Check(Path.GetFileName(output) == "clip (edited).mp4", $"the name: {Path.GetFileName(output)}");
        });

        Test("Video and audio", "Phone video (rotated): crops in the coordinates you see", () =>
        {
            var portrait = Fixtures.Isolated("portrait.mp4", "media-portrait");
            var probe = MediaConverter.ProbeFile(portrait);
            Check(probe.DisplaySize.Height > probe.DisplaySize.Width, $"the displayed size should be portrait: {probe.DisplaySize}");
            var edit = new MediaEdit { Duration = probe.Duration, End = probe.Duration, Crop = new NBox(0, 0, 1, 0.5) };
            var size = Probe.VideoSize(MediaEditRenderer.Export(portrait, edit));
            Check(size == (240, 160), $"top half of 240×320 → {size}");
        });

        Test("Edit Media", "The model reads a video: the player gets the file, the filmstrip has 12 frames", () =>
        {
            using var model = new MediaEditorModel(Fixtures.Url("clip.mp4"));
            model.Loaded.Wait();
            model.Details.Wait();
            Check(model.Failure is null && model.Ready && model.IsVideo && !model.IsProxy, $"ready: {model.Failure?.FullText}");
            Check(model.PlaybackSource == Fixtures.Url("clip.mp4"), "an MP4 plays as it is");
            Check(Near(model.Edit.Duration, 2, 0.1) && model.Edit.End == model.Edit.Duration && model.Edit.IsIdentity, "the whole clip is selected, nothing is changed");
            Check(model.Thumbnails.Count == 12 && model.Thumbnails.All(f => f.Width <= 240 && f.Height <= 240 && f.Width > 0), $"{model.Thumbnails.Count} frames");
            Check(model.Waveform.Length == 0, "a video shows its frames, not a waveform");
            model.Current = 1;
            model.Edit.Start = 0.2;
            model.SetStart();
            Check(model.Edit.Start == 1, "Start Here uses the position");
            model.Current = 0.5;
            model.SetStart();
            Check(model.Edit.Start == 0.5, "and can move back");
            model.Current = 0;
            model.SetEnd();
            Check(Near(model.Edit.End, model.Edit.Start + 0.1, 1e-9), "the end never goes before the start");
            model.Edit.Turns = 1;
            Check(model.DisplaySize == (240, 320), "turned, the picture is 240×320");
            model.Reset();
            Check(model.Edit.IsIdentity && model.Edit.End == model.Edit.Duration, "reset keeps the length of the file");
        });

        Test("Edit Media", "A format the player may not know gets a preview copy, and it is removed afterwards", () =>
        {
            string? copy;
            using (var model = new MediaEditorModel(Fixtures.Url("clip.mkv")))
            {
                model.Loaded.Wait();
                Check(model.Failure is null && model.IsProxy && model.PlaybackSource != Fixtures.Url("clip.mkv"), $"a preview copy: {model.PlaybackSource}");
                copy = model.PlaybackSource;
                Require(File.Exists(copy), "the copy exists while the editor is open");
                var size = Probe.VideoSize(copy!);
                Check(size.Height <= 720, $"the copy is light: {size}");
                // The player refusing even the copy ends in an explanation.
                model.PlayerFailed();
                Check(model.Failure is not null && model.Failure.Message.Contains("no sound or picture"), "an explanation, not an empty window");
            }
            Check(!File.Exists(copy), "the copy goes when the editor closes");
        });

        Test("Edit Media", "The model reads a sound: a waveform for the stage, and no picture tools", () =>
        {
            using var model = new MediaEditorModel(Fixtures.Url("tone.wav"));
            model.Loaded.Wait();
            model.Details.Wait();
            Check(model.Failure is null && model.Ready && !model.IsVideo && model.Probe.ContainsAudio, "ready");
            Check(model.Waveform.Length > 100 && model.Waveform.Max() == 1 && model.Thumbnails.Count == 0, $"waveform {model.Waveform.Length}");
            Check(Near(model.Edit.Duration, 3, 0.1), $"3 s: {model.Edit.Duration}");
        });

        Test("Edit Media", "A file that can't be edited is explained", () =>
        {
            using var model = new MediaEditorModel(CollageTests.Garbage("fake.png"));
            model.Loaded.Wait();
            Check(model.Failure is not null && !model.Ready && model.Loading is null, "a failure is shown, not a spinner");
            Check(model.Failure!.FullText.Length > 25, $"explained: {model.Failure.FullText}");
            using var broken = new MediaEditorModel(Fixtures.Url("broken.mp4"));
            broken.Loaded.Wait();
            broken.Details.Wait();
            Check(broken.Failure is not null || broken.Ready, "a cut-short file either opens or is explained");
        });

        Test("Edit Media", "Saving: next to the original with “(edited)”, the frame as a PNG, the original untouched", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_media-save");
            Directory.CreateDirectory(folder);
            var clip = Path.Combine(folder, "clip.mp4");
            File.Copy(Fixtures.Url("clip.mp4"), clip, overwrite: true);
            var before = Files.Sha(clip);
            using var model = new MediaEditorModel(clip);
            model.Loaded.Wait();
            model.Edit.Start = 0.5;
            model.Edit.End = 1.5;
            model.Edit.Speed = 2;
            model.Save();
            var saved = CollageTests.Finished("Exporting");
            Check(saved.State == ActivityState.Succeeded && saved.Outputs.Count == 1, $"{saved.State} {saved.Summary}");
            var output = Path.Combine(folder, "clip (edited).mp4");
            Require(File.Exists(output), "“(edited)” next to the original");
            Check(Near(Probe.Duration(output), 0.5, 0.15), $"half a second: {Probe.Duration(output)}");

            model.Current = 1;
            model.SaveFrame();
            CollageTests.Finished("Saving the frame");
            Check(File.Exists(Path.Combine(folder, "clip (frame at 1.0 s).png")), "the frame is named after its time");

            Check(Files.Sha(clip) == before, "the original is never changed");
            Check(!Directory.EnumerateFileSystemEntries(folder, ".split-*").Any(), "no temporary files stay behind");

            var bin = Path.Combine(folder, "bin");
            Directory.CreateDirectory(bin);
            var previous = ActivityLog.MoveToRecycleBin;
            ActivityLog.MoveToRecycleBin = path => File.Move(path, Path.Combine(bin, Path.GetFileName(path)));
            try { Check(ActivityLog.Shared.Undo(saved.Id).Count == 0 && !File.Exists(output) && File.Exists(clip), "undo takes the export back"); }
            finally { ActivityLog.MoveToRecycleBin = previous; }
        });
    }
}
