using System.Collections.Concurrent;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Split;

/// <summary>One page of the new document: which source it comes from, which page of it, and how far it is turned on top of its own rotation.</summary>
public sealed record PageItem(int Source, int Page)
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Extra rotation in degrees (multiples of 90).</summary>
    public int Rotation { get; init; }
}

/// <summary>A PDF the organizer takes pages from. Pictures are turned into one-page PDFs when added.</summary>
public sealed class PageSource : IDisposable
{
    private readonly byte[]? bytes;
    private readonly string? path;

    internal PageSource(string name, string? path, byte[]? bytes, PdfiumDocument document)
    {
        (Name, this.path, this.bytes, Document) = (name, path, bytes, document);
        PageCount = document.PageCount;
    }

    public string Name { get; }

    /// <summary>For drawing thumbnails; saving reads the source again, so it still works after this is disposed.</summary>
    public PdfiumDocument Document { get; }

    public int PageCount { get; }

    internal PdfDocument OpenForImport() =>
        bytes is not null ? PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import) : PdfReader.Open(path!, PdfDocumentOpenMode.Import);

    public void Dispose() => Document.Dispose();
}

public static class PdfOrganizer
{
    /// <summary>PDFs as they are; pictures become one page each.</summary>
    public static PageSource Load(string path)
    {
        var name = Path.GetFileName(path);
        if (Formats.Detect(path) == Format.Pdf) return new PageSource(name, path, null, PdfTools.Open(path));
        var folder = Output.ScratchDirectory();
        try
        {
            var page = Path.Combine(folder, "page.pdf");
            try
            {
                var format = Formats.Detect(path) ?? Format.Png;
                if (format == Format.Svg)
                {
                    using var drawn = ImageConverter.RenderSvg(path);
                    ImageConverter.WriteSingle(drawn, null, page, Format.Pdf, format, faithful: false);
                }
                else
                {
                    using var frames = ImageConverter.Open(path, format);
                    ImageConverter.WriteSingle(frames.Images[frames.IsIcon ? frames.Largest : 0], path, page, Format.Pdf, format, faithful: false);
                }
            }
            catch (Exception e) when (e is not Problem and not OutOfMemoryException)
            {
                throw new Problem(L("Couldn’t turn “%@” into a PDF page.", name), technical: e.Message);
            }
            return new PageSource(name, null, File.ReadAllBytes(page), PdfTools.Open(page));
        }
        finally { Output.Discard(folder); }
    }

    /// <summary>Writes the new document from the page list: pages are copied from their sources and the rotations added.</summary>
    public static void Write(IReadOnlyList<PageItem> items, IReadOnlyList<PageSource> sources, string output)
    {
        if (items.Count == 0) throw new Problem(L("The document would have no pages left."), L("Keep at least one page."));
        var temp = Output.TempSibling(output);
        // A page used twice is imported from its own reader: the copies must not share one object, or they would turn together.
        var readers = new List<(int Source, PdfDocument Reader, HashSet<int> Used)>();
        try
        {
            using var document = new PdfDocument();
            foreach (var item in items)
            {
                JobProgress.CheckCancelled();
                if (item.Source < 0 || item.Source >= sources.Count || item.Page < 0 || item.Page >= sources[item.Source].PageCount)
                    throw new Problem(L("One of the pages is no longer available in its source document."));
                var entry = readers.FirstOrDefault(r => r.Source == item.Source && !r.Used.Contains(item.Page));
                if (entry.Reader is null)
                {
                    entry = (item.Source, sources[item.Source].OpenForImport(), []);
                    readers.Add(entry);
                }
                entry.Used.Add(item.Page);
                var page = document.AddPage(entry.Reader.Pages[item.Page]);
                page.Rotate = (((page.Rotate + item.Rotation) % 360) + 360) % 360;
                JobProgress.Report((document.PageCount) / (items.Count + 1.0));
            }
            using (var file = File.Create(temp)) document.Save(file);
        }
        catch (Exception e) when (e is not Problem and not IOException and not UnauthorizedAccessException)
        {
            Output.Discard(temp);
            throw new Problem(L("Split couldn’t save the PDF."), FileCheck.PrivacyHint(Path.GetDirectoryName(output) ?? output), e.Message);
        }
        catch
        {
            Output.Discard(temp);
            throw;
        }
        finally
        {
            foreach (var (_, reader, _) in readers) reader.Dispose();
        }
        Output.Commit(temp, output);
    }
}

/// <summary>The state of the Organize PDF editor: the pages in order, which are selected, and what was added or changed.</summary>
public sealed class PdfOrganizerModel : IDisposable
{
    private const double ThumbnailWidth = 240, ThumbnailHeight = 300;

    private readonly List<PageSource> sources = [];
    private readonly ConcurrentDictionary<(int Source, int Page), SKBitmap> thumbnails = new();
    private readonly List<PageItem> original;
    private volatile bool closing;

    public PdfOrganizerModel(IReadOnlyList<string> paths)
    {
        First = paths[0];
        try
        {
            foreach (var path in paths) Add(path);
        }
        catch
        {
            Dispose();
            throw;
        }
        original = [.. Items];
    }

