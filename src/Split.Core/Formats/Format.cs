namespace Split;

public enum FormatGroup { Image, Document, Spreadsheet, Presentation, Audio, Video, Subtitle, Archive, Data }

/// <summary>
/// Every format the app reads or writes. Some are inputs only (doc, odt, md, m4v…), because
/// they can be read but there is no good reason to produce them.
/// </summary>
public enum Format
{
    Jpg, Png, Webp, Jxl, Heic, Avif, Tiff, Bmp, Gif, Ico, Svg,
    Pdf, Docx, Doc, Odt, Rtf, Rtfd, Txt, Html, Md, Epub, Pages,
    Xlsx, Xls, Ods, Numbers, Csv, Tsv,
    Pptx, Ppt, Odp, Key,
    Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma,
    Mp4, Mov, M4v, Mkv, Webm, Avi, Wmv,
    Srt, Vtt,
    Zip, Tar, Tgz, Rar, SevenZip,
    Json, Xml, Yaml,
}

public static class Formats
{
    public static readonly IReadOnlyList<Format> All = Enum.GetValues<Format>();

    private static readonly Dictionary<string, Format> ByRaw = All.ToDictionary(f => f.Raw());

    private static readonly Dictionary<string, Format> Aliases = new()
    {
        ["jpeg"] = Format.Jpg, ["jpe"] = Format.Jpg, ["tif"] = Format.Tiff, ["heif"] = Format.Heic, ["htm"] = Format.Html, ["xhtml"] = Format.Html,
        ["markdown"] = Format.Md, ["text"] = Format.Txt, ["aif"] = Format.Aiff, ["oga"] = Format.Ogg, ["qt"] = Format.Mov, ["tgz"] = Format.Tgz,
        ["mpeg"] = Format.Mp4, ["mpg"] = Format.Mp4, ["3gp"] = Format.Mp4, ["aac"] = Format.M4a, ["caf"] = Format.M4a, ["yml"] = Format.Yaml,
        ["geojson"] = Format.Json,
        // Saved by browsers and social sites: JFIF is plain JPEG, the "_large"/"_orig" names
        // come from downloading full-size pictures.
        ["jfif"] = Format.Jpg, ["pjpeg"] = Format.Jpg, ["jpg_large"] = Format.Jpg, ["jpeg_large"] = Format.Jpg, ["jpg_orig"] = Format.Jpg,
        ["png_large"] = Format.Png,
    };

    /// <summary>The name used in identifiers and on the command line: "jpg", "7z".</summary>
    public static string Raw(this Format format) => format == Format.SevenZip ? "7z" : format.ToString().ToLowerInvariant();

    public static string Ext(this Format format) => format == Format.Tgz ? "tar.gz" : format.Raw();

    public static string Label(this Format format) => format switch
    {
        Format.Tgz => "TAR.GZ",
        Format.Md => "Markdown",
        Format.Pages => "Pages",
        Format.Numbers => "Numbers",
        Format.Key => "Keynote",
        Format.Jxl => "JPEG XL",
        Format.Webp => "WebP",
        _ => format.Raw().ToUpperInvariant(),
    };

