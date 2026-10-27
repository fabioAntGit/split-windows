using System.Text;
using System.Text.RegularExpressions;

namespace Split;

/// <summary>
/// HTML, CSS and SVG are cleaned before any renderer sees them: any attribute or CSS value that
/// points elsewhere is dropped, after decoding entities and escapes so <c>&amp;#104;ttp:</c> can't
/// hide a scheme; local stylesheets are inlined after the same cleaning, since they could import
/// remote ones. Plain links (<c>&lt;a href&gt;</c>) stay: they don't load anything.
/// </summary>
public static class LocalOnly
{
    /// <summary>Attributes whose value a renderer fetches (href only outside a and area).</summary>
    private static readonly HashSet<string> Fetched =
    [
        "src", "srcset", "href", "background", "poster", "data", "xlink:href", "lowsrc", "dynsrc", "imagesrcset",
        "codebase", "archive", "longdesc", "icon", "manifest", "action", "formaction", "ping", "profile", "usemap",
        "attributionsrc", "itemtype",
    ];

    /// <summary>Elements that only load or run things, removed outright; our own base is added later.</summary>
    private static readonly string[] ElementRules =
    [
        @"<script\b[^>]*>[\s\S]*?</script\s*>",
        @"<script\b[^>]*/?>",
        @"</?(iframe|frame|frameset|object|embed|applet|video|audio|source|track|portal|base)\b[^>]*>",
        @"<meta\b[^>]*http-equiv\s*=\s*[""']?\s*refresh[^>]*>",
    ];

    private static readonly Regex Tag = new(@"<([a-zA-Z][^\s/>]*)((?:[^>""']|""[^""]*""|'[^']*')*)>");
    private static readonly Regex Attribute = new(@"([^\s""'>/=]+)(?:\s*=\s*(""[^""]*""|'[^']*'|[^\s""'>]+))?");
    private static readonly Regex StyleBlock = new(@"(<style\b[^>]*>)([\s\S]*?)(</style\s*>)", RegexOptions.IgnoreCase);
    private static readonly Regex CssReference = new(@"url\(\s*([""']?)([^)]*?)\1\s*\)|""([^""]*)""|'([^']*)'", RegexOptions.IgnoreCase);
    private static readonly Regex ImportRule = new(@"@import\s+(?:url\(\s*([""']?)([^)]*?)\1\s*\)|([""'])(.*?)\3)[^;]*;?", RegexOptions.IgnoreCase);
    private static readonly Regex EntityRule = new(@"&(#[0-9]+|#[xX][0-9a-fA-F]+|[a-zA-Z]+);?");
    private static readonly Regex CssEscape = new(@"\\([0-9a-fA-F]{1,6})\s?|\\(.)");

    /// <summary><paramref name="baseFolder"/> is the document's folder: local stylesheets there are inlined (cleaned), others dropped.</summary>
    public static string Html(string text, string? baseFolder = null)
    {
        var result = text;
        foreach (var pattern in ElementRules) result = Regex.Replace(result, pattern, "", RegexOptions.IgnoreCase);
        result = StyleBlock.Replace(result, m => m.Groups[1].Value + Css(m.Groups[2].Value, baseFolder) + m.Groups[3].Value);
        return Tag.Replace(result, m => CleanTag(m, baseFolder));
    }

    /// <summary>SVG is XML with the same loading attributes (image, use, style).</summary>
    public static string Svg(string text) => Html(text);

