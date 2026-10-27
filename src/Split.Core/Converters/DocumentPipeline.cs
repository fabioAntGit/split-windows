using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;

namespace Split;

/// <summary>
/// Documents. Every text format is read into a <see cref="RichDocument"/> and written from it, so any of them can become any
/// other; HTML, Markdown and EPUB keep their own structure when they go to HTML or EPUB. Pages files are recognised but have no targets.
/// </summary>
public sealed class DocumentPipeline : IConversionFamily
{
    public static readonly Format[] TextTargets = [Format.Pdf, Format.Docx, Format.Odt, Format.Rtf, Format.Txt, Format.Html, Format.Epub];

    public IReadOnlyList<Format> Targets(Format source) => source.Group() != FormatGroup.Document ? [] : source switch
    {
        Format.Pdf => [Format.Png, Format.Jpg, Format.Txt, Format.Docx, Format.Rtf, Format.Html, Format.Epub],
        Format.Pages => [],
        _ => TextTargets,
    };

    public IReadOnlyList<string> Convert(string path, Format source, Format target)
    {
        if (source == Format.Pdf && target is Format.Png or Format.Jpg) return PdfTools.RenderPages(path, target);
        // Word keeps the real layout (headers, columns, tables); the writer below is the fallback.
        if (target == Format.Pdf && source is Format.Docx or Format.Doc or Format.Odt or Format.Rtf && OfficeBridge.CanUseWord)
        {
            try { return [OfficeBridge.WordToPdf(path)]; }
            catch (Problem problem) when (!problem.IsCancellation)
            {
                Fidelity.Note(L("Word couldn’t export the document (%@), so Split’s own converter was used and the layout may differ.", problem.Message));
            }
        }
        var output = Output.Url(path, target.Ext());
        var title = Output.BaseName(path);
        switch (target)
        {
            case Format.Epub:
                EpubWriter.Write(Html(path, source, target), title, Path.GetDirectoryName(Path.GetFullPath(path)), output);
                break;
            case Format.Html when source == Format.Md:
                Output.WriteText(output, Markdown.Document(ReadText(path), title));
                break;
            default:
                var document = Load(path, source);
                ReportLosses(path, source, target, document);
                Save(document, target, output, title);
                break;
        }
        return [output];
    }

    // MARK: Fidelity

