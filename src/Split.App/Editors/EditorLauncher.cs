using System.Windows;

namespace Split.App;

/// <summary>Opens the editors. Each editor window opens once for the same files: asking again brings it to the front.</summary>
public static class EditorLauncher
{
    public static bool Handles(EditorTool tool) => tool is EditorTool.Crop or EditorTool.Annotate or EditorTool.Adjust or EditorTool.Background;

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
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            NotificationHud.Shared.ShowError(Explain.Error(error, paths[0]), L("Couldn’t open the editor “%@”", tool.Title()));
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
