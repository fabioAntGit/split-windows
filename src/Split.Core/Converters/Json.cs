using System.Globalization;
using System.Text;

namespace Split;

public enum JsonKind { Object, Array, String, Number, Bool, Null }

/// <summary>
/// A JSON document as written: object keys keep their order and numbers keep their exact
/// text, so nothing changes on the way to another format (the framework's parser loses both).
/// </summary>
public sealed class JsonValue : IEquatable<JsonValue>
{
    public JsonKind Kind { get; private init; }

    /// <summary>The text of a string, or the literal of a number as it appears in the file (e.g. <c>1.50</c>, <c>12345678901234567890</c>).</summary>
    public string Text { get; private init; } = "";

    public bool Flag { get; private init; }
    public IReadOnlyList<JsonValue> Items { get; private init; } = [];
    public IReadOnlyList<(string Key, JsonValue Value)> Members { get; private init; } = [];

    public static JsonValue Object(IEnumerable<(string Key, JsonValue Value)> members) => new() { Kind = JsonKind.Object, Members = members.ToList() };
    public static JsonValue Array(IEnumerable<JsonValue> items) => new() { Kind = JsonKind.Array, Items = items.ToList() };
    public static JsonValue String(string text) => new() { Kind = JsonKind.String, Text = text };
    public static JsonValue Number(string literal) => new() { Kind = JsonKind.Number, Text = literal };
    public static JsonValue Bool(bool flag) => new() { Kind = JsonKind.Bool, Flag = flag };
    public static JsonValue Null { get; } = new() { Kind = JsonKind.Null };

    public bool Equals(JsonValue? other)
    {
        if (other is null || Kind != other.Kind) return false;
        return Kind switch
        {
            JsonKind.Object => Members.Count == other.Members.Count && Members.Zip(other.Members).All(p => p.First.Key == p.Second.Key && p.First.Value.Equals(p.Second.Value)),
            JsonKind.Array => Items.Count == other.Items.Count && Items.Zip(other.Items).All(p => p.First.Equals(p.Second)),
            JsonKind.String or JsonKind.Number => Text == other.Text,
            JsonKind.Bool => Flag == other.Flag,
            _ => true,
        };
    }

    public override bool Equals(object? obj) => Equals(obj as JsonValue);
    public override int GetHashCode() => HashCode.Combine(Kind, Text);
}

// MARK: - Parsing

/// <summary>Strict RFC 8259 parser over UTF-8 bytes, with a nesting limit so a hostile file (<c>[[[[…</c>) can't exhaust the stack.</summary>
public sealed class JsonParser
{
    public const int MaxDepth = 512;

    public sealed class Failure(string message, int line, int column) : Exception(message)
    {
        public int Line { get; } = line;
        public int Column { get; } = column;
    }

    private readonly byte[] bytes;
    private int index;

    private JsonParser(byte[] bytes) => this.bytes = bytes;

    public static JsonValue Parse(byte[] data)
    {
        var parser = new JsonParser(data);
        // A UTF-8 byte order mark is allowed by many editors; skip it.
        if (data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) parser.index = 3;
        parser.SkipWhitespace();
        var value = parser.Value(0);
        parser.SkipWhitespace();
        if (parser.index != data.Length) throw parser.Fail(L("there is more text after the end of the data"));
        return value;
    }

    private Failure Fail(string message)
    {
        var upTo = bytes.AsSpan(0, Math.Min(index, bytes.Length));
        var line = upTo.Count((byte)0x0A) + 1;
        var column = upTo.Length - (upTo.LastIndexOf((byte)0x0A) + 1) + 1;
        return new Failure(message, line, column);
    }

    private int Current => index < bytes.Length ? bytes[index] : -1;

    /// <summary>A cut-off file is told apart from a wrong character, since that is the usual cause.</summary>
    private Failure Unexpected(string message) => Current < 0 ? Fail(L("the data ends too early")) : Fail(message);

    private void SkipWhitespace()
    {
        while (Current is 0x20 or 0x0A or 0x0D or 0x09) index++;
    }

    private JsonValue Expect(string literal, JsonValue value)
    {
        var expected = Encoding.ASCII.GetBytes(literal);
        if (bytes.Length - index < expected.Length || !bytes.AsSpan(index, expected.Length).SequenceEqual(expected)) throw Fail(L("unexpected character"));
        index += expected.Length;
        return value;
    }