    /// <summary>What the format is good for, in a few words.</summary>
    public static string Summary(this Format format) => format switch
    {
        Format.Jpg => L("Photos. Small files, without transparency."),
        Format.Png => L("Screenshots and graphics. Lossless, with transparency."),
        Format.Webp => L("Pictures for the web, smaller than JPG and PNG."),
        Format.Jxl => L("The newest picture format: very small, lossless when needed."),
        Format.Heic => L("iPhone photos, about half the size of JPG."),
        Format.Avif => L("Very small pictures for the web."),
        Format.Tiff => L("Printing and archiving. Lossless, large files."),
        Format.Bmp => L("Uncompressed pictures for older programs."),
        Format.Gif => L("Short animations with 256 colours."),
        Format.Ico => L("Icons for Windows and websites."),
        Format.Svg => L("Drawings that stay sharp at any size."),
        Format.Pdf => L("Looks the same everywhere. Ready to share and print."),
        Format.Docx => L("Word documents, ready to edit."),
        Format.Doc => L("Documents from older versions of Word."),
        Format.Odt => L("LibreOffice documents."),
        Format.Rtf => L("Formatted text almost any editor opens."),
        Format.Rtfd => L("TextEdit documents with pictures."),
        Format.Txt => L("Plain text, without formatting."),
        Format.Html => L("A web page that opens in any browser."),
        Format.Md => L("Plain text with simple formatting marks."),
        Format.Epub => L("E-books for Books and e-readers."),
        Format.Pages => L("Pages documents."),
        Format.Xlsx => L("Excel workbooks, with formulas."),
        Format.Xls => L("Workbooks from older versions of Excel."),
        Format.Ods => L("LibreOffice spreadsheets."),
        Format.Numbers => L("Numbers spreadsheets."),
        Format.Csv => L("A table as plain text, for any app or database."),
        Format.Tsv => L("A table as plain text, with tabs between columns."),
        Format.Pptx => L("PowerPoint presentations."),
        Format.Ppt => L("Presentations from older versions of PowerPoint."),
        Format.Odp => L("LibreOffice presentations."),
        Format.Key => L("Keynote presentations."),
        Format.Mp3 => L("Music and sound that play everywhere."),
        Format.M4a => L("Apple’s audio: small, good quality."),
        Format.Wav => L("Uncompressed sound, for editing."),
        Format.Flac => L("Lossless music, about half the size of WAV."),
        Format.Ogg => L("Open audio format, common in games and Linux."),
        Format.Opus => L("Very small voice and music files."),
        Format.Aiff => L("Uncompressed sound, the Mac’s own WAV."),
        Format.Wma => L("Windows Media audio."),
        Format.Mp4 => L("Video that plays everywhere."),
        Format.Mov => L("QuickTime video, for Apple apps and editing."),
        Format.M4v => L("Video from the iTunes Store and older Apple apps."),
        Format.Mkv => L("Video with several audio tracks and subtitles."),
        Format.Webm => L("Video for web pages."),
        Format.Avi => L("Video for older Windows programs."),
        Format.Wmv => L("Windows Media video."),
        Format.Srt => L("Subtitles almost every player reads."),
        Format.Vtt => L("Subtitles for video on the web."),
        Format.Zip => L("A compressed folder that opens everywhere."),
        Format.Tar => L("An uncompressed archive, common on Linux."),
        Format.Tgz => L("A compressed archive, common on Linux."),
        Format.Rar => L("An archive made with WinRAR."),
        Format.SevenZip => L("An archive made with 7-Zip."),
        Format.Json => L("Structured data for apps and the web."),
        Format.Xml => L("Structured data in tags."),
        _ => L("Easy-to-read data, common in settings files."),
    };

    public static FormatGroup Group(this Format format) => format switch
    {
        <= Format.Svg => FormatGroup.Image,
        <= Format.Pages => FormatGroup.Document,
        <= Format.Tsv => FormatGroup.Spreadsheet,
        <= Format.Key => FormatGroup.Presentation,
        <= Format.Wma => FormatGroup.Audio,
        <= Format.Wmv => FormatGroup.Video,
        <= Format.Vtt => FormatGroup.Subtitle,
        <= Format.SevenZip => FormatGroup.Archive,
        _ => FormatGroup.Data,
    };

    public static Format? Detect(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal)) return Format.Tgz;
        return Lookup(Paths.Extension(path).ToLowerInvariant());
    }

    public static Format? Parse(string text)
    {
        var key = text.ToLowerInvariant().Trim('.', ' ');
        return key == "tar.gz" ? Format.Tgz : Lookup(key);
    }

    private static Format? Lookup(string key) =>
        ByRaw.TryGetValue(key, out var format) ? format : Aliases.TryGetValue(key, out var alias) ? alias : null;
}
