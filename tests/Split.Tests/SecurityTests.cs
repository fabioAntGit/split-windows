using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>
/// The local-only guarantee and hostile inputs: nothing may reach the network, escape the
/// destination folder, run commands through a file name, or expand without limit. Every remote
/// reference points at a local tripwire, so "0 connections" is measured, not assumed.
/// </summary>
public static class SecurityTests
{
    public static void Register()
    {
        var wire = new Tripwire();
        var net = wire.Base;

        // Runs the body and fails if anything connected to the tripwire meanwhile (late loads included).
        void Offline(string what, Action body)
        {
            var before = wire.Connections;
            body();
            Thread.Sleep(400);
            var made = wire.Connections - before;
            var paths = wire.RequestsSince(before).Select(line => line.Split(' ').Skip(1).FirstOrDefault() ?? line).Distinct().Order();
            Check(made == 0, $"{what}: {made} network connection(s): {string.Join(", ", paths)}");
        }

        // Every destination the wheel offers; returns what failed.
        List<string> ConvertAll(string path)
        {
            if (Formats.Detect(path) is not { } format) return ["unknown format"];
            var failures = new List<string>();
            foreach (var target in Conversions.Targets(format))
            {
                try { Conversions.Convert(path, target); }
                catch (Exception error) { failures.Add($"{target.Label()}: {Explain.Error(error).Message}"); }
            }
            return failures;
        }

        Test("Security", "The network tripwire detects connections (control)", () =>
            Check(wire.SelfTest(), "the tripwire didn’t count the control connection, so the other tests would prove nothing"));

        Test("Security", "Hostile Markdown (images and embedded HTML) without touching the network", () =>
        {
            var markdown = $"# Title\n\n![picture]({net}/md-image.png)\n\n<img src=\"{net}/md-raw.png\"><link rel=\"stylesheet\" href=\"{net}/md.css\">\n<iframe src=\"{net}/md-frame\"></iframe>\n\nA paragraph with a [link]({net}/link) that must not be followed.";
            Offline("Markdown", () =>
            {
                var clean = LocalOnly.Html(Markdown.Document(markdown, "hostile"));
                // Raw HTML in Markdown is shown as text, and the picture syntax becomes an img: none may keep an address.
                foreach (var tag in new[] { "img", "link", "iframe", "script", "source" })
                    Check(!System.Text.RegularExpressions.Regex.IsMatch(clean, $@"<{tag}\b[^>]*127\.0\.0\.1"), $"a <{tag}> still refers to the tripwire: {clean}");
                Check(clean.Contains("<h1>Title</h1>") && clean.Contains($"<a href=\"{net}/link\">link</a>"), "the text and the plain link survive");
            });
        });

        Test("Security", "Hostile SVG (image, use, @import, foreignObject) without touching the network", () =>
        {
            var svg = Hostile.File("hostile.svg", "svg", $$"""
                <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="200" height="100" viewBox="0 0 200 100">
                  <style>@import url({{net}}/svg.css); .x { fill: url({{net}}/fill.svg#g); }</style>
                  <image href="{{net}}/a.png" width="50" height="50"/>
                  <image xlink:href="{{net}}/b.png" x="50" width="50" height="50"/>
                  <use href="{{net}}/sprite.svg#icon"/>
                  <rect x="100" width="100" height="100" fill="#ff8800"/>
                  <foreignObject width="10" height="10"><iframe xmlns="http://www.w3.org/1999/xhtml" src="{{net}}/fo"></iframe></foreignObject>
                </svg>
                """);
            Offline("SVG", () =>
            {
                var failures = ConvertAll(svg);
                Check(failures.Count == 0, $"conversions failed: {string.Join("; ", failures)}");
            });
            var png = Path.ChangeExtension(svg, "png");
            using var image = Pixels.Load(png);
            if (image is null) { Check(false, "SVG → PNG produced no image"); return; }
            var pixel = Pixels.At(image, (int)image.Width * 3 / 4, (int)image.Height / 2);
            Check(pixel[0] > 200 && pixel[1] > 100 && pixel[1] < 170 && pixel[2] < 60, $"the local drawing should still show: {string.Join(",", pixel)}");
        });

        Test("Security", "Hostile EPUB (images, CSS and an external entity) without touching the network", () =>
        {
            Dictionary<string, string> Book(bool entity) => new()
            {
                ["META-INF/container.xml"] = """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""",
                ["OEBPS/content.opf"] = (entity ? $"""<?xml version="1.0"?><!DOCTYPE package [<!ENTITY remote SYSTEM "{net}/xxe-opf">]>""" : "<?xml version=\"1.0\"?>")
                    + $"""<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">x</dc:identifier><dc:title>Book {(entity ? "&remote;" : "")}</dc:title><dc:language>en</dc:language></metadata><manifest><item id="c1" href="ch1.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/></spine></package>""",
                ["OEBPS/ch1.xhtml"] = $"""<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" "{net}/xhtml11.dtd"><html xmlns="http://www.w3.org/1999/xhtml"><head><title>C</title><link rel="stylesheet" href="{net}/epub.css"/><link rel="stylesheet" href="local.css"/></head><body><h1>Chapter</h1><p>The book’s text.</p><img src="{net}/epub.png"/><p style="background:url({net}/epub-bg.png)">end</p></body></html>""",
                ["OEBPS/local.css"] = $"@import url({net}/epub-import.css); h1 {{ color: #c50; background: url('{net}/epub-css-bg.png'); }}",
            };
            var stored = new Dictionary<string, string> { ["mimetype"] = "application/epub+zip" };
            Offline("EPUB", () =>
            {
                // A book whose package file defines an entity is refused with an explanation.
                var hostile = Hostile.Package("hostile.epub", "epub", Book(entity: true), stored);
                ExpectError("an EPUB with an external entity", ["EPUB"], () => EpubReader.CombinedHtml(hostile));
                // The same book without it opens; its chapter, with a DOCTYPE that points at the network, is read without following anything.
                var book = Hostile.Package("book.epub", "epub", Book(entity: false), stored);
                var (combined, workspace) = EpubReader.CombinedHtml(book);
                try
                {
                    var clean = LocalOnly.Html(File.ReadAllText(combined), workspace);
                    Check(clean.Contains("The book’s text."), "the text survives");
                    Check(!clean.Contains("127.0.0.1"), $"the clean-up left a remote reference: {clean}");
                }
                finally { Output.Discard(workspace); }
            });
        });

        Test("Security", "Playlists disguised as video don’t make ffmpeg go online", () =>
        {
            var playlists = new[]
            {
                ("playlist.mp4", $"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n{net}/seg0.ts\n#EXT-X-ENDLIST\n"),
                ("concat.mov", $"ffconcat version 1.0\nfile '{net}/a.mp4'\n"),
                ("playlist.mp3", $"#EXTM3U\n#EXTINF:2,\n{net}/audio.mp3\n"),
            };
            Offline("disguised playlists", () =>
            {
                foreach (var (name, content) in playlists)
                {
                    var file = Hostile.File(name, "playlists", content);
                    foreach (var target in new[] { Format.Mp3, Format.Mov, Format.Gif })
                    {
                        try
                        {
                            Conversions.Convert(file, target);
                            Check(false, $"{name} → {target.Label()} should fail: it isn’t a real video");
                        }
                        catch (Exception error) { Check(Explain.Error(error).FullText.Length > 25, $"{name}: an error without an explanation"); }
                    }
                    try { MediaConverter.ProbeFile(file); } catch (Problem) { }
                    // The media editor reads the same file for its player, filmstrip and waveform.
                    using (var model = new MediaEditorModel(file)) model.Loaded.Wait();
                }
            });
        });

        Test("Security", "XLSX with external entities (XXE) reads no files and doesn’t go online", () =>
        {
            var xlsx = Hostile.Spreadsheet("xxe.xlsx", $"""
                <?xml version="1.0" encoding="UTF-8"?><!DOCTYPE worksheet [<!ENTITY remote SYSTEM "{net}/xxe"><!ENTITY passwd SYSTEM "file:///C:/Windows/win.ini">]>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>Name</t></is></c><c r="B1" t="inlineStr"><is><t>Value</t></is></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>a&remote;</t></is></c><c r="B2" t="inlineStr"><is><t>b&passwd;</t></is></c></row>
                </sheetData></worksheet>
                """);
            Offline("XXE", () =>
            {
                foreach (var target in Conversions.Targets(Format.Xlsx))
                {
                    try
                    {
                        foreach (var output in Conversions.Convert(xlsx, target).Where(_ => target is Format.Csv or Format.Tsv or Format.Html))
                            Check(!Files.Text(output).Contains("[fonts]", StringComparison.OrdinalIgnoreCase), $"{target.Label()}: the content of win.ini got into the result");
                    }
                    catch (Exception error) { Check(Explain.Error(error).FullText.Length > 25, $"{target.Label()}: an error without an explanation"); }
                }
            });
        });

        Test("Security", "An XLSX “billion laughs” ends quickly instead of using up memory", () =>
        {
            var entities = "<!ENTITY l0 \"ha\">";
            for (var level = 1; level <= 9; level++) entities += $"<!ENTITY l{level} \"" + string.Concat(Enumerable.Repeat($"&l{level - 1};", 10)) + "\">";
            var xlsx = Hostile.Spreadsheet("laughs.xlsx", $"""
                <?xml version="1.0" encoding="UTF-8"?><!DOCTYPE worksheet [{entities}]>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>&l9;</t></is></c></row></sheetData></worksheet>
                """);
            long size = 0;
            var done = Task.Run(() =>
            {
                try { size = new FileInfo(Conversions.Convert(xlsx, Format.Csv)[0]).Length; } catch (Problem) { }
            }).Wait(TimeSpan.FromSeconds(20));
            Check(done, "the conversion didn’t finish in 20 s (unbounded entity expansion)");
            Check(size < 10_000_000, $"the result has {size} bytes: the entities were expanded");
        });

        Test("Security", "Archives with paths out of the folder (zip slip) stay contained", () =>
        {
            var dir = Hostile.Folder("archives");
            var victim = Path.Combine(dir, "victim");
            Directory.CreateDirectory(victim);
            var absolute = Path.Combine(dir, "absolute.txt").Replace('\\', '/');

            static void Regular(TarWriter tar, string name) =>
                tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream("x"u8.ToArray()) });
            static void Link(TarWriter tar, string name, string target) =>
                tar.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, name) { LinkName = target });
            void Tar(string name, bool gzip, Action<TarWriter> write)
            {
                File.Delete(Path.Combine(dir, name));
                using var file = File.Create(Path.Combine(dir, name));
                using Stream stream = gzip ? new GZipStream(file, CompressionLevel.Optimal) : file;
                using var tar = new TarWriter(stream, TarEntryFormat.Ustar);
                write(tar);
            }
            File.Delete(Path.Combine(dir, "escape.zip"));
            using (var zip = ZipFile.Open(Path.Combine(dir, "escape.zip"), ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("../../outside-zip.txt").Open())) writer.Write("x");
                using (var writer = new StreamWriter(zip.CreateEntry("ok.txt").Open())) writer.Write("ok");
            }
            Tar("escape.tar.gz", true, tar => { Regular(tar, "../../outside-tar.txt"); Regular(tar, absolute); Regular(tar, "ok.txt"); });
            Tar("link.tar", false, tar => { Link(tar, "link", victim.Replace('\\', '/')); Regular(tar, "link/inside.txt"); Regular(tar, "ok.txt"); });
            Tar("only-link.tar", false, tar => { Link(tar, "Folder/out", "C:/Windows"); Regular(tar, "Folder/ok.txt"); });
            File.Delete(Path.Combine(dir, "link.zip"));
            using (var zip = ZipFile.Open(Path.Combine(dir, "link.zip"), ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("link");
                entry.ExternalAttributes = 0b1010_0001_1111_1111 << 16; // a symbolic link, mode 777
                using (var writer = new StreamWriter(entry.Open())) writer.Write(victim);
                using (var writer = new StreamWriter(zip.CreateEntry("link/inside-zip.txt").Open())) writer.Write("x");
            }
            foreach (var name in new[] { "escape.zip", "escape.tar.gz", "link.tar", "only-link.tar", "link.zip" })
            {
                try
                {
                    foreach (var result in ArchiveTools.Extract(Path.Combine(dir, name)))
                    {
                        Check(Paths.IsInside(result, dir), $"{name}: extracted outside the folder: {result}");
                        var links = Directory.EnumerateFileSystemEntries(result, "*", SearchOption.AllDirectories)
                            .Where(item => File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint)).ToList();
                        Check(links.Count == 0, $"{name}: a link out of the archive was left: {string.Join(", ", links)}");
                    }
                }
                catch (Exception error)
                {
                    var message = Explain.Error(error).FullText;
                    Check(message.Contains("outside the destination folder") || message.Contains("damaged"), $"{name}: {message}");
                }
            }
            var root = Path.GetDirectoryName(Fixtures.Root)!;
            foreach (var leaked in new[] { "outside-zip.txt", "outside-tar.txt" })
                foreach (var folder in new[] { Path.GetDirectoryName(dir)!, Fixtures.Root, root, Path.GetDirectoryName(root)! })
                    Check(!File.Exists(Path.Combine(folder, leaked)), $"{leaked} escaped to {folder}");
            Check(!File.Exists(Path.Combine(dir, "absolute.txt")), "an absolute path inside the TAR was written outside the folder");
            Check(Files.List(victim).Count == 0, $"a link inside the archive wrote into the victim folder: {string.Join(", ", Files.List(victim).Select(Path.GetFileName))}");
        });

        Test("Security", "File names with shell characters run nothing", () =>
        {
            var dir = Hostile.Folder("names");
            var names = new (string Name, string Sample, Format Target)[]
            {
                ("$(copy NUL PWNED1).jpg", "quadrants.jpg", Format.Png),
                ("`copy NUL PWNED2`.png", "quadrants.png", Format.Webp),
                ("a&copy NUL PWNED3&b ^ %COMSPEC% !x!.png", "quadrants.png", Format.Jpg),
                ("-y.wav", "tone.wav", Format.Mp3),
                ("-i.mp4", "clip.mp4", Format.Gif),
                ("name & ; ' ^ % ! ação 日本.mp3", "tone.mp3", Format.Wav),
                ("--help.srt", "subs.srt", Format.Vtt),
                ("a b & c.zip", "folder.zip", Format.Tar),
            };
            foreach (var (name, sample, target) in names)
            {
                var file = Path.Combine(dir, name);
                if (!File.Exists(file)) File.Copy(Fixtures.Url(sample), file);
                try
                {
                    var outputs = Conversions.Convert(file, target);
                    Check(outputs.All(o => Path.GetDirectoryName(o) == dir), $"{name}: result outside the folder");
                }
                catch (Exception error) { Check(false, $"{name} → {target.Label()}: {Explain.Error(error).FullText}"); }
            }
            foreach (var folder in new[] { dir, Directory.GetCurrentDirectory(), Fixtures.Root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
            {
                var pwned = Directory.EnumerateFileSystemEntries(folder, "PWNED*").ToList();
                Check(pwned.Count == 0, $"a file name ran a command: {string.Join(", ", pwned)}");
            }
        });

        Test("Security", "ffmpeg only opens local files in every command", () =>
        {
            // Every "-i" the app passes goes through MediaConverter.LocalInput (protocol whitelist "file").
            foreach (var file in Hostile.Sources())
            {
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!lines[index].Contains("\"-i\"")) continue;
                    // lavfi inputs are ffmpeg's own generators (silence, colour), not files or URLs.
                    Check(lines[index].Contains("protocol_whitelist") || lines[index].Contains("\"lavfi\""), $"{Path.GetFileName(file)}:{index + 1} passes -i without the local protocol whitelist");
                }
            }
            Check(MediaConverter.LocalInput("C:\\temp\\a.mp4").SequenceEqual(["-protocol_whitelist", "file", "-i", "C:\\temp\\a.mp4"]), "LocalInput changed");
            Check(Hostile.Sources().Count > 20, $"the source files weren’t found: {Hostile.Sources().Count}");
        });

        Test("Security", "Concat list entries read back as the path they were made from", () =>
        {
            Check(MediaConverter.ConcatEntry("C:\\Videos\\it's mine.mp4") == "file 'C:/Videos/it'\\''s mine.mp4'", MediaConverter.ConcatEntry("C:\\Videos\\it's mine.mp4"));
            Check(MediaConverter.ConcatEntry("D:\\a & b ^ c.mp4") == "file 'D:/a & b ^ c.mp4'", "shell characters stay as they are");
        });

        Test("Security", "HTML clean-up: single cases", () =>
        {
            var cases = new (string Input, string Leaked)[]
            {
                ("<img src=\"http://x.pt/a.png\">", "x.pt"),
                ("<IMG SRC='https://x.pt/a.png'>", "x.pt"),
                ("<img src=//x.pt/a.png>", "x.pt"),
                ("<link rel=stylesheet href=\"https://x.pt/s.css\">", "x.pt"),
                ("<style>@import url('https://x.pt/s.css');</style>", "x.pt"),
                ("<div style=\"background:url(https://x.pt/b.png)\">", "x.pt"),
                ("<script>fetch('https://x.pt')</script>", "fetch"),
                ("<iframe src=\"https://x.pt\"></iframe>", "iframe"),
                ("<base href=\"https://x.pt/\">", "x.pt"),
                ("<meta http-equiv=\"refresh\" content=\"0;url=https://x.pt\">", "x.pt"),
                ("<img srcset=\"a.png 1x, https://x.pt/b.png 2x\">", "x.pt"),
                ("<img src=\"&#104;ttps://x.pt/a.png\">", "x.pt"),
                ("<img src=\"&#x68;ttps&colon;//x.pt/a.png\">", "x.pt"),
                ("<img src=\"ht&#9;tps://x.pt/a.png\">", "x.pt"),
                ("<img/src=\"https://x.pt/a.png\">", "x.pt"),
                ("<image href=\"https://x.pt/a.png\"/>", "x.pt"),
                ("<div style=\"background:url(\\68 ttps://x.pt/b.png)\">", "x.pt"),
                ("<div style=\"background-image:image-set('https://x.pt/b.png' 1x)\">", "x.pt"),
                ("<style>@import 'https://x.pt/s.css';</style>", "x.pt"),
                ("<img src=x onerror=\"fetch('https://x.pt')\">", "x.pt"),
                ("<embed src=\"https://x.pt/e\">", "x.pt"),
            };
            foreach (var (input, leaked) in cases)
            {
                var clean = LocalOnly.Html(input);
                Check(!clean.ToLowerInvariant().Contains(leaked), $"still refers to {leaked}: {input} → {clean}");
            }
            var local = "<p class=\"a>b\">Hello <img src=\"photo.png\" alt='Photo > all'> <a href=\"https://x.pt\">link</a> &amp; 5 &lt; 6</p>";
            Check(LocalOnly.Html(local) == local, $"the clean-up changed HTML with nothing remote: {LocalOnly.Html(local)}");
            Check(LocalOnly.Html("<img src=\"data:image/png;base64,AAAA\">").Contains("data:"), "embedded pictures (data:) are local");
            Check(LocalOnly.IsRemote("HTTP://x") && LocalOnly.IsRemote("//x") && LocalOnly.IsRemote("http:x.pt") && !LocalOnly.IsRemote("folder/x.png")
                  && !LocalOnly.IsRemote("file:///x") && !LocalOnly.IsRemote("pictures/ação.png"), "IsRemote");
            var prose = "<img src=\"a.png\" alt=\"Note: a chart\" title=\"Source: Eurostat, 2024\">";
            Check(LocalOnly.Html(prose) == prose, $"text with a colon isn’t an address: {LocalOnly.Html(prose)}");
            Check(!LocalOnly.Html("<meta property=\"og:image\" content=\"https://x.pt/i.png\">").Contains("x.pt"), "any attribute with an address goes");
            Check(LocalOnly.Html("<link rel=\"icon\" href=\"favicon.png\">").Length == 0, "a <link> that isn’t a stylesheet goes");
            Check(!LocalOnly.Svg("<image href=\"https://x.pt/a.png\"/>").Contains("x.pt"), "SVG <image href>");
        });

        Test("Security", "Local stylesheets are inlined cleaned, three levels deep at most", () =>
        {
            var page = Hostile.File("page.html", "stylesheets", $"<html><head><link rel=\"stylesheet\" href=\"style.css\"></head><body><p>x</p></body></html>");
            Hostile.Stylesheets(net, page);
            var clean = LocalOnly.Html(File.ReadAllText(page), Path.GetDirectoryName(page));
            Check(clean.Contains("<style>") && !clean.Contains("127.0.0.1") && !clean.Contains("<link"), $"the local stylesheet is inlined and its remote references are gone: {clean}");
        });

        Test("Security", "Hostile HTML → every target without touching the network", () =>
        {
            var page = Hostile.File("hostile.html", "html", Hostile.Html(net));
            Hostile.Stylesheets(net, page);
            Offline("HTML", () =>
            {
                var failures = ConvertAll(page);
                Check(failures.Count == 0, $"conversions failed: {string.Join("; ", failures)}");
            });
            Check(Files.Text(Path.ChangeExtension(page, "txt")).Contains("Text that must survive"), "the page’s text should survive the clean-up");
        });

        Test("Security", "Hostile EPUB, ODT and Markdown files → every target without touching the network", () =>
        {
            var book = Hostile.Package("chapters.epub", "documents", new()
            {
                ["META-INF/container.xml"] = """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""",
                ["OEBPS/content.opf"] = """<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">x</dc:identifier><dc:title>Book</dc:title><dc:language>en</dc:language></metadata><manifest><item id="c1" href="ch1.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/></spine></package>""",
                ["OEBPS/ch1.xhtml"] = $"""<?xml version="1.0" encoding="UTF-8"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>C</title><link rel="stylesheet" href="{net}/epub.css"/></head><body><h1>Chapter</h1><p>The book’s text.</p><img src="{net}/epub.png"/><p style="background:url({net}/epub-bg.png)">end</p></body></html>""",
            }, new() { ["mimetype"] = "application/epub+zip" });
            const string odf = "urn:oasis:names:tc:opendocument:xmlns";
            var odt = Hostile.Package("remote.odt", "documents", new()
            {
                ["content.xml"] = $"""<?xml version="1.0" encoding="UTF-8"?><office:document-content xmlns:office="{odf}:office:1.0" xmlns:text="{odf}:text:1.0" xmlns:draw="{odf}:drawing:1.0" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:svg="{odf}:svg-compatible:1.0"><office:body><office:text><text:p>An OpenDocument text.</text:p><text:p><draw:frame svg:width="2cm" svg:height="2cm"><draw:image xlink:href="{net}/odt.png"/></draw:frame><draw:frame svg:width="2cm" svg:height="2cm"><draw:image xlink:href="../../outside.png"/></draw:frame></text:p></office:text></office:body></office:document-content>""",
            }, new() { ["mimetype"] = "application/vnd.oasis.opendocument.text" });
            var markdown = Hostile.File("remote.md", "documents", $"# Title\n\n![picture]({net}/md-image.png)\n\n<img src=\"{net}/md-raw.png\">\n\n[link]({net}/link)\n");
            Offline("EPUB, ODT and Markdown", () =>
            {
                foreach (var file in new[] { book, odt, markdown })
                {
                    var failures = ConvertAll(file);
                    Check(failures.Count == 0, $"{Path.GetFileName(file)}: {string.Join("; ", failures)}");
                }
            });
        });

        Test("Security", "DOCX and RTF with pictures linked to the Internet (canary tokens) without touching the network", () =>
        {
            const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var docx = Hostile.Package("canary.docx", "docx", new()
            {
                ["[Content_Types].xml"] = """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""",
                ["_rels/.rels"] = $"""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{r}/officeDocument" Target="word/document.xml"/></Relationships>""",
                ["word/_rels/document.xml.rels"] = $"""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId9" Type="{r}/image" Target="{net}/canary.png" TargetMode="External"/><Relationship Id="rId8" Type="{r}/hyperlink" Target="{net}/link" TargetMode="External"/></Relationships>""",
                ["word/document.xml"] = $"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="{w}" xmlns:r="{r}" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><w:body><w:p><w:r><w:t>A document with a remote picture.</w:t></w:r></w:p><w:p><w:r><w:drawing><wp:inline><wp:extent cx="952500" cy="952500"/><wp:docPr id="1" name="Canary"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="1" name="c.png"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:link="rId9"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="952500" cy="952500"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p><w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> INCLUDEPICTURE "{net}/field.png" \d </w:instrText></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p><w:p><w:hyperlink r:id="rId8"><w:r><w:t>link</w:t></w:r></w:hyperlink></w:p></w:body></w:document>""",
            });
            var rtf = Hostile.File("canary.rtf", "rtf", "{\\rtf1\\ansi{\\fonttbl\\f0 Arial;}\\f0 RTF text.\\par{\\field{\\*\\fldinst INCLUDEPICTURE \"" + net + "/rtf.png\" \\\\d}{\\fldrslt }}\\par"
                + "{\\field{\\*\\fldinst HYPERLINK \"" + net + "/rtf-link\"}{\\fldrslt link}}\\par}");
            Offline("DOCX/RTF", () =>
            {
                var docxFailures = ConvertAll(docx);
                Check(docxFailures.Count == 0, $"DOCX: {string.Join("; ", docxFailures)}");
                var rtfFailures = ConvertAll(rtf);
                Check(rtfFailures.Count == 0, $"RTF: {string.Join("; ", rtfFailures)}");
            });
            Check(Files.Text(Path.ChangeExtension(docx, "txt")).Contains("A document with a remote picture."), "the text of the DOCX survives");
        });

        Test("Security", "Web pages disguised as .doc or .rtf (as Word saves them) without touching the network", () =>
        {
            foreach (var name in new[] { "page.doc", "page.rtf" })
            {
                var file = Hostile.File(name, "disguised", Hostile.Html(net));
                Hostile.Stylesheets(net, file);
                Offline(name, () =>
                {
                    foreach (var target in new[] { Format.Pdf, Format.Txt, Format.Docx })
                    {
                        try { Conversions.Convert(file, target); }
                        catch (Exception error) { Check(Explain.Error(error).FullText.Length > 25, $"{name} → {target.Label()}: an error without an explanation"); }
                    }
                });
            }
        });
    }
}