    private JsonValue Value(int depth)
    {
        if (depth >= MaxDepth) throw Fail(L("the data is nested more than %@ levels deep", MaxDepth.ToString()));
        if (Current < 0) throw Fail(L("the data ends too early"));
        switch ((char)Current)
        {
            case '{': return ParseObject(depth);
            case '[': return ParseArray(depth);
            case '"': return JsonValue.String(ParseString());
            case 't': return Expect("true", JsonValue.Bool(true));
            case 'f': return Expect("false", JsonValue.Bool(false));
            case 'n': return Expect("null", JsonValue.Null);
            case '-' or (>= '0' and <= '9'): return JsonValue.Number(ParseNumber());
            default: throw Fail(L("unexpected character"));
        }
    }

    private JsonValue ParseObject(int depth)
    {
        index++;
        var members = new List<(string, JsonValue)>();
        SkipWhitespace();
        if (Current == '}') { index++; return JsonValue.Object(members); }
        while (true)
        {
            SkipWhitespace();
            if (Current != '"') throw Unexpected(L("a key in quotes was expected"));
            var key = ParseString();
            SkipWhitespace();
            if (Current != ':') throw Unexpected(L("“:” was expected after a key"));
            index++;
            SkipWhitespace();
            members.Add((key, Value(depth + 1)));
            SkipWhitespace();
            if (Current == ',') { index++; continue; }
            if (Current == '}') { index++; return JsonValue.Object(members); }
            throw Unexpected(L("“,” or “}” was expected"));
        }
    }

    private JsonValue ParseArray(int depth)
    {
        index++;
        var elements = new List<JsonValue>();
        SkipWhitespace();
        if (Current == ']') { index++; return JsonValue.Array(elements); }
        while (true)
        {
            SkipWhitespace();
            elements.Add(Value(depth + 1));
            SkipWhitespace();
            if (Current == ',') { index++; continue; }
            if (Current == ']') { index++; return JsonValue.Array(elements); }
            throw Unexpected(L("“,” or “]” was expected"));
        }
    }

    private string ParseNumber()
    {
        var start = index;
        if (Current == '-') index++;
        int Digits()
        {
            var from = index;
            while (Current is >= '0' and <= '9') index++;
            return index - from;
        }
        if (Current == '0') index++;
        else if (Digits() == 0) throw Fail(L("invalid number"));
        if (Current == '.')
        {
            index++;
            if (Digits() == 0) throw Fail(L("invalid number"));
        }
        if (Current is 'e' or 'E')
        {
            index++;
            if (Current is '+' or '-') index++;
            if (Digits() == 0) throw Fail(L("invalid number"));
        }
        return Encoding.UTF8.GetString(bytes, start, index - start);
    }

    private string ParseString()
    {
        index++;
        var text = new StringBuilder();
        var raw = new List<byte>();
        void Flush()
        {
            if (raw.Count == 0) return;
            text.Append(Encoding.UTF8.GetString(raw.ToArray()));
            raw.Clear();
        }
        while (Current >= 0)
        {
            var current = Current;
            if (current == '"')
            {
                index++;
                Flush();
                return text.ToString();
            }
            if (current == '\\')
            {
                Flush();
                index++;
                if (Current < 0) throw Fail(L("the data ends too early"));
                var escape = (char)Current;
                index++;
                switch (escape)
                {
                    case '"': text.Append('"'); break;
                    case '\\': text.Append('\\'); break;
                    case '/': text.Append('/'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'u':
                        var code = Hex4();
                        if (code is >= 0xD800 and <= 0xDBFF && Current == '\\' && index + 1 < bytes.Length && bytes[index + 1] == 'u')
                        {
                            index += 2;
                            var low = Hex4();
                            if (low is < 0xDC00 or > 0xDFFF) throw Fail(L("invalid character code"));
                            code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                        }
                        // A lone surrogate can't be stored as text; keep the replacement character.
                        text.Append(code is >= 0xD800 and <= 0xDFFF ? "�" : char.ConvertFromUtf32(code));
                        break;
                    default: throw Fail(L("invalid escape sequence"));
                }
            }
            else if (current < 0x20) throw Fail(L("a control character must be escaped inside text"));
            else
            {
                raw.Add((byte)current);
                index++;
            }
        }
        throw Fail(L("the data ends too early"));
    }

    private int Hex4()
    {
        if (bytes.Length - index < 4 || !int.TryParse(Encoding.ASCII.GetString(bytes, index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
            || bytes.AsSpan(index, 4).ToArray().Any(b => !Uri.IsHexDigit((char)b)))
            throw Fail(L("invalid character code"));
        index += 4;
        return code;
    }
}

// MARK: - Writing

public static class JsonWriter
{
    /// <summary>Pretty-printed with two spaces, keys in their original order, numbers as written.</summary>
    public static string String(JsonValue value, string indent = "")
    {
        var inner = indent + "  ";
        switch (value.Kind)
        {
            case JsonKind.Object:
                if (value.Members.Count == 0) return "{}";
                return "{\n" + string.Join(",\n", value.Members.Select(m => inner + Quote(m.Key) + ": " + String(m.Value, inner))) + "\n" + indent + "}";
            case JsonKind.Array:
                if (value.Items.Count == 0) return "[]";
                return "[\n" + string.Join(",\n", value.Items.Select(e => inner + String(e, inner))) + "\n" + indent + "]";
            case JsonKind.String: return Quote(value.Text);
            case JsonKind.Number: return value.Text;
            case JsonKind.Bool: return value.Flag ? "true" : "false";
            default: return "null";
        }
    }

    public static string Quote(string text)
    {
        var output = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                default:
                    if (c < ' ' || (int)c is 0x2028 or 0x2029) output.Append($"\\u{(int)c:x4}"); else output.Append(c);
                    break;
            }
        }
        return output.Append('"').ToString();
    }
}

/// <summary>
/// YAML 1.2 block style. Every JSON document is valid YAML, so nothing is lost: text that
/// could be read as something else (numbers, <c>yes</c>, dates, empty) is quoted.
/// </summary>
public static class YamlWriter
{
    private static bool IsFilled(JsonValue value) => value.Kind == JsonKind.Object ? value.Members.Count > 0 : value.Kind == JsonKind.Array && value.Items.Count > 0;

