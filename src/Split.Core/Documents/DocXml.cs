using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Split;

/// <summary>What the Word and OpenDocument readers and writers share.</summary>
internal static class DocXml
{
    /// <summary>The largest part of a package that is read: a document is never this big, a bomb is.</summary>
    private const long MaxPart = 256 << 20;

    /// <summary>Text for an element or an attribute; control characters XML can't hold are dropped.</summary>
    public static string Escape(string text, bool attribute)
    {
        var xml = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': xml.Append("&amp;"); break;
                case '<': xml.Append("&lt;"); break;
                case '>': xml.Append("&gt;"); break;
                case '"' when attribute: xml.Append("&quot;"); break;
                default:
                    if (c >= ' ' || c is '\n' or '\t') xml.Append(c);
                    break;
            }
        }
        return xml.ToString();
    }

    /// <summary>A DOCTYPE is skipped, never followed: no entity is fetched or expanded.</summary>
    public static XDocument Load(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 1_000_000 };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader);
    }

    public static XDocument? Part(ZipArchive archive, string name)
    {
        if (archive.GetEntry(name) is not { } entry || entry.Length > MaxPart) return null;
        using var stream = entry.Open();
        return Load(stream);
    }

    public static byte[]? Bytes(ZipArchive archive, string name)
    {
        if (archive.GetEntry(name) is not { } entry || entry.Length > MaxPart) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    public static IEnumerable<XElement> Children(XElement element, string localName) => element.Elements().Where(e => e.Name.LocalName == localName);

    public static XElement? Child(XElement element, string localName) => element.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    public static bool IsPlainLink(string address) => RichDocument.IsPlainLink(address);
}
