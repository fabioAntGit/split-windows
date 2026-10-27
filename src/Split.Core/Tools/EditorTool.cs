namespace Split;

/// <summary>Tools that open a window, listed first in the tools wheel.</summary>
public enum EditorTool { Crop, Annotate, Adjust, Background, Collage, Media, PdfPages }

public static class EditorTools
{
    public static readonly IReadOnlyList<EditorTool> All = Enum.GetValues<EditorTool>();

    private static readonly string[] Raws = ["crop", "annotate", "adjust", "background", "collage", "media", "pdfPages"];

    public static string Raw(this EditorTool tool) => Raws[(int)tool];

    public static string Title(this EditorTool tool) => tool switch
    {
        EditorTool.Crop => L("Crop"),
        EditorTool.Annotate => L("Annotate and Cover"),
        EditorTool.Adjust => L("Adjust"),
        EditorTool.Background => L("Add Background"),
        EditorTool.Collage => L("Collage"),
        EditorTool.Media => L("Edit Media"),
        _ => L("Organize PDF"),
    };

    public static string Symbol(this EditorTool tool) => tool switch
    {
        EditorTool.Crop => "crop.rotate",
        EditorTool.Annotate => "pencil.tip.crop.circle",
        EditorTool.Adjust => "slider.horizontal.3",
        EditorTool.Background => "square.on.square.dashed",
        EditorTool.Collage => "square.grid.2x2",
        EditorTool.Media => "timeline.selection",
        _ => "doc.richtext",
    };

    public static string Detail(this EditorTool tool) => tool switch
    {
        EditorTool.Crop => L("Crop, rotate, flip"),
        EditorTool.Annotate => L("Arrows, text, blur"),
        EditorTool.Adjust => L("Light, color and filters"),
        EditorTool.Background => L("Frame and background"),
        EditorTool.Collage => L("Several images in one"),
        EditorTool.Media => L("Trim, speed, cover"),
        _ => L("Reorder, rotate, delete"),
    };

    /// <summary>What it does and which files it takes, for Settings.</summary>
    public static string Explanation(this EditorTool tool) => tool switch
    {
        EditorTool.Crop => L("Crop, straighten, rotate or flip pictures, several at once. For images."),
        EditorTool.Annotate => L("Draw arrows, shapes and text, or blur and cover what shouldn’t be seen. For up to 4 images."),
        EditorTool.Adjust => L("Change light, colour and sharpness, or apply a filter. For images."),
        EditorTool.Background => L("Put a picture on a coloured background or in a frame, with rounded corners and a shadow. For images."),
        EditorTool.Collage => L("Arrange several pictures into one image. For 2 to 30 images."),
        EditorTool.Media => L("Trim, change the speed and volume, fade, crop or cover parts of a video or a sound. For audio and video."),
        _ => L("Reorder, rotate, duplicate, delete and insert pages, or save some apart. For PDFs."),
    };

    public static bool Applies(this EditorTool tool, IReadOnlyList<string> paths)
    {
        var formats = paths.Select(Formats.Detect).OfType<Format>().ToList();
        if (formats.Count != paths.Count || formats.Count == 0) return false;
        var images = formats.All(f => f.Group() == FormatGroup.Image);
        return tool switch
        {
            EditorTool.Crop or EditorTool.Adjust or EditorTool.Background => images && paths.Count <= 100,
            EditorTool.Annotate => images && paths.Count <= 4,
            EditorTool.Collage => images && paths.Count is >= 2 and <= 30,
            EditorTool.Media => formats.All(f => f.Group() is FormatGroup.Audio or FormatGroup.Video) && paths.Count <= 3,
            _ => formats.Contains(Format.Pdf) && formats.All(f => f == Format.Pdf || f.Group() == FormatGroup.Image) && paths.Count <= 20,
        };
    }

    public static IEnumerable<EditorTool> Available(IReadOnlyList<string> paths) => All.Where(tool => tool.Applies(paths));
}
