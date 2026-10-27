using ImageMagick;

namespace Split;

/// <summary>Turns the bytes of a picture found in a document into the PNG or JPEG every writer accepts.</summary>
public static class Pictures
{
    /// <summary>The widest a picture is placed: the text column of an A4 page with the standard margins.</summary>
    public const double ColumnWidth = 453;

    private static MagickFormat? Kind(byte[] data)
    {
        bool Has(params byte[] signature) => data.Length >= signature.Length && data.AsSpan(0, signature.Length).SequenceEqual(signature);
        if (Has(0xFF, 0xD8, 0xFF)) return MagickFormat.Jpeg;
        if (Has(0x89, 0x50, 0x4E, 0x47)) return MagickFormat.Png;
        if (Has((byte)'G', (byte)'I', (byte)'F', (byte)'8')) return MagickFormat.Gif;
        if (data.Length > 12 && Has((byte)'R', (byte)'I', (byte)'F', (byte)'F') && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return MagickFormat.WebP;
        if (Has((byte)'B', (byte)'M')) return MagickFormat.Bmp;
        if (Has(0x49, 0x49, 0x2A, 0x00) || Has(0x4D, 0x4D, 0x00, 0x2A)) return MagickFormat.Tiff;
        return null;
    }

    /// <summary>
    /// The picture, or null when the bytes aren't a picture format Split reads. The format comes from the first bytes, never
    /// from a name. A size of 0 takes the picture's own (CSS pixels when it has no resolution).
    /// </summary>
    public static DocPicture? FromBytes(byte[] data, double width = 0, double height = 0)
    {
        if (Kind(data) is not { } kind) return null;
        Imaging.Initialize();
        try
        {
            var settings = new MagickReadSettings { Format = kind };
            using var image = new MagickImage(data, settings);
            if (image.Width == 0 || image.Height == 0) return null;
            var dpi = image.Density.Units == DensityUnit.PixelsPerInch && image.Density.X >= 36 && image.Density.X <= 1200 ? image.Density.X : 96;
            double naturalWidth = image.Width * 72 / dpi, naturalHeight = image.Height * 72 / dpi;
            if (width <= 0 && height <= 0) (width, height) = (naturalWidth, naturalHeight);
            else if (width <= 0) width = height * image.Width / image.Height;
            else if (height <= 0) height = width * image.Height / image.Width;
            if (kind is MagickFormat.Png or MagickFormat.Jpeg)
                return new DocPicture(data, kind == MagickFormat.Png ? "png" : "jpeg", width, height);
            return new DocPicture(image.ToByteArray(MagickFormat.Png), "png", width, height);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>The picture a <c>data:</c> address holds.</summary>
    public static byte[]? FromDataUri(string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0 || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            return uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(uri[(comma + 1)..].Trim())
                : System.Text.Encoding.Latin1.GetBytes(Uri.UnescapeDataString(uri[(comma + 1)..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A picture a page refers to: inside it (<c>data:</c>) or a file on this PC. Addresses on other machines
    /// (a UNC path, a <c>file://server/…</c> address) are not opened, because reading them would use the network.
    /// </summary>
    public static byte[]? Load(string src, string? baseFolder)
    {
        src = src.Trim();
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return FromDataUri(src);
        if (LocalOnly.IsRemote(src)) return null;
        string path;
        if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(src, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc) return null;
            path = uri.LocalPath;
        }
        else
        {
            if (baseFolder is null) return null;
            path = Path.GetFullPath(Uri.UnescapeDataString(src.Split('?', '#')[0]).Replace('/', '\\'), baseFolder);
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        try { return File.Exists(path) && new FileInfo(path).Length < 64 << 20 ? File.ReadAllBytes(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>Never wider than the text column.</summary>
    public static (double Width, double Height) Fit(DocPicture picture, double maxWidth = ColumnWidth)
    {
        var scale = picture.Width > maxWidth ? maxWidth / picture.Width : 1;
        return (picture.Width * scale, picture.Height * scale);
    }
}
