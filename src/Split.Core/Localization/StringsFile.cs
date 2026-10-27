using System.Globalization;
using System.Text;

namespace Split;

/// <summary>Reads a <c>.strings</c> table: <c>"key" = "value";</c> pairs between comments.</summary>
public static class StringsFile
{
    /// <summary>A damaged table gives what was read before the damage, so the app still opens in its own language.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var table = new Dictionary<string, string>();
        var at = 0;
        while (true)
        {
            SkipSpace(text, ref at);
            if (at >= text.Length || text[at] != '"' || !TryString(text, ref at, out var key)) break;
            SkipSpace(text, ref at);
            if (at >= text.Length || text[at] != '=') break;
            at++;
            SkipSpace(text, ref at);
            if (at >= text.Length || text[at] != '"' || !TryString(text, ref at, out var value)) break;
            SkipSpace(text, ref at);
            if (at >= text.Length || text[at] != ';') break;
            at++;
            table[key] = value;
        }
        return table;
    }

    private static void SkipSpace(string text, ref int at)
    {
        while (at < text.Length)
        {
            if (char.IsWhiteSpace(text[at])) at++;
            else if (text.AsSpan(at).StartsWith("/*"))
            {
                var end = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
                at = end < 0 ? text.Length : end + 2;
            }
            else if (text.AsSpan(at).StartsWith("//"))
            {
                var end = text.IndexOf('\n', at);
                at = end < 0 ? text.Length : end + 1;
            }
            else break;
        }
    }

    private static bool TryString(string text, ref int at, out string result)
    {
        var builder = new StringBuilder();
        at++;
        while (at < text.Length)
        {
            var c = text[at++];
            if (c == '"') { result = builder.ToString(); return true; }
            if (c != '\\' || at >= text.Length) { builder.Append(c); continue; }
            var escaped = text[at++];
            switch (escaped)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'U' or 'u' when at + 4 <= text.Length && int.TryParse(text.AsSpan(at, 4), NumberStyles.HexNumber, null, out var code):
                    builder.Append((char)code);
                    at += 4;
                    break;
                default: builder.Append(escaped); break;
            }
        }
        result = "";
        return false;
    }
}
