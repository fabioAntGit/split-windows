namespace Split;

/// <summary>One-click tools on the tools wheel: no editor window, the result goes beside the file.</summary>
public enum QuickTool
{
    Compress, CompressToSize, StripMetadata, RemoveBackground,
    CombinePdf, SplitPages, Extract, Zip,
    Normalize, Mono, TrimSilence, Mute, ConcatVideos,
    Snapshots, WaveformVideo, ReadQr,
}

public sealed record ToolResult(IReadOnlyList<string> Outputs, string? Message = null);

public static class QuickTools
{
    public static readonly IReadOnlyList<QuickTool> All = Enum.GetValues<QuickTool>();

    private static readonly string[] Raws =
    [
        "compress", "compress-to", "strip-metadata", "remove-background", "merge-pdf", "split-pdf", "extract", "zip",
        "normalize", "mono", "trim-silence", "mute", "join-videos", "snapshots", "audio-to-video", "read-qr",
    ];

    public static string Raw(this QuickTool tool) => Raws[(int)tool];

    public static QuickTool? FromRaw(string raw) => Array.IndexOf(Raws, raw) is var index and >= 0 ? (QuickTool)index : null;

    public static string Title(this QuickTool tool) => tool switch
    {
        QuickTool.Compress => L("Compress"),
        QuickTool.CompressToSize => L("Compress To…"),
        QuickTool.StripMetadata => L("Remove Metadata"),
        QuickTool.RemoveBackground => L("Remove Background"),
        QuickTool.CombinePdf => L("Combine into PDF"),
        QuickTool.SplitPages => L("Split PDF"),
        QuickTool.Extract => L("Extract"),
        QuickTool.Zip => L("Create ZIP"),
        QuickTool.Normalize => L("Normalize Volume"),
        QuickTool.Mono => L("Convert to Mono"),
        QuickTool.TrimSilence => L("Trim Silence"),
        QuickTool.Mute => L("Remove Audio"),
        QuickTool.ConcatVideos => L("Join Videos"),
        QuickTool.Snapshots => L("Frames"),
        QuickTool.WaveformVideo => L("Audio to Video"),
        _ => L("Read QR Code"),
    };

    public static string Symbol(this QuickTool tool) => tool switch
    {
        QuickTool.Compress => "arrow.down.right.and.arrow.up.left",
        QuickTool.CompressToSize => "gauge.with.dots.needle.33percent",
        QuickTool.StripMetadata => "eye.slash",
        QuickTool.RemoveBackground => "person.crop.rectangle",
        QuickTool.CombinePdf => "doc.on.doc",
        QuickTool.SplitPages => "scissors",
        QuickTool.Extract => "shippingbox",
        QuickTool.Zip => "archivebox",
        QuickTool.Normalize => "speaker.wave.2",
        QuickTool.Mono => "speaker.wave.1",
        QuickTool.TrimSilence => "waveform.badge.minus",
        QuickTool.Mute => "speaker.slash",
        QuickTool.ConcatVideos => "film.stack",
        QuickTool.Snapshots => "photo.on.rectangle",
        QuickTool.WaveformVideo => "waveform.and.magnifyingglass",
        _ => "qrcode.viewfinder",
    };

    /// <summary>Shown in the centre of the wheel while hovering.</summary>
    public static string Detail(this QuickTool tool) => tool switch
    {
        QuickTool.Compress => L("A lighter copy"),
        QuickTool.CompressToSize => L("Choose the largest size"),
        QuickTool.StripMetadata => L("No GPS, camera or dates"),
        QuickTool.RemoveBackground => L("PNG with transparency"),
        QuickTool.CombinePdf => L("One PDF"),
        QuickTool.SplitPages => L("One PDF per page"),
        QuickTool.Extract => L("Opens the archive"),
        QuickTool.Zip => L("Compresses into a ZIP"),
        QuickTool.Normalize => L("Even volume"),
        QuickTool.Mono => L("A single channel"),
        QuickTool.TrimSilence => L("Removes silence at the start and end"),
        QuickTool.Mute => L("Video without sound"),
        QuickTool.ConcatVideos => L("One continuous video"),
        QuickTool.Snapshots => L("12 pictures from the video"),
        QuickTool.WaveformVideo => L("MP4 with a waveform"),
        _ => L("Copies the text"),
    };

