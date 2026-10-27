using System.Globalization;
using System.Text;

namespace Split;

/// <summary>Writes a document as RTF: fonts, colours, character formatting, tables and PNG or JPEG pictures.</summary>
public static class RtfWriter
{
    /// <summary>Width of the text column of an A4 page with 2.5 cm margins, in twips.</summary>
    private const int ColumnTwips = 9060;

    private sealed class Tables(RichDocument document)
    {
        public List<string> Fonts { get; } = [RichDocument.DefaultFont];
        public List<string> Colors { get; } = ["0563C1"];

        public void Collect(IEnumerable<DocBlock> blocks)
        {
            foreach (var block in blocks)
            {
                if (block is DocParagraph paragraph)
                {
                    foreach (var run in paragraph.Runs)
                    {
                        if (run.Font is { } font && !Fonts.Contains(font)) Fonts.Add(font);
                        if (run.Color is { } color && !Colors.Contains(color)) Colors.Add(color);
                    }
                }
                else if (block is DocTable table)
                {
                    foreach (var cell in table.Rows.SelectMany(r => r)) Collect(cell.Blocks);
                }
            }
        }

        public Tables Build()
        {
            Collect(document.Blocks);
            return this;
        }
    }

    public static void Write(RichDocument document, string output)
    {
        var tables = new Tables(document).Build();
        var rtf = new StringBuilder("{\\rtf1\\ansi\\ansicpg1252\\deff0\\deflang1033{\\fonttbl");
        for (var index = 0; index < tables.Fonts.Count; index++) rtf.Append($"{{\\f{index}\\fnil\\fcharset0 {Escape(tables.Fonts[index])};}}");
        rtf.Append("}{\\colortbl;");
        foreach (var color in tables.Colors)
            rtf.Append($"\\red{int.Parse(color[..2], NumberStyles.HexNumber)}\\green{int.Parse(color[2..4], NumberStyles.HexNumber)}\\blue{int.Parse(color[4..], NumberStyles.HexNumber)};");
        rtf.Append("}\\paperw11906\\paperh16838\\margl1417\\margr1417\\margt1417\\margb1417\n");
        Blocks(document.Blocks, rtf, tables, inCell: false);
        rtf.Append('}');
        Output.WriteText(output, rtf.ToString());
    }

