using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using AngleSharp;
using AngleSharp.Xhtml;
using ImageMagick;

namespace Split;

public static class EpubReader
{
    private static XDocument Load(string file)
    {
        // A DOCTYPE is skipped, never followed: no entity is fetched or expanded.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 1_000_000 };
        using var reader = XmlReader.Create(file, settings);
        return XDocument.Load(reader);
    }

    private static string? Attribute(XElement element, string name) => element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    /// <summary>
    /// Unzips the book and joins its spine chapters into one HTML file whose image references are
    /// absolute, so the HTML reader can load them. The caller removes <c>Workspace</c> when done.
    /// </summary>
    public static (string Html, string Workspace) CombinedHtml(string epub)
    {
        var workspace = Output.ScratchDirectory();
        try
        {
            ArchiveTools.Unpack(epub, workspace);
            string? rootPath = null;
            try
            {
                rootPath = Load(Path.Combine(workspace, "META-INF", "container.xml")).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "rootfile") is { } rootfile ? Attribute(rootfile, "full-path") : null;
            }
            catch (Exception e) when (e is IOException or XmlException) { }
            var malformed = new Problem(L("The EPUB is malformed: the file that lists its contents (container.xml/OPF) is missing."), L("Open it in Calibre to check whether it is damaged."));
            if (rootPath is null) throw malformed;
            var opf = Path.GetFullPath(Path.Combine(workspace, rootPath));
            if (!Paths.IsInside(opf, workspace)) throw malformed;
            XDocument package;
            try { package = Load(opf); }
            catch (Exception e) when (e is IOException or XmlException) { throw malformed; }

            var manifest = new Dictionary<string, string>();
            foreach (var item in package.Descendants().Where(e => e.Name.LocalName == "item" && e.Parent?.Name.LocalName == "manifest"))
                if (Attribute(item, "id") is { } id && Attribute(item, "href") is { } href) manifest[id] = Uri.UnescapeDataString(href);
            var title = package.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "EPUB";
            var bodies = new List<string>();
            foreach (var reference in package.Descendants().Where(e => e.Name.LocalName == "itemref" && e.Parent?.Name.LocalName == "spine"))
            {
                if (Attribute(reference, "idref") is not { } id || !manifest.TryGetValue(id, out var href)) continue;
                var chapter = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(opf)!, href.Replace('/', '\\')));
                if (Paths.IsInside(chapter, workspace) && ChapterBody(chapter) is { } body) bodies.Add(body);
            }
            if (bodies.Count == 0)
                throw new Problem(L("The EPUB has no text chapters (it may be only pictures or have DRM)."), L("Books bought with DRM can’t be converted."));
            var html = $"""
                <!DOCTYPE html><html><head><meta charset="utf-8"><title>{Markdown.Escape(title)}</title></head>
                <body>{string.Join("\n<hr>\n", bodies)}</body></html>
                """;
            var file = Path.Combine(workspace, "combined.html");
            File.WriteAllText(file, html, new UTF8Encoding(false));
            return (file, workspace);
        }
        catch
        {
            Output.Discard(workspace);
            throw;
        }
    }

    /// <summary>The inside of the chapter's body, with its pictures pointing at the files they came in.</summary>
    public static string? ChapterBody(string chapter)
    {
        string text;
        try { text = DocumentPipeline.ReadText(chapter); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var body = new HtmlParser().ParseDocument(text).Body;
        if (body is null) return null;
        var folder = Path.GetDirectoryName(chapter)!;
        foreach (var image in body.QuerySelectorAll("img"))
        {
            if (image.GetAttribute("src") is not { } src || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || LocalOnly.IsRemote(src)) continue;
            var file = Path.GetFullPath(Uri.UnescapeDataString(src.Split('?', '#')[0]).Replace('/', '\\'), folder);
            image.SetAttribute("src", new Uri(file).AbsoluteUri);
        }
        return body.InnerHtml;
    }
}

