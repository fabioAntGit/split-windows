using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;
using global::Windows.Storage.Streams;
using WpfImage = System.Windows.Controls.Image;

namespace Split.Windows;

/// <summary>Reading RTF, recognising text and driving Office for the document converters.</summary>
public sealed partial class WindowsPlatform
{
    public void ExportWithOffice(OfficeApp app, string source, string output, OfficeExport export) => OfficeApps.Export(app, source, output, export);

    // MARK: OCR

    public string RecognizeText(byte[] png)
    {
        try
        {
            if (OcrEngine.TryCreateFromUserProfileLanguages() is not { } engine) return "";
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            using var bitmap = decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
            // The engine refuses pictures bigger than it can take; such a page reads as no text.
            if (bitmap.PixelWidth > OcrEngine.MaxImageDimension || bitmap.PixelHeight > OcrEngine.MaxImageDimension) return "";
            var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            return string.Join("\n", result.Lines.Select(line => line.Text));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return "";
        }
    }

    // MARK: RTF

    public RichDocument ReadRtf(string rtf)
    {
        RichDocument? document = null;
        Exception? failure = null;
        // The rich text engine needs a single-threaded apartment; no window or application is involved.
        var thread = new Thread(() =>
        {
            try { document = ReadRtfHere(rtf); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidDataException(failure.Message, failure);
        return document!;
    }

    private static RichDocument ReadRtfHere(string rtf)
    {
        // Text with no font of its own is Arial 12 pt, as in every other format.
        var flow = new FlowDocument { FontFamily = new FontFamily(RichDocument.DefaultFont), FontSize = RichDocument.DefaultSize / 0.75 };
        using (var stream = new MemoryStream(System.Text.Encoding.Latin1.GetBytes(rtf)))
            new TextRange(flow.ContentStart, flow.ContentEnd).Load(stream, DataFormats.Rtf);
        var document = new RichDocument();
        Blocks(flow.Blocks, document.Blocks);
        return document;
    }

    private static void Blocks(IEnumerable<Block> source, List<DocBlock> target)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    ReadParagraph(paragraph, target, null, 0);
                    break;
                case List list:
                    var number = list.StartIndex;
                    var counted = list.MarkerStyle is not (TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box or TextMarkerStyle.None);
                    foreach (var item in list.ListItems)
                    {
                        var first = true;
                        foreach (var inner in item.Blocks)
                        {
                            if (inner is Paragraph content)
                            {
                                ReadParagraph(content, target, first ? counted ? number++ + "." : "•" : null, 1);
                                first = false;
                            }
                            else Blocks([inner], target);
                        }
                    }
                    break;
                case Table table:
                    var converted = new DocTable();
                    foreach (var row in table.RowGroups.SelectMany(g => g.Rows))
                    {
                        var cells = new List<DocCell>();
                        foreach (var cell in row.Cells)
                        {
                            var made = new DocCell { Span = Math.Max(1, cell.ColumnSpan) };
                            Blocks(cell.Blocks, made.Blocks);
                            cells.Add(made);
                        }
                        converted.Rows.Add(cells);
                    }
                    target.Add(converted);
                    break;
                case Section section:
                    Blocks(section.Blocks, target);
                    break;
                case BlockUIContainer { Child: WpfImage image } when Picture(image) is { } picture:
                    target.Add(picture);
                    break;
            }
        }
    }

    private static void ReadParagraph(Paragraph source, List<DocBlock> target, string? marker, int level)
    {
        var paragraph = new DocParagraph
        {
            Alignment = source.TextAlignment switch
            {
                TextAlignment.Center => DocAlignment.Center, TextAlignment.Right => DocAlignment.Right, TextAlignment.Justify => DocAlignment.Justify,
                _ => DocAlignment.Left,
            },
            Indent = Math.Max(0, source.Margin.Left * 0.75) + (marker is null ? 0 : 18 * level),
            SpacingBefore = Math.Max(0, source.Margin.Top * 0.75),
            SpacingAfter = Math.Max(0, source.Margin.Bottom * 0.75),
            Marker = marker, ListLevel = level,
        };
        var start = target.Count;
        Inlines(source.Inlines, paragraph, target, false, false, null);
        if (paragraph.Runs.Count > 0 || target.Count == start) target.Add(paragraph);
    }

    private static void Inlines(IEnumerable<Inline> inlines, DocParagraph paragraph, List<DocBlock> target, bool underline, bool strike, string? link)
    {
        foreach (var inline in inlines)
        {
            var decorations = inline.TextDecorations;
            var underlined = underline || decorations.Any(d => d.Location == TextDecorationLocation.Underline);
            var struck = strike || decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);
            switch (inline)
            {
                case Run run when run.Text.Length > 0:
                    var color = (run.Foreground as SolidColorBrush)?.Color;
                    var item = new DocRun
                    {
                        Text = run.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(((char)0x2028).ToString(), "\n"),
                        Font = run.FontFamily.Source == RichDocument.DefaultFont ? null : run.FontFamily.Source,
                        Size = Math.Round(run.FontSize * 0.75 * 2) / 2,
                        Bold = run.FontWeight.ToOpenTypeWeight() >= 600,
                        Italic = run.FontStyle != FontStyles.Normal,
                        Underline = underlined, Strike = struck, Link = link,
                        Superscript = run.BaselineAlignment == BaselineAlignment.Superscript,
                        Subscript = run.BaselineAlignment == BaselineAlignment.Subscript,
                        Color = color is { } c && (c.R, c.G, c.B) != (0, 0, 0) ? $"{c.R:X2}{c.G:X2}{c.B:X2}" : null,
                    };
                    if (item.Size == RichDocument.DefaultSize) item.Size = 0;
                    if (paragraph.Runs.Count > 0 && paragraph.Runs[^1].SameLook(item)) paragraph.Runs[^1].Text += item.Text;
                    else paragraph.Runs.Add(item);
                    break;
                case LineBreak:
                    if (paragraph.Runs.Count > 0) paragraph.Runs[^1].Text += "\n";
                    break;
                case Hyperlink hyperlink:
                    var address = hyperlink.NavigateUri?.OriginalString;
                    Inlines(hyperlink.Inlines, paragraph, target, underlined, struck, address is not null && RichDocument.IsPlainLink(address) ? address : link);
                    break;
                case Span span:
                    Inlines(span.Inlines, paragraph, target, underlined, struck, link);
                    break;
                case InlineUIContainer { Child: WpfImage image } when Picture(image) is { } picture:
                    // A picture stands between two paragraphs, as in the model.
                    if (paragraph.Runs.Count > 0)
                    {
                        var before = new DocParagraph { Alignment = paragraph.Alignment, Indent = paragraph.Indent, Marker = paragraph.Marker };
                        before.Runs.AddRange(paragraph.Runs.Select(r => r with { }));
                        target.Add(before);
                        paragraph.Runs.Clear();
                        paragraph.Marker = null;
                    }
                    target.Add(picture);
                    break;
            }
        }
    }

    private static DocPicture? Picture(WpfImage image)
    {
        if (image.Source is not BitmapSource bitmap) return null;
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        var width = double.IsNaN(image.Width) ? bitmap.Width : image.Width;
        var height = double.IsNaN(image.Height) ? bitmap.Height : image.Height;
        return new DocPicture(stream.ToArray(), "png", width * 0.75, height * 0.75);
    }
}