    /// <summary>True when a URL (already decoded) would be fetched from somewhere other than this PC.</summary>
    public static bool IsRemote(string value)
    {
        // Browsers ignore tabs, newlines and controls inside URLs ("ht\ttp:"), so do we.
        var compact = new string(value.Where(c => c > ' ' && c != '\u007F').ToArray()).ToLowerInvariant();
        if (compact.StartsWith("//", StringComparison.Ordinal) || compact.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        var colon = compact.IndexOf(':');
        if (colon < 0) return false;
        var scheme = compact[..colon];
        static bool Ascii(char c) => c < 128 && char.IsLetter(c);
        if (scheme.Length == 0 || !Ascii(scheme[0]) || !scheme.All(c => Ascii(c) || (c < 128 && char.IsDigit(c)) || "+.-".Contains(c))) return false;
        return scheme is not ("file" or "data" or "cid" or "about");
    }

    /// <summary>For attributes that are only text (alt, title, data-*): an absolute address inside them.</summary>
    private static bool MentionsRemote(string value) =>
        value.ToLowerInvariant().Split([',', '(', '\'', '"', ' ', '\t', '\r', '\n', '\u00A0'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.StartsWith("//", StringComparison.Ordinal) || (token.Contains("://") && IsRemote(token)));

    private static string CleanTag(Match match, string? baseFolder)
    {
        var original = match.Value;
        var name = match.Groups[1].Value;
        var region = match.Groups[2].Value;
        var lower = name.ToLowerInvariant();
        if (lower == "link") return InlineStylesheet(region, baseFolder);
        var isLink = lower is "a" or "area";
        var kept = new List<string>();
        var changed = false;
        foreach (Match item in Attribute.Matches(region))
        {
            var raw = item.Value;
            var key = item.Groups[1].Value.ToLowerInvariant();
            var value = Unquote(item.Groups[2].Value);
            var decoded = DecodeEntities(value);
            if (key.StartsWith("on", StringComparison.Ordinal))
            {
                changed = true;
                continue;
            }
            if (key == "style")
            {
                var clean = Css(decoded, null);
                if (clean != decoded)
                {
                    kept.Add($"style=\"{clean.Replace("\"", "&quot;")}\"");
                    changed = true;
                    continue;
                }
            }
            else if (Fetched.Contains(key) && !(isLink && key == "href"))
            {
                var candidates = key.EndsWith("srcset", StringComparison.Ordinal) ? decoded.Split(',') : [decoded];
                if (candidates.Any(c => IsRemote(c.Trim().Split(' ')[0])))
                {
                    changed = true;
                    continue;
                }
            }
            else if (!(isLink && key == "href") && MentionsRemote(decoded))
            {
                // Any other attribute with an address in it: a renderer may know a loading use we don't.
                changed = true;
                continue;
            }
            kept.Add(raw);
        }
        if (!changed) return original;
        var selfClosing = region.Trim().EndsWith('/');
        return "<" + name + string.Concat(kept.Select(k => " " + k)) + (selfClosing ? " /" : "") + ">";
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') ? value[1..^1] : value.Length == 1 && (value[0] == '"' || value[0] == '\'') ? "" : value;

    /// <summary>A link never stays: a local stylesheet becomes a cleaned style block, anything else (icons, preloads, remote or unreadable stylesheets) is dropped.</summary>
    private static string InlineStylesheet(string region, string? baseFolder)
    {
        var values = new Dictionary<string, string>();
        foreach (Match item in Attribute.Matches(region))
            values[item.Groups[1].Value.ToLowerInvariant()] = DecodeEntities(Unquote(item.Groups[2].Value));
        if (baseFolder is null || !values.TryGetValue("href", out var href) || IsRemote(href)
            || !values.GetValueOrDefault("rel", "").ToLowerInvariant().Contains("stylesheet")
            || LocalFile(href, baseFolder) is not { } file) return "";
        string text;
        try { text = DocumentPipeline.ReadText(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
        return "<style>" + Css(text, Path.GetDirectoryName(file)) + "</style>";
    }

    private static string? LocalFile(string reference, string baseFolder)
    {
        var path = reference.Split('?', '#')[0];
        if (path.Length == 0) return null;
        try
        {
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath) ? uri.LocalPath : null;
            var file = Path.GetFullPath(Uri.UnescapeDataString(path).Replace('/', '\\'), baseFolder);
            return File.Exists(file) ? file : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException) { return null; }
    }

    /// <summary>CSS with escapes decoded; @import is inlined when local (cleaned, three levels deep at most) and dropped otherwise; any url() or string that points elsewhere becomes inert.</summary>
    public static string Css(string text, string? baseFolder, int depth = 0)
    {
        var unescaped = DecodeCssEscapes(text);
        var decoded = ImportRule.Replace(unescaped, m =>
        {
            var target = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
            if (baseFolder is null || depth >= 3 || IsRemote(target) || LocalFile(target, baseFolder) is not { } file) return "";
            string imported;
            try { imported = DocumentPipeline.ReadText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
            return Css(imported, Path.GetDirectoryName(file), depth + 1);
        });
        return CssReference.Replace(decoded, m =>
        {
            if (m.Groups[2].Success) return IsRemote(m.Groups[2].Value) ? "none" : m.Value;
            var text = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            return IsRemote(text) ? "\"\"" : m.Value;
        });
    }

    private static readonly Dictionary<string, string> Named = new()
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'", ["colon"] = ":", ["sol"] = "/", ["tab"] = "\t", ["newline"] = "\n",
        ["period"] = ".", ["lpar"] = "(", ["rpar"] = ")", ["comma"] = ",", ["semi"] = ";", ["equals"] = "=", ["num"] = "#", ["excl"] = "!", ["quest"] = "?",
        ["percnt"] = "%", ["nbsp"] = "\u00A0", ["bsol"] = "\\",
    };

    private static string Scalar(string digits, System.Globalization.NumberStyles style)
    {
        if (!uint.TryParse(digits, style, null, out var code) || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) return "";
        return char.ConvertFromUtf32((int)code);
    }

    public static string DecodeEntities(string text)
    {
        if (!text.Contains('&')) return text;
        return EntityRule.Replace(text, m =>
        {
            var body = m.Groups[1].Value;
            if (body.StartsWith("#x", StringComparison.OrdinalIgnoreCase)) return Scalar(body[2..], System.Globalization.NumberStyles.AllowHexSpecifier);
            if (body.StartsWith('#')) return Scalar(body[1..], System.Globalization.NumberStyles.None);
            return Named.GetValueOrDefault(body.ToLowerInvariant(), m.Value);
        });
    }

    private static string DecodeCssEscapes(string text)
    {
        if (!text.Contains('\\')) return text;
        return CssEscape.Replace(text, m => m.Groups[1].Success ? Scalar(m.Groups[1].Value, System.Globalization.NumberStyles.AllowHexSpecifier) : m.Groups[2].Value);
    }
}
