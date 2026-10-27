using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Split;

/// <summary>
/// Prints a document to PDF: A4 (or A4 on its side), 56 pt margins, Arial 12 for text with no look of its own.
/// Uses the fonts installed in Windows and embeds what it needs.
/// </summary>
public static class PdfWriter
{
    private const double Margin = 56;

    static PdfWriter() => GlobalFontSettings.UseWindowsFontsUnderWindows = true;

    public static void Write(RichDocument document, string output, bool landscape = false)
    {
        var temp = Output.TempSibling(output);
        try
        {
            try { Render(document, temp, landscape, installedOnly: false); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException or NotImplementedException)
            {
                // A font the document names isn't installed: the text is set in the default one instead of failing.
                Render(document, temp, landscape, installedOnly: true);
            }
        }
        catch (Exception e) when (e is not Problem and not IOException and not UnauthorizedAccessException and not OperationCanceledException)
        {
            Output.Discard(temp);
            throw new Problem(L("Split couldn’t create the PDF."),
                              L("Check that there is disk space and that the folder can be written to. If the document is very large, split it."), e.ToString());
        }
        catch
        {
            Output.Discard(temp);
            throw;
        }
        Output.Commit(temp, output);
    }

    private static void Render(RichDocument source, string file, bool landscape, bool installedOnly)
    {
        var document = new MigraDoc.DocumentObjectModel.Document();
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = RichDocument.DefaultFont;
        normal.Font.Size = RichDocument.DefaultSize;
        normal.Font.Color = Colors.Black;
        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = landscape ? Orientation.Landscape : Orientation.Portrait;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromPoint(Margin);
        var width = (landscape ? 841.8 : 595.2) - 2 * Margin;
        var height = (landscape ? 595.2 : 841.8) - 2 * Margin;

        var written = Blocks(source.Blocks, section.Elements, width, height, installedOnly);
        if (!written) section.AddParagraph();

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(file);
    }

    private static bool Blocks(IEnumerable<DocBlock> blocks, DocumentElements target, double width, double height, bool installedOnly)
    {
        var any = false;
        foreach (var block in blocks)
        {
            any = true;
            switch (block)
            {
                case DocParagraph paragraph:
                    Paragraph(paragraph, target.AddParagraph(), installedOnly);
                    break;
                case DocPicture picture:
                    var (w, h) = Pictures.Fit(picture, width);
                    if (h > height) (w, h) = (w * height / h, height);
                    var image = target.AddImage("base64:" + Convert.ToBase64String(picture.Data));
                    image.Width = Unit.FromPoint(w);
                    image.Height = Unit.FromPoint(h);
                    break;
                case DocPageBreak:
                    target.AddPageBreak();
                    break;
                case DocTable table:
                    TableOf(table, target, width, height, installedOnly);
                    break;
            }
        }
        return any;
    }

    private static void TableOf(DocTable source, DocumentElements target, double width, double height, bool installedOnly)
    {
        var columns = Math.Max(1, source.Rows.Select(r => r.Sum(c => Math.Max(1, c.Span))).DefaultIfEmpty(1).Max());
        var table = target.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = Color.FromRgb(0x99, 0x99, 0x99);
        table.Format.SpaceAfter = 0;
        var columnWidth = width / columns;
        for (var i = 0; i < columns; i++) table.AddColumn(Unit.FromPoint(columnWidth));
        foreach (var sourceRow in source.Rows)
        {
            var row = table.AddRow();
            row.TopPadding = row.BottomPadding = 2;
            var index = 0;
            foreach (var cell in sourceRow)
            {
                var span = Math.Max(1, cell.Span);
                var target2 = row.Cells[index];
                if (span > 1) target2.MergeRight = span - 1;
                // Cells hold text and pictures; a table inside a cell is written as its rows of text.
                var flat = cell.Blocks.SelectMany(b => b is DocTable inner
                    ? inner.Rows.Select(r => (DocBlock)new DocParagraph().Add(new DocRun { Text = string.Join("  ", r.Select(c => string.Concat(c.Blocks.OfType<DocParagraph>().Select(p => p.Text)))) }))
                    : [b]);
                var written = Blocks(flat, target2.Elements, Math.Max(10, columnWidth * span - 8), height, installedOnly);
                if (!written) target2.AddParagraph();
                index += span;
            }
        }
        // Space after the table, so the next paragraph doesn't touch it.
        target.AddParagraph().Format.SpaceAfter = 0;
    }

    private static void Paragraph(DocParagraph source, MigraDoc.DocumentObjectModel.Paragraph target, bool installedOnly)
    {
        target.Format.Alignment = source.Alignment switch
        {
            DocAlignment.Center => ParagraphAlignment.Center, DocAlignment.Right => ParagraphAlignment.Right, DocAlignment.Justify => ParagraphAlignment.Justify,
            _ => ParagraphAlignment.Left,
        };
        if (source.Indent > 0) target.Format.LeftIndent = Unit.FromPoint(source.Indent);
        target.Format.SpaceBefore = Unit.FromPoint(source.SpacingBefore);
        target.Format.SpaceAfter = Unit.FromPoint(source.SpacingAfter);
        if (source.HeadingLevel > 0) target.Format.KeepWithNext = true;
        if (source.Marker is { } marker) Text(target, new DocRun { Text = marker + "\t" }, installedOnly, null);
        foreach (var run in source.Runs)
        {
            if (run.Link is { } link && DocXml.IsPlainLink(link))
            {
                var hyperlink = target.AddHyperlink(link, HyperlinkType.Web);
                Text(target, run with { Underline = true, Color = run.Color ?? "0563C1" }, installedOnly, hyperlink);
            }
            else Text(target, run, installedOnly, null);
        }
    }

    private static void Text(MigraDoc.DocumentObjectModel.Paragraph paragraph, DocRun run, bool installedOnly, Hyperlink? hyperlink)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var c in run.Text)
        {
            if (c is '\t' or '\n' or '\f' or (char)0x2028)
            {
                parts.Add(current.ToString());
                parts.Add(c == '\t' ? "\t" : "\n");
                current.Clear();
            }
            else if (c >= ' ') current.Append(c);
        }
        parts.Add(current.ToString());
        foreach (var part in parts)
        {
            switch (part)
            {
                case "\t": if (hyperlink is null) paragraph.AddTab(); else hyperlink.AddTab(); break;
                case "\n": if (hyperlink is null) paragraph.AddLineBreak(); else hyperlink.AddCharacter(SymbolName.LineBreak); break;
                case "": break;
                default:
                    var formatted = hyperlink is null ? paragraph.AddFormattedText(part) : hyperlink.AddFormattedText(part);
                    formatted.Font.Name = installedOnly ? RichDocument.DefaultFont : run.Font ?? RichDocument.DefaultFont;
                    formatted.Font.Size = run.Size > 0 ? run.Size : RichDocument.DefaultSize;
                    formatted.Font.Bold = run.Bold;
                    formatted.Font.Italic = run.Italic;
                    formatted.Font.Underline = run.Underline ? Underline.Single : Underline.None;
                    formatted.Font.Superscript = run.Superscript;
                    formatted.Font.Subscript = run.Subscript;
                    if (run.Color is { Length: 6 } color)
                        formatted.Font.Color = Color.FromRgb(Convert.ToByte(color[..2], 16), Convert.ToByte(color[2..4], 16), Convert.ToByte(color[4..], 16));
                    break;
            }
        }
    }
}
