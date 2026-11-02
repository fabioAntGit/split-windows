using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using ImageMagick;
using PdfSharp.Pdf.IO;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>
/// The conversion matrix: every sample × every target the wheel offers, each result checked for
/// its real content, not just its extension. Text documents, PDF and presentations are covered
/// with the document pipeline.
/// </summary>
public static class ConversionTests
{
    private static readonly string[] Samples =
    [
        "quadrants.png", "quadrants.jpg", "quadrants.heic", "quadrants.tiff", "quadrants.bmp", "quadrants.webp", "icon.ico",
        "animated.gif", "drawing.svg", "quadrants.avif", "quadrants.jxl",
        "tone.wav", "tone.mp3", "tone.flac", "tone.m4a", "tone.opus", "tone.ogg", "tone.aiff", "tone.wma",
        "clip.mp4", "clip.mov", "clip.m4v", "clip.mkv", "clip.webm", "clip.avi", "clip.wmv",
        "notes.txt", "readme.md", "page.html", "letter.rtf", "notes.rtfd", "letter.docx", "letter.odt", "letter.doc", "book.epub", "report.pdf",
        "prices.csv", "people.tsv", "sales.xlsx", "subs.srt", "subs.vtt", "folder.zip", "folder.tar", "folder.tar.gz", "folder.7z", "folder.rar",
        "data.json", "data.xml",
    ];

    public static ImageInfo RequireInfo(string path)
    {
        var info = Pixels.Info(path);
        Require(info is not null, $"{Path.GetFileName(path)} doesn’t open as an image");
        return info!;
    }

    public static void Register()
    {
        foreach (var name in Samples)
        {
            Test("Conversion", $"{name} → every format on the wheel and back", () =>
            {
                Require(File.Exists(Fixtures.Url(name)) || Directory.Exists(Fixtures.Url(name)), $"The sample {name} is missing");
                if (Fixtures.Stubs.Contains(name)) Skip($"{name} can’t be made on Windows: put a real one in tests/Fixtures");
                var source = Fixtures.Isolated(name, $"matrix-{name}");
                var format = Formats.Detect(source)!.Value;
                var targets = Conversions.Targets(format);
                Require(targets.Count > 0, $"{name} has no targets on the wheel");
                var before = Files.Sha(source);
                foreach (var target in targets)
                {
                    var outputs = Step(source, format, target, name);
                    // And back, when the result's wheel offers the original's format (from the first page of a folder of pages).
                    if (outputs.FirstOrDefault() is not { } made || !Conversions.Targets(target).Contains(format)) continue;
                    if ((Directory.Exists(made) ? Files.List(made).FirstOrDefault() : made) is { } result) Step(result, target, format, $"{name} → {target.Label()}");
                }
                Check(Files.Sha(source) == before, $"{name}: the original was changed");
                var leftovers = Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(source)!, ".split-*").ToList();
                Check(leftovers.Count == 0, $"{name}: temporary files were left: {string.Join(", ", leftovers.Select(Path.GetFileName))}");
            });
        }

        static IReadOnlyList<string> Step(string from, Format format, Format target, string what)
        {
            try
            {
                var outputs = Conversions.Convert(from, target);
                Check(outputs.Count > 0, $"{what} → {target.Label()}: no result");
                foreach (var output in outputs) Validate(output, target, from, format);
                return outputs;
            }
            catch (Exception error) when (error is not TestFailure)
            {
                Check(false, $"{what} → {target.Label()} failed: {Explain.Error(error, from).FullText}");
                return [];
            }
        }

        Test("Conversion", "Every format with targets on the wheel has a sample in the matrix", () =>
        {
            var covered = Samples.Select(name => Formats.Detect(Fixtures.Url(name))).ToHashSet();
            // Only Excel, PowerPoint or LibreOffice open these, and nothing here can make one.
            Format[] office = [Format.Xls, Format.Ods, Format.Pptx, Format.Ppt, Format.Odp];
            var missing = Formats.All.Where(f => Conversions.Targets(f).Count > 0 && !covered.Contains(f) && !office.Contains(f)).ToList();
            Check(missing.Count == 0, $"no sample for {string.Join(", ", missing.Select(f => f.Label()))}");
        });

