using System.Text;

namespace Split;

public enum DocAlignment { Left, Center, Right, Justify }

/// <summary>A piece of text with one look. Tabs and line breaks stay inside the text as \t and \n.</summary>
public sealed record DocRun
{
    public string Text { get; set; } = "";

    /// <summary>Null means the document's own font.</summary>
    public string? Font { get; set; }

    /// <summary>In points; 0 means the document's own size.</summary>
    public double Size { get; set; }

    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strike { get; set; }
    public bool Superscript { get; set; }
    public bool Subscript { get; set; }

    /// <summary>"RRGGBB"; null means black.</summary>
    public string? Color { get; set; }

    /// <summary>Only web and mail addresses are kept.</summary>
    public string? Link { get; set; }

    public bool SameLook(DocRun other) =>
        Font == other.Font && Size == other.Size && Bold == other.Bold && Italic == other.Italic && Underline == other.Underline
        && Strike == other.Strike && Superscript == other.Superscript && Subscript == other.Subscript && Color == other.Color && Link == other.Link;
}

public abstract class DocBlock;

public sealed class DocParagraph : DocBlock
{
    public List<DocRun> Runs { get; } = [];
    public DocAlignment Alignment { get; set; }

    /// <summary>1 to 6 for headings, 0 for body text.</summary>
    public int HeadingLevel { get; set; }

    /// <summary>"•" or "1." when the paragraph is a list item; writers put it in front, followed by a tab.</summary>
    public string? Marker { get; set; }

    public int ListLevel { get; set; }
    public double SpacingBefore { get; set; }
    public double SpacingAfter { get; set; }

    /// <summary>Points from the left margin.</summary>
    public double Indent { get; set; }

    public string Text => string.Concat(Runs.Select(r => r.Text));

    public DocParagraph Add(DocRun run)
    {
        if (run.Text.Length > 0) Runs.Add(run);
        return this;
    }
}

public sealed class DocCell
{
    public List<DocBlock> Blocks { get; } = [];
    public int Span { get; set; } = 1;
}

public sealed class DocTable : DocBlock
{
    public List<List<DocCell>> Rows { get; } = [];
}

/// <summary>A picture as PNG or JPEG bytes, with the size it takes on the page.</summary>
public sealed class DocPicture(byte[] data, string extension, double width, double height) : DocBlock
{
    public byte[] Data { get; } = data;

    /// <summary>"png" or "jpeg".</summary>
    public string Extension { get; } = extension;

    public string Mime => "image/" + Extension;
    public double Width { get; } = width;
    public double Height { get; } = height;
}

public sealed class DocPageBreak : DocBlock;

/// <summary>
/// Text with its look, as every reader produces it and every writer takes it. Formats that can't keep
/// something (pictures in TXT, links in RTF…) drop it; <see cref="DocumentPipeline"/> says so.
/// </summary>
public sealed class RichDocument
{
    /// <summary>What text without a font or size of its own is set in.</summary>
    public const string DefaultFont = "Arial";
    public const double DefaultSize = 12;

    public List<DocBlock> Blocks { get; } = [];

    public static IEnumerable<DocBlock> Flatten(IEnumerable<DocBlock> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            if (block is DocTable table)
                foreach (var inner in Flatten(table.Rows.SelectMany(r => r).SelectMany(c => c.Blocks))) yield return inner;
        }
    }

    /// <summary>Only web and mail addresses are kept as links.</summary>
    public static bool IsPlainLink(string address) =>
        address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<DocBlock> All => Flatten(Blocks);

    public int PictureCount => All.Count(b => b is DocPicture);

    public int LinkCount => All.OfType<DocParagraph>().Sum(p => p.Runs.Count(r => r.Link is not null));

    public string PlainText
    {
        get
        {
            var text = new StringBuilder();
            Write(Blocks, text);
            return text.ToString();
        }
    }

    private static void Write(IEnumerable<DocBlock> blocks, StringBuilder text)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocParagraph paragraph:
                    if (paragraph.Marker is not null) text.Append(paragraph.Marker).Append('\t');
                    text.Append(paragraph.Text).Append('\n');
                    break;
                case DocTable table:
                    foreach (var row in table.Rows)
                    {
                        var cells = new List<string>();
                        foreach (var cell in row)
                        {
                            var inner = new StringBuilder();
                            Write(cell.Blocks, inner);
                            cells.Add(inner.ToString().TrimEnd('\n').Replace('\n', ' '));
                        }
                        text.Append(string.Join('\t', cells)).Append('\n');
                    }
                    break;
                case DocPageBreak:
                    text.Append('\f').Append('\n');
                    break;
            }
        }
    }
}