    public static string Document(JsonValue value) => (IsFilled(value) ? Block(value, "") : Scalar(value)) + "\n";

    private static string Block(JsonValue value, string indent)
    {
        switch (value.Kind)
        {
            case JsonKind.Object:
                return string.Join("\n", value.Members.Select(member =>
                {
                    var key = indent + Scalar(JsonValue.String(member.Key)) + ":";
                    return IsFilled(member.Value) ? key + "\n" + Block(member.Value, indent + "  ") : key + " " + Scalar(member.Value);
                }));
            case JsonKind.Array:
                return string.Join("\n", value.Items.Select(element =>
                {
                    if (element.Kind == JsonKind.Object && IsFilled(element))
                    {
                        // The first key goes on the dash line; the rest line up under it.
                        var nested = Block(element, indent + "  ");
                        return indent + "- " + nested[(indent.Length + 2)..];
                    }
                    if (element.Kind == JsonKind.Array && IsFilled(element)) return indent + "-\n" + Block(element, indent + "  ");
                    return indent + "- " + Scalar(element);
                }));
            default:
                return indent + Scalar(value);
        }
    }

    public static string Scalar(JsonValue value) => value.Kind switch
    {
        JsonKind.Object => "{}",
        JsonKind.Array => "[]",
        JsonKind.Number => value.Text,
        JsonKind.Bool => value.Flag ? "true" : "false",
        JsonKind.Null => "null",
        _ => IsPlainSafe(value.Text) ? value.Text : JsonWriter.Quote(value.Text),
    };

    private static readonly HashSet<string> Reserved = ["true", "false", "yes", "no", "on", "off", "y", "n", "null", "~", ".nan", ".inf", "-.inf", "<<"];

    /// <summary>Plain (unquoted) only for text that YAML can't mistake for anything else.</summary>
    public static bool IsPlainSafe(string text)
    {
        if (text.Length == 0 || Reserved.Contains(text.ToLowerInvariant())) return false;
        var first = Rune.GetRuneAt(text, 0);
        var firstCategory = Rune.GetUnicodeCategory(first);
        if (!(IsLetter(firstCategory) || IsMark(firstCategory) || first.Value == '_')) return false;
        if (text[^1] is ' ' or ':') return false;
        if (text.Contains(": ") || text.Contains(" #")) return false;
        return text.EnumerateRunes().All(rune =>
        {
            var category = Rune.GetUnicodeCategory(rune);
            return IsLetter(category) || IsMark(category) || category is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
                   || " _-./()'&+,".Contains((char)Math.Min(rune.Value, 0xFFFF)) && rune.Value < 0x80;
        });
    }

    private static bool IsLetter(UnicodeCategory c) => c is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

    private static bool IsMark(UnicodeCategory c) => c is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}