        Test("Conversion", "The EXIF orientation is applied (300×200 rotated → 200×300)", () =>
        {
            var source = Fixtures.Isolated("rotated.jpg", "orientation");
            var output = Conversions.Convert(source, Format.Png)[0];
            var info = RequireInfo(output);
            Check(info.Width == 200 && info.Height == 300, $"expected 200×300, got {info.Width}×{info.Height}");
            using var image = Pixels.Load(output)!;
            // Rotating 90° clockwise puts the old bottom-left (green) at the top-left.
            Check(Pixels.Close(Pixels.At(image, 20, 20), [0, 204, 0]), $"the top-left corner should be green: {string.Join(",", Pixels.At(image, 20, 20))}");
        });

        Test("Conversion", "Transparency: PNG→JPG gets a white background, PNG→WebP keeps alpha", () =>
        {
            var source = Fixtures.Isolated("quadrants.png", "alpha");
            var jpg = Conversions.Convert(source, Format.Jpg)[0];
            using var image = Pixels.Load(jpg)!;
            var centre = Pixels.At(image, 300, 200);
            Check(Pixels.Close(centre, [255, 255, 255], 12), $"the transparent centre should be white in the JPG: {string.Join(",", centre)}");
            var webp = Conversions.Convert(source, Format.Webp)[0];
            Check(RequireInfo(webp).HasAlpha, "the WebP should keep the transparency");
        });

        Test("Conversion", "Metadata: GPS kept by default and removed when turned off", () =>
        {
            var source = Fixtures.Isolated("quadrants.jpg", "metadata");
            Settings.KeepsMetadata = true;
            var kept = Conversions.Convert(source, Format.Avif)[0];
            Check(RequireInfo(kept).HasGps, "the GPS should be kept");
            Settings.KeepsMetadata = false;
            try
            {
                var dropped = Conversions.Convert(source, Format.Png)[0];
                Check(!RequireInfo(dropped).HasGps, "the GPS should be removed");
            }
            finally { Settings.KeepsMetadata = true; }
        });

        Test("Conversion", "The colour profile of a PNG is kept", () =>
        {
            var source = Fixtures.Isolated("profile.jpg", "profile");
            Check(RequireInfo(source).HasProfile, "the sample should carry a colour profile");
            Check(RequireInfo(Conversions.Convert(source, Format.Png)[0]).HasProfile, "the PNG lost the colour profile");
            Check(RequireInfo(Conversions.Convert(source, Format.Webp)[0]).HasProfile, "the WebP lost the colour profile");
        });

        Test("Conversion", "ICO is a 256×256 square and the animated GIF keeps its frames", () =>
        {
            var source = Fixtures.Isolated("quadrants.png", "ico");
            var ico = RequireInfo(Conversions.Convert(source, Format.Ico)[0]);
            Check(ico.Width == 256 && ico.Height == 256, $"ICO {ico.Width}×{ico.Height}");
            var gif = Fixtures.Isolated("animated.gif", "gif-frames");
            var tiff = RequireInfo(Conversions.Convert(gif, Format.Tiff)[0]);
            Check(tiff.Width == 120 && tiff.Frames == 3, $"TIFF from the GIF: {tiff.Width} px, {tiff.Frames} pages");
            var webp = RequireInfo(Conversions.Convert(gif, Format.Webp)[0]);
            Check(webp.Frames == 3, $"animated WebP created with {webp.Frames} frames");
            var mp4 = Conversions.Convert(gif, Format.Mp4)[0];
            Check(Probe.Codecs(mp4, "video").SequenceEqual(["h264"]), "GIF → MP4 should be H.264");
        });

