using System.IO;
using System.IO.Compression;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Split.Tests;

/// <summary>The samples of the document suites: an OpenDocument text, a PDF with a password, a scanned page and a slide.</summary>
public static partial class Fixtures
{
    private static void DocumentSamples()
    {
        var letter = new RichDocument();
        letter.Blocks.Add(new DocParagraph { HeadingLevel = 1 }.Add(new DocRun { Text = "Letter", Bold = true, Size = 24 }));
        letter.Blocks.Add(new DocParagraph().Add(new DocRun { Text = Phrase + "." }));
        OdtWriter.Write(letter, Url("letter.odt"));

        // A PDF only the password opens.
        using (var source = PdfReader.Open(Url("report.pdf"), PdfDocumentOpenMode.Import))
        using (var locked = new PdfDocument())
        {
            foreach (PdfPage page in source.Pages) locked.AddPage(page);
            locked.SecuritySettings.UserPassword = "secret";
            locked.SecuritySettings.OwnerPassword = "secret";
            locked.Save(Url("locked.pdf"));
        }

        // A page that is only pixels: its words can be read by OCR and by nothing else.
        using (var surface = SKSurface.Create(new SKImageInfo(1240, 1754)))
        {
            surface.Canvas.Clear(SKColors.White);
            using var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 64);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            surface.Canvas.DrawText("Invoice number 2024", 120, 350, SKTextAlign.Left, font, paint);
            using var png = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
            using var scan = new PdfDocument();
            var page = scan.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using (var graphics = XGraphics.FromPdfPage(page))
            using (var stream = new MemoryStream(png.ToArray()))
            using (var picture = XImage.FromStream(stream))
                graphics.DrawImage(picture, 0, 0, 595, 842);
            scan.Save(Url("scanned.pdf"));
        }
    }

    /// <summary>A one-slide presentation with "Hello Split", for the tests that need PowerPoint or LibreOffice.</summary>
    public static void Pptx(string output)
    {
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
        var ns = $"xmlns:a=\"{a}\" xmlns:r=\"{r}\" xmlns:p=\"{p}\"";
        const string emptyTree = "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";
        var colors = string.Concat(new[]
        {
            ("dk1", "000000"), ("lt1", "FFFFFF"), ("dk2", "1F2937"), ("lt2", "F3F4F6"), ("accent1", "FF9500"), ("accent2", "2E8BFF"),
            ("accent3", "43E97B"), ("accent4", "FA709A"), ("accent5", "A18CD1"), ("accent6", "FEE140"), ("hlink", "0563C1"), ("folHlink", "954F72"),
        }.Select(c => $"<a:{c.Item1}><a:srgbClr val=\"{c.Item2}\"/></a:{c.Item1}>"));
        const string fill = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        const string line = "<a:ln w=\"9525\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>";
        const string effect = "<a:effectStyle><a:effectLst/></a:effectStyle>";
        var theme = $"{header}<a:theme xmlns:a=\"{a}\" name=\"Split\"><a:themeElements><a:clrScheme name=\"C\">{colors}</a:clrScheme>"
            + "<a:fontScheme name=\"C\"><a:majorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>"
            + $"<a:fmtScheme name=\"C\"><a:fillStyleLst>{fill}{fill}{fill}</a:fillStyleLst><a:lnStyleLst>{line}{line}{line}</a:lnStyleLst><a:effectStyleLst>{effect}{effect}{effect}</a:effectStyleLst><a:bgFillStyleLst>{fill}{fill}{fill}</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>";
        string Rels(params (string Id, string Type, string Target)[] items) =>
            $"{header}<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + string.Concat(items.Select(i => $"<Relationship Id=\"{i.Id}\" Type=\"{r}/{i.Type}\" Target=\"{i.Target}\"/>")) + "</Relationships>";
        var files = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = $"{header}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>"
                + "<Override PartName=\"/ppt/slides/slide1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>"
                + "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>"
                + "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>"
                + "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/></Types>",
            ["_rels/.rels"] = Rels(("rId1", "officeDocument", "ppt/presentation.xml")),
            ["ppt/presentation.xml"] = $"{header}<p:presentation {ns}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/></p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = Rels(("rId1", "slideMaster", "slideMasters/slideMaster1.xml"), ("rId2", "slide", "slides/slide1.xml"), ("rId3", "theme", "theme/theme1.xml")),
            ["ppt/slideMasters/slideMaster1.xml"] = $"{header}<p:sldMaster {ns}><p:cSld>{emptyTree}</p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>",
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", "theme", "../theme/theme1.xml")),
            ["ppt/slideLayouts/slideLayout1.xml"] = $"{header}<p:sldLayout {ns} type=\"blank\"><p:cSld name=\"Blank\">{emptyTree}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = Rels(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")),
            ["ppt/slides/slide1.xml"] = $"{header}<p:sld {ns}><p:cSld>{emptyTree}<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Text\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"914400\" y=\"914400\"/><a:ext cx=\"7315200\" cy=\"1371600\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>"
                + "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\" sz=\"4000\"/><a:t>Hello Split</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>",
            ["ppt/slides/_rels/slide1.xml.rels"] = Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml")),
            ["ppt/theme/theme1.xml"] = theme,
        };
        File.Delete(output);
        using var archive = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var (path, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
