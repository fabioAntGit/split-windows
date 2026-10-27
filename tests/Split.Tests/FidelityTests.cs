using System.IO;
using System.Text;
using ImageMagick;
using PdfSharp.Pdf.IO;
using static Split.Tests.Harness;
using static Split.Tests.ConversionTests;

namespace Split.Tests;

/// <summary>
/// Converting must never drop content in silence: what the target can hold is kept, and what it
/// can't is reported as a note (shown in the HUD, the activity list and the command line).
/// Documents are covered with the document pipeline.
/// </summary>
public static class FidelityTests
{
    private static string Folder(string name)
    {
        var folder = Path.Combine(Fixtures.Root, "_fidelity-" + name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void Register()
    {
        Test("Fidelity", "Animations and pages: kept where the format can, said where it can’t", () =>
        {
            var gif = Fixtures.Isolated("animated.gif", "fidelity-gif");
            var (apng, apngNotes) = Fidelity.Collect(() => Conversions.Convert(gif, Format.Png)[0]);
            Check(RequireInfo(apng).Frames == 3 && apngNotes.Count == 0, $"GIF → PNG is an animated PNG with 3 frames: {apngNotes.Count} notes");
            using (var frames = ImageConverter.Open(apng, Format.Png))
                Check(frames.Animated && frames.Delays.All(d => Near(d, 0.1, 0.002)) && frames.Loops == 0, $"the delays and the loop count come across: {string.Join(",", frames.Delays)} / {frames.Loops}");
            var (jpg, jpgNotes) = Fidelity.Collect(() => Conversions.Convert(gif, Format.Jpg)[0]);
            Check(RequireInfo(jpg).Frames == 1 && jpgNotes.Any(n => n.Contains("first of the 3 frames")), $"GIF → JPG says it kept one frame: {string.Join("; ", jpgNotes)}");
            var (_, tiffNotes) = Fidelity.Collect(() => Conversions.Convert(gif, Format.Tiff));
            Check(tiffNotes.Any(n => n.Contains("timing")), $"GIF → TIFF says the timing is lost: {string.Join("; ", tiffNotes)}");

            var pages = Path.Combine(Folder("pages"), "scan.tiff");
            using (var collection = new MagickImageCollection())
            {
                foreach (var gray in new[] { "#333333", "#808080", "#cccccc" }) collection.Add(new MagickImage(new MagickColor(gray), 200, 300));
                using var file = File.Create(pages);
                collection.Write(file, MagickFormat.Tiff);
            }
            var split = Conversions.Convert(pages, Format.Png);
            Check(split.Count == 1 && Directory.Exists(split[0]) && Files.List(split[0]).Count == 3, $"3 pages → a folder of 3 PNG files: {string.Join(", ", split.Select(Path.GetFileName))}");
            var pdf = Conversions.Convert(pages, Format.Pdf)[0];
            Check(PdfReader.Open(pdf, PdfDocumentOpenMode.Import).PageCount == 3, "3 pages → a 3-page PDF");
        });

        Test("Fidelity", "Animated WebP and JPEG XL keep their frames, delays and loops", () =>
        {
            var gif = Fixtures.Isolated("animated.gif", "fidelity-anim");
            foreach (var target in new[] { Format.Webp, Format.Jxl })
            {
                var output = Conversions.Convert(gif, target)[0];
                using var frames = ImageConverter.Open(output, target);
                Check(frames.Animated && frames.Images.Count == 3, $"{target.Label()}: {frames.Images.Count} frames, animated {frames.Animated}");
                Check(frames.Delays.All(d => Near(d, 0.1, 0.011)), $"{target.Label()}: delays {string.Join(",", frames.Delays)}");
                // The animation reads back as an animation and goes to GIF again with the same timing.
                var again = Conversions.Convert(output, Format.Gif)[0];
                using var back = ImageConverter.Open(again, Format.Gif);
                Check(back.Images.Count == 3 && back.Delays.All(d => Near(d, 0.1, 0.011)), $"{target.Label()} → GIF: {back.Images.Count} frames");
            }
            // An animated PNG with its own timing: 0.5 s and 0.25 s, twice.
            using var source = new MagickImageCollection();
            source.Add(new MagickImage(MagickColors.Red, 40, 30));
            source.Add(new MagickImage(MagickColors.Blue, 40, 30));
            var apng = Path.Combine(Folder("apng"), "timed.png");
            var shots = new List<(byte[] Png, double Delay)>();
            foreach (var (image, delay) in source.Zip(new[] { 0.5, 0.25 }))
            {
                image.Settings.SetDefine(MagickFormat.Png, "color-type", "6");
                shots.Add((image.ToByteArray(MagickFormat.Png), delay));
            }
            File.WriteAllBytes(apng, PngChunks.Assemble(shots, 2));
            var webp = Conversions.Convert(apng, Format.Webp)[0];
            using var read = ImageConverter.Open(webp, Format.Webp);
            Check(read.Images.Count == 2 && Near(read.Delays[0], 0.5, 0.011) && Near(read.Delays[1], 0.25, 0.011) && read.Loops == 2, $"APNG → WebP keeps 0.5 s and 0.25 s and 2 loops: {string.Join(",", read.Delays)} / {read.Loops}");
        });

        Test("Fidelity", "Transparency, orientation and metadata", () =>
        {
            var png = Fixtures.Isolated("quadrants.png", "fidelity-alpha");
            var (_, notes) = Fidelity.Collect(() => Conversions.Convert(png, Format.Jpg));
            Check(notes.Any(n => n.Contains("transparency")), $"PNG → JPG says the transparency became white: {string.Join("; ", notes)}");
            var (_, bmp) = Fidelity.Collect(() => Conversions.Convert(png, Format.Bmp));
            Check(bmp.Count == 0, $"BMP keeps the alpha, so there is nothing to say: {string.Join("; ", bmp)}");
            Check(RequireInfo(Conversions.Convert(png, Format.Bmp)[0]).HasAlpha, "the BMP keeps its transparency");
            var jpg = Fixtures.Isolated("quadrants.jpg", "fidelity-metadata");
            var (_, opaque) = Fidelity.Collect(() => Conversions.Convert(jpg, Format.Png));
            Check(opaque.Count == 0, $"an opaque JPG → PNG has nothing to report: {string.Join("; ", opaque)}");
            var avif = RequireInfo(Conversions.Convert(jpg, Format.Avif)[0]);
            Check(avif.HasGps && avif.UserComment == "test", $"AVIF keeps GPS and EXIF: gps {avif.HasGps}, comment {avif.UserComment}");
            var rotated = Fixtures.Isolated("rotated.jpg", "fidelity-orientation");
            var tiff = RequireInfo(Conversions.Convert(rotated, Format.Tiff)[0]);
            Check(tiff.Orientation == 6 && tiff.Width == 300, $"the orientation stays a tag (the pixels aren’t re-rotated): {tiff.Orientation}, {tiff.Width}");
            var upright = RequireInfo(Conversions.Convert(rotated, Format.Webp)[0]);
            Check(upright.Width == 200 && upright.Height == 300, $"a format without the tag gets turned pixels: {upright.Width}×{upright.Height}");
            var rotatedPng = Pixels.Info(Conversions.Convert(rotated, Format.Png)[0])!;
            Check(rotatedPng.Orientation <= 1, $"a PNG of a rotated photo is turned, not tagged: {rotatedPng.Orientation}");
        });

        Test("Fidelity", "JPEG XL: pictures go in and come back; a JPEG uses the chosen quality", () =>
        {
            var jpg = Fixtures.Isolated("photo.jpg", "fidelity-jxl");
            var jxl = Conversions.Convert(jpg, Format.Jxl)[0];
            Check(RequireInfo(jxl).Width == 2400, "the JXL reads back at full size");
            Check(new FileInfo(jxl).Length < new FileInfo(jpg).Length, $"the JXL is smaller: {new FileInfo(jxl).Length} vs {new FileInfo(jpg).Length}");
            var png = Fixtures.Isolated("quadrants.png", "fidelity-jxl");
            var lossless = Conversions.Convert(png, Format.Jxl)[0];
            using (var original = Pixels.Load(png)!)
            using (var back = Pixels.Load(lossless)!)
                Check(original.Compare(back, ErrorMetric.Absolute) == 0, "a PNG stays pixel for pixel the same in JPEG XL");
            Check(RequireInfo(Conversions.Convert(jxl, Format.Png)[0]).Width == 2400, "JXL → PNG");
        });

        Test("Fidelity", "Audio: cover art, tags, channels and bit depth", () =>
        {
            var dir = Folder("audio");
            var cover = Path.Combine(dir, "cover.png");
            using (var image = Fixtures.Quads(300, 300)) Fixtures.Save(image, "_fidelity-audio/cover.png", MagickFormat.Png);
            File.Move(Fixtures.Url("_fidelity-audio/cover.png"), cover, overwrite: true);
            var mp3 = Path.Combine(dir, "song.mp3");
            Fixtures.Ffmpeg("-i", Fixtures.Url("tone.wav"), "-i", cover, "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-c:v", "png",
                            "-disposition:v", "attached_pic", "-id3v2_version", "3", "-metadata", "title=Song", mp3);
            foreach (var target in new[] { Format.M4a, Format.Flac, Format.Aiff })
            {
                var output = Conversions.Convert(mp3, target)[0];
                Check(Probe.Streams(output).Count(Probe.IsCover) == 1, $"{target.Label()} keeps the cover");
                Check(Probe.Tags(output).GetValueOrDefault("title") == "Song", $"{target.Label()} keeps the title: {string.Join(", ", Probe.Tags(output))}");
            }
            var (_, oggNotes) = Fidelity.Collect(() => Conversions.Convert(mp3, Format.Ogg));
            Check(oggNotes.Any(n => n.Contains("cover")), $"OGG says the cover is left out: {string.Join("; ", oggNotes)}");

            var surround = Path.Combine(dir, "surround.wav");
            Fixtures.Ffmpeg("-f", "lavfi", "-i", "sine=frequency=300:duration=1", "-af", "pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0", surround);
            var (stereo, surroundNotes) = Fidelity.Collect(() => Conversions.Convert(surround, Format.Mp3)[0]);
            Check(Probe.Channels(stereo) == 2 && surroundNotes.Any(n => n.Contains("stereo")), "5.1 → MP3 is mixed to stereo and says so");
            Check(Probe.Channels(Conversions.Convert(surround, Format.Flac)[0]) == 6, "FLAC keeps the 6 channels");

            var deep = Path.Combine(dir, "deep.flac");
            Fixtures.Ffmpeg("-f", "lavfi", "-i", "sine=frequency=500:duration=1", "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", deep);
            var wav = Conversions.Convert(deep, Format.Wav)[0];
            var codec = Probe.Codecs(wav, "audio").FirstOrDefault() ?? "";
            Check(codec is "pcm_s24le" or "pcm_s32le", $"24-bit FLAC → WAV keeps the depth: {codec}");
        });

        Test("Fidelity", "Video: every audio track, subtitles and chapters, or a note", () =>
        {
            var dir = Folder("video");
            var srt = Path.Combine(dir, "subs.srt");
            File.Copy(Fixtures.Url("subs.srt"), srt, overwrite: true);
            var chapters = Path.Combine(dir, "chapters.txt");
            File.WriteAllText(chapters, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=One\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=Two\n");
            var mkv = Path.Combine(dir, "movie.mkv");
            Fixtures.Ffmpeg("-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                            "-f", "lavfi", "-i", "sine=frequency=880:duration=2", "-i", srt, "-i", chapters,
                            "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-map_metadata", "4", "-map_chapters", "4",
                            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt", "-shortest", mkv);
            var (mp4, mp4Notes) = Fidelity.Collect(() => Conversions.Convert(mkv, Format.Mp4)[0]);
            Check(Probe.Codecs(mp4, "audio").Count == 2, $"MP4 keeps both audio tracks: {string.Join(",", Probe.Codecs(mp4, "audio"))}");
            Check(Probe.Codecs(mp4, "subtitle").SequenceEqual(["mov_text"]), $"MP4 keeps the subtitles as mov_text: {string.Join(",", Probe.Codecs(mp4, "subtitle"))}");
            Check(Probe.Chapters(mp4) == 2 && mp4Notes.Count == 0, $"MP4 keeps both chapters, nothing to report: {string.Join("; ", mp4Notes)}");
            var webm = Conversions.Convert(mkv, Format.Webm)[0];
            Check(Probe.Codecs(webm, "subtitle").SequenceEqual(["webvtt"]) && Probe.Codecs(webm, "audio").Count == 2, "WebM: WebVTT and both tracks");
            var (_, aviNotes) = Fidelity.Collect(() => Conversions.Convert(mkv, Format.Avi));
            Check(aviNotes.Any(n => n.Contains("subtitle")) && aviNotes.Any(n => n.Contains("chapter")), $"AVI says what it can’t keep: {string.Join("; ", aviNotes)}");
            var (_, audioNotes) = Fidelity.Collect(() => Conversions.Convert(mkv, Format.Mp3));
            Check(audioNotes.Any(n => n.Contains("audio track")), $"MP3 says only one track was kept: {string.Join("; ", audioNotes)}");
        });

        Test("Fidelity", "Spreadsheets: formulas and sheets", () =>
        {
            var sales = Fixtures.Isolated("sales.xlsx", "fidelity-sheets");
            var (csv, notes) = Fidelity.Collect(() => Conversions.Convert(sales, Format.Csv));
            Check(csv.Count == 2 && notes.Any(n => n.Contains("one sheet per file")), $"two sheets → two CSV files, and it says so: {string.Join(", ", csv.Select(Path.GetFileName))} {string.Join("; ", notes)}");
            var prices = Fixtures.Isolated("prices.csv", "fidelity-formulas");
            var xlsx = Conversions.Convert(prices, Format.Xlsx)[0];
            var (_, formulaNotes) = Fidelity.Collect(() => Conversions.Convert(xlsx, Format.Tsv));
            Check(formulaNotes.Any(n => n.Contains("formulas")), $"XLSX → TSV says the formulas became values: {string.Join("; ", formulaNotes)}");
        });

        Test("Fidelity", "Subtitles and JSON from older Windows tools keep their accents", () =>
        {
            var dir = Folder("encodings");
            var srt = Path.Combine(dir, "windows.srt");
            File.WriteAllBytes(srt, [.. "1\r\n00:00:01,000 --> 00:00:02,000\r\n"u8, 0x4F, 0x6C, 0xE1, 0x20, 0x63, 0x61, 0x66, 0xE9, .. "\r\n"u8]);
            var vtt = Files.Text(Conversions.Convert(srt, Format.Vtt)[0]);
            Check(vtt.Contains("Olá café"), $"a Windows-1252 subtitle keeps its accents: {vtt}");
            var json = Path.Combine(dir, "latin.json");
            File.WriteAllBytes(json, [0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xE9, 0x22, 0x7D]);
            Check(JsonConverter.Read(json).Equals(JsonValue.Object([("a", JsonValue.String("é"))])), "JSON saved as Windows-1252 still reads");
        });

        Test("Fidelity", "Pictures saved by browsers and social sites are recognized", () =>
        {
            var dir = Folder("browser-names");
            foreach (var name in new[] { "photo.jfif", "photo.jpg_large", "photo.jpeg_large", "photo.jpg_orig" })
            {
                var file = Path.Combine(dir, name);
                if (!File.Exists(file)) File.Copy(Fixtures.Url("quadrants.jpg"), file);
                Check(Formats.Detect(file) == Format.Jpg, $"{name} is a JPEG");
            }
            var png = Path.Combine(dir, "graphic.png_large");
            if (!File.Exists(png)) File.Copy(Fixtures.Url("quadrants.png"), png);
            Check(Formats.Detect(png) == Format.Png, "png_large is a PNG");
            Check(RequireInfo(Conversions.Convert(Path.Combine(dir, "photo.jpg_large"), Format.Webp)[0]).Width == 600, "and converts");
        });
    }
}