public static class EpubWriter
{
    /// <summary>
    /// Builds an EPUB 3 (with an EPUB 2 contents file) from HTML; chapters split at each h1. Pictures
    /// that are inside the page (data:) or are local files next to it (<paramref name="baseFolder"/>) are packed into the book.
    /// </summary>
    public static void Write(string html, string title, string? baseFolder, string output)
    {
        var document = new HtmlParser().ParseDocument(html);
        var styles = string.Join("\n", document.QuerySelectorAll("style").Select(s => s.TextContent));
        var pictures = new List<(string Name, byte[] Data, string Type)>();
        foreach (var image in document.QuerySelectorAll("img").ToList())
        {
            if (image.GetAttribute("src") is not { } src || PackedPicture(src, baseFolder) is not { } picture)
            {
                // A picture that can't be packed would be a broken link in the book.
                image.Remove();
                continue;
            }
            var name = $"images/picture-{pictures.Count + 1}.{picture.Extension}";
            pictures.Add((name, picture.Data, picture.Type));
            image.SetAttribute("src", name);
        }
        var body = document.Body ?? throw new Problem(L("The document has no text to put in the EPUB."));
        var chapters = new List<(string Title, string Xhtml)>();
        var current = new StringBuilder();
        var currentTitle = title;
        foreach (var child in body.ChildNodes)
        {
            var isHeading = child is AngleSharp.Dom.IElement { LocalName: "h1" };
            if (isHeading && current.Length > 0)
            {
                chapters.Add((currentTitle, current.ToString()));
                current.Clear();
            }
            if (isHeading) currentTitle = child.TextContent.Trim();
            current.Append(child.ToHtml(XhtmlMarkupFormatter.Instance));
        }
        if (current.Length > 0 || chapters.Count == 0) chapters.Add((currentTitle, current.ToString()));

        var escapedTitle = Markdown.Escape(title);
        var language = Localizer.Shared.Language.Resolved().Raw();
        const string xmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n";
        var identifier = $"urn:uuid:{Guid.NewGuid().ToString().ToLowerInvariant()}";
        var entries = new List<(string Path, byte[] Data)>
        {
            ("mimetype", Encoding.ASCII.GetBytes("application/epub+zip")),
            ("META-INF/container.xml", Utf8(xmlHeader + """
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """)),
            ("OEBPS/style.css", Utf8("body{font-family:serif;line-height:1.5}img{max-width:100%}table{border-collapse:collapse}td,th{border:1px solid #999;padding:.3em}\n" + styles)),
        };
        foreach (var picture in pictures) entries.Add(("OEBPS/" + picture.Name, picture.Data));
        for (var index = 0; index < chapters.Count; index++)
        {
            entries.Add(($"OEBPS/chapter-{index + 1}.xhtml", Utf8(xmlHeader + $"""
                <!DOCTYPE html>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="{language}">
                <head><meta charset="utf-8"/><title>{Markdown.Escape(chapters[index].Title)}</title><link rel="stylesheet" href="style.css"/></head>
                <body>{chapters[index].Xhtml}</body></html>
                """)));
        }
        var navItems = string.Concat(chapters.Select((c, i) => $"<li><a href=\"chapter-{i + 1}.xhtml\">{Markdown.Escape(c.Title)}</a></li>"));
        entries.Add(("OEBPS/nav.xhtml", Utf8(xmlHeader + $"""
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="{language}">
            <head><meta charset="utf-8"/><title>{escapedTitle}</title></head>
            <body><nav epub:type="toc" id="toc"><h1>{Markdown.Escape(L("Contents"))}</h1><ol>{navItems}</ol></nav></body></html>
            """)));
        var navPoints = string.Concat(chapters.Select((c, i) =>
            $"<navPoint id=\"np{i + 1}\" playOrder=\"{i + 1}\"><navLabel><text>{Markdown.Escape(c.Title)}</text></navLabel><content src=\"chapter-{i + 1}.xhtml\"/></navPoint>"));
        entries.Add(("OEBPS/toc.ncx", Utf8(xmlHeader + $"""
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
            <head><meta name="dtb:uid" content="{identifier}"/></head>
            <docTitle><text>{escapedTitle}</text></docTitle><navMap>{navPoints}</navMap></ncx>
            """)));
        var manifest = string.Concat(chapters.Select((_, i) => $"<item id=\"c{i + 1}\" href=\"chapter-{i + 1}.xhtml\" media-type=\"application/xhtml+xml\"/>"))
            + string.Concat(pictures.Select((p, i) => $"<item id=\"p{i + 1}\" href=\"{p.Name}\" media-type=\"{p.Type}\"/>"));
        var spine = string.Concat(chapters.Select((_, i) => $"<itemref idref=\"c{i + 1}\"/>"));
        var modified = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        entries.Add(("OEBPS/content.opf", Utf8(xmlHeader + $"""
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="{language}">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="bookid">{identifier}</dc:identifier>
                <dc:title>{escapedTitle}</dc:title>
                <dc:language>{language}</dc:language>
                <meta property="dcterms:modified">{modified}</meta>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="css" href="style.css" media-type="text/css"/>
                {manifest}
              </manifest>
              <spine toc="ncx">{spine}</spine>
            </package>
            """)));
        // The mimetype entry must be first and stored uncompressed.
        Zip.Write(output, entries, new HashSet<string> { "mimetype" });
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    /// <summary>A picture for the book: embedded (data:) or a local file. Never anything remote.</summary>
    public static (byte[] Data, string Extension, string Type)? PackedPicture(string src, string? baseFolder)
    {
        byte[]? data = null;
        try
        {
            var comma = src.IndexOf(',');
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0 && src[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                data = Convert.FromBase64String(src[(comma + 1)..]);
            else if (!LocalOnly.IsRemote(src) && baseFolder is not null)
            {
                var file = src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(src, UriKind.Absolute, out var uri) && uri.IsFile
                    ? uri.LocalPath
                    : Path.GetFullPath(Uri.UnescapeDataString(src).Replace('/', '\\'), baseFolder);
                if (File.Exists(file)) data = File.ReadAllBytes(file);
            }
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or UriFormatException) { }
        if (data is null || data.Length < 4) return null;
        if (data[0] == 0x89 && data[1] == 0x50) return (data, "png", "image/png");
        if (data[0] == 0xFF && data[1] == 0xD8) return (data, "jpg", "image/jpeg");
        if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F') return (data, "gif", "image/gif");
        if (Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 512)).Contains("<svg", StringComparison.OrdinalIgnoreCase)) return (data, "svg", "image/svg+xml");
        // Any other picture format becomes a PNG, which every reader shows.
        var scratch = Output.ScratchDirectory();
        try
        {
            var file = Path.Combine(scratch, "picture");
            File.WriteAllBytes(file, data);
            Imaging.Initialize();
            if (Imaging.FormatOf(file, Format.Png) is not { } format) return null;
            using var image = new MagickImage(data, new MagickReadSettings { Format = format });
            return (image.ToByteArray(MagickFormat.Png), "png", "image/png");
        }
        catch (MagickException) { return null; }
        finally { Output.Discard(scratch); }
    }
}
