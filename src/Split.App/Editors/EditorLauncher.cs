using System.Windows;

namespace Split.App;

/// <summary>Opens the editors. Each editor window opens once for the same files: asking again brings it to the front.</summary>
public static class EditorLauncher
{
    /// <summary>Opens the editor; a file that can't be read is explained instead of opening an empty window.</summary>
    public static void Open(EditorTool tool, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            switch (tool)
            {
                case EditorTool.Crop:
                    Single(tool, paths, () => new CropEditor(paths, WindowTitle(tool, paths), Preview(paths[0])));
                    break;
                case EditorTool.Annotate:
                    foreach (var path in paths) Single(tool, [path], () => new AnnotateEditor(path, WindowTitle(tool, [path]), Preview(path, 1600)));
                    break;
                case EditorTool.Adjust:
                    Single(tool, paths, () => new AdjustEditor(paths, WindowTitle(tool, paths), Preview(paths[0])));
                    break;
                case EditorTool.Background:
                    Single(tool, paths, () => new BackgroundEditor(paths, WindowTitle(tool, paths), Preview(paths[0])));
                    break;
                case EditorTool.Collage:
                    Single(tool, paths, () =>
                    {
                        foreach (var path in paths) FileCheck.Preflight(path, waitForDownload: false);
                        var model = new CollageModel(paths);
                        if (model.Thumbnails.Count == 0) throw new Problem(L("None of the images could be read."), L("Check that they open in Photos."));
                        return new CollageEditor(model, WindowTitle(tool, paths));
                    });
                    break;
                case EditorTool.Media:
                    foreach (var path in paths) Single(tool, [path], () => new MediaEditor(path, WindowTitle(tool, [path])));
                    break;
                case EditorTool.PdfPages:
                    Single(tool, paths, () =>
                    {
                        foreach (var path in paths) FileCheck.Preflight(path, waitForDownload: false);
                        return new PdfOrganizerEditor(new PdfOrganizerModel(paths), WindowTitle(tool, paths));
                    });
                    break;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Hud.Current.ShowDone([], L("Couldn’t open the editor “%@”", tool.Title()), [new JobFailure(Path.GetFileName(paths[0]), Explain.Error(error, paths[0]))]);
        }
    }

    /// <summary>The picture at the size editing works on; the full resolution is read only when saving.</summary>
    public static SkiaSharp.SKBitmap Preview(string path, int maxSide = EditorImages.PreviewSide)
    {
        FileCheck.Preflight(path, waitForDownload: false);
        return EditorImages.Load(path, maxSide);
    }

    public static string WindowTitle(EditorTool tool, IReadOnlyList<string> paths)
    {
        var subject = paths.Count == 1 ? Path.GetFileName(paths[0])
            : paths.All(p => Formats.Detect(p)?.Group() == FormatGroup.Image) ? L("%@ images", paths.Count) : L("%@ files", paths.Count);
        return $"{tool.Title()} — {subject}";
    }

    private static void Single<T>(EditorTool tool, IReadOnlyList<string> paths, Func<T> make) where T : Window =>
        SingleWindows.Single($"editor:{tool}:{string.Join('|', paths.Select(p => Path.GetFullPath(p).ToLowerInvariant()))}", make);
}
