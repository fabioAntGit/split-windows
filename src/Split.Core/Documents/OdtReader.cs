using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Split.DocXml;

namespace Split;

/// <summary>An OpenDocument text read straight from its package; pictures kept inside it come along, nothing is fetched.</summary>
public static class OdtReader
{
    private sealed record Look
    {
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public bool Underline { get; init; }
        public bool Strike { get; init; }
        public bool Sup { get; init; }
        public bool Sub { get; init; }
        public string? Color { get; init; }
        public string? Font { get; init; }
        public double Size { get; init; }
        public DocAlignment? Align { get; init; }
        public bool PageBreak { get; init; }
        public bool PageBreakAfter { get; init; }
        public double Indent { get; init; }
        public double Before { get; init; }
        public double After { get; init; }
    }

    private sealed class Context(ZipArchive archive)
    {
        public ZipArchive Archive { get; } = archive;
        public Dictionary<string, XElement> Styles { get; } = [];
        public Dictionary<string, Look> Resolved { get; } = [];
        public HashSet<string> NumberedLists { get; } = [];
    }

    public static RichDocument Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var content = Part(archive, "content.xml") ?? throw new InvalidDataException("content.xml is missing");
        var context = new Context(archive);
        foreach (var part in new[] { Part(archive, "styles.xml"), content })
        {
            if (part?.Root is null) continue;
            foreach (var style in part.Root.Descendants().Where(e => e.Name.LocalName == "style"))
                if (Attribute(style, "name") is { } name) context.Styles[name] = style;
            foreach (var list in part.Root.Descendants().Where(e => e.Name.LocalName == "list-style"))
                if (Attribute(list, "name") is { } name && list.Elements().FirstOrDefault()?.Name.LocalName == "list-level-style-number") context.NumberedLists.Add(name);
        }
        var document = new RichDocument();
        if (content.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "body")?.Elements().FirstOrDefault(e => e.Name.LocalName == "text") is { } text)
            Blocks(text, document.Blocks, context, null, 0);
        return document;
    }

    // MARK: Styles

    private static double? Points(string? text)
    {
        if (text is null) return null;
        var match = Regex.Match(text.Trim(), @"^(-?\d+(?:\.\d+)?)(pt|cm|mm|in|px)?$");
        if (!match.Success) return null;
        var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch { "cm" => number * 28.3465, "mm" => number * 2.83465, "in" => number * 72, "px" => number * 0.75, _ => number };
    }

    private static Look Resolve(string? name, Context context, int depth = 0)
    {
        if (name is null || !context.Styles.TryGetValue(name, out var style) || depth > 8) return new Look();
        if (context.Resolved.TryGetValue(name, out var known)) return known;
        var look = Resolve(Attribute(style, "parent-style-name"), context, depth + 1);
        if (Child(style, "text-properties") is { } text)
        {
            if (Attribute(text, "font-weight") is { } weight) look = look with { Bold = weight == "bold" || (int.TryParse(weight, out var w) && w >= 600) };
            if (Attribute(text, "font-style") is { } italic) look = look with { Italic = italic is "italic" or "oblique" };
            if (Attribute(text, "text-underline-style") is { } underline) look = look with { Underline = underline != "none" };
            if (Attribute(text, "text-line-through-style") is { } strike) look = look with { Strike = strike != "none" };
            if (Attribute(text, "color") is { Length: 7 } color && color[0] == '#') look = look with { Color = color[1..].ToUpperInvariant() };
            if (Points(Attribute(text, "font-size")) is { } size) look = look with { Size = size };
            if ((Attribute(text, "font-family") ?? Attribute(text, "font-name")) is { } font) look = look with { Font = font.Trim('\'', '"') };
            if (Attribute(text, "text-position") is { } position)
                look = look with { Sup = position.StartsWith("super"), Sub = position.StartsWith("sub") };
        }
        if (Child(style, "paragraph-properties") is { } paragraph)
        {
            if (Attribute(paragraph, "text-align") is { } align)
                look = look with { Align = align switch { "center" => DocAlignment.Center, "end" or "right" => DocAlignment.Right, "justify" => DocAlignment.Justify, _ => DocAlignment.Left } };
            if (Attribute(paragraph, "break-before") == "page") look = look with { PageBreak = true };
            if (Attribute(paragraph, "break-after") == "page") look = look with { PageBreakAfter = true };
            if (Points(Attribute(paragraph, "margin-left")) is { } left) look = look with { Indent = left };
            if (Points(Attribute(paragraph, "margin-top")) is { } top) look = look with { Before = top };
            if (Points(Attribute(paragraph, "margin-bottom")) is { } bottom) look = look with { After = bottom };
        }
        return context.Resolved[name] = look;
    }

    // MARK: Body

    private static void Blocks(XElement container, List<DocBlock> blocks, Context context, string? listStyle, int level)
    {
        foreach (var element in container.Elements()) One(element, blocks, context, listStyle, level);
    }

    private static void One(XElement element, List<DocBlock> blocks, Context context, string? listStyle, int level)
    {
        switch (element.Name.LocalName)
        {
            case "p" or "h":
                Paragraph(element, blocks, context, null, level);
                break;
            case "list":
                var style = Attribute(element, "style-name") ?? listStyle;
                var number = 1;
                foreach (var item in element.Elements().Where(e => e.Name.LocalName is "list-item" or "list-header"))
                {
                    foreach (var inner in item.Elements())
                    {
                        if (inner.Name.LocalName is not ("p" or "h")) One(inner, blocks, context, style, level + 1);
                        else Paragraph(inner, blocks, context, style is not null && context.NumberedLists.Contains(style) ? number++ + "." : "•", level + 1);
                    }
                }
                break;
            case "table":
                blocks.Add(ReadTable(element, context));
                break;
            case "section" or "table-of-content" or "index-body":
                Blocks(element, blocks, context, listStyle, level);
                break;
        }
    }

    private static DocTable ReadTable(XElement element, Context context)
    {
        var table = new DocTable();
        foreach (var row in element.Descendants().Where(e => e.Name.LocalName == "table-row" && e.Ancestors().First(a => a.Name.LocalName == "table") == element))
        {
            var cells = new List<DocCell>();
            foreach (var source in row.Elements().Where(e => e.Name.LocalName == "table-cell"))
            {
                var cell = new DocCell { Span = int.TryParse(Attribute(source, "number-columns-spanned"), out var span) ? Math.Max(1, span) : 1 };
                Blocks(source, cell.Blocks, context, null, 0);
                cells.Add(cell);
            }
            table.Rows.Add(cells);
        }
        return table;
    }

    private static void Paragraph(XElement element, List<DocBlock> blocks, Context context, string? marker, int level)
    {
        var look = Resolve(Attribute(element, "style-name"), context);
        if (look.PageBreak) blocks.Add(new DocPageBreak());
        var heading = element.Name.LocalName == "h" ? int.TryParse(Attribute(element, "outline-level"), out var n) ? Math.Clamp(n, 1, 6) : 1 : 0;
        var paragraph = new DocParagraph
        {
            Alignment = look.Align ?? DocAlignment.Left, HeadingLevel = heading, Marker = marker, ListLevel = level,
            Indent = look.Indent > 0 ? look.Indent : marker is null ? 0 : 18 * level, SpacingBefore = look.Before, SpacingAfter = look.After,
        };
        var start = blocks.Count;
        Inline(element, paragraph, blocks, context, look, null);
        if (paragraph.Runs.Count > 0 || blocks.Count == start) blocks.Add(paragraph);
        if (look.PageBreakAfter) blocks.Add(new DocPageBreak());
    }

    private static void Inline(XElement container, DocParagraph paragraph, List<DocBlock> blocks, Context context, Look look, string? link)
    {
        void Add(string text, Look with)
        {
            if (text.Length == 0) return;
            var run = new DocRun
            {
                Text = text, Bold = with.Bold, Italic = with.Italic, Underline = with.Underline || link is not null, Strike = with.Strike,
                Superscript = with.Sup, Subscript = with.Sub, Color = with.Color, Font = with.Font, Size = with.Size, Link = link,
            };
            if (paragraph.Runs.Count > 0 && paragraph.Runs[^1].SameLook(run)) paragraph.Runs[^1].Text += text;
            else paragraph.Runs.Add(run);
        }
        foreach (var node in container.Nodes())
        {
            switch (node)
            {
                case XText text:
                    var collapsed = Regex.Replace(text.Value, @"[ \t\r\n]+", " ");
                    if (paragraph.Runs.Count == 0 || paragraph.Runs[^1].Text.EndsWith(' ') || paragraph.Runs[^1].Text.EndsWith('\n')) collapsed = collapsed.TrimStart(' ');
                    Add(collapsed, look);
                    break;
                case XElement element:
                    switch (element.Name.LocalName)
                    {
                        case "span":
                            Inline(element, paragraph, blocks, context, Merge(look, Resolve(Attribute(element, "style-name"), context)), link);
                            break;
                        case "a":
                            var address = Attribute(element, "href");
                            Inline(element, paragraph, blocks, context, look, address is not null && IsPlainLink(address) ? address : link);
                            break;
                        case "s":
                            Add(new string(' ', int.TryParse(Attribute(element, "c"), out var count) ? Math.Clamp(count, 1, 1000) : 1), look);
                            break;
                        case "tab":
                            Add("\t", look);
                            break;
                        case "line-break":
                            Add("\n", look);
                            break;
                        case "frame":
                            if (Picture(element, context) is { } picture)
                            {
                                if (paragraph.Runs.Count > 0) blocks.Add(Copy(paragraph));
                                blocks.Add(picture);
                                paragraph.Runs.Clear();
                                paragraph.Marker = null;
                            }
                            break;
                    }
                    break;
            }
        }
    }

    /// <summary>A span changes only what its own style says; everything else stays as the paragraph has it.</summary>
    private static Look Merge(Look outer, Look span) => outer with
    {
        Bold = outer.Bold || span.Bold, Italic = outer.Italic || span.Italic, Underline = outer.Underline || span.Underline, Strike = outer.Strike || span.Strike,
        Sup = span.Sup || outer.Sup, Sub = span.Sub || outer.Sub, Color = span.Color ?? outer.Color, Font = span.Font ?? outer.Font,
        Size = span.Size > 0 ? span.Size : outer.Size,
    };

    private static DocParagraph Copy(DocParagraph source)
    {
        var copy = new DocParagraph
        {
            Alignment = source.Alignment, HeadingLevel = source.HeadingLevel, Marker = source.Marker, ListLevel = source.ListLevel,
            SpacingBefore = source.SpacingBefore, SpacingAfter = source.SpacingAfter, Indent = source.Indent,
        };
        copy.Runs.AddRange(source.Runs.Select(r => r with { }));
        return copy;
    }

    private static DocPicture? Picture(XElement frame, Context context)
    {
        if (frame.Elements().FirstOrDefault(e => e.Name.LocalName == "image") is not { } image || Attribute(image, "href") is not { } href) return null;
        // Only files inside the package; an address (linked picture) is never opened.
        var name = href.TrimStart('/');
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains(':') || Bytes(context.Archive, name) is not { } data) return null;
        return Pictures.FromBytes(data, Points(Attribute(frame, "width")) ?? 0, Points(Attribute(frame, "height")) ?? 0);
    }
}
