using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Split;

/// <summary>
/// HTML as a <see cref="RichDocument"/>. The page is cleaned by <see cref="LocalOnly"/> first, and the parser never loads
/// anything: pictures come from the page itself (<c>data:</c>) or from files next to it.
/// </summary>
/// <remarks>shortcut: look comes from tags and inline styles, not from style sheets; upgrade when sheets must colour text.</remarks>
public static class HtmlReader
{
    private sealed record Style
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
        public string? Link { get; init; }
        public bool Pre { get; init; }
    }

    private static readonly double[] HeadingSizes = [24, 18, 14, 12, 10, 8];

    private static readonly HashSet<string> Skipped = ["script", "style", "head", "title", "template", "noscript", "meta", "link"];

    private static readonly HashSet<string> Blocks =
        ["p", "div", "section", "article", "header", "footer", "nav", "aside", "main", "figure", "figcaption", "address", "form", "fieldset",
         "center", "details", "summary", "caption", "dl", "dt", "dd", "blockquote", "pre", "h1", "h2", "h3", "h4", "h5", "h6", "legend"];

    private static readonly Dictionary<string, string> Named = new()
    {
        ["black"] = "000000", ["white"] = "FFFFFF", ["red"] = "FF0000", ["green"] = "008000", ["blue"] = "0000FF", ["yellow"] = "FFFF00",
        ["gray"] = "808080", ["grey"] = "808080", ["orange"] = "FFA500", ["purple"] = "800080", ["navy"] = "000080", ["maroon"] = "800000",
        ["teal"] = "008080", ["silver"] = "C0C0C0", ["lime"] = "00FF00", ["aqua"] = "00FFFF", ["fuchsia"] = "FF00FF", ["olive"] = "808000",
    };

    public static RichDocument Read(string html, string? baseFolder)
    {
        var page = new HtmlParser().ParseDocument(LocalOnly.Html(html, baseFolder));
        var document = new RichDocument();
        var flow = new Flow(document.Blocks, baseFolder);
        if (page.Body is { } body) flow.Walk(body, new Style());
        flow.Flush();
        return document;
    }

    private sealed class Flow(List<DocBlock> blocks, string? baseFolder)
    {
        private DocParagraph? current;
        private DocAlignment align;
        private int heading;
        private double indent, before, after;
        private string? marker;
        private int listLevel;
        private int listDepth;
        private readonly Stack<int?> counters = new();

        public void Flush()
        {
            if (current is null) return;
            if (current.Runs.Count > 0)
            {
                var last = current.Runs[^1];
                last.Text = last.Text.TrimEnd(' ', '\n');
                if (last.Text.Length == 0) current.Runs.RemoveAt(current.Runs.Count - 1);
            }
            blocks.Add(current);
            current = null;
        }

        private DocParagraph Open()
        {
            if (current is not null) return current;
            current = new DocParagraph
            {
                Alignment = align, HeadingLevel = heading, Indent = indent, SpacingBefore = before, SpacingAfter = after,
                Marker = marker, ListLevel = listLevel,
            };
            marker = null;
            return current;
        }

        private void Text(string text, Style style, bool preserve)
        {
            if (!preserve)
            {
                text = Regex.Replace(text, @"[ \t\r\n\f]+", " ");
                if (current is null || current.Runs.Count == 0 || current.Runs[^1].Text.EndsWith(' ') || current.Runs[^1].Text.EndsWith('\n'))
                    text = text.TrimStart(' ');
            }
            else
            {
                text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            }
            if (text.Length == 0) return;
            var run = new DocRun
            {
                Text = text, Bold = style.Bold, Italic = style.Italic, Underline = style.Underline || style.Link is not null, Strike = style.Strike,
                Superscript = style.Sup, Subscript = style.Sub, Color = style.Color, Font = style.Font, Size = style.Size, Link = style.Link,
            };
            var paragraph = Open();
            if (paragraph.Runs.Count > 0 && paragraph.Runs[^1].SameLook(run)) paragraph.Runs[^1].Text += text;
            else paragraph.Runs.Add(run);
        }

        public void Walk(INode node, Style style)
        {
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IText text:
                        Text(text.Data, style, style.Pre);
                        break;
                    case IElement element:
                        Element(element, style);
                        break;
                }
            }
        }

        private void Element(IElement element, Style outer)
        {
            var name = element.LocalName;
            if (Skipped.Contains(name)) return;
            var style = Styled(element, outer);
            switch (name)
            {
                case "br":
                    Text("\n", style with { Pre = true }, preserve: true);
                    return;
                case "hr":
                    Flush();
                    blocks.Add(new DocParagraph());
                    return;
                case "img":
                    Flush();
                    Image(element);
                    return;
                case "table":
                    Flush();
                    blocks.Add(ReadTable((IHtmlTableElement)element, style));
                    return;
                case "ul" or "ol":
                    Flush();
                    counters.Push(name == "ol" ? int.TryParse(element.GetAttribute("start"), out var start) ? start : 1 : null);
                    listDepth++;
                    Walk(element, style);
                    Flush();
                    listDepth--;
                    counters.Pop();
                    return;
                case "li":
                    Flush();
                    var inList = counters.Count > 0;
                    var kind = inList ? counters.Pop() : null;
                    marker = kind is { } number ? number + "." : "•";
                    if (inList) counters.Push(kind + 1);
                    var (savedIndent, savedLevel, savedAfter) = (indent, listLevel, after);
                    indent = 18 * Math.Max(1, listDepth);
                    listLevel = Math.Max(1, listDepth);
                    after = 2;
                    Walk(element, style);
                    Flush();
                    (indent, listLevel, after) = (savedIndent, savedLevel, savedAfter);
                    marker = null;
                    return;
            }
            if (Blocks.Contains(name))
            {
                Flush();
                var saved = (align, heading, indent, before, after);
                if (name == "center") align = DocAlignment.Center;
                if (AlignmentOf(element) is { } declared) align = declared;
                if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                {
                    heading = name[1] - '0';
                    before = 8;
                    after = 8;
                    style = style with { Bold = true, Size = style.Size > 0 ? style.Size : HeadingSizes[heading - 1] };
                }
                else if (name is "p" or "pre" or "blockquote" or "dl")
                {
                    after = 8;
                }
                if (name is "blockquote" or "dd") indent += 36;
                if (name == "pre") style = style with { Pre = true, Font = style.Font ?? "Courier New" };
                Walk(element, style);
                Flush();
                (align, heading, indent, before, after) = saved;
                return;
            }
            Walk(element, style);
        }

        private void Image(IElement element)
        {
            if (element.GetAttribute("src") is not { } src || Pictures.Load(src, baseFolder) is not { } data) return;
            var width = Length(element.GetAttribute("width"), 0) ?? 0;
            var height = Length(element.GetAttribute("height"), 0) ?? 0;
            if (Pictures.FromBytes(data, width * 0.75, height * 0.75) is { } picture) blocks.Add(picture);
        }

        private DocTable ReadTable(IHtmlTableElement element, Style style)
        {
            var table = new DocTable();
            foreach (var row in element.Rows)
            {
                var cells = new List<DocCell>();
                foreach (var source in row.Cells)
                {
                    var cell = new DocCell { Span = Math.Max(1, source.ColumnSpan) };
                    var inner = new Flow(cell.Blocks, baseFolder);
                    inner.Walk(source, source.LocalName == "th" ? style with { Bold = true } : style);
                    inner.Flush();
                    cells.Add(cell);
                }
                table.Rows.Add(cells);
            }
            return table;
        }

        private static DocAlignment? AlignmentOf(IElement element)
        {
            var value = (Declarations(element).GetValueOrDefault("text-align") ?? element.GetAttribute("align"))?.Trim().ToLowerInvariant();
            return value switch
            {
                "center" => DocAlignment.Center, "right" => DocAlignment.Right, "justify" => DocAlignment.Justify, "left" => DocAlignment.Left, _ => null,
            };
        }
    }

    private static Dictionary<string, string> Declarations(IElement element)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in (element.GetAttribute("style") ?? "").Split(';'))
        {
            var colon = part.IndexOf(':');
            if (colon > 0) result[part[..colon].Trim().ToLowerInvariant()] = part[(colon + 1)..].Trim();
        }
        return result;
    }

    private static Style Styled(IElement element, Style style)
    {
        switch (element.LocalName)
        {
            case "b" or "strong" or "th": style = style with { Bold = true }; break;
            case "i" or "em" or "cite" or "var" or "dfn": style = style with { Italic = true }; break;
            case "u" or "ins": style = style with { Underline = true }; break;
            case "s" or "strike" or "del": style = style with { Strike = true }; break;
            case "sup": style = style with { Sup = true }; break;
            case "sub": style = style with { Sub = true }; break;
            case "code" or "tt" or "kbd" or "samp": style = style with { Font = "Courier New" }; break;
            case "a" when element.GetAttribute("href") is { } href && IsPlainLink(href): style = style with { Link = href.Trim() }; break;
            case "font":
                if (Colour(element.GetAttribute("color")) is { } fontColour) style = style with { Color = fontColour };
                if (element.GetAttribute("face") is { } face) style = style with { Font = face.Split(',')[0].Trim() };
                break;
        }
        var declared = Declarations(element);
        if (declared.TryGetValue("color", out var color) && Colour(color) is { } hex) style = style with { Color = hex };
        if (declared.TryGetValue("font-weight", out var weight))
            style = style with { Bold = weight.Equals("bold", StringComparison.OrdinalIgnoreCase) || (int.TryParse(weight, out var w) && w >= 600) };
        if (declared.TryGetValue("font-style", out var italic)) style = style with { Italic = italic.Contains("italic") || italic.Contains("oblique") };
        if (declared.TryGetValue("text-decoration", out var decoration))
            style = style with { Underline = decoration.Contains("underline") || style.Underline, Strike = decoration.Contains("line-through") || style.Strike };
        if (declared.TryGetValue("font-family", out var family) && family.Split(',')[0].Trim().Trim('"', '\'') is { Length: > 0 } first && !IsGeneric(first))
            style = style with { Font = first };
        if (declared.TryGetValue("font-size", out var size) && Length(size, RichDocument.DefaultSize) is { } points) style = style with { Size = points * (size.Trim().EndsWith("px") ? 0.75 : 1) };
        return style;
    }

    private static bool IsGeneric(string family) => family.ToLowerInvariant() is "serif" or "sans-serif" or "monospace" or "cursive" or "fantasy" or "inherit" or "initial";

    private static bool IsPlainLink(string href) =>
        href.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || href.Trim().StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>A CSS or HTML length as a number of its own unit (pixels, points…); <paramref name="basis"/> is what em and % are taken of.</summary>
    private static double? Length(string? text, double basis)
    {
        if (text is null) return null;
        var match = Regex.Match(text.Trim().ToLowerInvariant(), @"^(\d+(?:\.\d+)?)\s*(px|pt|em|rem|%)?$");
        if (!match.Success) return null;
        var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch
        {
            "em" or "rem" => number * basis,
            "%" => number / 100 * basis,
            _ => number,
        };
    }

    private static string? Colour(string? text)
    {
        if (text is null) return null;
        text = text.Trim().ToLowerInvariant();
        if (Named.TryGetValue(text, out var named)) return named;
        var hex = Regex.Match(text, @"^#([0-9a-f]{6}|[0-9a-f]{3})$");
        if (hex.Success)
        {
            var digits = hex.Groups[1].Value;
            return (digits.Length == 3 ? string.Concat(digits.Select(c => $"{c}{c}")) : digits).ToUpperInvariant();
        }
        var rgb = Regex.Match(text, @"^rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$");
        return rgb.Success ? string.Concat(rgb.Groups.Cast<Group>().Skip(1).Select(g => Math.Clamp(int.Parse(g.Value), 0, 255).ToString("X2"))) : null;
    }
}
