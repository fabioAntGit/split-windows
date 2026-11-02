using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Split.App;
using ZXing;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>The one-click tools, "Compress To…" and the file actions.</summary>
public static class ToolTests
{
    // MARK: Samples

    private static string Folder(string name)
    {
        var directory = Path.Combine(Fixtures.Root, "_tools", name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static int PageCount(string path)
    {
        using var document = PdfTools.Open(path);
        return document.PageCount;
    }

    /// <summary>A PDF that says who wrote it, in its Info dictionary and in an XMP packet.</summary>
    private static string SignedPdf()
    {
        var path = Path.Combine(Folder("signed"), "report.pdf");
        if (File.Exists(path)) return path;
        using var document = PdfReader.Open(Fixtures.Url("report.pdf"), PdfDocumentOpenMode.Modify);
        document.Info.Author = "Secret Author";
        document.Info.Title = "Secret title";
        var packet = new PdfDictionary(document);
        packet.Elements.SetName("/Type", "/Metadata");
        packet.Elements.SetName("/Subtype", "/XML");
        packet.CreateStream(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><dc:creator>Secret Author</dc:creator></x:xmpmeta>"));
        document.Internals.AddObject(packet);
        document.Internals.Catalog.Elements["/Metadata"] = packet.Reference;
        document.Save(path);
        return path;
    }

    /// <summary>A photo shown small on a page: 2400 pixels over 8 inches, so 300 pixels per inch.</summary>
    private static string DenseScan()
    {
        var photo = Path.Combine(Folder("dense"), "photo.jpg");
        if (!File.Exists(photo))
        {
            using var image = new MagickImage(File.ReadAllBytes(Fixtures.Url("photo.jpg")), new MagickReadSettings { Format = MagickFormat.Jpeg });
            image.Density = new Density(300, 300, DensityUnit.PixelsPerInch);
            image.Quality = 100;
            File.WriteAllBytes(photo, image.ToByteArray(MagickFormat.Jpeg));
        }
        return Conversions.Convert(photo, Format.Pdf)[0];
    }

    /// <summary>A QR code of the text, black on white with its quiet zone.</summary>
    private static string QrPicture(string text)
    {
        var path = Path.Combine(Folder("qr"), "qr.png");
        if (File.Exists(path)) return path;
        var matrix = new ZXing.QrCode.QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0);
        const int scale = 8;
        var (width, height) = (matrix.Width * scale, matrix.Height * scale);
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                rgb[(y * width + x) * 3] = rgb[(y * width + x) * 3 + 1] = rgb[(y * width + x) * 3 + 2] = matrix[x / scale, y / scale] ? (byte)0 : (byte)255;
        }
        using var image = new MagickImage(rgb, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
        File.WriteAllBytes(path, image.ToByteArray(MagickFormat.Png));
        return path;
    }

    /// <summary>A photo with a hole in it: lossless, big, and needing transparency to stay itself.</summary>
    private static string TransparentPhoto()
    {
        var path = Path.Combine(Folder("alpha"), "cutout.png");
        if (File.Exists(path)) return path;
        using var image = new MagickImage(File.ReadAllBytes(Fixtures.Url("photo.jpg")), new MagickReadSettings { Format = MagickFormat.Jpeg });
        image.Resize(1200, 800);
        image.Alpha(AlphaAction.Set);
        new Drawables().FillColor(MagickColors.Transparent).Alpha(600, 400, PaintMethod.Floodfill).Draw(image);
        File.WriteAllBytes(path, image.ToByteArray(MagickFormat.Png));
        return path;
    }

    /// <summary>A figure (head and shoulders) on a plain wall: something a segmentation model has a subject in.</summary>
    private static string Subject()
    {
        var path = Path.Combine(Folder("subject"), "subject.png");
        if (File.Exists(path)) return path;
        using var image = new MagickImage(new MagickColor("#d8d8d8"), 600, 600);
        new Drawables()
            .FillColor(new MagickColor("#2b4a8b")).Ellipse(300, 560, 190, 170, 180, 360)
            .FillColor(new MagickColor("#e0ac84")).Rectangle(265, 330, 335, 400)
            .Ellipse(300, 250, 95, 115, 0, 360)
            .FillColor(new MagickColor("#3a2616")).Ellipse(300, 175, 100, 70, 180, 360)
            .Draw(image);
        File.WriteAllBytes(path, image.ToByteArray(MagickFormat.Png));
        return path;
    }

    /// <summary>The integrated loudness ffmpeg measures, in LUFS.</summary>
    private static double? Loudness(string path)
    {
        var result = ProcessRunner.Run(Engines.Require("ffmpeg"), ["-nostdin", "-hide_banner", "-i", path, "-af", "ebur128", "-f", "null", "NUL"]);
        var summary = result.Stderr[(result.Stderr.LastIndexOf("Summary:", StringComparison.Ordinal) is var at and >= 0 ? at : 0)..];
        return Regex.Match(summary, @"I:\s+(-?\d+(\.\d+)?) LUFS") is { Success: true } match
            ? double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static List<string> ZipEntries(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    private static ProcessResult Splitapp(params string[] arguments) => ProcessRunner.Run(Path.Combine(AppContext.BaseDirectory, "splitapp.exe"), arguments);

    public static void Register()
    {
        // MARK: Compress

        Test("Tools", "Compress: a large image gets smaller, an optimized one is explained", () =>
        {
            var photo = Fixtures.Isolated("photo.jpg", "compress-photo");
            var output = Tools.Compress(photo);
            Check(new FileInfo(output).Length < new FileInfo(photo).Length, $"{new FileInfo(output).Length} ≥ {new FileInfo(photo).Length}");
            var info = Pixels.Info(output)!;
            Check(Math.Max(info.Width, info.Height) <= 2560, "it should limit to 2560 px");
            Check(Path.GetFileName(output) == "photo (compressed).jpg", $"the name: {Path.GetFileName(output)}");
            var small = Fixtures.Isolated("icon.ico", "compress-small");
            try
            {
                var result = Tools.Compress(small);
                Check(new FileInfo(result).Length < new FileInfo(small).Length, "the compressed copy is larger than the original");
            }
            catch (Exception error)
            {
                Check(Explain.Error(error).Message.Contains("already well compressed"), $"message: {Explain.Error(error).Message}");
            }
        });

        Test("Tools", "Compress video, audio and PDF: smaller or explained", () =>
        {
            foreach (var name in new[] { "clip.mov", "tone.wav", "report.pdf" })
            {
                var source = Fixtures.Isolated(name, "compress-" + name);
                try
                {
                    var output = Tools.Compress(source);
                    Check(new FileInfo(output).Length < new FileInfo(source).Length, $"{name}: the compressed copy isn’t smaller");
                }
                catch (Exception error)
                {
                    Check(Explain.Error(error).Message.Contains("already well compressed"), $"{name}: {Explain.Error(error).Message}");
                }
            }
        });

        Test("Tools", "Compress a PDF: its pictures are scaled to 150 pixels per inch and recompressed", () =>
        {
            var scan = DenseScan();
            var output = Tools.Compress(scan);
            Check(Path.GetFileName(output).EndsWith("(compressed).pdf"), Path.GetFileName(output));
            Check(new FileInfo(output).Length < new FileInfo(scan).Length / 2, $"{new FileInfo(output).Length} bytes from {new FileInfo(scan).Length}");
            Check(PageCount(output) == 1, "the page stays");
            using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            var image = document.Internals.GetAllObjects().OfType<PdfDictionary>().FirstOrDefault(d => d.Elements.GetName("/Subtype") == "/Image");
            Require(image is not null, "the picture is still in the PDF");
            Check(image!.Elements.GetInteger("/Width") is > 1100 and <= 1210, $"2400 px over 8 in → about 1200 px: {image.Elements.GetInteger("/Width")}");
            Check(image.Elements.GetName("/Filter") == "/DCTDecode", "it is a JPEG");
            using var pixels = PdfTools.Open(output);
            using var page = pixels.Render(0, 0.5);
            Check(page is not null && Pixels.At(page, 100, 100).Take(3).Any(v => v != 255), "the page still shows the picture");
        });

        // MARK: Metadata

        Test("Tools", "Remove metadata: GPS, title and author go; the orientation stays", () =>
        {
            if (Engines.Path("exiftool") is null) Skip("exiftool is not installed");
            var jpg = Fixtures.Isolated("quadrants.jpg", "strip");
            Check(Pixels.Info(jpg)!.HasGps, "the sample has a GPS position");
            var clean = Tools.StripMetadata(jpg);
            Check(Pixels.Info(clean) is { HasGps: false, UserComment: null }, "the GPS stayed");
            Check(Pixels.Info(clean)!.Width == 600, "the picture is the same");
            var rotated = Fixtures.Isolated("rotated.jpg", "strip-rotated");
            Check(Pixels.Info(Tools.StripMetadata(rotated))!.Orientation == 6, "the orientation should stay");

            var mp3 = Fixtures.Isolated("tone.mp3", "strip-audio");
            Check(Probe.Tags(mp3).ContainsKey("title"), "the sample has tags");
            var tags = Probe.Tags(Tools.StripMetadata(mp3));
            Check(!tags.ContainsKey("title") && !tags.ContainsKey("artist"), $"tags in the MP3: {string.Join(", ", tags.Keys)}");

            var pdf = Tools.StripMetadata(SignedPdf());
            using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
            Check(document.Info.Author.Length == 0 && document.Info.Title.Length == 0, $"the author stayed in the PDF: {document.Info.Author}");
            Check(!Encoding.Latin1.GetString(File.ReadAllBytes(pdf)).Contains("Secret"), "the author's name is still written in the file");
            Check(PageCount(pdf) == 3, "the pages stay");
        });

        // MARK: Background

        Test("Tools", "Remove background: a PNG transparent around the subject, or an explanation", () =>
        {
            if (!Tools.CanRemoveBackground) Skip("the background-removal model is not installed (tools/get-model.ps1)");
            var source = Subject();
            try
            {
                var output = Tools.RemoveBackground(source);
                Check(Path.GetFileName(output) == "subject (no background).png", Path.GetFileName(output));
                using var image = Pixels.Load(output)!;
                Check(image.Width == 600 && image.Height == 600, "same size as the picture");
                Check(Pixels.At(image, 5, 5)[3] == 0, $"the corner should be transparent: {Pixels.At(image, 5, 5)[3]}");
                Check(Pixels.At(image, 300, 250)[3] > 200, $"the middle (the subject) should be opaque: {Pixels.At(image, 300, 250)[3]}");
            }
            catch (Exception error)
            {
                Check(Explain.Error(error).Message.Contains("main subject"), $"unexpected error: {Explain.Error(error).FullText}");
            }
        });

        // MARK: PDF and archives

        Test("Tools", "Combine and split PDFs", () =>
        {
            var report = Fixtures.Isolated("report.pdf", "merge");
            var scanned = Fixtures.Isolated("scanned.pdf", "merge");
            var image = Fixtures.Isolated("quadrants.png", "merge");
            var merged = Tools.CombinePdf([report, scanned, image]);
            Check(Path.GetFileName(merged) == "quadrants (combined).pdf" || Path.GetFileName(merged).EndsWith("(combined).pdf"), Path.GetFileName(merged));
            Check(PageCount(merged) == 5, $"3 + 1 + 1 pages → {PageCount(merged)}");
            var folder = Tools.SplitPages(report);
            var parts = Files.List(folder);
            Check(parts.Count == 3 && parts[0].EndsWith("report - page 1.pdf"), $"split: {string.Join(", ", parts.Select(Path.GetFileName))}");
            Check(parts.All(part => PageCount(part) == 1), "each part should have 1 page");
            Check(Tools.CombinePdf([image]) is var single && PageCount(single) == 1, "one picture becomes a one-page PDF");
            Check(Tools.NaturalCompare("page 2.pdf", "page 10.pdf") < 0 && Tools.NaturalCompare("b", "a") > 0, "names are sorted the way people count");
            ExpectError("combining a PDF that isn’t one", ["valid PDF"], () => Tools.CombinePdf([Hostile.File("fake.pdf", "tools-pdf", "<html>Page not found</html>")]));
        });

        Test("Tools", "Extract and create a ZIP", () =>
        {
            var zip = Fixtures.Isolated("folder.zip", "extract");
            var extracted = ArchiveTools.Extract(zip);
            Check(extracted.Count == 1 && Path.GetFileName(extracted[0]) == "Folder", $"a single folder comes out as it is: {string.Join(",", extracted)}");
            Check(Files.List(extracted[0]).Count == 2, "the folder should hold 2 files");
            var two = Fixtures.Isolated("two.zip", "extract-two");
            var loose = ArchiveTools.Extract(two);
            Check(loose.Count == 1 && Files.List(loose[0]).Count == 2, "loose files go into a new folder");
            var packed = Tools.Zip(Files.List(extracted[0]));
            var names = ZipEntries(packed);
            Check(names.Contains("one.txt") && names.Contains("two.txt"), $"ZIP: {string.Join(", ", names)}");
            Check(Path.GetFileName(packed) == "Archive.zip", Path.GetFileName(packed));
            Check(ZipEntries(Tools.Zip([extracted[0]])).Contains("Folder/one.txt"), "a folder keeps its name inside");
            Check(Path.GetFileName(Tools.Zip([Fixtures.Url("notes.txt")])) == "notes.txt.zip", "one item is named after itself");
        });

        // MARK: Audio and video

        Test("Tools", "Normalize the volume to −16 LUFS", () =>
        {
            var source = Fixtures.Isolated("tone.wav", "normalize");
            var after = Loudness(Tools.Normalize(source));
            Require(after is not null, "no loudness measurement");
            Check(Near(after!.Value, -16, 2), $"loudness {after} LUFS (before {Loudness(source)})");
            ExpectError("normalizing without sound", ["has no sound"], () => Tools.Normalize(Fixtures.Isolated("silent.mp4", "silent")));
        });

        Test("Tools", "Mono, trim silence, remove audio", () =>
        {
            var wav = Fixtures.Isolated("tone.wav", "mono");
            Check(Probe.Channels(Tools.Mono(wav)) == 1, "a mono WAV should have 1 channel");
            var ogg = Fixtures.Isolated("tone.ogg", "mono-ogg");
            Check(Probe.Codecs(Tools.Mono(ogg), "audio").Count > 0, "mono OGG failed");
            var mp4 = Fixtures.Isolated("clip.mp4", "mono-video");
            var monoVideo = Tools.Mono(mp4);
            Check(Probe.Channels(monoVideo) == 1 && Probe.Codecs(monoVideo, "video").SequenceEqual(["h264"]), "a mono video should keep the picture");

            var trimmed = Tools.TrimSilence(Fixtures.Isolated("tone.wav", "silence"));
            Check(Near(Probe.Duration(trimmed), 2, 0.2), $"3 s with 1 s of silence → {Probe.Duration(trimmed)} s");
            var flac = Fixtures.Isolated("tone.flac", "silence-twice");
            var once = Tools.TrimSilence(flac);
            ExpectError("trimming silence where there is none", ["has no silence"], () => Tools.TrimSilence(once));

            var muted = Tools.Mute(Fixtures.Isolated("clip.mp4", "mute"));
            Check(Probe.Codecs(muted, "audio").Count == 0 && Probe.Codecs(muted, "video").Count > 0, "no audio, with the picture");
        });

        Test("Tools", "Join videos (one silent), frames, audio to video", () =>
        {
            var a = Fixtures.Isolated("clip.mp4", "join");
            var b = Fixtures.Isolated("silent.mp4", "join");
            var joined = Tools.ConcatVideos([b, a]);
            Check(Near(Probe.Duration(joined), 4, 0.3), $"2 s + 2 s → {Probe.Duration(joined)}");
            Check(Probe.Codecs(joined, "audio").Count > 0, "the joined video should have sound");
            Check(Path.GetFileName(joined) == "clip (joined).mp4", $"named after the first in name order: {Path.GetFileName(joined)}");

            var folder = Tools.Snapshots(Fixtures.Isolated("clip.mp4", "snapshots"));
            var frames = Files.List(folder);
            Check(frames.Count == 12, $"12 frames → {frames.Count}");
            Check(frames.All(frame => Pixels.Info(frame)?.Width == 320), "frames at 320 px");
            Check(Path.GetFileName(frames[0]) == "Video frame 01.png", Path.GetFileName(frames[0]));

            var video = Tools.WaveformVideo(Fixtures.Isolated("tone.mp3", "waves"));
            Check(Probe.Codecs(video, "video").SequenceEqual(["h264"]) && Probe.Codecs(video, "audio").SequenceEqual(["aac"]), "MP4 H.264 + AAC");
            Check(Near(Probe.Duration(video), 3, 0.3), $"length {Probe.Duration(video)}");
            Check(Probe.VideoSize(video) == (1280, 720), $"1280×720: {Probe.VideoSize(video)}");
        });

        // MARK: QR codes

        Test("Tools", "Read a QR code in an image and in a PDF", () =>
        {
            var png = QrPicture("https://example.com/split");
            Check(Tools.ReadQr(png).SequenceEqual(["https://example.com/split"]), "QR in a PNG");
            var pdf = Tools.CombinePdf([png]);
            Check(Tools.ReadQr(pdf).SequenceEqual(["https://example.com/split"]), "QR in a PDF");
            ExpectError("an image without a QR code", ["No QR code"], () => Tools.ReadQr(Fixtures.Url("quadrants.png")));
            var result = QuickTool.ReadQr.Run([png]);
            Check(result.Outputs.Count == 0 && result.Message == "https://example.com/split", "the text is the message");
        });

        // MARK: Which tools show

        Test("Tools", "Each tool only shows for the right files", () =>
        {
            var image = new[] { Fixtures.Url("quadrants.jpg") };
            var video = new[] { Fixtures.Url("clip.mp4") };
            var audio = new[] { Fixtures.Url("tone.wav") };
            var pdf = new[] { Fixtures.Url("report.pdf") };
            Check(QuickTools.Available(image).Contains(QuickTool.Compress) && !QuickTools.Available(image).Contains(QuickTool.Mute), "image");
            Check(QuickTools.Available(video).Contains(QuickTool.Mute) && QuickTools.Available(video).Contains(QuickTool.Snapshots), "video");
            Check(QuickTools.Available(audio).Contains(QuickTool.TrimSilence) && QuickTools.Available(audio).Contains(QuickTool.WaveformVideo), "audio");
            Check(QuickTools.Available(pdf).Contains(QuickTool.SplitPages) && !QuickTools.Available(pdf).Contains(QuickTool.CombinePdf), "a single PDF isn’t combined");
            Check(!QuickTool.ConcatVideos.Applies(video), "joining needs 2 videos");
            Check(QuickTool.ConcatVideos.Applies([.. video, Fixtures.Url("clip.mov")]), "joining 2 videos");
            Check(QuickTools.Available(image).Contains(QuickTool.CompressToSize) && !QuickTool.CompressToSize.Applies(pdf), "“Compress To…” is for images, audio and video");
            Check(QuickTool.ReadQr.Applies(image) && QuickTool.ReadQr.Applies(pdf) && !QuickTool.ReadQr.Applies(audio), "QR codes are read in pictures and PDFs");
            Check(QuickTool.Zip.Applies([Fixtures.Url("A folder")]), "anything can be zipped, folders too");
            Check(QuickTool.Extract.Applies([Fixtures.Url("folder.zip")]), "archives open");
            Check(QuickTools.FromRaw("compress-to") == QuickTool.CompressToSize && QuickTools.All.Select(t => t.Raw()).Distinct().Count() == 16, "the sixteen ids");
        });

        Test("Tools", "Editors are offered for the right files, and come first on the tools wheel", () =>
        {
            var image = new[] { Fixtures.Url("quadrants.jpg") };
            var video = new[] { Fixtures.Url("clip.mp4") };
            var pdf = new[] { Fixtures.Url("report.pdf") };
            Check(EditorTools.Available(image).Contains(EditorTool.Crop) && !EditorTools.Available(image).Contains(EditorTool.Collage), "a collage needs 2 images");
            Check(EditorTools.Available([.. image, .. image]).Contains(EditorTool.Collage), "a collage with 2 images");
            Check(EditorTools.Available(video).SequenceEqual([EditorTool.Media]), "a video opens the media editor");
            Check(EditorTools.Available(pdf).SequenceEqual([EditorTool.PdfPages]), "a PDF opens the organizer");
            Check(ToolCatalog.Items(image).FirstOrDefault()?.Title == "Crop", "editors come first on the wheel");
        });

        Test("Tools", "A tool refuses files it doesn’t know, with an explanation", () =>
        {
            ExpectError("a tool run on a folder it can't use", ["isn’t"], () => QuickTool.Extract.Run([Fixtures.Url("mystery.xyz")]));
            ExpectError("no size chosen", ["No size was chosen"], () => QuickTool.CompressToSize.Run([Fixtures.Url("quadrants.jpg")]));
        });

        // MARK: Compress To…

        Test("Tools", "Compress To…: sizes are read the way people write them", () =>
        {
            Check(TargetSize.Parse("2MB") == 2_000_000 && TargetSize.Parse("1,5 MB") == 1_500_000 && TargetSize.Parse("800kb") == 800_000, "MB/KB");
            Check(TargetSize.Parse("2 Mo") == 2_000_000 && TargetSize.Parse("750 Ko") == 750_000, "French units");
            Check(TargetSize.Parse("300000") == 300_000 && TargetSize.Parse("abc") is null && TargetSize.Parse("-2 MB") is null, "plain bytes and nonsense");
            Check(TargetSize.Plain(25_000_000) == "25 MB" && TargetSize.Plain(500_000) == "500 KB" && TargetSize.Plain(1_500_000) == "1.5 MB", "field text");
            var sizes = new[] { 512L, 640_400, 999_700, 12_540_000, 1_254_000_000 }.Select(ByteCount.Format).ToList();
            Check(sizes.SequenceEqual(["512 bytes", "640 KB", "1 MB", "12.5 MB", "1.25 GB"]), $"sizes in messages, more digits for bigger units: {string.Join(" | ", sizes)}");
            Check(TargetSize.Uses.All(use => TargetSize.Parse(TargetSize.Plain(use.Bytes)) == use.Bytes), "presets read back");
            Check(TargetSize.Presets.All(bytes => TargetSize.Parse(TargetSize.Plain(bytes)) == bytes), "common sizes read back");
        });

        Test("Tools", "Compress To…: images, audio and video fit the size chosen", () =>
        {
            var photo = Fixtures.Isolated("photo.jpg", "target-photo");
            const int limit = 150_000;
            var (small, _) = Fidelity.Collect(() => TargetSize.Compress(photo, limit));
            var size = new FileInfo(small).Length;
            Check(size <= limit && size > limit / 3, $"JPG: {size} bytes for {limit}");
            Check(Path.GetFileName(small) == "photo (150 KB).jpg", Path.GetFileName(small));

            var png = TransparentPhoto();
            var (fromPng, notes) = Fidelity.Collect(() => TargetSize.Compress(png, 60_000));
            Check(new FileInfo(fromPng).Length <= 60_000 && Formats.Detect(fromPng) == Format.Webp, $"a transparent PNG becomes WebP: {Path.GetFileName(fromPng)}");
            Check(Pixels.Info(fromPng)!.HasAlpha, "and keeps its transparency");

            Check(notes.Any(note => note.Contains("WebP")), $"the change of format is said: {string.Join("; ", notes)}");

            var wav = Fixtures.Isolated("tone.wav", "target-audio");
            var audio = TargetSize.Compress(wav, 40_000);
            Check(new FileInfo(audio).Length <= 40_000 && Near(Probe.Duration(audio), 3, 0.2), $"audio: {new FileInfo(audio).Length} bytes");

            var longVideo = Fixtures.Isolated("long.mp4", "target-video");
            var budget = new FileInfo(longVideo).Length / 3;
            var smaller = TargetSize.Compress(longVideo, budget);
            Check(new FileInfo(smaller).Length <= budget && Near(Probe.Duration(smaller), 12, 0.5), $"video: {new FileInfo(smaller).Length} bytes for {budget}");

            ExpectError("a file already small enough", ["already", "Nothing was changed"], () => TargetSize.Compress(photo, 50_000_000));
            ExpectError("a size nothing can reach", ["too long to fit"], () => TargetSize.Compress(longVideo, 12_000));
        });

        Test("Tools", "Compress To…: the window asks for a size, reads it and remembers it", () => Ui.Run(() =>
        {
            var photo = Fixtures.Url("photo.jpg");
            long? picked = null;
            var window = new TargetSizeWindow([photo], bytes => picked = bytes);
            Check(window.Message.Contains("photo.jpg") && window.Message.Contains("is "), $"what the file weighs: {window.Message}");
            Check(new TargetSizeWindow([photo, photo], _ => { }).Message.Contains("Each of the 2 files"), "several files");
            window.Field.Box.Text = "abc";
            window.Accept();
            Check(picked is null && window.Message.Contains("Write a size like"), "nonsense is refused and explained");
            window.Field.Box.Text = "5 KB";
            window.Accept();
            Check(picked is null, "less than 10 KB is refused");
            var titles = window.CommonSizes.Select(item => item.Title).ToList();
            Check(titles.Contains("500 KB") && titles.Contains("25 MB") && titles.Contains(null) && titles.Any(t => t?.StartsWith("E-mail attachment") == true), $"common sizes: {string.Join("|", titles)}");
            window.CommonSizes.First(item => item.Title == "5 MB").Run!();
            Check(window.Field.Box.Text == "5 MB", "a common size fills the field");
            window.Field.Box.Text = "800 KB";
            window.Accept();
            Check(picked == 800_000 && Settings.LastTargetSize == "800 KB", $"{picked}, {Settings.LastTargetSize}");
        }));

        // MARK: Actions

        Test("Actions", "The actions are the ones this PC can do, with the same ids", () =>
        {
            Check(FileActions.All.Select(a => a.Raw()).SequenceEqual(["shelf", "copy", "copyPaths", "move", "terminal"]), "ids and order");
            Check(WheelCatalog.Items(WheelKind.Actions, [Fixtures.Url("quadrants.jpg")]).Select(i => i.Id).SequenceEqual(FileActions.All.Select(a => a.Raw())), "the wheel offers them");
        });

        Test("Actions", "Copy puts the files on the clipboard to be copied; Copy Paths puts one path per line", () => Ui.Run(() =>
        {
            var one = Fixtures.Url("notes.txt");
            var two = Fixtures.Url("readme.md");
            var host = new AppWheelHost();
            host.Perform(FileAction.Copy, [one, two]);
            var dropped = Clipboard.GetFileDropList().Cast<string>().ToList();
            Check(dropped.SequenceEqual([one, two]), $"files: {string.Join(", ", dropped)}");
            var effect = (MemoryStream)Clipboard.GetData("Preferred DropEffect")!;
            Check(BitConverter.ToInt32(effect.ToArray()) == (int)DragDropEffects.Copy, "marked to be copied, not moved");
            host.Perform(FileAction.CopyPaths, [one, two]);
            Check(Clipboard.GetText() == one + "\r\n" + two, $"paths: {Clipboard.GetText()}");
            host.Perform(FileAction.CopyPaths, [one]);
            Check(Clipboard.GetText() == one, "one path has no line end");
        }));

        Test("Actions", "Terminal: the folder is one argument, spaces, “&” and “;” included", () =>
        {
            var folder = @"C:\Work\Split test & co; v2";
            var wt = Split.Windows.Terminal.Start(folder, @"C:\wt.exe");
            Check(wt.ArgumentList.SequenceEqual(["-d", @"C:\Work\Split test & co\; v2"]), $"Windows Terminal: {string.Join(" | ", wt.ArgumentList)}");
            var shell = Split.Windows.Terminal.Start(folder, null);
            Check(shell.FileName == "powershell.exe" && shell.WorkingDirectory == folder && shell.ArgumentList.Count == 0, "PowerShell starts in the folder, with nothing to read");
        });

        Test("Actions", "Move To…: names taken get a number, a folder can't go into itself, the same folder is refused", () =>
        {
            var root = Folder("move");
            var from = Path.Combine(root, "from");
            var to = Path.Combine(root, "to");
            Directory.CreateDirectory(Path.Combine(from, "Album"));
            Directory.CreateDirectory(to);
            File.WriteAllText(Path.Combine(from, "a.txt"), "new");
            File.WriteAllText(Path.Combine(from, "Album", "song.txt"), "la");
            File.WriteAllText(Path.Combine(to, "a.txt"), "old");
            Directory.CreateDirectory(Path.Combine(to, "Album"));

            var (moved, failures) = FileMover.Move([Path.Combine(from, "a.txt"), Path.Combine(from, "Album")], to);
            Check(failures.Count == 0 && moved.Select(Path.GetFileName).SequenceEqual(["a 2.txt", "Album 2"]), $"{string.Join(", ", moved.Select(Path.GetFileName))} / {failures.Count} failures");
            Check(File.ReadAllText(Path.Combine(to, "a.txt")) == "old" && File.ReadAllText(Path.Combine(to, "a 2.txt")) == "new", "nothing is overwritten");
            Check(File.Exists(Path.Combine(to, "Album 2", "song.txt")) && !Directory.Exists(Path.Combine(from, "Album")), "a folder moves with what it holds");

            var album = Path.Combine(to, "Album 2");
            var inside = Path.Combine(album, "inside");
            Directory.CreateDirectory(inside);
            (moved, failures) = FileMover.Move([album], inside);
            Check(moved.Count == 0 && failures.Count == 1 && failures[0].Error.Message == "A folder can’t be moved into itself.", $"into itself: {failures.FirstOrDefault()?.Error.Message}");
            (moved, failures) = FileMover.Move([album], album);
            Check(failures.Count == 1 && failures[0].Error.Message == "A folder can’t be moved into itself.", "into itself, exactly");
            (moved, failures) = FileMover.Move([Path.Combine(to, "a 2.txt")], to);
            Check(moved.Count == 0 && failures.Count == 1 && failures[0].Error.Message == "It is already in that folder.", $"same folder: {failures.FirstOrDefault()?.Error.Message}");
            (moved, failures) = FileMover.Move([Path.Combine(to, "gone.txt"), Path.Combine(to, "a.txt")], from);
            Check(moved.Count == 1 && failures.Count == 1 && failures[0].File == "gone.txt", "one fails, the other still moves");
        });

        // MARK: Command line

        Test("Tools", "splitapp tool: the same tools from the command line", () =>
        {
            var zip = Splitapp("tool", "zip", Fixtures.Isolated("notes.txt", "cli"));
            Check(zip.Status == 0 && zip.Stdout.StartsWith("✓ ") && zip.Stdout.Contains("notes.txt.zip"), $"zip: {zip.Status} {zip.Stdout} {zip.Stderr}");
            Check(File.Exists(Path.Combine(Fixtures.Root, "_cli", "notes.txt.zip")), "the ZIP is next to the file");

            var target = Splitapp("tool", "compress-to", "100kb", Fixtures.Isolated("photo.jpg", "cli-target"));
            Check(target.Status == 0 && target.Stdout.Contains("photo (100 KB).jpg"), $"compress-to: {target.Status} {target.Stdout} {target.Stderr}");

            var note = Splitapp("tool", "compress-to", "60000", TransparentPhoto());
            Check(note.Status == 0 && note.Stdout.Contains("  ! "), $"the notes are printed: {note.Stdout}");

            var wrong = Splitapp("tool", "mute", Fixtures.Url("quadrants.jpg"));
            Check(wrong.Status == 1 && wrong.Stderr.Contains("The tool “Remove Audio” doesn’t apply"), $"wrong files: {wrong.Status} {wrong.Stderr}");

            var qr = Splitapp("tool", "read-qr", QrPicture("https://example.com/split"));
            Check(qr.Status == 0 && qr.Stdout.Contains("https://example.com/split"), $"QR: {qr.Stdout}");

            var failed = Splitapp("tool", "normalize", Fixtures.Isolated("silent.mp4", "cli-silent"));
            Check(failed.Status == 1 && failed.Stderr.StartsWith("✗ ") && failed.Stderr.Contains("has no sound"), $"errors are explained: {failed.Stderr}");

            Check(Splitapp("tool", "nonsense", "x").Status == 2 && Splitapp("tool", "compress-to", "abc", "x").Status == 2, "usage errors exit with 2");

            var folder = Path.GetDirectoryName(Fixtures.Isolated("notes.txt", "cli-glob"))!;
            File.WriteAllText(Path.Combine(folder, "more notes.txt"), "more");
            File.WriteAllText(Path.Combine(folder, "page.html"), "<p>not a match</p>");
            var glob = Splitapp("targets", Path.Combine(folder, "*.txt"), Path.Combine(folder, "*.htm"));
            Check(glob.Stdout.Contains("more notes.txt:") && glob.Stdout.Contains("\nnotes.txt:") && !glob.Stdout.Contains("page.html"), $"wildcards are expanded, “*.htm” finding no .html: {glob.Stdout} {glob.Stderr}");

            var language = Environment.GetEnvironmentVariable("SPLIT_LANGUAGE");
            Environment.SetEnvironmentVariable("SPLIT_LANGUAGE", "pt");
            try
            {
                var help = Splitapp("help");
                Check(help.Stdout.Contains("conversor local de ficheiros") && help.Stderr.Length == 0, $"it starts in another language: {help.Stdout} {help.Stderr}");
            }
            finally { Environment.SetEnvironmentVariable("SPLIT_LANGUAGE", language); }
        });
    }
}