    private static void Blocks(IEnumerable<DocBlock> blocks, StringBuilder rtf, Tables tables, bool inCell)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocParagraph paragraph:
                    ParagraphRtf(paragraph, rtf, tables, inCell);
                    break;
                case DocPicture picture:
                    rtf.Append(inCell ? "\\pard\\intbl " : "\\pard ").Append(PictureRtf(picture)).Append(inCell ? "\\par\n" : "\\par\n");
                    break;
                case DocPageBreak:
                    rtf.Append("\\pard\\page\\par\n");
                    break;
                case DocTable table when !inCell:
                    TableRtf(table, rtf, tables);
                    break;
                case DocTable nested:
                    // A table inside a cell has no place in RTF: its rows become lines of text.
                    foreach (var row in nested.Rows)
                        rtf.Append("\\pard\\intbl ").Append(Escape(string.Join("\t", row.Select(c => string.Concat(c.Blocks.OfType<DocParagraph>().Select(p => p.Text))))).Replace("\t", "\\tab ")).Append("\\par\n");
                    break;
            }
        }
    }

    private static void TableRtf(DocTable table, StringBuilder rtf, Tables tables)
    {
        var columns = Math.Max(1, table.Rows.Select(r => r.Sum(c => Math.Max(1, c.Span))).DefaultIfEmpty(1).Max());
        var width = ColumnTwips / columns;
        foreach (var row in table.Rows)
        {
            rtf.Append("\\trowd\\trgaph108");
            var edge = 0;
            var used = 0;
            foreach (var cell in row)
            {
                edge += width * Math.Max(1, cell.Span);
                used += Math.Max(1, cell.Span);
                rtf.Append("\\clbrdrt\\brdrs\\brdrw10\\clbrdrl\\brdrs\\brdrw10\\clbrdrb\\brdrs\\brdrw10\\clbrdrr\\brdrs\\brdrw10").Append($"\\cellx{edge}");
            }
            for (; used < columns; used++) rtf.Append("\\clbrdrt\\brdrs\\brdrw10\\clbrdrl\\brdrs\\brdrw10\\clbrdrb\\brdrs\\brdrw10\\clbrdrr\\brdrs\\brdrw10").Append($"\\cellx{edge += width}");
            rtf.Append('\n');
            foreach (var cell in row)
            {
                if (cell.Blocks.Count == 0) rtf.Append("\\pard\\intbl ");
                Blocks(cell.Blocks, rtf, tables, inCell: true);
                rtf.Append("\\cell\n");
            }
            for (var pad = used - row.Sum(c => Math.Max(1, c.Span)); pad > 0; pad--) rtf.Append("\\pard\\intbl \\cell\n");
            rtf.Append("\\row\n");
        }
        rtf.Append("\\pard\\par\n");
    }

    private static void ParagraphRtf(DocParagraph paragraph, StringBuilder rtf, Tables tables, bool inCell)
    {
        rtf.Append(inCell ? "\\pard\\intbl" : "\\pard");
        rtf.Append(paragraph.Alignment switch { DocAlignment.Center => "\\qc", DocAlignment.Right => "\\qr", DocAlignment.Justify => "\\qj", _ => "\\ql" });
        if (paragraph.Indent > 0) rtf.Append($"\\li{(int)(paragraph.Indent * 20)}");
        if (paragraph.SpacingBefore > 0) rtf.Append($"\\sb{(int)(paragraph.SpacingBefore * 20)}");
        if (paragraph.SpacingAfter > 0) rtf.Append($"\\sa{(int)(paragraph.SpacingAfter * 20)}");
        rtf.Append(' ');
        if (paragraph.Marker is { } marker) rtf.Append(RunRtf(new DocRun { Text = marker + "\t" }, tables));
        foreach (var run in paragraph.Runs)
        {
            if (run.Link is { } link && DocXml.IsPlainLink(link))
                rtf.Append($"{{\\field{{\\*\\fldinst{{HYPERLINK \"{Escape(link)}\"}}}}{{\\fldrslt{{{RunRtf(run with { Underline = true, Color = run.Color ?? "0563C1" }, tables)}}}}}}}");
            else rtf.Append(RunRtf(run, tables));
        }
        rtf.Append(inCell ? "\\par\n" : "\\par\n");
    }

    private static string RunRtf(DocRun run, Tables tables)
    {
        var look = new StringBuilder($"\\f{(run.Font is { } font ? tables.Fonts.IndexOf(font) : 0)}\\fs{(int)Math.Round((run.Size > 0 ? run.Size : RichDocument.DefaultSize) * 2)}");
        look.Append(run.Bold ? "\\b" : "").Append(run.Italic ? "\\i" : "").Append(run.Underline ? "\\ul" : "").Append(run.Strike ? "\\strike" : "");
        look.Append(run.Superscript ? "\\super" : run.Subscript ? "\\sub" : "");
        look.Append(run.Color is { } color ? $"\\cf{tables.Colors.IndexOf(color) + 1}" : "");
        return "{" + look + " " + Escape(run.Text).Replace("\t", "\\tab ").Replace("\n", "\\line ").Replace("\f", "\\page ") + "}";
    }

    /// <summary>Text for an RTF group: control characters escaped, everything beyond ASCII as a signed UTF-16 unit.</summary>
    private static string Escape(string text)
    {
        var rtf = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\' or '{' or '}': rtf.Append('\\').Append(c); break;
                case >= ' ' and < (char)127 or '\t' or '\n' or '\f': rtf.Append(c); break;
                case >= (char)127: rtf.Append("\\u").Append((short)c).Append('?'); break;
            }
        }
        return rtf.ToString();
    }

    private static string PictureRtf(DocPicture picture)
    {
        var (width, height) = Pictures.Fit(picture);
        var hex = Convert.ToHexString(picture.Data);
        var lines = string.Join("\n", Enumerable.Range(0, (hex.Length + 127) / 128).Select(i => hex.Substring(i * 128, Math.Min(128, hex.Length - i * 128))));
        return $"{{\\pict\\{(picture.Extension == "png" ? "pngblip" : "jpegblip")}\\picwgoal{(int)(width * 20)}\\pichgoal{(int)(height * 20)}\n{lines}}}";
    }
}
