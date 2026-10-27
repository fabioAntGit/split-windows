using ImageMagick;
using ImageMagick.Configuration;

namespace Split;

/// <summary>
/// The one door to the picture library. Its security policy is set before the first use: the
/// coders that fetch from the network or run other programs (URL, HTTP, HTTPS, FTP, MSL, MVG, TEXT,
/// LABEL, EPHEMERAL, PS, EPS, PDF, XPS), <c>@file</c> lists and every external delegate are off.
/// Files are always read with an explicit format taken from their content, never from their name.
/// </summary>
public static class Imaging
{
    private static readonly object Gate = new();
    private static bool started;

    private static readonly string[] DisabledCoders = ["URL", "HTTP", "HTTPS", "FTP", "MSL", "MVG", "TEXT", "LABEL", "EPHEMERAL", "PS", "EPS", "PDF", "XPS"];

    /// <summary>Safe to call from anywhere, any number of times; every Magick use goes through here first.</summary>
    public static void Initialize()
    {
        if (started) return;
        lock (Gate)
        {
            if (started) return;
            var files = ConfigurationFiles.Default;
            files.Policy.Data = "<policymap>"
                + string.Concat(DisabledCoders.Select(coder => $"<policy domain=\"coder\" rights=\"none\" pattern=\"{coder}\" />"))
                + "<policy domain=\"path\" rights=\"none\" pattern=\"@*\" />"
                + "<policy domain=\"delegate\" rights=\"none\" pattern=\"*\" />"
                + "</policymap>";
            MagickNET.Initialize(files);
            started = true;
        }
    }

    /// <summary>What the first bytes of the file say it is, as the library's own name for it; null when it isn't a picture format we read.</summary>
    public static MagickFormat? FormatOf(string path, Format declared)
    {
        var format = FileCheck.Sniff(path) switch
        {
            "JPEG" => MagickFormat.Jpeg,
            "PNG" => MagickFormat.Png,
            "GIF" => MagickFormat.Gif,
            "WebP" => MagickFormat.WebP,
            "HEIC" => MagickFormat.Heic,
            "AVIF" => MagickFormat.Avif,
            "TIFF" => MagickFormat.Tiff,
            "BMP" => MagickFormat.Bmp,
            "ICO" => MagickFormat.Ico,
            // JPEG XL has no signature in the first bytes we look at: its extension decides, and the decoder checks.
            null when declared == Format.Jxl => MagickFormat.Jxl,
            _ => (MagickFormat?)null,
        };
        return format;
    }
}
