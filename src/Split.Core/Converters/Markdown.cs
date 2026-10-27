using System.Text.RegularExpressions;

namespace Split;

/// <summary>
/// Small Markdown → HTML renderer (headings, emphasis, links, images, code, lists, quotes,
/// rules and pipe tables), enough for notes and READMEs.
/// </summary>
public static class Markdown
{
    public static string Document(string markdown, string title) => $$"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>{{Escape(title)}}</title>
        <style>body{font:12pt "Helvetica Neue",Helvetica,Arial,sans-serif;line-height:1.45;max-width:46em;margin:2em auto;color:#111}
        pre{background:#f4f4f4;padding:.8em;overflow-x:auto}code{font-family:Menlo,monospace;font-size:.92em}
        blockquote{border-left:3px solid #ccc;margin-left:0;padding-left:1em;color:#444}
        table{border-collapse:collapse}td,th{border:1px solid #bbb;padding:.3em .6em}th{background:#eee}</style>
        </head><body>
        {{ToHtml(markdown)}}
        </body></html>
        """;

    public static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var html = new List<string>();
        var paragraph = new List<string>();
        var listStack = new List<(string Tag, int Indent)>();
        var index = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Add($"<p>{Inline(string.Join(" ", paragraph))}</p>");
            paragraph.Clear();
        }
        void CloseLists(int indent = -1)
        {
            while (listStack.Count > 0 && listStack[^1].Indent > indent)
            {
                html.Add($"</li></{listStack[^1].Tag}>");
                listStack.RemoveAt(listStack.Count - 1);
            }
        }

        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            var indent = line.TakeWhile(c => c is ' ' or '\t').Count();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph(); CloseLists();
                var fence = trimmed[..3];
                var code = new List<string>();
                index++;
                while (index < lines.Length && !lines[index].Trim().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(Escape(lines[index]));
                    index++;
                }
                html.Add($"<pre><code>{string.Join("\n", code)}</code></pre>");
                index++;
                continue;
            }
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                var next = lines.Skip(index + 1).FirstOrDefault(l => l.Trim().Length > 0);
                if (next is not null && ListMarker(next.Trim()) is null) CloseLists();
                index++;
                continue;
            }
            if (AtxLevel(trimmed) is { } level)
            {
                FlushParagraph(); CloseLists();
                var text = trimmed[level..].Trim(' ', '#');
                html.Add($"<h{level}>{Inline(text)}</h{level}>");
                index++;
                continue;
            }
            if (IsRule(trimmed))
            {
                FlushParagraph(); CloseLists();
                html.Add("<hr>");
                index++;
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph(); CloseLists();
                var quote = new List<string>();
                while (index < lines.Length)
                {
                    var t = lines[index].Trim();
                    if (!t.StartsWith('>')) break;
                    quote.Add(t[1..].Trim());
                    index++;
                }
                html.Add($"<blockquote>{ToHtml(string.Join("\n", quote))}</blockquote>");
                continue;
            }
            if (trimmed.StartsWith('|') && index + 1 < lines.Length && IsTableSeparator(lines[index + 1]))
            {
                FlushParagraph(); CloseLists();
                var rows = new List<List<string>> { Cells(trimmed) };
                index += 2;
                while (index < lines.Length && lines[index].Trim().StartsWith('|'))
                {
                    rows.Add(Cells(lines[index].Trim()));
                    index++;
                }
                var table = "<table><thead><tr>" + string.Concat(rows[0].Select(c => $"<th>{Inline(c)}</th>")) + "</tr></thead><tbody>";
                foreach (var row in rows.Skip(1)) table += "<tr>" + string.Concat(row.Select(c => $"<td>{Inline(c)}</td>")) + "</tr>";
                html.Add(table + "</tbody></table>");
                continue;
            }
            if (ListMarker(trimmed) is { } marker)
            {
                var (tag, content) = marker;
                FlushParagraph();
                if (listStack.Count > 0 && indent > listStack[^1].Indent)
                {
                    html.Add($"<{tag}><li>{Inline(content)}");
                    listStack.Add((tag, indent));
                }
                else
                {
                    CloseLists(indent);
                    if (listStack.Count > 0 && listStack[^1].Indent == indent && listStack[^1].Tag == tag)
                    {
                        html.Add($"</li><li>{Inline(content)}");
                    }
                    else
                    {
                        if (listStack.Count > 0 && listStack[^1].Indent == indent)
                        {
                            html.Add($"</li></{listStack[^1].Tag}>");
                            listStack.RemoveAt(listStack.Count - 1);
                        }
                        html.Add($"<{tag}><li>{Inline(content)}");
                        listStack.Add((tag, indent));
                    }
                }
                index++;
                continue;
            }
            if (listStack.Count > 0 && indent > 0)
            {
                html.Add(" " + Inline(trimmed));
            }
            else
            {
                CloseLists();
                paragraph.Add(trimmed);
            }
            index++;
        }
        FlushParagraph();
        CloseLists();
        return string.Join("\n", html);
    }

    // MARK: Block helpers

    public static int? AtxLevel(string line)
    {
        var hashes = line.TakeWhile(c => c == '#').Count();
        if (hashes is < 1 or > 6) return null;
        var rest = line[hashes..];
        return rest.Length == 0 || rest.StartsWith(' ') ? hashes : null;
    }

    public static bool IsRule(string line)
    {
        var compact = line.Replace(" ", "");
        return compact.Length >= 3 && "-*_".Contains(compact[0]) && compact.All(c => c == compact[0]);
    }

    public static (string Tag, string Content)? ListMarker(string line)
    {
        foreach (var bullet in new[] { "- ", "* ", "+ " })
            if (line.StartsWith(bullet, StringComparison.Ordinal)) return ("ul", line[2..]);
        var digits = line.TakeWhile(char.IsNumber).Count();
        if (digits is > 0 and < 10)
        {
            var rest = line[digits..];
            if (rest.StartsWith(". ", StringComparison.Ordinal) || rest.StartsWith(") ", StringComparison.Ordinal)) return ("ol", rest[2..]);
        }
        return null;
    }

    public static bool IsTableSeparator(string line)
    {
        var t = line.Trim();
        return t.StartsWith('|') && t.Contains('-') && t.All(c => "|-: ".Contains(c));
    }

    public static List<string> Cells(string row)
    {
        var t = row;
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToList();
    }

    // MARK: Inline

    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string Inline(string text)
    {
        // Protect code spans first so their contents aren't formatted.
        var codes = new List<string>();
        var working = Replace("`([^`]+)`", text, g =>
        {
            codes.Add($"<code>{Escape(g[1])}</code>");
            return $"\u0001{codes.Count - 1}\u0001";
        });
        working = Escape(working);
        working = Replace(@"!\[([^\]]*)\]\(([^)\s]+)(?:\s+&quot;[^)]*&quot;)?\)", working, g => $"<img alt=\"{g[1]}\" src=\"{g[2]}\">");
        working = Replace(@"\[([^\]]+)\]\(([^)\s]+)(?:\s+&quot;[^)]*&quot;)?\)", working, g => $"<a href=\"{g[2]}\">{g[1]}</a>");
        working = Replace(@"&lt;(https?://[^&\s]+)&gt;", working, g => $"<a href=\"{g[1]}\">{g[1]}</a>");
        working = Replace(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", working, g => $"<strong>{g[2]}</strong>");
        working = Replace(@"(?<![\w*])(\*|_)(?=\S)(.+?)(?<=\S)\1(?![\w*])", working, g => $"<em>{g[2]}</em>");
        working = Replace("~~(.+?)~~", working, g => $"<del>{g[1]}</del>");
        working = Replace("\u0001(\\d+)\u0001", working, g => codes[int.Parse(g[1])]);
        return working;
    }

    private static string Replace(string pattern, string text, Func<string[], string> transform) =>
        Regex.Replace(text, pattern, match => transform(match.Groups.Cast<Group>().Select(g => g.Success ? g.Value : "").ToArray()));
}
