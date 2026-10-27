using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Split;

/// <summary>
/// RTF and RTFD. The text and its look are read by Windows' own rich text engine (through the platform); an RTFD folder
/// also holds its pictures as files next to <c>TXT.rtf</c>, which are put back where the text refers to them.
/// </summary>
public static class RtfReader
{
    private static readonly Regex Attachment = new(@"\{(?:\\\*)?\\NeXTGraphic\s+(?<name>[^\\{}]*?)\s*(?<rest>(?:\\[a-zA-Z]+-?\d*\s*)*)\}");

    /// <summary>The first private-use character; marker <c>i</c> stands for the i-th attachment.</summary>
    private const int FirstMarker = 0xE000;

    public static RichDocument Read(string path)
    {
        var folder = Directory.Exists(path) ? path : null;
        var rtf = Encoding.Latin1.GetString(File.ReadAllBytes(folder is null ? path : Path.Combine(folder, "TXT.rtf")));
        var attachments = new List<(string Name, double Width, double Height)>();
        if (folder is not null)
        {
            rtf = Attachment.Replace(rtf, m =>
            {
                double Points(string key) => Regex.Match(m.Groups["rest"].Value, $@"\\{key}(\d+)") is { Success: true } found
                    ? double.Parse(found.Groups[1].Value, CultureInfo.InvariantCulture) / 20 : 0;
                attachments.Add((m.Groups["name"].Value.Trim(), Points("width"), Points("height")));
                return $"\\u{FirstMarker + attachments.Count - 1 - 65536}?";
            });
        }
        var document = Platform.Current.ReadRtf(rtf);
        if (attachments.Count > 0) PutPictures(document.Blocks, attachments, folder!);
        return document;
    }

    /// <summary>Splits every paragraph at its markers and puts the picture where each stood.</summary>
    private static void PutPictures(List<DocBlock> blocks, List<(string Name, double Width, double Height)> attachments, string folder)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index] is DocTable table)
            {
                foreach (var cell in table.Rows.SelectMany(r => r)) PutPictures(cell.Blocks, attachments, folder);
                continue;
            }
            if (blocks[index] is not DocParagraph paragraph || !paragraph.Text.Any(c => c >= FirstMarker && c < FirstMarker + attachments.Count)) continue;
            var pieces = new List<DocBlock>();
            var current = new DocParagraph { Alignment = paragraph.Alignment, HeadingLevel = paragraph.HeadingLevel, Indent = paragraph.Indent };
            foreach (var run in paragraph.Runs)
            {
                var text = new StringBuilder();
                void Close()
                {
                    if (text.Length > 0) current.Runs.Add(run with { Text = text.ToString() });
                    text.Clear();
                }
                foreach (var c in run.Text)
                {
                    if (c < FirstMarker || c >= FirstMarker + attachments.Count)
                    {
                        text.Append(c);
                        continue;
                    }
                    Close();
                    if (current.Runs.Count > 0) pieces.Add(current);
                    current = new DocParagraph { Alignment = paragraph.Alignment, Indent = paragraph.Indent };
                    var (name, width, height) = attachments[c - FirstMarker];
                    if (File.Exists(Path.Combine(folder, Path.GetFileName(name))) && Pictures.FromBytes(File.ReadAllBytes(Path.Combine(folder, Path.GetFileName(name))), width, height) is { } picture)
                        pieces.Add(picture);
                }
                Close();
            }
            if (current.Runs.Count > 0) pieces.Add(current);
            blocks.RemoveAt(index);
            blocks.InsertRange(index, pieces);
            index += pieces.Count - 1;
        }
    }
}
