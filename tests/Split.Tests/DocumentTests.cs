using System.IO;
using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf.IO;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>Documents and PDFs: reading, writing, what is kept and what is said to be lost, and the Office routes.</summary>
public static class DocumentTests
{
    /// <summary>The text of every page, as PDFium reads it (no OCR).</summary>
    public static string PdfText(string path)
    {
        using var document = PdfiumDocument.Open(path);
        return string.Concat(Enumerable.Range(0, document.PageCount).SelectMany(page => document.Characters(page)).Select(c => c.Character));
    }

    private static int PdfPages(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    private static string Folder(string name)
    {
        var folder = Path.Combine(Fixtures.Root, "_documents", name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string ZipText(string archive, string entry)
    {
        using var zip = ZipFile.OpenRead(archive);
        using var reader = new StreamReader(zip.GetEntry(entry)?.Open() ?? throw new TestFailure($"{entry} isn’t in {Path.GetFileName(archive)}"));
        return reader.ReadToEnd();
    }

    private static List<string> ZipNames(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>Text, a bold run, a link, a list item, a table, a picture and a page break: what every format should carry.</summary>
    private static RichDocument Sample()
    {
        var document = new RichDocument();
        document.Blocks.Add(new DocParagraph { HeadingLevel = 1 }.Add(new DocRun { Text = "Title Split", Bold = true, Size = 24 }));
        var body = new DocParagraph();
        body.Add(new DocRun { Text = "Plain " }).Add(new DocRun { Text = "bold", Bold = true }).Add(new DocRun { Text = " and " })
            .Add(new DocRun { Text = "link", Link = "https://example.com/page" }).Add(new DocRun { Text = " café — crème" });
        document.Blocks.Add(body);
        document.Blocks.Add(new DocParagraph { Marker = "•", Indent = 18, ListLevel = 1 }.Add(new DocRun { Text = "First point" }));
        var table = new DocTable();
        foreach (var (a, b) in new[] { ("Month", "Value"), ("March", "42") })
        {
            var row = new List<DocCell> { new(), new() };
            row[0].Blocks.Add(new DocParagraph().Add(new DocRun { Text = a }));
            row[1].Blocks.Add(new DocParagraph().Add(new DocRun { Text = b }));
            table.Rows.Add(row);
        }
        document.Blocks.Add(table);
        document.Blocks.Add(Pictures.FromBytes(File.ReadAllBytes(Fixtures.Url("quadrants.png")))!);
        document.Blocks.Add(new DocPageBreak());
        document.Blocks.Add(new DocParagraph().Add(new DocRun { Text = "Second page" }));
        return document;
    }

    private static IEnumerable<DocRun> Runs(RichDocument document) => document.All.OfType<DocParagraph>().SelectMany(p => p.Runs);

    public static void Register()
    {
        foreach (var (format, extension) in new[] { (Format.Docx, "docx"), (Format.Odt, "odt"), (Format.Rtf, "rtf"), (Format.Html, "html") })
        {
            Test("Conversion", $"{extension.ToUpperInvariant()}: what a document holds is written and read back", () =>
            {
                var file = Path.Combine(Folder("roundtrip"), "sample." + extension);
                DocumentPipeline.Save(Sample(), format, file, "sample");
                var back = DocumentPipeline.Load(file, format);
                var text = back.PlainText;
                Check(text.Contains("Title Split") && text.Contains("café — crème") && text.Contains("First point") && text.Contains("Second page"), $"text: {text}");
                Check(text.Contains("March\t42"), $"the table: {text}");
                Check(back.All.OfType<DocTable>().Any(), "the table is a table");
                Check(Runs(back).Any(r => r.Bold && r.Text.Contains("bold")), "the bold run");
                Check(back.PictureCount == 1, $"pictures: {back.PictureCount}");
                if (format != Format.Rtf) Check(Runs(back).Any(r => r.Link == "https://example.com/page" && r.Text.Contains("link")), "the link");
                if (format is Format.Docx or Format.Odt) Check(back.All.OfType<DocPageBreak>().Any(), "the page break");
                if (format == Format.Odt) Check(back.All.OfType<DocParagraph>().Any(p => p.HeadingLevel == 1), "the heading");
                // Word, OpenDocument and RTF get the marker as text in front of the item; HTML keeps the list.
                Check(format == Format.Html ? back.All.OfType<DocParagraph>().Any(p => p.Marker == "•") : text.Contains("•	First point"), "the list item");
            });
        }

        Test("Conversion", "Documents: text and accents survive every round trip", () =>
        {
            var source = Fixtures.Isolated("readme.md", "doc-roundtrip");
            var html = Conversions.Convert(source, Format.Html)[0];
            Check(Files.Text(html).Contains("<h1>Report</h1>"), "Markdown → HTML without its heading");
            var epub = Conversions.Convert(source, Format.Epub)[0];
            var back = Conversions.Convert(epub, Format.Txt)[0];
            Check(Files.Text(back).Contains(Fixtures.Phrase), $"EPUB → TXT lost the text or the accents: {Files.Text(back)[..Math.Min(120, Files.Text(back).Length)]}");
            var docx = Conversions.Convert(source, Format.Docx)[0];
            var pdf = Conversions.Convert(docx, Format.Pdf)[0];
            Check(PdfText(pdf).Contains("Split"), "DOCX → PDF without the text");
            Check(PdfText(pdf).Contains("crème brûlée"), "DOCX → PDF lost the accents");
        });

        Test("Conversion", "PDF: pages to PNG in a folder, and OCR on scans", () =>
        {
            var source = Fixtures.Isolated("report.pdf", "pdf-pages");
            var folder = Conversions.Convert(source, Format.Png)[0];
            var pages = Files.List(folder);
            Check(pages.Count == 3, $"3 pages → {pages.Count} PNG");
            Check(Path.GetFileName(pages[0]) == "Page 1.png", $"page names: {string.Join(", ", pages.Select(Path.GetFileName))}");
            var info = Pixels.Info(pages[0])!;
            Check(info.Width is >= 1189 and <= 1191 && info.Height is >= 1683 and <= 1685, $"a page is drawn at twice its size: {info.Width}×{info.Height}");
            using (var image = Pixels.Load(pages[0])!)
                Check(Pixels.Close(Pixels.At(image, 5, 5), [255, 255, 255], 2), "the page is on white");

            if (Platform.Current.OcrLanguages.Count == 0) Skip("Windows has no OCR language installed");
            var scanned = Fixtures.Isolated("scanned.pdf", "pdf-ocr");
            var text = Conversions.Convert(scanned, Format.Txt)[0];
            Check(Files.Text(text).Contains("2024"), $"OCR should read “Invoice number 2024”: {Files.Text(text)[..Math.Min(80, Files.Text(text).Length)]}");
        });

        Test("Conversion", "PDF text keeps its size and weight: headings, bold and paragraphs", () =>
        {
            var made = new RichDocument();
            made.Blocks.Add(new DocParagraph().Add(new DocRun { Text = "Big heading", Bold = true, Size = 28 }));
            var body = new DocParagraph().Add(new DocRun { Text = "Some body text that goes on for a little while, with a " }).Add(new DocRun { Text = "bold word", Bold = true })
                .Add(new DocRun { Text = " in the middle of it, so the line wraps around the page at least once or twice over and over again until it ends." });
            made.Blocks.Add(body);
            made.Blocks.Add(new DocParagraph { SpacingBefore = 20 }.Add(new DocRun { Text = "Second paragraph." }));
            var file = Path.Combine(Folder("pdf-text"), "styled.pdf");
            PdfWriter.Write(made, file);
            Check(PdfPages(file) == 1, "one page");
            var read = PdfTools.Text(file);
            var paragraphs = read.All.OfType<DocParagraph>().ToList();
            Check(paragraphs.Count == 3, $"three paragraphs, got {paragraphs.Count}: {string.Join(" | ", paragraphs.Select(p => p.Text))}");
            Check(paragraphs[0].HeadingLevel == 1 && paragraphs[0].Runs.All(r => r.Bold), "the big bold line is a heading");
            Check(paragraphs[1].Runs.Any(r => r.Bold && r.Text.Contains("bold word")) && paragraphs[1].Runs.Any(r => !r.Bold), "bold is kept inside a paragraph");
            Check(paragraphs[1].Runs.All(r => r.Size == 12) && paragraphs[1].Runs.All(r => r.Font == "Arial"), $"size and font: {string.Join(",", paragraphs[1].Runs.Select(r => $"{r.Font}/{r.Size}"))}");
            Check(!paragraphs[1].Text.Contains('\n') && paragraphs[1].Text.Contains("wraps around"), "wrapped lines are joined into one paragraph");
        });

        Test("Conversion", "A page break reaches the PDF as a second page, and a wide table prints on its side", () =>
        {
            var file = Path.Combine(Folder("pdf-pages"), "two.pdf");
            PdfWriter.Write(Sample(), file);
            Check(PdfPages(file) == 2, $"{PdfPages(file)} pages");
            var text = PdfText(file);
            Check(text.Contains("Title Split") && text.Contains("café") && text.Contains("Second page") && text.Contains("March"), $"text of the PDF: {text}");
            using (var page = PdfiumDocument.Open(file))
            using (var drawn = page.Render(0, 1)!)
                Check(drawn.BoundingBox is { Width: > 400 }, "the picture is drawn at its size, not as a dot");
            using (var pdf = PdfReader.Open(file, PdfDocumentOpenMode.Import))
                Check(Math.Abs(pdf.Pages[0].Width.Point - 595.28) < 1 && Math.Abs(pdf.Pages[0].Height.Point - 841.89) < 1, $"A4: {pdf.Pages[0].Width.Point}×{pdf.Pages[0].Height.Point}");
            var wide = new RichDocument();
            wide.Blocks.Add(new DocParagraph().Add(new DocRun { Text = "wide" }));
            var landscape = Path.Combine(Folder("pdf-pages"), "wide.pdf");
            PdfWriter.Write(wide, landscape, landscape: true);
            using var side = PdfReader.Open(landscape, PdfDocumentOpenMode.Import);
            Check(side.Pages[0].Width.Point > side.Pages[0].Height.Point, "landscape");
        });

        Test("Conversion", "Spreadsheets become PDF through the table writer when no Office app is used", () =>
        {
            var source = Fixtures.Isolated("sales.xlsx", "sheet-pdf");
            var wasOn = Settings.UseOfficeApps;
            Settings.UseOfficeApps = false;
            var pdf = Conversions.Convert(source, Format.Pdf)[0];
            Settings.UseOfficeApps = wasOn;
            Check(PdfPages(pdf) >= 1 && PdfText(pdf).Length > 10, $"a PDF with the cells: {PdfText(pdf)}");
        });

        Test("Conversion", "An RTFD folder brings its picture back where the text refers to it", () =>
        {
            var folder = Path.Combine(Folder("rtfd"), "notes.rtfd");
            Directory.CreateDirectory(folder);
            File.Copy(Fixtures.Url("quadrants.png"), Path.Combine(folder, "Pasted Graphic.png"), overwrite: true);
            File.WriteAllText(Path.Combine(folder, "TXT.rtf"),
                "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0 Arial;}}\\f0\\fs24 Before the picture.\\par{{\\NeXTGraphic Pasted Graphic.png \\width2400 \\height1600 \\noorient}\\'ac}\\par After the picture, caf\\'e9.\\par}",
                Encoding.ASCII);
            var document = DocumentPipeline.Load(folder, Format.Rtfd);
            var kinds = document.Blocks.Select(b => b.GetType().Name).ToList();
            Check(document.PictureCount == 1, $"the picture: {string.Join(", ", kinds)}");
            var picture = document.Blocks.OfType<DocPicture>().First();
            Check(Math.Abs(picture.Width - 120) < 1, $"the size in the text: {picture.Width}");
            var before = document.Blocks.TakeWhile(b => b is not DocPicture).OfType<DocParagraph>().Select(p => p.Text);
            var after = document.Blocks.SkipWhile(b => b is not DocPicture).OfType<DocParagraph>().Select(p => p.Text);
            Check(before.Any(t => t.Contains("Before the picture")) && after.Any(t => t.Contains("After the picture, café")), $"text on both sides: {string.Join(" | ", document.Blocks.OfType<DocParagraph>().Select(p => p.Text))}");
        });

        // MARK: Fidelity

        Test("Fidelity", "Documents keep pictures, links and tables, or say they can’t", () =>
        {
            var picture = Convert.ToBase64String(File.ReadAllBytes(Fixtures.Url("quadrants.png")));
            var page = Hostile.File("notes.html", "fidelity-documents",
                $"<p>Hello Split — café.</p><p><a href=\"https://example.com/page\">A link</a></p><p><img src=\"data:image/png;base64,{picture}\"></p><p>The end.</p>");
            var docx = Conversions.Convert(page, Format.Docx)[0];
            Check(ZipNames(docx).Any(n => n.StartsWith("word/media/")), "DOCX keeps the picture");
            var xml = ZipText(docx, "word/document.xml");
            Check(xml.Contains("<w:hyperlink") && xml.Contains("<w:drawing>"), "DOCX keeps the link and draws the picture");
            Check(ZipText(docx, "word/_rels/document.xml.rels").Contains("https://example.com/page"), "the link points where it did");

            var (odt, odtNotes) = Fidelity.Collect(() => Conversions.Convert(page, Format.Odt));
            Check(ZipNames(odt[0]).Any(n => n.StartsWith("Pictures/")) && odtNotes.Count == 0, $"ODT keeps the picture and has nothing to say: {string.Join(" | ", odtNotes)}");
            var rtf = Conversions.Convert(page, Format.Rtf)[0];
            Check(Files.Text(rtf).Contains("\\pict"), "RTF keeps the picture");
            var (_, txtNotes) = Fidelity.Collect(() => Conversions.Convert(page, Format.Txt));
            Check(txtNotes.Any(n => n.Contains("only text")), $"TXT says the picture is left out: {string.Join(" | ", txtNotes)}");
            var html = Files.Text(Conversions.Convert(docx, Format.Html)[0]);
            Check(html.Contains("data:image/") && !html.Contains("file://"), "HTML embeds the picture instead of a broken file:// link");
            var epub = Conversions.Convert(docx, Format.Epub)[0];
            Check(ZipNames(epub).Any(n => n.StartsWith("OEBPS/images/")), "EPUB packs the picture");

            var table = Hostile.File("table.html", "fidelity-documents", "<table><tr><th>Month</th><th>Value</th></tr><tr><td>March</td><td>42</td></tr></table>");
            Check(ZipText(Conversions.Convert(table, Format.Docx)[0], "word/document.xml").Contains("<w:tbl>"), "DOCX keeps the table");
            var (_, pdfNotes) = Fidelity.Collect(() => Conversions.Convert(Fixtures.Isolated("report.pdf", "fidelity-pdf"), Format.Docx));
            Check(pdfNotes.Any(n => n.Contains("only the text")), $"PDF → DOCX says only the text comes across: {string.Join(" | ", pdfNotes)}");
        });

        Test("Fidelity", "A document whose pictures can’t be read says how many were left out", () =>
        {
            // A Word file that names a picture it doesn't hold.
            var docx = Hostile.Package("missing-picture.docx", "fidelity-documents", new()
            {
                ["[Content_Types].xml"] = """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/></Types>""",
                ["_rels/.rels"] = """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""",
                ["word/document.xml"] = """<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Text.</w:t></w:r></w:p></w:body></w:document>""",
                ["word/media/image1.bmp"] = "not a picture",
            });
            var (_, notes) = Fidelity.Collect(() => Conversions.Convert(docx, Format.Txt));
            Check(notes.Any(n => n.Contains("1 of the pictures")), $"the note: {string.Join(" | ", notes)}");
        });

        // MARK: Errors

        Test("Errors", "A password-protected PDF", () =>
        {
            ExpectError("a password-protected PDF", ["password", "unprotected copy"], () => Conversions.Convert(Fixtures.Url("locked.pdf"), Format.Txt));
            ExpectError("a password-protected PDF to pictures", ["password"], () => Conversions.Convert(Fixtures.Url("locked.pdf"), Format.Png));
            Check(Directory.EnumerateFileSystemEntries(Fixtures.Root, "locked*").All(p => Path.GetFileName(p) == "locked.pdf"), "a failed conversion left files behind");
        });

        Test("Errors", "A PDF that isn’t one, or has nothing to read, is explained", () =>
        {
            var fake = Hostile.File("fake.pdf", "errors-pdf", "<html>Page not found</html>");
            ExpectError("a web page saved as .pdf", ["isn’t a valid PDF"], () => Conversions.Convert(fake, Format.Txt));
            var blank = Path.Combine(Folder("errors-pdf"), "blank.pdf");
            PdfWriter.Write(new RichDocument(), blank);
            if (Platform.Current.OcrLanguages.Count > 0) ExpectError("a blank PDF to text", ["has no text"], () => Conversions.Convert(blank, Format.Txt));
            var broken = Path.Combine(Folder("errors-pdf"), "broken.docx");
            File.WriteAllBytes(broken, [0x50, 0x4B, 3, 4, .. new byte[200]]);
            ExpectError("a damaged DOCX", ["damaged"], () => Conversions.Convert(broken, Format.Txt));
        });

        Test("Errors", "Normalizing without sound", () => Skip("needs the quick tools"));

        // MARK: Office

        Test("Office", "Without Word, Excel, PowerPoint or LibreOffice the old formats say what to install", () =>
        {
            if (Engines.Soffice is not null || Engines.Word || Engines.Excel || Engines.PowerPoint) Skip("Office or LibreOffice is installed here: the missing-program messages can’t be seen");
            ExpectError("an old Word file", ["Word or LibreOffice", "LibreOffice"], () => Conversions.Convert(Fixtures.Url("letter.doc"), Format.Txt));
            var xls = Path.Combine(Folder("office-missing"), "old.xls");
            File.WriteAllBytes(xls, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0, 0, 0, 0, 0]);
            ExpectError("an old workbook", ["Excel or LibreOffice"], () => SpreadsheetConverter.Read(xls, Format.Xls));
            var pptx = Path.Combine(Folder("office-missing"), "slides.pptx");
            Fixtures.Pptx(pptx);
            ExpectError("a presentation", ["PowerPoint or LibreOffice"], () => Conversions.Convert(pptx, Format.Pdf));
            Check(Conversions.Targets(Format.Pptx).Count == 0, "no targets are offered for slides");
        });

        Test("Office", "An app that refuses a file from the web is explained as Protected View", () =>
        {
            if (Engines.Word) Skip("Word is installed: the refusal would be Word’s own");
            var file = Path.Combine(Folder("office-mark"), "downloaded.docx");
            File.Copy(Fixtures.Url("letter.docx"), file, overwrite: true);
            var output = Path.Combine(Folder("office-mark"), "out.pdf");
            var plain = ExpectError("Word missing", ["Word"], () => Platform.Current.ExportWithOffice(OfficeApp.Word, file, output, OfficeExport.Pdf));
            Check(plain?.FullText.Contains("Protected View") != true, $"no web mark, no Protected View: {plain?.FullText}");
            try { File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
            catch (Exception e) when (e is IOException or NotSupportedException) { Skip("this disk keeps no alternate data streams"); }
            ExpectError("a file marked as from the web", ["Protected View", "Unblock"], () => Platform.Current.ExportWithOffice(OfficeApp.Word, file, output, OfficeExport.Pdf));
            Check(!File.Exists(output), "no output was left");
        });

        if (Environment.GetEnvironmentVariable("SPLIT_OFFICE") != "1") return;

        // These open Word, Excel and PowerPoint (SPLIT_OFFICE=1 ./test.ps1 Office).
        void WithOffice(Action body)
        {
            Settings.UseOfficeApps = true;
            try { body(); }
            finally
            {
                Settings.UseOfficeApps = false;
                Platform.Current.QuitLaunchedOfficeApps();
            }
        }

        Test("Office", "PowerPoint: PPTX → PDF with the slide’s text", () =>
        {
            if (!Engines.PowerPoint) Skip("PowerPoint isn’t installed");
            var pptx = Path.Combine(Folder("office-slides"), "slides.pptx");
            Fixtures.Pptx(pptx);
            WithOffice(() =>
            {
                var pdf = Conversions.Convert(pptx, Format.Pdf).First();
                Check(PdfPages(pdf) == 1, $"1 slide → {PdfPages(pdf)} pages");
                Check(PdfText(pdf).Contains("Hello Split"), "the slide’s text should be in the PDF");
            });
        });

        Test("Office", "Word: DOCX → PDF with accents", () =>
        {
            if (!Engines.Word) Skip("Word isn’t installed");
            var docx = Fixtures.Isolated("letter.docx", "office-word");
            WithOffice(() => Check(PdfText(Conversions.Convert(docx, Format.Pdf).First()).Contains("crème brûlée"), "the accents"));
        });

        Test("Office", "Excel: XLSX → PDF", () =>
        {
            if (!Engines.Excel) Skip("Excel isn’t installed");
            var xlsx = Fixtures.Isolated("sales.xlsx", "office-excel");
            WithOffice(() => Check(PdfPages(Conversions.Convert(xlsx, Format.Pdf).First()) >= 1, "empty PDF"));
        });
    }
}
