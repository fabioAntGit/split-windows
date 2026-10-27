using System.Xml;

namespace Split;

/// <summary>
/// Reads XML as a stream of events. Every file read here comes from a stranger, so a DOCTYPE is
/// refused outright (no entities to expand, none to fetch) and the document size is capped.
/// </summary>
internal static class XmlEvents
{
    /// <summary>The line of the element being reported, for errors raised by the callers.</summary>
    [ThreadStatic] public static int Line;

    public static XmlReaderSettings Safe() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1_000_000,
        MaxCharactersInDocument = 256_000_000,
    };

    /// <summary>
    /// Calls <paramref name="start"/> with each element's name and attributes, <paramref name="text"/> with
    /// its text (CDATA included), <paramref name="end"/> when it closes and <paramref name="other"/> for
    /// comments and processing instructions. Throws <see cref="XmlException"/> when the data isn't well-formed.
    /// </summary>
    public static void Read(Stream stream, Action<string, List<(string Name, string Value)>> start, Action<string> text, Action<string> end, Action? other = null)
    {
        using var reader = XmlReader.Create(stream, Safe());
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var name = reader.Name;
                    Line = (reader as IXmlLineInfo)?.LineNumber ?? 0;
                    var empty = reader.IsEmptyElement;
                    var attributes = new List<(string, string)>();
                    while (reader.MoveToNextAttribute()) attributes.Add((reader.Name, reader.Value));
                    start(name, attributes);
                    if (empty) end(name);
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    text(reader.Value);
                    break;
                case XmlNodeType.EndElement:
                    end(reader.Name);
                    break;
                case XmlNodeType.Comment or XmlNodeType.ProcessingInstruction:
                    other?.Invoke();
                    break;
            }
        }
    }
}
