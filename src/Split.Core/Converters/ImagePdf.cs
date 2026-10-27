using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Split;

/// <summary>A PDF with one picture per page, each page as big as its picture is at its own resolution.</summary>
internal static class ImagePdf
{
    /// <summary>The longest side a PDF page may have (200 inches, the limit of most readers).</summary>
    private const double MaxPoints = 14_400;

    /// <param name="pages">Each picture with its pixels already upright. When <c>Original</c> is set, those bytes (a JPEG) go in as they are.</param>
    public static void Write(IReadOnlyList<(IMagickImage<ushort> Image, byte[]? Original)> pages, string output)
    {
        var temp = Output.TempSibling(output);
        var streams = new List<MemoryStream>();
        try
        {
            using var document = new PdfDocument();
            foreach (var (image, original) in pages)
            {
                JobProgress.CheckCancelled();
                var (width, height) = PointSize(image);
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(width);
                page.Height = XUnit.FromPoint(height);
                var bytes = original ?? Png(image);
                var stream = new MemoryStream(bytes);
                streams.Add(stream);
                using var graphics = XGraphics.FromPdfPage(page);
                using var picture = XImage.FromStream(stream);
                graphics.DrawImage(picture, 0, 0, width, height);
            }
            using (var file = File.Create(temp)) document.Save(file);
        }
        catch (Exception e) when (e is not Problem and not IOException and not UnauthorizedAccessException)
        {
            Output.Discard(temp);
            throw new Problem(L("Split could not put the %@ images together into one %@.", pages.Count.ToString(), "PDF"), technical: e.Message);
        }
        catch
        {
            Output.Discard(temp);
            throw;
        }
        finally
        {
            foreach (var stream in streams) stream.Dispose();
        }
        Output.Commit(temp, output);
    }

    /// <summary>
    /// The picture as a plain RGB or RGBA PNG with 8 bits per channel: the writer would otherwise pick a palette
    /// for pictures with few colours, which PDF import does not read. An alpha channel nobody can see is dropped.
    /// </summary>
    private static byte[] Png(IMagickImage<ushort> image)
    {
        using var copy = image.Clone();
        var transparent = ImageConverter.HasTransparency(copy);
        if (!transparent) copy.Alpha(AlphaAction.Remove);
        copy.Settings.SetDefine(MagickFormat.Png, "color-type", transparent ? "6" : "2");
        copy.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8");
        return copy.ToByteArray(MagickFormat.Png);
    }

    /// <summary>Pixels at the picture's own density (72 dpi when it has none), kept inside what a PDF page can be.</summary>
    private static (double Width, double Height) PointSize(IMagickImage<ushort> image)
    {
        var density = image.Density;
        var dpi = density.Units switch
        {
            DensityUnit.PixelsPerInch => density.X,
            DensityUnit.PixelsPerCentimeter => density.X * 2.54,
            _ => 72,
        };
        if (dpi < 36 || dpi > 1200) dpi = 72;
        double width = image.Width * 72 / dpi, height = image.Height * 72 / dpi;
        var shrink = Math.Min(1, MaxPoints / Math.Max(width, height));
        return (width * shrink, height * shrink);
    }
}
