using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Split;

/// <summary>
/// JSON ↔ XML without losing anything. Each key becomes an element; what XML can't say on its
/// own is marked with a <c>type</c> attribute (number, boolean, null, array, object), and keys
/// that aren't valid element names go in <c>&lt;entry key="…"&gt;</c>. Reading such a file back gives
/// the same JSON; any other XML maps attributes to <c>@name</c> and mixed text to <c>#text</c>.
/// </summary>
public static class XmlBridge
{
    public static string Document(JsonValue value) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + Element("root", null, value, "") + "\n";

    private static string Element(string name, string? key, JsonValue value, string indent)
    {
        var attributes = key is null ? "" : $" key=\"{Escape(key, attribute: true)}\"";
        var inner = indent + "  ";
        switch (value.Kind)
        {
            case JsonKind.Object:
                if (value.Members.Count == 0) return $"{indent}<{name}{attributes} type=\"object\"/>";
                var children = value.Members.Select(member => IsName(member.Key)
                    ? Element(member.Key, null, member.Value, inner)
                    : Element("entry", member.Key, member.Value, inner));
                return $"{indent}<{name}{attributes}>\n" + string.Join("\n", children) + $"\n{indent}</{name}>";
            case JsonKind.Array:
                attributes += " type=\"array\"";
                if (value.Items.Count == 0) return $"{indent}<{name}{attributes}/>";
                return $"{indent}<{name}{attributes}>\n" + string.Join("\n", value.Items.Select(item => Element("item", null, item, inner))) + $"\n{indent}</{name}>";
            case JsonKind.String:
                return value.Text.Length == 0 ? $"{indent}<{name}{attributes}/>" : $"{indent}<{name}{attributes}>{Escape(value.Text, attribute: false)}</{name}>";
            case JsonKind.Number:
                return $"{indent}<{name}{attributes} type=\"number\">{value.Text}</{name}>";
            case JsonKind.Bool:
                return $"{indent}<{name}{attributes} type=\"boolean\">{(value.Flag ? "true" : "false")}</{name}>";
            default:
                return $"{indent}<{name}{attributes} type=\"null\"/>";
        }
    }

