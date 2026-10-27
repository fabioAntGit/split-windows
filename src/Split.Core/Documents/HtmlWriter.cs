using System.Globalization;
using System.Text;

namespace Split;

/// <summary>A <see cref="RichDocument"/> as a web page. Pictures are placed inside the page, so the file stands alone.</summary>
public static class HtmlWriter
{
    public static string Write(RichDocument document, string title)
    {
        var body = new StringBuilder();
        Blocks(document.Blocks, body);
        return $$"""
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>{{Markdown.Escape(title)}}</title>
            <style>body{font:{{RichDocument.DefaultSize}}pt {{RichDocument.DefaultFont}},sans-serif}table{border-collapse:collapse}td,th{border:1px solid #999;padding:3px 6px;vertical-align:top}</style>
            </head><body>{{body}}</body></html>
            """;
    }

    private static void Blocks(IReadOnlyList<DocBlock> blocks, StringBuilder html)
    {
        string? open = null;
        void Close()
        {
            if (open is not null) html.Append($"</{open}>");
            open = null;
        }
        foreach (var block in blocks)
        {
            if (block is DocParagraph { Marker: { } marker } item)
            {
                var kind = marker == "•" ? "ul" : "ol";
                if (open != kind)
                {
                    Close();
                    html.Append($"<{kind}>");
                    open = kind;
                }
                html.Append("<li>");
                Runs(item, html);
                html.Append("</li>");
                continue;
            }
            Close();
            switch (block)
            {
                case DocParagraph paragraph:
                    var tag = paragraph.HeadingLevel is >= 1 and <= 6 ? "h" + paragraph.HeadingLevel : "p";
                    var style = paragraph.Alignment == DocAlignment.Left ? "" : $" style=\"text-align:{paragraph.Alignment.ToString().ToLowerInvariant()}\"";
                    html.Append($"<{tag}{style}>");
                    Runs(paragraph, html);
                    html.Append($"</{tag}>");
                    break;
                case DocTable table:
                    html.Append("<table>");
                    foreach (var row in table.Rows)
                    {
                        html.Append("<tr>");
                        foreach (var cell in row)
                        {
                            html.Append(cell.Span > 1 ? $"<td colspan=\"{cell.Span}\">" : "<td>");
                            Blocks(cell.Blocks, html);
                            html.Append("</td>");
                        }
                        html.Append("</tr>");
                    }
                    html.Append("</table>");
                    break;
                case DocPicture picture:
                    html.Append($"<p><img src=\"data:{picture.Mime};base64,{Convert.ToBase64String(picture.Data)}\" width=\"{Number(picture.Width / 0.75)}\" height=\"{Number(picture.Height / 0.75)}\"></p>");
                    break;
                case DocPageBreak:
                    html.Append("<div style=\"page-break-after:always\"></div>");
                    break;
            }
        }
        Close();
    }

    private static string Number(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);

    private static void Runs(DocParagraph paragraph, StringBuilder html)
    {
        foreach (var run in paragraph.Runs)
        {
            var text = Markdown.Escape(run.Text).Replace("\n", "<br>").Replace("\f", "");
            var css = new StringBuilder();
            if (run.Font is { } font && font != RichDocument.DefaultFont) css.Append($"font-family:'{font.Replace("'", "")}';");
            if (run.Size > 0 && run.Size != RichDocument.DefaultSize && paragraph.HeadingLevel == 0) css.Append($"font-size:{run.Size.ToString("0.#", CultureInfo.InvariantCulture)}pt;");
            if (run.Color is { } color) css.Append($"color:#{color};");
            var open = "";
            var close = "";
            void Wrap(bool on, string tag)
            {
                if (!on) return;
                open += $"<{tag}>";
                close = $"</{tag}>" + close;
            }
            if (css.Length > 0)
            {
                open += $"<span style=\"{css}\">";
                close = "</span>" + close;
            }
            if (paragraph.HeadingLevel == 0) Wrap(run.Bold, "b");
            Wrap(run.Italic, "i");
            Wrap(run.Underline && run.Link is null, "u");
            Wrap(run.Strike, "s");
            Wrap(run.Superscript, "sup");
            Wrap(run.Subscript, "sub");
            if (run.Link is { } link)
            {
                open = $"<a href=\"{Markdown.Escape(link).Replace("\"", "&quot;")}\">" + open;
                close += "</a>";
            }
            html.Append(open).Append(text).Append(close);
        }
    }
}
