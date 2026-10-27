using System.Globalization;
using System.Text;

namespace Split;

/// <summary>
/// Writes a document as a Word file: character formatting, alignment, tables, pictures and links. The page is A4
/// with 2.5 cm margins. Written by hand, so nothing leaves the PC and nothing needs Word.
/// </summary>
public static class DocxWriter
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private sealed class Builder
    {
        public List<string> Rels { get; } = [];
        public List<(string Name, byte[] Data)> Media { get; } = [];
        public Dictionary<string, string> Links { get; } = [];
        public int DrawingId;

        public string LinkId(string address)
        {
            if (Links.TryGetValue(address, out var known)) return known;
            var id = $"rId{Rels.Count + 10}";
            Rels.Add($"<Relationship Id=\"{id}\" Type=\"{Relationships}/hyperlink\" Target=\"{DocXml.Escape(address, true)}\" TargetMode=\"External\"/>");
            return Links[address] = id;
        }

        public (string Id, string Name) AddPicture(DocPicture picture)
        {
            var id = $"rId{Rels.Count + 10}";
            var name = $"image{Media.Count + 1}.{picture.Extension}";
            Rels.Add($"<Relationship Id=\"{id}\" Type=\"{Relationships}/image\" Target=\"media/{name}\"/>");
            Media.Add((name, picture.Data));
            return (id, name);
        }
    }

    public static void Write(RichDocument document, string output)
    {
        var builder = new Builder();
        var body = Blocks(document.Blocks, builder);
        var imageTypes = string.Concat(builder.Media.Select(m => Path.GetExtension(m.Name)[1..]).Distinct()
            .Select(ext => $"<Default Extension=\"{ext}\" ContentType=\"image/{ext}\"/>"));
        var entries = new List<(string, string)>
        {
            ("[Content_Types].xml", Header + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + $"<Default Extension=\"xml\" ContentType=\"application/xml\"/>{imageTypes}"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", Header + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + $"<Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/_rels/document.xml.rels", Header + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(builder.Rels) + "</Relationships>"),
            ("word/document.xml", Header + $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"{Relationships}\" "
                + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
                + $"xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>{(body.Length == 0 ? "<w:p/>" : body)}"
                + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1417\" w:right=\"1417\" w:bottom=\"1417\" w:left=\"1417\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>"
                + "</w:body></w:document>"),
        };
        var utf8 = new UTF8Encoding(false);
        Zip.Write(output, entries.Select(e => (e.Item1, utf8.GetBytes(e.Item2)))
            .Concat(builder.Media.Select(m => ($"word/media/{m.Name}", m.Data))));
    }

    // MARK: Body

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
                    xml.Append(TableXml(table, builder));
                    break;
                case DocPicture picture:
                    xml.Append("<w:p>").Append(Drawing(picture, builder)).Append("</w:p>");
                    break;
                case DocPageBreak:
                    xml.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                    break;
            }
        }
        return xml.ToString();
    }

    private static string TableXml(DocTable table, Builder builder)
    {
        var columns = Math.Max(1, table.Rows.Select(r => r.Sum(c => Math.Max(1, c.Span))).DefaultIfEmpty(1).Max());
        var border = "w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"999999\"";
        var xml = new StringBuilder("<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>"
            + $"<w:top {border}/><w:left {border}/><w:bottom {border}/><w:right {border}/><w:insideH {border}/><w:insideV {border}/></w:tblBorders></w:tblPr><w:tblGrid>"
            + string.Concat(Enumerable.Repeat("<w:gridCol/>", columns)) + "</w:tblGrid>");
        foreach (var row in table.Rows)
        {
            xml.Append("<w:tr>");
            var used = 0;
            foreach (var cell in row)
            {
                var span = Math.Max(1, cell.Span);
                var content = Blocks(cell.Blocks, builder);
                xml.Append($"<w:tc><w:tcPr>{(span > 1 ? $"<w:gridSpan w:val=\"{span}\"/>" : "")}</w:tcPr>{(content.Length == 0 ? "<w:p/>" : content)}</w:tc>");
                used += span;
            }
            for (; used < columns; used++) xml.Append("<w:tc><w:p/></w:tc>");
            xml.Append("</w:tr>");
        }
        return xml.Append("</w:tbl><w:p/>").ToString();
    }

    private static string ParagraphXml(DocParagraph paragraph, Builder builder)
    {
        var properties = paragraph.Alignment switch
        {
            DocAlignment.Center => "<w:jc w:val=\"center\"/>",
            DocAlignment.Right => "<w:jc w:val=\"right\"/>",
            DocAlignment.Justify => "<w:jc w:val=\"both\"/>",
            _ => "",
        };
        if (paragraph.SpacingBefore > 0 || paragraph.SpacingAfter > 0)
            properties = $"<w:spacing w:before=\"{(int)(paragraph.SpacingBefore * 20)}\" w:after=\"{(int)(paragraph.SpacingAfter * 20)}\"/>" + properties;
        if (paragraph.Indent > 0) properties += $"<w:ind w:left=\"{(int)(paragraph.Indent * 20)}\"/>";
        if (paragraph.HeadingLevel > 0) properties = $"<w:keepNext/><w:outlineLvl w:val=\"{paragraph.HeadingLevel - 1}\"/>" + properties;
        var xml = new StringBuilder("<w:p>" + (properties.Length == 0 ? "" : $"<w:pPr>{properties}</w:pPr>"));
        if (paragraph.Marker is { } marker) xml.Append(RunXml(marker + "\t", new DocRun { Text = marker }));
        string? open = null;
        foreach (var run in paragraph.Runs)
        {
            var link = run.Link is { } address && DocXml.IsPlainLink(address) ? address : null;
            if (open != link)
            {
                if (open is not null) xml.Append("</w:hyperlink>");
                if (link is not null) xml.Append($"<w:hyperlink r:id=\"{builder.LinkId(link)}\">");
                open = link;
            }
            xml.Append(RunXml(run.Text, run, link is not null));
        }
        if (open is not null) xml.Append("</w:hyperlink>");
        return xml.Append("</w:p>").ToString();
    }

    private static string RunXml(string content, DocRun run, bool isLink = false)
    {
        var font = DocXml.Escape(run.Font ?? RichDocument.DefaultFont, true);
        var size = run.Size > 0 ? run.Size : RichDocument.DefaultSize;
        var properties = $"<w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:cs=\"{font}\"/>"
            + (run.Bold ? "<w:b/>" : "") + (run.Italic ? "<w:i/>" : "")
            + $"<w:sz w:val=\"{(int)Math.Round(size * 2)}\"/>"
            + (run.Underline || isLink ? "<w:u w:val=\"single\"/>" : "") + (run.Strike ? "<w:strike/>" : "");
        if (run.Color is { } color && color != "000000") properties += $"<w:color w:val=\"{color}\"/>";
        else if (isLink) properties += "<w:color w:val=\"0563C1\"/>";
        if (run.Superscript) properties += "<w:vertAlign w:val=\"superscript\"/>";
        else if (run.Subscript) properties += "<w:vertAlign w:val=\"subscript\"/>";
        var rPr = $"<w:rPr>{properties}</w:rPr>";

        // Tabs and line breaks are elements of their own in Word.
        var xml = new StringBuilder();
        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length > 0) xml.Append($"<w:r>{rPr}<w:t xml:space=\"preserve\">{DocXml.Escape(buffer.ToString(), false)}</w:t></w:r>");
            buffer.Clear();
        }
        foreach (var c in content)
        {
            switch (c)
            {
                case '\t': Flush(); xml.Append($"<w:r>{rPr}<w:tab/></w:r>"); break;
                case '\n' or (char)0x2028 or '\u000B': Flush(); xml.Append($"<w:r>{rPr}<w:br/></w:r>"); break;
                case '\f': Flush(); xml.Append("<w:r><w:br w:type=\"page\"/></w:r>"); break;
                default: buffer.Append(c); break;
            }
        }
        Flush();
        return xml.ToString();
    }

    private static string Drawing(DocPicture picture, Builder builder)
    {
        var (id, name) = builder.AddPicture(picture);
        var drawing = ++builder.DrawingId;
        var (width, height) = Pictures.Fit(picture);
        var cx = (long)(width * 12700);
        var cy = (long)(height * 12700);
        return $"<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/>"
            + $"<wp:docPr id=\"{drawing}\" name=\"Picture {drawing}\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">"
            + $"<pic:pic><pic:nvPicPr><pic:cNvPr id=\"{drawing}\" name=\"{name}\"/><pic:cNvPicPr/></pic:nvPicPr>"
            + $"<pic:blipFill><a:blip r:embed=\"{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
            + $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
            + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";
    }
}