    /// <summary>XML 1.0 names, kept to ASCII-safe rules and never starting with "xml".</summary>
    public static bool IsName(string key) =>
        Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_.-]*\z") && !key.StartsWith("xml", StringComparison.OrdinalIgnoreCase);

    public static string Escape(string text, bool attribute)
    {
        var output = new StringBuilder();
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': output.Append("&amp;"); break;
                case '<': output.Append("&lt;"); break;
                case '>': output.Append("&gt;"); break;
                case '"' when attribute: output.Append("&quot;"); break;
                case '\n' when attribute: output.Append("&#10;"); break;
                case '\t' when attribute: output.Append("&#9;"); break;
                case '\r': output.Append("&#13;"); break;
                default:
                    // Control characters can't appear in XML 1.0 at all, not even escaped.
                    if (c < ' ' && c is not ('\n' or '\t')) Fidelity.Note(L("Control characters were left out: XML can’t hold them."));
                    else output.Append(c);
                    break;
            }
        }
        return output.ToString();
    }

    // MARK: Reading

    public sealed class Node(string name, List<(string Name, string Value)> attributes)
    {
        public string Name { get; } = name;
        public List<(string Name, string Value)> Attributes { get; } = attributes;
        public List<Node> Children { get; } = [];
        public string Text { get; set; } = "";
        public bool HasMixedText { get; set; }
    }

    private sealed class TooDeep : Exception;

    /// <summary>Parses with no DTD (so no external entities and no expansion) and a depth limit.</summary>
    public static Node Parse(byte[] data)
    {
        Node? root = null;
        var stack = new List<Node>();
        var droppedComments = false;
        try
        {
            XmlEvents.Read(new MemoryStream(data),
                (name, attributes) =>
                {
                    if (stack.Count >= JsonParser.MaxDepth) throw new TooDeep();
                    // Attributes are sorted so the output is stable.
                    var node = new Node(name, attributes.OrderBy(a => a.Name, StringComparer.Ordinal).ToList());
                    if (stack.Count > 0)
                    {
                        var parent = stack[^1];
                        if (parent.Text.Trim().Length > 0) parent.HasMixedText = true;
                        parent.Children.Add(node);
                    }
                    else root = node;
                    stack.Add(node);
                },
                text =>
                {
                    if (stack.Count == 0) return;
                    var node = stack[^1];
                    if (node.Children.Count > 0 && text.Trim().Length > 0) node.HasMixedText = true;
                    node.Text += text;
                },
                _ =>
                {
                    var node = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (node.Children.Count > 0 && node.Text.Trim().Length == 0) node.Text = "";
                },
                () => droppedComments = true);
        }
        catch (TooDeep)
        {
            throw Unreadable(L("the data is nested more than %@ levels deep", JsonParser.MaxDepth.ToString()), XmlEvents.Line, null);
        }
        catch (XmlException error)
        {
            throw Unreadable(Reason(error), error.LineNumber, error);
        }
        if (root is null) throw Unreadable(L("the data ends too early"), 0, null);
        if (droppedComments) Fidelity.Note(L("Comments and processing instructions in the XML were left out: JSON has no place for them."));
        return root;
    }

    private static Problem Unreadable(string reason, int line, Exception? error) =>
        new(L("The XML could not be read: %@ (line %@).", reason, line.ToString()),
            L("Check that the file is complete and is really XML."), error is null ? null : $"{error.GetType().Name}: {error.Message}");

    /// <summary>The parser's error in plain words.</summary>
    private static string Reason(XmlException error)
    {
        var message = error.Message;
        if (message.Contains("DTD", StringComparison.OrdinalIgnoreCase) || message.Contains("entit", StringComparison.OrdinalIgnoreCase))
            return L("it defines entities that refer to themselves or expand without limit");
        if (message.Contains("unexpected end", StringComparison.OrdinalIgnoreCase) || message.Contains("root element is missing", StringComparison.OrdinalIgnoreCase))
            return L("the data ends too early");
        if (message.Contains("does not match the end tag", StringComparison.OrdinalIgnoreCase)) return L("an element is closed by a tag with another name");
        if (message.Contains("multiple root", StringComparison.OrdinalIgnoreCase) || message.Contains("at the root level", StringComparison.OrdinalIgnoreCase))
            return L("there is more text after the last element");
        return L("it isn’t well-formed XML");
    }

    /// <summary>The document's JSON. Files written by <see cref="Document"/> come back exactly; any other XML keeps its root element's name as the single top-level key.</summary>
    public static JsonValue Json(Node root) => root.Name == "root" ? ToJson(root) : JsonValue.Object([(root.Name, ToJson(root))]);

    private static JsonValue ToJson(Node node)
    {
        var isEntry = node.Name == "entry" && node.Attributes.Any(a => a.Name == "key");
        var typeValue = node.Attributes.FirstOrDefault(a => a.Name == "type").Value;
        var marksType = typeValue is "number" or "boolean" or "null" or "array" or "object";
        var own = node.Attributes.Where(a => !(isEntry && a.Name == "key")).ToList();
        var trimmed = node.Text.Trim();
        if (marksType && own.Count == 1)
        {
            switch (typeValue)
            {
                case "number" when node.Children.Count == 0 && JsonConverter.IsCanonicalNumber(trimmed): return JsonValue.Number(trimmed);
                case "boolean" when node.Children.Count == 0 && trimmed is "true" or "false": return JsonValue.Bool(trimmed == "true");
                case "null" when node.Children.Count == 0 && trimmed.Length == 0: return JsonValue.Null;
                case "array" when trimmed.Length == 0 && node.Children.All(c => c.Name == "item"): return JsonValue.Array(node.Children.Select(ToJson));
                case "object" when node.Children.Count == 0 && trimmed.Length == 0: return JsonValue.Object([]);
            }
        }
        if (node.Children.Count == 0 && own.Count == 0) return JsonValue.String(node.Text);
        var members = own.Select(a => ("@" + a.Name, JsonValue.String(a.Value))).ToList();
        // Children with the same name become one array, at the place of the first one.
        var order = new List<string>();
        var groups = new Dictionary<string, List<JsonValue>>();
        foreach (var child in node.Children)
        {
            var key = child.Name == "entry" ? child.Attributes.FirstOrDefault(a => a.Name == "key").Value ?? child.Name : child.Name;
            if (!groups.ContainsKey(key)) { order.Add(key); groups[key] = []; }
            groups[key].Add(ToJson(child));
        }
        foreach (var key in order) members.Add((key, groups[key].Count == 1 ? groups[key][0] : JsonValue.Array(groups[key])));
        if (trimmed.Length > 0) members.Add(("#text", JsonValue.String(node.Text)));
        if (node.HasMixedText) Fidelity.Note(L("Text mixed between elements was joined into “#text”."));
        return JsonValue.Object(members);
    }
}