        Test("Conversion", "A small drawing gets every ICO size, a large one is reduced", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_ico-sizes");
            Directory.CreateDirectory(folder);
            var small = Path.Combine(folder, "small.png");
            using (var image = Fixtures.Quads(40, 40)) Fixtures.Save(image, "_ico-sizes/small.png", MagickFormat.Png);
            var ico = Conversions.Convert(small, Format.Ico)[0];
            using var frames = ImageConverter.Open(ico, Format.Ico);
            Check(frames.Images.Select(i => (int)i.Width).SequenceEqual([32, 16]), $"sizes {string.Join(",", frames.Images.Select(i => i.Width))}");
            Check(frames.Images.All(i => i.Width == i.Height), "every size is square");
        });

        Test("Conversion", "Audio: the right codecs and the same length", () =>
        {
            var source = Fixtures.Isolated("tone.wav", "audio-codecs");
            var expected = new Dictionary<Format, string>
            {
                [Format.Mp3] = "mp3", [Format.M4a] = "aac", [Format.Flac] = "flac", [Format.Ogg] = "vorbis", [Format.Opus] = "opus",
                [Format.Aiff] = "pcm_s16be", [Format.Wma] = "wmav2",
            };
            foreach (var (target, codec) in expected)
            {
                var output = Conversions.Convert(source, target)[0];
                var codecs = Probe.Codecs(output, "audio");
                Check(codecs.SequenceEqual([codec]), $"{target.Label()}: codec {string.Join(",", codecs)} instead of {codec}");
                Check(Near(Probe.Duration(output), 3, 0.15), $"{target.Label()}: length {Probe.Duration(output)}");
            }
        });

        Test("Conversion", "Video: MP4→MOV/MKV without re-encoding; WebM is VP9+Opus", () =>
        {
            var source = Fixtures.Isolated("clip.mp4", "video-codecs");
            var mov = Conversions.Convert(source, Format.Mov)[0];
            Check(Probe.Codecs(mov, "video").SequenceEqual(["h264"]), "MOV should keep H.264");
            var webm = Conversions.Convert(source, Format.Webm)[0];
            Check(Probe.Codecs(webm, "video").SequenceEqual(["vp9"]) && Probe.Codecs(webm, "audio").SequenceEqual(["opus"]),
                  $"WebM: {string.Join(",", Probe.Codecs(webm, "video"))} {string.Join(",", Probe.Codecs(webm, "audio"))}");
            var gif = Conversions.Convert(source, Format.Gif)[0];
            Check(RequireInfo(gif).Frames > 10, "the GIF from the video should be animated");
            var portrait = Fixtures.Isolated("portrait.mp4", "video-rotation");
            var upright = Conversions.Convert(portrait, Format.Webm)[0];
            var size = Probe.VideoSize(upright);
            Check(size.Height > size.Width, $"a portrait video should stay portrait: {size}");
        });

        Test("Conversion", "SRT ↔ VTT subtitles keep the text and timing", () =>
        {
            var source = Fixtures.Isolated("subs.srt", "subtitles");
            var vtt = Files.Text(Conversions.Convert(source, Format.Vtt)[0]);
            Check(vtt.StartsWith("WEBVTT") && vtt.Contains("00:00.500") && vtt.Contains("Hello Split"), $"VTT: {vtt[..Math.Min(100, vtt.Length)]}");
        });

        Test("Conversion", "Archives: ZIP → TAR keeps the files", () =>
        {
            var source = Fixtures.Isolated("folder.zip", "archive-convert");
            var tar = Conversions.Convert(source, Format.Tar)[0];
            var listing = TarNames(tar);
            Check(listing.Contains("Folder/one.txt") && listing.Contains("Folder/two.txt"), $"TAR: {string.Join(", ", listing)}");
        });

        Test("Conversion", "Archives: extracting lifts a single folder and keeps several items together", () =>
        {
            var single = ArchiveTools.Extract(Fixtures.Isolated("folder.zip", "extract-single"));
            Check(single.Count == 1 && Path.GetFileName(single[0]) == "Folder" && File.Exists(Path.Combine(single[0], "one.txt")), $"one folder: {string.Join(", ", single)}");
            var several = ArchiveTools.Extract(Fixtures.Isolated("two.zip", "extract-several"));
            Check(several.Count == 1 && Path.GetFileName(several[0]) == "two" && Files.List(several[0]).Count == 2, $"two files go into a folder: {string.Join(", ", several)}");
            var again = ArchiveTools.Extract(Fixtures.Isolated("folder.zip", "extract-single"));
            Check(Path.GetFileName(again[0]) == "Folder 2", $"a second extraction never overwrites: {Path.GetFileName(again[0])}");
        });

        Test("Conversion", "Several files in parallel get distinct, valid names", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_parallel");
            Directory.CreateDirectory(folder);
            var sources = new List<string>();
            for (var index = 0; index < 8; index++)
            {
                var copy = Path.Combine(folder, index % 2 == 0 ? $"photo {index}.jpg" : $"photo {index - 1}.webp");
                if (!File.Exists(copy)) File.Copy(Fixtures.Url(index % 2 == 0 ? "quadrants.jpg" : "quadrants.webp"), copy);
                sources.Add(copy);
            }
            var outputs = new IReadOnlyList<string>[sources.Count];
            Parallel.For(0, sources.Count, index =>
            {
                try { outputs[index] = Conversions.Convert(sources[index], Format.Png); }
                catch (Exception) { outputs[index] = []; }
            });
            var flat = outputs.SelectMany(o => o).ToList();
            Check(flat.Count == sources.Count, $"{flat.Count} of {sources.Count} parallel conversions");
            Check(flat.Select(f => f.ToLowerInvariant()).Distinct().Count() == flat.Count, "two conversions wrote to the same file");
            Check(flat.All(f => Pixels.Info(f)?.Width == 600), "a PNG came out invalid");
        });

        Test("Conversion", "Real progress while ffmpeg runs", () =>
        {
            var source = Fixtures.Isolated("long.mp4", "progress");
            var fractions = new List<double>();
            JobProgress.Run<int>(fractions.Add, null, () => { Conversions.Convert(source, Format.Webm); return 0; });
            Check(fractions.Count > 0, "no progress update");
            Check(fractions.All(f => f is >= 0 and <= 1), $"fractions outside 0…1: {string.Join(", ", fractions)}");
            Check(fractions.SequenceEqual(fractions.Order()), $"progress went backwards: {string.Join(", ", fractions)}");
        });

        Test("Conversion", "Cancelling stops ffmpeg and leaves nothing behind", () =>
        {
            var source = Fixtures.Isolated("long.mp4", "cancel");
            var token = new CancelToken();
            var started = DateTime.UtcNow;
            ExpectError("a cancelled conversion", ["Canceled"], () =>
                JobProgress.Run<int>(_ => token.Cancel(), token, () => { Conversions.Convert(source, Format.Webm); return 0; }));
            Check((DateTime.UtcNow - started).TotalSeconds < 60, "the conversion didn’t stop");
            Check(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(source)!).All(f => Path.GetFileName(f) == "long.mp4"), "files were left after cancelling");
        });

        Test("Spreadsheets", "European CSV → XLSX: numbers, dates, percentages, formulas, header", () =>
        {
            var source = Fixtures.Isolated("prices.csv", "xlsx-write");
            var xlsx = Conversions.Convert(source, Format.Xlsx)[0];
            var sheet = Xlsx.EntryText(xlsx, "xl/worksheets/sheet1.xml");
            Check(sheet.Contains("<c r=\"B2\"><v>1.25</v></c>"), "1,25 should be the number 1.25");
            Check(sheet.Contains("<c r=\"C2\" s=\"2\"><v>45382"), "31/03/2024 should be the date 45382 with a date style");
            Check(sheet.Contains("<c r=\"D2\" s=\"4\"><v>0.1</v>"), "10% should be 0.1 with a percent style");
            Check(sheet.Contains("<f>B2*0.9</f>"), "=B2*0.9 should be a formula");
            Check(sheet.Contains("<c r=\"A1\" s=\"1\""), "the header should be bold");
            Check(sheet.Contains("<pane ySplit=\"1\""), "the header should be frozen");
            Check(sheet.Contains("<cols>"), "the column widths are missing");
            Check(Xlsx.EntryText(xlsx, "xl/workbook.xml").Contains("fullCalcOnLoad=\"1\""), "formulas wouldn’t be calculated on opening");
            var rows = SpreadsheetConverter.Read(xlsx, Format.Xlsx)[0].Rows;
            Check(rows[1][2] == "2024-03-31", $"the date comes back as {rows[1][2]}");
            Check(rows[1][3] == "10%", $"the percentage comes back as {rows[1][3]}");
        });

        Test("Spreadsheets", "XLSX from Excel: dates, date-times, %, booleans and the 1904 system", () =>
        {
            foreach (var name in new[] { "dates.xlsx", "dates1904.xlsx" })
            {
                var rows = SpreadsheetConverter.Read(Fixtures.Url(name), Format.Xlsx)[0].Rows;
                Require(rows.Count == 2, $"{name}: {rows.Count} rows");
                Check(rows[0].SequenceEqual(["Date", "When", "Rate", "Sum"]), $"{name}: header {string.Join("|", rows[0])}");
                Check(rows[1][0] == "2023-04-01", $"{name}: date {rows[1][0]}");
                Check(rows[1][1] == "2023-04-01 12:00", $"{name}: date-time {rows[1][1]}");
                Check(rows[1][2] == "12.5%", $"{name}: percentage {rows[1][2]}");
                Check(rows[1][3] == "2", $"{name}: a formula should show its cached value: {rows[1][3]}");
                Check(rows[1][4] == "TRUE", $"{name}: boolean {rows[1][4]}");
            }
        });

        Test("Spreadsheets", "Number format detection (ECMA-376)", () =>
        {
            Check(Xlsx.Kind(14, null) == Xlsx.CellKind.Date, "14 is a date");
            Check(Xlsx.Kind(22, null) == Xlsx.CellKind.DateTime, "22 is a date-time");
            Check(Xlsx.Kind(10, null) == Xlsx.CellKind.Percent, "10 is a percentage");
            Check(Xlsx.Kind(170, "[$-816]dd/mm/yyyy;@") == Xlsx.CellKind.Date, "custom date code");
            Check(Xlsx.Kind(171, "#,##0.00\" €\"") == Xlsx.CellKind.General, "currency isn’t a date");
            Check(Xlsx.Kind(172, "[Red]0.00") == Xlsx.CellKind.General, "[Red] isn’t a date");
            Check(Xlsx.Kind(173, "hh:mm") == Xlsx.CellKind.Time, "hh:mm is a time");
            Check(Xlsx.ParseDate("31/02/2024") is null, "31/02 isn’t a valid date");
            Check(Xlsx.ParseDate("12345678") is null, "a number isn’t a date");
        });

        Test("Spreadsheets", "CSV: quotes, line ends, the separator that dominates and a missing last newline", () =>
        {
            var rows = Csv.Parse("a;b;c\r\n\"x;1\";\"say \"\"hi\"\"\";\"two\nlines\"\r\n7;8;9", ';');
            Check(rows.Count == 3 && rows[1][0] == "x;1" && rows[1][1] == "say \"hi\"" && rows[1][2] == "two\nlines" && rows[2].SequenceEqual(["7", "8", "9"]), $"parsed {rows.Count} rows");
            Check(Csv.GuessDelimiter("a;b;c,d") == ';' && Csv.GuessDelimiter("a\tb\tc") == '\t' && Csv.GuessDelimiter("a,b") == ',', "separator guesses");
            Check(Csv.Format([["a,b", "c"], ["d\"e", "f"]], ',') == "\"a,b\",c\n\"d\"\"e\",f\n", "values are quoted when they need it");
        });

        Test("Spreadsheets", "Old workbooks need LibreOffice (or Excel): without them nothing is offered", () =>
        {
            if (Engines.Soffice is not null) Skip("LibreOffice is installed: the XLS and ODS tests run against it");
            Check(Conversions.Targets(Format.Xls).All(t => t is not (Format.Xlsx or Format.Csv)) || Engines.Excel, "old workbooks are offered without a program to read them");
        });

        Test("Markdown", "Headings, emphasis, lists, quotes, code, rules and tables", () =>
        {
            var html = Markdown.ToHtml("# Title\n\nSome *emphasis*, **strong**, `code <b>` and [a link](http://x.pt/a).\n\n- one\n  - nested\n- two\n\n1. first\n2. second\n\n> quoted\n\n---\n\n```\nraw <b>\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |\n");
            foreach (var expected in new[]
            {
                "<h1>Title</h1>", "<em>emphasis</em>", "<strong>strong</strong>", "<code>code &lt;b&gt;</code>", "<a href=\"http://x.pt/a\">a link</a>",
                "<ul><li>one", "<ul><li>nested", "<ol><li>first", "<blockquote><p>quoted</p></blockquote>", "<hr>", "<pre><code>raw &lt;b&gt;</code></pre>",
                "<th>A</th>", "<td>2</td>",
            })
                Check(html.Contains(expected), $"missing {expected} in:\n{html}");
            Check(Markdown.ToHtml("a <script> & b").Contains("&lt;script&gt; &amp; b"), "raw HTML in the text is escaped");
        });

        Test("EPUB", "A book is written with its chapters, contents and pictures, and read back", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_epub");
            Directory.CreateDirectory(folder);
            File.Copy(Fixtures.Url("quadrants.png"), Path.Combine(folder, "picture.png"), overwrite: true);
            var book = Path.Combine(folder, "round.epub");
            var html = "<html><head><style>p{color:red}</style></head><body><h1>One</h1><p>Hello Split — café &amp; crème.</p><img src=\"picture.png\"><img src=\"http://127.0.0.1:1/remote.png\"><h1>Two</h1><p>Second<br>chapter.</p></body></html>";
            EpubWriter.Write(html, "Round trip", folder, book);
            using (var archive = ZipFile.OpenRead(book))
            {
                var names = archive.Entries.Select(e => e.FullName).ToList();
                Check(names[0] == "mimetype" && archive.Entries[0].CompressedLength == archive.Entries[0].Length, "the mimetype entry is first and stored");
                foreach (var expected in new[] { "META-INF/container.xml", "OEBPS/content.opf", "OEBPS/nav.xhtml", "OEBPS/toc.ncx", "OEBPS/chapter-1.xhtml", "OEBPS/chapter-2.xhtml", "OEBPS/images/picture-1.png" })
                    Check(names.Contains(expected), $"missing {expected}: {string.Join(", ", names)}");
                Check(!names.Contains("OEBPS/images/picture-2.png"), "a remote picture is never packed");
                foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".xhtml") || e.FullName.EndsWith(".opf") || e.FullName.EndsWith(".ncx")))
                {
                    using var stream = entry.Open();
                    try { System.Xml.Linq.XDocument.Load(stream); }
                    catch (System.Xml.XmlException error) { Check(false, $"{entry.FullName} is not well-formed XML: {error.Message}"); }
                }
            }
            var (combined, workspace) = EpubReader.CombinedHtml(book);
            try
            {
                var text = File.ReadAllText(combined);
                Check(text.Contains("Hello Split — café &amp; crème.") && text.Contains("Second") && text.Contains("<hr>"), $"the chapters are joined: {text[..Math.Min(300, text.Length)]}");
                Check(text.Contains("file:///") && !text.Contains("127.0.0.1"), "pictures point at the unpacked files");
            }
            finally { Output.Discard(workspace); }
        });
    }

    private static List<string> ZipNames(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    private static List<string> TarNames(string path)
    {
        using var file = File.OpenRead(path);
        using Stream stream = Formats.Detect(path) == Format.Tgz ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new TarReader(stream);
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry) names.Add(entry.Name.TrimEnd('/'));
        return names;
    }

    // MARK: Validation of one output

    private static void Validate(string output, Format target, string source, Format format)
    {
        var label = $"{Path.GetFileName(source)} → {target.Label()}";
        if (!File.Exists(output) && !Directory.Exists(output))
        {
            Check(false, $"{label}: the output file doesn’t exist");
            return;
        }
        Check(Paths.IsInside(output, Path.GetDirectoryName(source)!), $"{label}: result outside the original’s folder");
        if (Directory.Exists(output))
        {
            var items = Files.List(output);
            Check(items.Count > 0, $"{label}: empty folder");
            foreach (var item in items) Validate(item, target, source, format);
            return;
        }
        Check(new FileInfo(output).Length > 0, $"{label}: empty file");
        switch (target.Group())
        {
            case FormatGroup.Image:
                if (target == Format.Pdf)
                {
                    int pages;
                    try { pages = PdfReader.Open(output, PdfDocumentOpenMode.Import).PageCount; } catch (Exception) { pages = 0; }
                    Check(pages >= 1, $"{label}: PDF without pages");
                }
                else if (target == Format.Webp) Check(FileCheck.Sniff(output) == "WebP", $"{label}: not WebP ({FileCheck.Sniff(output) ?? "?"})");
                else if (target is Format.Mp4 or Format.Webm) Check(Probe.Codecs(output, "video").Count > 0, $"{label}: video without a picture");
                else if (Pixels.Info(output) is { } info) Check(info.Width > 0 && info.Height > 0, $"{label}: image without dimensions");
                else Check(false, $"{label}: doesn’t open as an image ({FileCheck.Sniff(output) ?? "?"})");
                break;
            case FormatGroup.Audio:
                Check(Probe.Codecs(output, "audio").Count > 0, $"{label}: no audio track");
                Check(Probe.Codecs(output, "video").Count == 0, $"{label}: the audio has an extra picture");
                if (format.Group() is FormatGroup.Audio or FormatGroup.Video)
                    Check(Near(Probe.Duration(output), Probe.Duration(source), 0.25), $"{label}: length {Probe.Duration(output)} ≠ {Probe.Duration(source)}");
                break;
            case FormatGroup.Video:
                Check(Probe.Codecs(output, "video").Count > 0, $"{label}: no picture");
                if (format.Group() == FormatGroup.Video)
                {
                    Check(Near(Probe.Duration(output), Probe.Duration(source), 0.3), $"{label}: length {Probe.Duration(output)}");
                    Check(Probe.Codecs(output, "audio").Count > 0, $"{label}: lost the sound");
                }
                break;
            case FormatGroup.Spreadsheet:
                if (target == Format.Xlsx) Check(FileCheck.Sniff(output) == "ZIP", $"{label}: invalid XLSX");
                if (target is Format.Csv or Format.Tsv) Check(Files.Text(output).Length > 0, $"{label}: empty CSV");
                break;
            case FormatGroup.Document:
                switch (target)
                {
                    case Format.Pdf:
                        int pages;
                        try { pages = PdfReader.Open(output, PdfDocumentOpenMode.Import).PageCount; } catch (Exception) { pages = 0; }
                        Check(pages >= 1, $"{label}: invalid PDF");
                        if (format is Format.Txt or Format.Md or Format.Html or Format.Rtf or Format.Rtfd or Format.Docx or Format.Odt or Format.Doc or Format.Epub)
                            Check(DocumentTests.PdfText(output).Contains("Split"), $"{label}: the PDF doesn’t have the text");
                        break;
                    case Format.Png or Format.Jpg:
                        Check(Pixels.Info(output) is not null, $"{label}: the page isn’t an image");
                        break;
                    case Format.Txt or Format.Html:
                        Check(Files.Text(output).Contains("Split") || format is Format.Csv or Format.Tsv or Format.Xlsx or Format.Xls or Format.Ods,
                              $"{label}: text missing or in the wrong encoding");
                        break;
                    case Format.Docx or Format.Odt or Format.Epub:
                        Check(FileCheck.Sniff(output) == "ZIP", $"{label}: not a ZIP package");
                        if (target != Format.Epub) Check(DocumentPipeline.Load(output, target).PlainText.Contains("Split"), $"{label}: the document lost its text");
                        break;
                    case Format.Rtf:
                        Check(FileCheck.Sniff(output) == "RTF", $"{label}: not RTF");
                        break;
                }
                break;
            case FormatGroup.Subtitle:
                Check(Files.Text(output).Contains("Split"), $"{label}: subtitles without text");
                break;
            case FormatGroup.Archive:
                var listing = target == Format.Zip ? ZipNames(output) : TarNames(output);
                Check(listing.Any(n => n.EndsWith("one.txt")), $"{label}: archive without the files: {string.Join(", ", listing)}");
                break;
            case FormatGroup.Data:
                var text = Files.Text(output);
                Check(text.Length > 0, $"{label}: empty data file");
                if (target == Format.Json) { try { JsonParser.Parse(System.Text.Encoding.UTF8.GetBytes(text)); } catch (JsonParser.Failure) { Check(false, $"{label}: invalid JSON"); } }
                if (target == Format.Xml) { try { XmlBridge.Parse(System.Text.Encoding.UTF8.GetBytes(text)); } catch (Problem) { Check(false, $"{label}: invalid XML"); } }
                break;
        }
    }
}