    public string First { get; }
    public List<PageItem> Items { get; private set; } = [];
    public HashSet<Guid> Selection { get; set; } = [];
    public string? Note { get; private set; }

    /// <summary>Raised when the pages, the selection or the note changed, on the calling thread.</summary>
    public event Action? Changed;

    /// <summary>Raised from the worker that drew thumbnails.</summary>
    public event Action? ThumbnailsChanged;

    /// <summary>Thumbnails are drawn one after the other in the background; this ends when all are drawn.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<PageSource> Sources => sources;
    public IReadOnlyList<string> Names => sources.Select(s => s.Name).ToList();

    public bool HasChanges => !Items.SequenceEqual(original);

    public SKBitmap? Thumbnail(PageItem item) => thumbnails.GetValueOrDefault((item.Source, item.Page));

    public void Add(string path)
    {
        var source = PdfOrganizer.Load(path);
        var index = sources.Count;
        sources.Add(source);
        var added = Enumerable.Range(0, source.PageCount).Select(page => new PageItem(index, page)).ToList();
        Items.AddRange(added);
        var previous = Pending;
        Pending = Task.Run(() =>
        {
            previous.Wait();
            foreach (var item in added)
            {
                if (closing) return;
                try
                {
                    var (width, height) = source.Document.PageSize(item.Page);
                    var scale = Math.Min(ThumbnailWidth / Math.Max(width, 1), ThumbnailHeight / Math.Max(height, 1));
                    using var image = source.Document.Render(item.Page, scale);
                    if (image is null) continue;
                    using var pixels = image.GetPixels();
                    thumbnails[(item.Source, item.Page)] = EditorImages.FromStraight(pixels.ToByteArray(ImageMagick.PixelMapping.RGBA)!, (int)image.Width, (int)image.Height);
                    ThumbnailsChanged?.Invoke();
                }
                catch (Exception e) when (e is PdfDamagedException or Problem) { }
            }
        });
        Changed?.Invoke();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (var path in Intake.Resolve(paths))
        {
            try { Add(path); }
            catch (Exception e) when (e is not OutOfMemoryException) { failed.Add($"{Path.GetFileName(path)}: {Explain.Error(e, path).Message}"); }
        }
        Note = failed.Count == 0 ? null : L("Couldn’t add: %@", string.Join(" · ", failed));
        Changed?.Invoke();
    }

    /// <summary>The selected pages, or every page when none is selected.</summary>
    private HashSet<Guid> Targets => Selection.Count == 0 ? Items.Select(i => i.Id).ToHashSet() : Selection;

    public void Rotate(int degrees)
    {
        var targets = Targets;
        Items = Items.Select(item => targets.Contains(item.Id) ? item with { Rotation = (item.Rotation + degrees + 360) % 360 } : item).ToList();
        Changed?.Invoke();
    }

    public void RemoveSelected()
    {
        if (Selection.Count == 0) return;
        if (Selection.Count == Items.Count)
        {
            Note = L("You can’t delete every page: the PDF would be empty.");
            Changed?.Invoke();
            return;
        }
        Items.RemoveAll(item => Selection.Contains(item.Id));
        Selection = [];
        Changed?.Invoke();
    }

    public void DuplicateSelection()
    {
        var result = new List<PageItem>();
        foreach (var item in Items)
        {
            result.Add(item);
            if (Selection.Contains(item.Id)) result.Add(new PageItem(item.Source, item.Page) { Rotation = item.Rotation });
        }
        Items = result;
        Changed?.Invoke();
    }

    public void Move(Guid id, Guid before)
    {
        var from = Items.FindIndex(i => i.Id == id);
        var to = Items.FindIndex(i => i.Id == before);
        if (id == before || from < 0 || to < 0) return;
        var item = Items[from];
        Items.RemoveAt(from);
        Items.Insert(to, item);
        Changed?.Invoke();
    }

    public void SelectAll()
    {
        Selection = Items.Select(i => i.Id).ToHashSet();
        Changed?.Invoke();
    }

    public void Toggle(Guid id, bool extend)
    {
        if (extend)
        {
            if (!Selection.Remove(id)) Selection.Add(id);
        }
        else
        {
            Selection = Selection.Count == 1 && Selection.Contains(id) ? [] : [id];
        }
        Changed?.Invoke();
    }

    public void Save(bool onlySelection)
    {
        var chosen = onlySelection ? Items.Where(i => Selection.Contains(i.Id)).ToList() : Items.ToList();
        var chosenSources = sources.ToList();
        var first = First;
        var suffix = " (" + (onlySelection ? L("extracted pages") : L("organized")) + ")";
        JobRunner.Shared.Single(onlySelection ? L("Extracting pages…") : L("Saving the PDF…"), Path.GetFileName(first), [], () =>
        {
            var output = Output.Url(first, "pdf", suffix);
            PdfOrganizer.Write(chosen, chosenSources, output);
            return [output];
        });
    }

    public void Dispose()
    {
        closing = true;
        try { Pending.Wait(); } catch (AggregateException) { }
        foreach (var source in sources) source.Dispose();
        foreach (var thumbnail in thumbnails.Values) thumbnail.Dispose();
        thumbnails.Clear();
    }
}
