using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using static Split.DocXml;

namespace Split;

/// <summary>
/// A Word file read straight from its package. Pictures kept inside the file come along; pictures and fields
/// that point at addresses (linked pictures, INCLUDEPICTURE) are never followed.
/// </summary>
public static class DocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private sealed class Context(ZipArchive archive, Dictionary<string, (string Target, bool External)> relationships)
    {
        public ZipArchive Archive { get; } = archive;
        public Dictionary<string, (string Target, bool External)> Relationships { get; } = relationships;
        public Dictionary<string, bool> Numbered { get; } = [];
        public Dictionary<(string, int), int> Counters { get; } = [];
        public Dictionary<string, int> Headings { get; } = [];
        public double DefaultSize { get; set; } = RichDocument.DefaultSize;
    }

    public static RichDocument Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var main = Part(archive, "word/document.xml") ?? throw new InvalidDataException("word/document.xml is missing");
        var relationships = new Dictionary<string, (string, bool)>();
        if (Part(archive, "word/_rels/document.xml.rels") is { } rels)
            foreach (var item in rels.Descendants().Where(e => e.Name.LocalName == "Relationship"))
                if (Attribute(item, "Id") is { } id && Attribute(item, "Target") is { } target)
                    relationships[id] = (target, Attribute(item, "TargetMode") == "External");
        var context = new Context(archive, relationships);
        ReadStyles(context);
        ReadNumbering(context);

        var document = new RichDocument();
        if (main.Root?.Element(W + "body") is { } body) Blocks(body, document.Blocks, context);
        return document;
    }

    private static void ReadStyles(Context context)
    {
        if (Part(context.Archive, "word/styles.xml") is not { Root: { } styles }) return;
        if (styles.Element(W + "docDefaults")?.Descendants(W + "sz").FirstOrDefault() is { } size && double.TryParse(Attribute(size, "val"), CultureInfo.InvariantCulture, out var half))
            context.DefaultSize = half / 2;
        foreach (var style in styles.Elements(W + "style"))
        {
            if (Attribute(style, "styleId") is not { } id) continue;
            var name = (Attribute(style, "name") ?? "").Replace(" ", "").ToLowerInvariant();
            if (name.StartsWith("heading") && int.TryParse(name[7..], out var level) && level is >= 1 and <= 6) context.Headings[id] = level;
            else if (name == "title") context.Headings[id] = 1;
        }
    }

    /// <summary>Which numbering definitions count (1, 2, 3) and which are bullets.</summary>
    private static void ReadNumbering(Context context)
    {
        if (Part(context.Archive, "word/numbering.xml") is not { Root: { } numbering }) return;
        var abstractCounted = new Dictionary<string, Dictionary<int, bool>>();
        foreach (var definition in numbering.Elements(W + "abstractNum"))
        {
            if (Attribute(definition, "abstractNumId") is not { } id) continue;
            abstractCounted[id] = definition.Elements(W + "lvl").ToDictionary(
                level => int.TryParse(Attribute(level, "ilvl"), out var number) ? number : 0,
                level => Attribute(level.Element(W + "numFmt") ?? level, "val") is { } format && format != "bullet" && format != "none");
        }
        foreach (var number in numbering.Elements(W + "num"))
        {
            if (Attribute(number, "numId") is not { } id || number.Element(W + "abstractNumId") is not { } link || Attribute(link, "val") is not { } abstractId) continue;
            if (!abstractCounted.TryGetValue(abstractId, out var levels)) continue;
            for (var level = 0; level < 9; level++) context.Numbered[$"{id}/{level}"] = levels.GetValueOrDefault(level);
        }
    }

    private static void Blocks(XElement container, List<DocBlock> blocks, Context context)
    {
        foreach (var element in container.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "p":
                    Paragraph(element, blocks, context);
                    break;
                case "tbl":
                    blocks.Add(ReadTable(element, context));
                    break;
                case "sdt":
                    if (element.Element(W + "sdtContent") is { } content) Blocks(content, blocks, context);
                    break;
                case "ins" or "customXml" or "smartTag":
                    Blocks(element, blocks, context);
                    break;
            }
        }
    }

    private static DocTable ReadTable(XElement element, Context context)
    {
        var table = new DocTable();
        foreach (var row in element.Elements(W + "tr"))
        {
            var cells = new List<DocCell>();
            foreach (var source in row.Elements(W + "tc"))
            {
                var span = source.Element(W + "tcPr")?.Element(W + "gridSpan") is { } grid && int.TryParse(Attribute(grid, "val"), out var n) ? n : 1;
                var cell = new DocCell { Span = Math.Max(1, span) };
                Blocks(source, cell.Blocks, context);
                cells.Add(cell);
            }
            table.Rows.Add(cells);
        }
        return table;
    }

    private static void Paragraph(XElement element, List<DocBlock> blocks, Context context)
    {
        var paragraph = new DocParagraph();
        var properties = element.Element(W + "pPr");
        if (properties is not null)
        {
            paragraph.Alignment = Attribute(properties.Element(W + "jc") ?? properties, "val") switch
            {
                "center" => DocAlignment.Center, "right" or "end" => DocAlignment.Right, "both" or "distribute" => DocAlignment.Justify, _ => DocAlignment.Left,
            };
            if (properties.Element(W + "ind") is { } indent && double.TryParse(Attribute(indent, "left") ?? Attribute(indent, "start"), CultureInfo.InvariantCulture, out var twips))
                paragraph.Indent = twips / 20;
            if (properties.Element(W + "spacing") is { } spacing)
            {
                if (double.TryParse(Attribute(spacing, "before"), CultureInfo.InvariantCulture, out var before)) paragraph.SpacingBefore = before / 20;
                if (double.TryParse(Attribute(spacing, "after"), CultureInfo.InvariantCulture, out var after)) paragraph.SpacingAfter = after / 20;
            }
            if (Attribute(properties.Element(W + "pStyle") ?? properties, "val") is { } style && context.Headings.TryGetValue(style, out var level)) paragraph.HeadingLevel = level;
            if (properties.Element(W + "numPr") is { } numbering && Attribute(numbering.Element(W + "numId") ?? numbering, "val") is { } numId && numId != "0")
            {
                var depth = int.TryParse(Attribute(numbering.Element(W + "ilvl") ?? numbering, "val"), out var l) ? l : 0;
                var counted = context.Numbered.GetValueOrDefault($"{numId}/{depth}");
                var next = context.Counters[(numId, depth)] = context.Counters.GetValueOrDefault((numId, depth)) + 1;
                paragraph.Marker = counted ? next + "." : "•";
                paragraph.ListLevel = depth + 1;
                if (paragraph.Indent == 0) paragraph.Indent = 18 * (depth + 1);
            }
        }
        var start = blocks.Count;
        Inline(element, paragraph, blocks, context, null);
        // A paragraph that only held a picture or a page break leaves no empty paragraph behind.
        if (paragraph.Runs.Count > 0 || blocks.Count == start) blocks.Add(paragraph);
    }

    private static void Inline(XElement container, DocParagraph paragraph, List<DocBlock> blocks, Context context, string? link)
    {
        foreach (var child in container.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "r":
                    Run(child, paragraph, blocks, context, link);
                    break;
                case "hyperlink":
                    string? address = null;
                    if (Attribute(child, "id") is { } id && context.Relationships.TryGetValue(id, out var target) && target.External && IsPlainLink(target.Target)) address = target.Target;
                    Inline(child, paragraph, blocks, context, address);
                    break;
                case "ins" or "smartTag" or "sdt" or "sdtContent" or "fldSimple" or "customXml":
                    Inline(child.Element(W + "sdtContent") ?? child, paragraph, blocks, context, link);
                    break;
            }
        }
    }

    private static void Run(XElement run, DocParagraph paragraph, List<DocBlock> blocks, Context context, string? link)
    {
        var look = new DocRun { Link = link };
        if (run.Element(W + "rPr") is { } properties)
        {
            if (properties.Element(W + "rFonts") is { } fonts) look.Font = Attribute(fonts, "ascii") ?? Attribute(fonts, "hAnsi");
            look.Bold = Toggle(properties.Element(W + "b"));
            look.Italic = Toggle(properties.Element(W + "i"));
            look.Strike = Toggle(properties.Element(W + "strike"));
            look.Underline = properties.Element(W + "u") is { } u && Attribute(u, "val") is not ("none" or "0");
            if (properties.Element(W + "sz") is { } size && double.TryParse(Attribute(size, "val"), CultureInfo.InvariantCulture, out var half)) look.Size = half / 2;
            if (Attribute(properties.Element(W + "color") ?? properties, "val") is { Length: 6 } color && color != "auto" && color != "000000") look.Color = color.ToUpperInvariant();
            if (Attribute(properties.Element(W + "vertAlign") ?? properties, "val") is { } align)
            {
                look.Superscript = align == "superscript";
                look.Subscript = align == "subscript";
            }
        }
        void Text(string text)
        {
            if (text.Length == 0) return;
            if (paragraph.Runs.Count > 0 && paragraph.Runs[^1].SameLook(look)) paragraph.Runs[^1].Text += text;
            else paragraph.Runs.Add(look with { Text = text });
        }
        foreach (var part in run.Elements())
        {
            switch (part.Name.LocalName)
            {
                case "t":
                    Text(part.Value);
                    break;
                case "tab":
                    Text("\t");
                    break;
                case "br" or "cr":
                    if (Attribute(part, "type") == "page")
                    {
                        if (paragraph.Runs.Count > 0) blocks.Add(Clone(paragraph));
                        blocks.Add(new DocPageBreak());
                        paragraph.Runs.Clear();
                    }
                    else Text("\n");
                    break;
                case "drawing" or "pict":
                    if (Picture(part, context) is { } picture)
                    {
                        if (paragraph.Runs.Count > 0 || paragraph.Marker is not null) blocks.Add(Clone(paragraph));
                        blocks.Add(picture);
                        paragraph.Runs.Clear();
                        paragraph.Marker = null;
                    }
                    break;
                case "AlternateContent":
                    foreach (var choice in part.Elements().Where(e => e.Name.LocalName == "Choice").Take(1))
                        foreach (var inner in choice.Elements().Where(e => e.Name.LocalName == "drawing"))
                            if (Picture(inner, context) is { } alternative)
                            {
                                if (paragraph.Runs.Count > 0) blocks.Add(Clone(paragraph));
                                blocks.Add(alternative);
                                paragraph.Runs.Clear();
                            }
                    break;
            }
        }
    }

    /// <summary>The text collected so far, set aside as a paragraph of its own while the builder goes on.</summary>
    private static DocParagraph Clone(DocParagraph source)
    {
        var copy = new DocParagraph
        {
            Alignment = source.Alignment, HeadingLevel = source.HeadingLevel, Marker = source.Marker, ListLevel = source.ListLevel,
            SpacingBefore = source.SpacingBefore, SpacingAfter = source.SpacingAfter, Indent = source.Indent,
        };
        copy.Runs.AddRange(source.Runs.Select(r => r with { }));
        return copy;
    }

    private static bool Toggle(XElement? element) => element is not null && Attribute(element, "val") is not ("0" or "false" or "off");

    /// <summary>A picture stored in the file; a linked one (an address) is ignored.</summary>
    private static DocPicture? Picture(XElement drawing, Context context)
    {
        var blip = drawing.Descendants().FirstOrDefault(e => e.Name.LocalName == "blip");
        var id = blip is null ? drawing.Descendants().FirstOrDefault(e => e.Name.LocalName == "imagedata") is { } legacy
            ? legacy.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value : null
            : blip.Attributes().FirstOrDefault(a => a.Name.LocalName == "embed")?.Value;
        if (id is null || !context.Relationships.TryGetValue(id, out var target) || target.External) return null;
        var name = target.Target.TrimStart('/').Replace('\\', '/');
        var entry = name.StartsWith("word/", StringComparison.Ordinal) ? name : "word/" + name;
        // Keep the entry name inside the package: no ".." out of the word folder.
        if (entry.Contains("..", StringComparison.Ordinal) || Bytes(context.Archive, entry) is not { } data) return null;
        double width = 0, height = 0;
        if (drawing.Descendants().FirstOrDefault(e => e.Name.LocalName == "extent") is { } extent)
        {
            if (double.TryParse(Attribute(extent, "cx"), CultureInfo.InvariantCulture, out var cx)) width = cx / 12700;
            if (double.TryParse(Attribute(extent, "cy"), CultureInfo.InvariantCulture, out var cy)) height = cy / 12700;
        }
        return Pictures.FromBytes(data, width, height);
    }
}
