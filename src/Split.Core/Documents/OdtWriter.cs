using System.Globalization;
using System.Text;

namespace Split;

/// <summary>Writes an OpenDocument text: paragraphs, character formatting, links, tables and pictures, on A4 with 2.5 cm margins.</summary>
public static class OdtWriter
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n";

    private const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" "
        + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" "
        + "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" "
        + "xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" office:version=\"1.2\"";

    private sealed class Builder
    {
        public Dictionary<string, string> Styles { get; } = [];
        public int Tables;
        public List<(string Name, byte[] Data, string Mime)> Media { get; } = [];

        /// <summary>One automatic style per distinct set of properties.</summary>
        public string Style(string family, string properties)
        {
            var key = family + properties;
            if (Styles.TryGetValue(key, out var known)) return known;
            var name = (family == "paragraph" ? "P" : "T") + (Styles.Count + 1);
            Styles[key] = name;
            return name;
        }

        public string Definitions() => string.Concat(Styles.Select(s =>
        {
            var family = s.Key.StartsWith("paragraph", StringComparison.Ordinal) ? "paragraph" : "text";
            var properties = s.Key[family.Length..];
            return $"<style:style style:name=\"{s.Value}\" style:family=\"{family}\">{properties}</style:style>";
        }));
    }

    public static void Write(RichDocument document, string output)
    {
        var builder = new Builder();
        var body = Blocks(document.Blocks, builder);
        var utf8 = new UTF8Encoding(false);
        var mimetype = "application/vnd.oasis.opendocument.text";
        var manifest = Header + "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">"
            + $"<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"{mimetype}\"/>"
            + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>"
            + "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>"
            + string.Concat(builder.Media.Select(m => $"<manifest:file-entry manifest:full-path=\"Pictures/{m.Name}\" manifest:media-type=\"{m.Mime}\"/>"))
            + "</manifest:manifest>";
        var content = Header + $"<office:document-content {Namespaces}><office:automatic-styles>{builder.Definitions()}"
            + "<style:style style:name=\"Cell\" style:family=\"table-cell\"><style:table-cell-properties fo:padding=\"2pt\" fo:border=\"0.5pt solid #999999\"/></style:style>"
            + $"</office:automatic-styles><office:body><office:text>{body}</office:text></office:body></office:document-content>";
        var styles = Header + $"<office:document-styles {Namespaces}><office:styles>"
            + $"<style:default-style style:family=\"paragraph\"><style:text-properties style:font-name=\"{RichDocument.DefaultFont}\" fo:font-family=\"{RichDocument.DefaultFont}\" fo:font-size=\"{RichDocument.DefaultSize}pt\"/></style:default-style>"
            + "</office:styles><office:automatic-styles><style:page-layout style:name=\"A4\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\" "
            + "fo:margin-top=\"2.5cm\" fo:margin-bottom=\"2.5cm\" fo:margin-left=\"2.5cm\" fo:margin-right=\"2.5cm\"/></style:page-layout></office:automatic-styles>"
            + "<office:master-styles><style:master-page style:name=\"Standard\" style:page-layout-name=\"A4\"/></office:master-styles></office:document-styles>";

        // The mimetype comes first and stays uncompressed: readers find the kind of file in the first bytes.
        Zip.Write(output,
            new (string, byte[])[] { ("mimetype", Encoding.ASCII.GetBytes(mimetype)), ("META-INF/manifest.xml", utf8.GetBytes(manifest)),
                                     ("content.xml", utf8.GetBytes(content)), ("styles.xml", utf8.GetBytes(styles)) }
                .Concat(builder.Media.Select(m => ($"Pictures/{m.Name}", m.Data))),
            new HashSet<string> { "mimetype" });
    }

    private static string Blocks(IEnumerable<DocBlock> blocks, Builder builder)
    {
        var xml = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocParagraph paragraph:
                    xml.Append(ParagraphXml(paragraph, builder));
                    break;
                case DocTable table:
                    var columns = Math.Max(1, table.Rows.Select(r => r.Sum(c => Math.Max(1, c.Span))).DefaultIfEmpty(1).Max());
                    xml.Append($"<table:table table:name=\"Table{++builder.Tables}\"><table:table-column table:number-columns-repeated=\"{columns}\"/>");
                    foreach (var row in table.Rows)
                    {
                        xml.Append("<table:table-row>");
                        var used = 0;
                        foreach (var cell in row)
                        {
                            var span = Math.Max(1, cell.Span);
                            var content = Blocks(cell.Blocks, builder);
                            xml.Append($"<table:table-cell table:style-name=\"Cell\" office:value-type=\"string\"{(span > 1 ? $" table:number-columns-spanned=\"{span}\"" : "")}>"
                                + (content.Length == 0 ? "<text:p/>" : content) + "</table:table-cell>");
                            used += span;
                            if (span > 1) xml.Append(string.Concat(Enumerable.Repeat("<table:covered-table-cell/>", span - 1)));
                        }
                        for (; used < columns; used++) xml.Append("<table:table-cell table:style-name=\"Cell\" office:value-type=\"string\"><text:p/></table:table-cell>");
                        xml.Append("</table:table-row>");
                    }
                    xml.Append("</table:table>");
                    break;
                case DocPicture picture:
                    xml.Append("<text:p>").Append(Frame(picture, builder)).Append("</text:p>");
                    break;
                case DocPageBreak:
                    xml.Append($"<text:p text:style-name=\"{builder.Style("paragraph", "<style:paragraph-properties fo:break-after=\"page\"/>")}\"/>");
                    break;
            }
        }
        return xml.ToString();
    }

    private static string Points(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    private static string ParagraphXml(DocParagraph paragraph, Builder builder)
    {
        var properties = new StringBuilder();
        if (paragraph.Alignment != DocAlignment.Left)
            properties.Append($" fo:text-align=\"{paragraph.Alignment switch { DocAlignment.Center => "center", DocAlignment.Right => "end", _ => "justify" }}\"");
        if (paragraph.Indent > 0) properties.Append($" fo:margin-left=\"{Points(paragraph.Indent)}\"");
        if (paragraph.SpacingBefore > 0) properties.Append($" fo:margin-top=\"{Points(paragraph.SpacingBefore)}\"");
        if (paragraph.SpacingAfter > 0) properties.Append($" fo:margin-bottom=\"{Points(paragraph.SpacingAfter)}\"");
        var style = properties.Length == 0 ? "" : $" text:style-name=\"{builder.Style("paragraph", $"<style:paragraph-properties{properties}/>")}\"";
        var tag = paragraph.HeadingLevel > 0 ? $"text:h text:outline-level=\"{paragraph.HeadingLevel}\"" : "text:p";
        var xml = new StringBuilder($"<{tag}{style}>");
        if (paragraph.Marker is { } marker) xml.Append(Escape(marker)).Append("<text:tab/>");
        foreach (var run in paragraph.Runs)
        {
            var text = new StringBuilder($"<text:span text:style-name=\"{builder.Style("text", RunProperties(run))}\">");
            foreach (var part in Pieces(run.Text)) text.Append(part);
            text.Append("</text:span>");
            if (run.Link is { } link && DocXml.IsPlainLink(link))
                xml.Append($"<text:a xlink:type=\"simple\" xlink:href=\"{DocXml.Escape(link, true)}\">{text}</text:a>");
            else xml.Append(text);
        }
        return xml.Append(paragraph.HeadingLevel > 0 ? "</text:h>" : "</text:p>").ToString();
    }

    private static string Escape(string text) => DocXml.Escape(text, false);

    /// <summary>Text with the tabs, line breaks and runs of spaces ODF wants as elements of their own.</summary>
    private static IEnumerable<string> Pieces(string text)
    {
        var plain = new StringBuilder();
        var spaces = 0;
        string Flush()
        {
            var result = Escape(plain.ToString());
            plain.Clear();
            return result;
        }
        foreach (var c in text)
        {
            if (c == ' ')
            {
                if (spaces++ == 0) plain.Append(' ');
                continue;
            }
            if (spaces > 1)
            {
                yield return Flush();
                yield return $"<text:s text:c=\"{spaces - 1}\"/>";
            }
            spaces = 0;
            switch (c)
            {
                case '\t': yield return Flush(); yield return "<text:tab/>"; break;
                case '\n' or (char)0x2028: yield return Flush(); yield return "<text:line-break/>"; break;
                case '\f': break;
                default: plain.Append(c); break;
            }
        }
        if (spaces > 1)
        {
            yield return Flush();
            yield return $"<text:s text:c=\"{spaces - 1}\"/>";
        }
        yield return Flush();
    }

    private static string RunProperties(DocRun run)
    {
        var font = DocXml.Escape(run.Font ?? RichDocument.DefaultFont, true);
        var properties = new StringBuilder($" style:font-name=\"{font}\" fo:font-family=\"{font}\" fo:font-size=\"{Points(run.Size > 0 ? run.Size : RichDocument.DefaultSize)}\"");
        if (run.Bold) properties.Append(" fo:font-weight=\"bold\"");
        if (run.Italic) properties.Append(" fo:font-style=\"italic\"");
        if (run.Underline || run.Link is not null) properties.Append(" style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\"");
        if (run.Strike) properties.Append(" style:text-line-through-style=\"solid\"");
        if (run.Color is { } color) properties.Append($" fo:color=\"#{color}\"");
        else if (run.Link is not null) properties.Append(" fo:color=\"#0563C1\"");
        if (run.Superscript) properties.Append(" style:text-position=\"super 58%\"");
        else if (run.Subscript) properties.Append(" style:text-position=\"sub 58%\"");
        return $"<style:text-properties{properties}/>";
    }

    private static string Frame(DocPicture picture, Builder builder)
    {
        var name = $"image{builder.Media.Count + 1}.{(picture.Extension == "jpeg" ? "jpg" : picture.Extension)}";
        builder.Media.Add((name, picture.Data, picture.Mime));
        var (width, height) = Pictures.Fit(picture);
        return $"<draw:frame draw:name=\"Picture {builder.Media.Count}\" text:anchor-type=\"as-char\" svg:width=\"{Points(width)}\" svg:height=\"{Points(height)}\">"
            + $"<draw:image xlink:href=\"Pictures/{name}\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"/></draw:frame>";
    }
}