    /// <summary>What it does and which files it takes, for Settings.</summary>
    public static string Explanation(this QuickTool tool) => tool switch
    {
        QuickTool.Compress => L("Makes a lighter copy that still looks and sounds good. For images, PDFs, audio and video."),
        QuickTool.CompressToSize => L("Asks for the largest size, such as 2 MB, and makes a copy that fits. For images, audio and video."),
        QuickTool.StripMetadata => L("Removes the location, camera, dates and other hidden details before sharing. For images, PDFs, audio and video."),
        QuickTool.RemoveBackground => L("Cuts out the subject and saves it as a PNG with transparency. For images."),
        QuickTool.CombinePdf => L("Joins PDFs and pictures into one PDF, in the order they were chosen. For PDFs and images."),
        QuickTool.SplitPages => L("Saves each page of a PDF as a PDF of its own. For PDFs."),
        QuickTool.Extract => L("Unpacks an archive into a folder next to it. For ZIP, RAR, 7z and TAR."),
        QuickTool.Zip => L("Compresses files and folders into one ZIP. For anything."),
        QuickTool.Normalize => L("Brings the volume to the same comfortable level from start to end. For audio and video."),
        QuickTool.Mono => L("Mixes the sound into a single channel. For audio and video."),
        QuickTool.TrimSilence => L("Cuts the silence at the start and at the end. For audio."),
        QuickTool.Mute => L("Keeps the picture and takes the sound out. For video."),
        QuickTool.ConcatVideos => L("Joins several videos into one, one after the other. For 2 or more videos."),
        QuickTool.Snapshots => L("Saves 12 pictures spread evenly through the video. For video."),
        QuickTool.WaveformVideo => L("Turns a sound into an MP4 with a moving waveform, ready to post. For audio."),
        _ => L("Finds the QR codes in a picture and copies their text. For images and PDFs."),
    };

    /// <summary>Batch tools produce one result from all files; the rest run per file.</summary>
    public static bool IsBatch(this QuickTool tool) => tool is QuickTool.CombinePdf or QuickTool.Zip or QuickTool.ConcatVideos;

    public static bool Applies(this QuickTool tool, IReadOnlyList<string> paths)
    {
        var formats = paths.Select(Formats.Detect).OfType<Format>().ToList();
        var allKnown = formats.Count == paths.Count;
        bool All(Func<Format, bool> test) => allKnown && formats.Count > 0 && formats.All(test);
        static bool Media(Format f) => f.Group() is FormatGroup.Audio or FormatGroup.Video;
        return tool switch
        {
            QuickTool.Compress => All(f => f.Group() is FormatGroup.Image or FormatGroup.Audio or FormatGroup.Video || f == Format.Pdf),
            QuickTool.CompressToSize => All(f => f.Group() is FormatGroup.Image or FormatGroup.Audio or FormatGroup.Video && f != Format.Svg),
            QuickTool.StripMetadata => All(f => f.Group() is FormatGroup.Image or FormatGroup.Audio or FormatGroup.Video || f == Format.Pdf),
            QuickTool.RemoveBackground => All(f => f.Group() == FormatGroup.Image && f != Format.Svg) && Tools.CanRemoveBackground,
            QuickTool.CombinePdf => paths.Count >= 1 && All(f => f == Format.Pdf || f.Group() == FormatGroup.Image)
                && (paths.Count > 1 || formats.FirstOrDefault() != Format.Pdf),
            QuickTool.SplitPages => All(f => f == Format.Pdf),
            QuickTool.Extract => All(f => f.Group() == FormatGroup.Archive),
            QuickTool.Zip => paths.Count > 0,
            QuickTool.Normalize or QuickTool.Mono => All(Media),
            QuickTool.TrimSilence or QuickTool.WaveformVideo => All(f => f.Group() == FormatGroup.Audio),
            QuickTool.Mute or QuickTool.Snapshots => All(f => f.Group() == FormatGroup.Video),
            QuickTool.ConcatVideos => paths.Count >= 2 && All(f => f.Group() == FormatGroup.Video),
            _ => All(f => f.Group() == FormatGroup.Image || f == Format.Pdf),
        };
    }

    public static IEnumerable<QuickTool> Available(IReadOnlyList<string> paths) => All.Where(tool => tool.Applies(paths));

    /// <summary><paramref name="targetBytes"/> is the size chosen for "Compress To…".</summary>
    public static ToolResult Run(this QuickTool tool, IReadOnlyList<string> paths, long? targetBytes = null)
    {
        foreach (var path in paths) FileCheck.Preflight(path, allowFolders: tool == QuickTool.Zip);
        throw Pending.Error();
    }
}
