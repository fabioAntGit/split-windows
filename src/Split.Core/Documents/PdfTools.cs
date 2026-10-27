using System.Text.RegularExpressions;

namespace Split;

/// <summary>Reading PDFs: opening them with the right errors, drawing their pages, and getting their text.</summary>
public static class PdfTools
{
    /// <summary>The page picture is drawn at twice its size in points; the text of a scanned page is read from the same picture.</summary>
    private const double RenderScale = 2;

    public static PdfiumDocument Open(string path)
    {
        var name = Path.GetFileName(path);
        PdfiumDocument document;
        try { document = PdfiumDocument.Open(path); }
        catch (PdfPasswordException)
        {
            throw new Problem(L("The PDF “%@” is password-protected.", name),
                              L("Open it in a PDF reader with the password and save an unprotected copy, then use that copy."));
        }
        catch (PdfDamagedException)
        {
            if (FileCheck.Sniff(path) != "PDF")
            {
                throw new Problem(L("“%@” isn’t a valid PDF.", name),
                                  FileCheck.Mismatch(path) ?? L("The content doesn’t start like a PDF: the file is damaged or incomplete."));
            }
            throw Damaged(name);
        }
        if (document.PageCount > 0) return document;
        document.Dispose();
        throw Damaged(name);
    }

    private static Problem Damaged(string name) =>
        new(L("The PDF “%@” is damaged or has no pages.", name),
            L("Open it in a PDF reader to check. If it opens there, print it to “Microsoft Print to PDF” to repair it."));

    // MARK: Pages as pictures

    public static IReadOnlyList<string> RenderPages(string path, Format format)
    {
        using var document = Open(path);
        var name = Path.GetFileName(path);
        Problem Undrawn(int index) => new(L("Page %@ of “%@” couldn’t be drawn.", index + 1, name),
                                          L("The page may be damaged or huge. Open the PDF in a PDF reader to check."));
        if (document.PageCount == 1)
        {
            using var image = document.Render(0, RenderScale) ?? throw Undrawn(0);
            var output = Output.Url(path, format.Ext());
            ImageConverter.WriteSingle(image, null, output, format, Format.Pdf, faithful: false);
            return [output];
        }
        var folder = Output.Folder(path, $" ({format.Label()})");
        Directory.CreateDirectory(folder);
        try
        {
            var digits = document.PageCount.ToString().Length;
            for (var index = 0; index < document.PageCount; index++)
            {
                JobProgress.CheckCancelled();
                using var image = document.Render(index, RenderScale) ?? throw Undrawn(index);
                var page = Path.Combine(folder, L("Page %@", (index + 1).ToString("D" + digits)) + "." + format.Ext());
                ImageConverter.WriteSingle(image, null, page, format, Format.Pdf, faithful: false);
                JobProgress.Report((index + 1.0) / document.PageCount);
            }
        }
        catch
        {
            Output.Discard(folder);
            throw;
        }
        return [folder];
    }

    // MARK: Text

    private sealed class Line(List<PdfChar> characters)
    {
        public List<PdfChar> Characters { get; } = characters;
        public PdfChar First => Characters.FirstOrDefault(c => !char.IsWhiteSpace(c.Character), Characters[0]);
    }