    /// <summary>Pictures the source file holds, counted in the file itself (not in what was read).</summary>
    public static int PictureCount(string path, Format format)
    {
        try
        {
            switch (format)
            {
                case Format.Docx or Format.Odt:
                    var folder = format == Format.Docx ? "word/media/" : "Pictures/";
                    using (var archive = ZipFile.OpenRead(path))
                        return archive.Entries.Count(e => e.FullName.StartsWith(folder, StringComparison.Ordinal) && !e.FullName.EndsWith('/'));
                case Format.Rtf:
                    return Regex.Matches(ReadText(path), @"\{\\pict").Count;
                case Format.Html:
                    return Regex.Matches(ReadText(path), @"<img\b[^>]*\bsrc\s*=\s*[""']?(?!https?:|//)", RegexOptions.IgnoreCase).Count;
                case Format.Md:
                    return Regex.Matches(ReadText(path), @"!\[").Count;
                default:
                    return 0;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Says which pictures didn't make it, and which formats would have kept them.</summary>
    private static void ReportLosses(string path, Format source, Format target, RichDocument loaded)
    {
        var found = loaded.PictureCount;
        var expected = PictureCount(path, source);
        if (expected > found)
        {
            Fidelity.Note(L("Split couldn’t read %@ of the pictures in the document, so they were left out. Turn on “Use Word, Excel and PowerPoint” in Settings (or install LibreOffice) to keep them.",
                            (expected - found).ToString()));
        }
        if (source == Format.Pdf && target != Format.Pdf)
            Fidelity.Note(L("From a PDF only the text comes across; pictures and page layout stay in the PDF."));
        if (found > 0 && target == Format.Txt)
            Fidelity.Note(L("TXT holds only text, so %@ pictures were left out.", found.ToString()));
    }

    // MARK: Reading

    /// <summary>Any text file as a string, whatever encoding it was saved in (see <see cref="TextDecoding"/>).</summary>
    public static string ReadText(string path) => TextDecoding.Decode(File.ReadAllBytes(path)).Text;

    /// <summary>Plain text as a document: one paragraph per line, in the default font.</summary>
    public static RichDocument FromText(string text)
    {
        var document = new RichDocument();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // The newline that ends the last line does not start another.
        foreach (var line in lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines)
            document.Blocks.Add(new DocParagraph().Add(new DocRun { Text = line }));
        return document;
    }

    public static RichDocument Load(string path, Format source)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var name = Path.GetFileName(path);
        try
        {
            switch (source)
            {
                case Format.Txt:
                    return FromText(ReadText(path));
                case Format.Md:
                    return HtmlReader.Read(Markdown.Document(ReadText(path), Output.BaseName(path)), folder);
                case Format.Html:
                    return HtmlReader.Read(ReadText(path), folder);
                case Format.Epub:
                    var (book, workspace) = EpubReader.CombinedHtml(path);
                    try { return HtmlReader.Read(ReadText(book), Path.GetDirectoryName(book)); }
                    finally { Output.Discard(workspace); }
                case Format.Pdf:
                    return PdfTools.Text(path);
                case Format.Rtfd:
                    return RtfReader.Read(path);
                default:
                    // Word saves "web pages" as .doc; they go through the local-only HTML path, never a reader that would fetch their pictures.
                    var kind = FileCheck.Sniff(path);
                    if (kind == "HTML") return HtmlReader.Read(ReadText(path), folder);
                    if (kind == "RTF" || source == Format.Rtf) return RtfReader.Read(path);
                    return source switch
                    {
                        Format.Docx => DocxReader.Read(path),
                        Format.Odt => OdtReader.Read(path),
                        _ => ReadOldWord(path),
                    };
            }
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or KeyNotFoundException or FormatException)
        {
            throw new Problem(L("Split couldn’t read “%@”: it is damaged or not in the format its extension says.", name),
                              L("Open it in the app it came from to check it, and export it again."), e.Message);
        }
    }

    private static RichDocument ReadOldWord(string path)
    {
        var scratch = Output.ScratchDirectory();
        try { return DocxReader.Read(OfficeBridge.DocToDocx(path, scratch)); }
        finally { Output.Discard(scratch); }
    }

    /// <summary>HTML used to build EPUBs; Markdown and HTML keep their own structure (and their local pictures).</summary>
    private static string Html(string path, Format source, Format target)
    {
        var title = Output.BaseName(path);
        switch (source)
        {
            case Format.Md:
                return Markdown.Document(ReadText(path), title);
            case Format.Html:
                return LocalOnly.Html(ReadText(path), Path.GetDirectoryName(Path.GetFullPath(path)));
            default:
                var document = Load(path, source);
                ReportLosses(path, source, target, document);
                return HtmlWriter.Write(document, title);
        }
    }

    // MARK: Writing

    public static void Save(RichDocument document, Format target, string output, string title)
    {
        switch (target)
        {
            case Format.Pdf:
                PdfWriter.Write(document, output);
                break;
            case Format.Txt:
                Output.WriteText(output, document.PlainText);
                break;
            case Format.Docx:
                DocxWriter.Write(document, output);
                break;
            case Format.Odt:
                OdtWriter.Write(document, output);
                break;
            case Format.Rtf:
                RtfWriter.Write(document, output);
                break;
            case Format.Html:
                Output.WriteText(output, HtmlWriter.Write(document, title));
                break;
            default:
                throw new Problem(L("Choose DOCX, RTF or PDF."));
        }
    }
}
