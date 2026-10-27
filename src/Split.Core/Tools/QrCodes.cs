using ImageMagick;
using ZXing;
using ZXing.Common;

namespace Split;

/// <summary>Finding QR codes and barcodes in a picture, or in the first pages of a PDF, with ZXing.</summary>
internal static class QrCodes
{
    /// <summary>A PDF is searched page by page up to this many pages.</summary>
    private const int MaxPages = 20;

    public static IReadOnlyList<string> Read(string path)
    {
        var codes = new List<string>();
        void Add(IMagickImage<ushort> image)
        {
            foreach (var text in Decode(image))
            {
                if (!codes.Contains(text)) codes.Add(text);
            }
        }
        var format = Formats.Detect(path);
        if (format == Format.Pdf)
        {
            using var document = PdfTools.Open(path);
            for (var index = 0; index < Math.Min(document.PageCount, MaxPages); index++)
            {
                JobProgress.CheckCancelled();
                using var page = document.Render(index, 3);
                if (page is not null) Add(page);
            }
        }
        else if (format == Format.Svg)
        {
            using var drawn = ImageConverter.RenderSvg(path);
            Add(drawn);
        }
        else
        {
            using var frames = ImageConverter.Open(path, format ?? Format.Png);
            Add(frames.Images[0]);
        }
        if (codes.Count == 0)
        {
            throw new Problem(L("No QR code or barcode was found in “%@”.", Path.GetFileName(path)),
                              L("The code has to be sharp, whole and not too small. If it is a photo, crop around the code and try again."));
        }
        return codes;
    }

    private static IEnumerable<string> Decode(IMagickImage<ushort> image)
    {
        using var flat = image.Clone();
        // Transparent areas are white, like paper.
        flat.BackgroundColor = MagickColors.White;
        flat.Alpha(AlphaAction.Remove);
        flat.AutoOrient();
        var rgb = flat.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var source = new RGBLuminanceSource(rgb, (int)flat.Width, (int)flat.Height, RGBLuminanceSource.BitmapFormat.RGB24);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true },
        };
        var found = reader.DecodeMultiple(source);
        if (found is null || found.Length == 0) found = reader.Decode(source) is { } one ? [one] : [];
        return found.Select(result => result.Text).Where(text => !string.IsNullOrEmpty(text));
    }
}