    /// <summary>
    /// The document's text with its look: lines that follow each other closely in the same type become one paragraph, big
    /// type becomes headings, and a page with no text at all is read by OCR.
    /// </summary>
    public static RichDocument Text(string path)
    {
        using var document = Open(path);
        var pages = new List<List<Line>?>();
        for (var index = 0; index < document.PageCount; index++)
        {
            JobProgress.CheckCancelled();
            pages.Add(Lines(document.Characters(index)));
        }
        var bodySize = pages.Where(p => p is not null).SelectMany(p => p!).SelectMany(l => l.Characters).Where(c => !char.IsWhiteSpace(c.Character))
            .GroupBy(c => Math.Round(c.Size * 2) / 2).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(RichDocument.DefaultSize);

        var result = new RichDocument();
        var foundText = false;
        for (var index = 0; index < pages.Count; index++)
        {
            JobProgress.CheckCancelled();
            if (index > 0) result.Blocks.Add(new DocPageBreak());
            var lines = pages[index]!;
            if (lines.Any(l => l.Characters.Any(c => !char.IsWhiteSpace(c.Character))))
            {
                Paragraphs(lines, bodySize, result.Blocks);
                foundText = true;
            }
            else if (Recognize(document, index) is { Length: > 0 } recognized)
            {
                // A scanned page has no text layer: its words are read from the picture of the page.
                foreach (var line in recognized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    result.Blocks.Add(new DocParagraph().Add(new DocRun { Text = line }));
                foundText = true;
            }
            JobProgress.Report((index + 1.0) / pages.Count);
        }
        if (!foundText)
        {
            throw new Problem(L("“%@” has no text: neither selectable text nor text that can be recognized in its pictures (OCR).", Path.GetFileName(path)),
                              L("If the pages are photos, convert to PNG or JPG instead of text."));
        }
        return result;
    }

    private static string Recognize(PdfiumDocument document, int index)
    {
        using var image = document.Render(index, RenderScale);
        return image is null ? "" : Platform.Current.RecognizeText(image.ToByteArray(ImageMagick.MagickFormat.Png)).Trim();
    }

    private static List<Line> Lines(List<PdfChar> characters)
    {
        var lines = new List<Line>();
        var current = new List<PdfChar>();
        void Close()
        {
            if (current.Count > 0) lines.Add(new Line(current));
            current = [];
        }
        foreach (var c in characters)
        {
            if (c.Character is '\r' or '\n')
            {
                Close();
                continue;
            }
            if (c.Character < ' ' && c.Character != '\t') continue;
            // A jump up or down of more than half the type's height is another line (smaller shifts are superscripts).
            if (current.Count > 0 && Math.Abs(c.Y - current[^1].Y) > 0.6 * Math.Max(c.Size, current[^1].Size)) Close();
            current.Add(c);
        }
        Close();
        return lines;
    }

    private static void Paragraphs(List<Line> lines, double bodySize, List<DocBlock> blocks)
    {
        DocParagraph? paragraph = null;
        Line? previous = null;
        void Close()
        {
            if (paragraph is { Runs.Count: > 0 })
            {
                var first = paragraph.Runs[0];
                paragraph.Runs[0] = first with { Text = first.Text.TrimStart() };
                paragraph.Runs[^1] = paragraph.Runs[^1] with { Text = paragraph.Runs[^1].Text.TrimEnd() };
                var ratio = first.Size / bodySize;
                if (paragraph.Text.Length < 160) paragraph.HeadingLevel = ratio >= 1.6 ? 1 : ratio >= 1.3 ? 2 : ratio >= 1.12 && first.Bold ? 3 : 0;
                if (paragraph.Text.Trim().Length > 0) blocks.Add(paragraph);
            }
            paragraph = null;
        }
        foreach (var line in lines)
        {
            var (a, b) = (previous?.First, line.First);
            var joins = a is { } p && paragraph is not null && Math.Abs(p.Y - b.Y) <= 1.35 * p.Size && Math.Abs(p.Size - b.Size) < 0.5 && p.Bold == b.Bold;
            if (!joins)
            {
                Close();
                paragraph = new DocParagraph();
            }
            else if (paragraph!.Runs.Count > 0 && !paragraph.Runs[^1].Text.EndsWith(' ')) paragraph.Runs[^1] = paragraph.Runs[^1] with { Text = paragraph.Runs[^1].Text + " " };
            DocRun? open = null;
            foreach (var c in line.Characters)
            {
                var look = new DocRun
                {
                    Text = c.Character.ToString(), Font = Family(c.Font), Size = Math.Round(c.Size * 2) / 2, Bold = c.Bold, Italic = c.Italic, Color = c.Color,
                };
                if (open is not null && open.SameLook(look))
                {
                    open.Text += c.Character;
                    continue;
                }
                open = look;
                paragraph!.Runs.Add(open);
            }
            previous = line;
        }
        Close();
    }

    /// <summary>"ABCDEF+TimesNewRomanPS-BoldMT" is Times New Roman; the weight and slant travel in the run itself.</summary>
    private static string? Family(string name)
    {
        if (name.Length == 0) return null;
        if (name.Length > 7 && name[6] == '+') name = name[7..];
        var family = Regex.Split(name, "[-,]")[0];
        family = Regex.Replace(family, "(PS)?MT$|PS$", "");
        if (family.StartsWith("Helvetica", StringComparison.OrdinalIgnoreCase)) return "Arial";
        if (family.StartsWith("Times", StringComparison.OrdinalIgnoreCase)) return "Times New Roman";
        if (family.StartsWith("Courier", StringComparison.OrdinalIgnoreCase)) return "Courier New";
        return Regex.Replace(family, "(?<=[a-z])(?=[A-Z])", " ");
    }
}
