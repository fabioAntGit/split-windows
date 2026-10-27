using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace Split;

/// <summary>Making a PDF lighter (its pictures and streams) and clearing what it says about its author.</summary>
internal static class PdfCompression
{
    /// <summary>Pictures shown bigger than this many pixels per inch are scaled down to it.</summary>
    private const double TargetDpi = 150;

    public static string Compress(string path)
    {
        PdfTools.Open(path).Dispose();
        var output = Output.Url(path, "pdf", " (" + L("compressed") + ")");
        var temp = Output.TempSibling(output);
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            var uses = ImageUses(document);
            foreach (var (image, size) in uses) Recompress(image, size);
            document.Options.CompressContentStreams = true;
            // Streams that were stored plain (page contents, fonts, uncompressed pictures) are deflated.
            foreach (var item in document.Internals.GetAllObjects())
            {
                if (item is PdfDictionary { Stream: { } stream } dictionary && !dictionary.Elements.ContainsKey("/Filter") && dictionary.Elements.GetName("/Type") != "/XRef")
                    stream.Zip();
            }
            document.Save(temp);
        }
        catch (Exception e) when (e is not Problem and not IOException and not UnauthorizedAccessException)
        {
            Output.Discard(temp);
            Output.Discard(output);
            throw new Problem(L("Split couldn’t create the compressed version of “%@”.", Path.GetFileName(path)),
                              L("Some PDFs (forms, PDF/A, signed) can’t be rewritten. Try “Remove Metadata” or convert to images."), e.Message);
        }
        catch
        {
            Output.Discard(temp);
            Output.Discard(output);
            throw;
        }
        Output.Commit(temp, output);
        return output;
    }

    public static void StripMetadata(string path, string output)
    {
        PdfTools.Open(path).Dispose();
        var temp = Output.TempSibling(output);
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            // The Info dictionary and the XMP packet are the two places a PDF keeps who made it and with what.
            document.Info.Elements.Clear();
            document.Internals.Catalog.Elements.Remove("/Metadata");
            document.Save(temp);
        }
        catch (Exception e) when (e is not Problem and not IOException and not UnauthorizedAccessException)
        {
            Output.Discard(temp);
            throw new Problem(L("Split couldn’t save the clean copy of “%@”.", Path.GetFileName(path)),
                              FileCheck.PrivacyHint(Path.GetDirectoryName(Path.GetFullPath(path))!), e.Message);
        }
        catch
        {
            Output.Discard(temp);
            throw;
        }
        Output.Commit(temp, output);
    }

    /// <summary>The pictures a page draws directly, with the biggest size in points they are shown at on any page.</summary>
    private static Dictionary<PdfDictionary, (double Width, double Height)> ImageUses(PdfDocument document)
    {
        var uses = new Dictionary<PdfDictionary, (double Width, double Height)>();
        foreach (var page in document.Pages)
        {
            var objects = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
            if (objects is null) continue;
            var matrices = new Stack<double[]>();
            var current = new double[] { 1, 0, 0, 1, 0, 0 };
            foreach (var item in ContentReader.ReadContent(page))
            {
                if (item is not COperator op) continue;
                switch (op.OpCode.OpCodeName)
                {
                    case OpCodeName.q:
                        matrices.Push(current);
                        break;
                    case OpCodeName.Q:
                        if (matrices.Count > 0) current = matrices.Pop();
                        break;
                    case OpCodeName.cm when op.Operands.Count == 6:
                        var m = op.Operands.Select(Number).ToArray();
                        current = Multiply(m, current);
                        break;
                    case OpCodeName.Do when op.Operands.Count == 1 && op.Operands[0] is CName name:
                        if (objects.Elements.GetDictionary(name.Name) is { } image && image.Elements.GetName("/Subtype") == "/Image")
                        {
                            var shown = (Math.Sqrt(current[0] * current[0] + current[1] * current[1]), Math.Sqrt(current[2] * current[2] + current[3] * current[3]));
                            uses[image] = uses.TryGetValue(image, out var before) ? (Math.Max(before.Width, shown.Item1), Math.Max(before.Height, shown.Item2)) : shown;
                        }
                        break;
                }
            }
        }
        return uses;
    }

    private static double Number(CObject value) => value switch
    {
        CReal real => real.Value,
        CInteger integer => integer.Value,
        _ => 0,
    };

    /// <summary>The matrix <paramref name="a"/> applied first, then <paramref name="b"/> (PDF row-vector convention).</summary>
    private static double[] Multiply(double[] a, double[] b) =>
    [
        a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3],
        a[2] * b[0] + a[3] * b[2], a[2] * b[1] + a[3] * b[3],
        a[4] * b[0] + a[5] * b[2] + b[4], a[4] * b[1] + a[5] * b[3] + b[5],
    ];

    /// <summary>
    /// JPEG or Flate pictures of 8-bit RGB or grey become JPEGs at compact quality, scaled down to 150 pixels per inch of the
    /// size they are shown at, when that is smaller. Masks, CMYK, indexed and anything else stays as it is.
    /// </summary>
    private static void Recompress(PdfDictionary image, (double Width, double Height) shown)
    {
        var elements = image.Elements;
        if (image.Stream is not { } stream || shown.Width < 1 || shown.Height < 1) return;
        if (elements.ContainsKey("/SMask") || elements.ContainsKey("/Mask") || elements.ContainsKey("/Decode") || elements.GetBoolean("/ImageMask")) return;
        if (elements.GetInteger("/BitsPerComponent") != 8) return;
        var gray = elements.GetName("/ColorSpace") == "/DeviceGray";
        if (!gray && elements.GetName("/ColorSpace") != "/DeviceRGB") return;
        var filter = elements["/Filter"] is PdfArray { Elements.Count: 1 } list ? (list.Elements[0] as PdfName)?.Value : (elements["/Filter"] as PdfName)?.Value;
        int width = elements.GetInteger("/Width"), height = elements.GetInteger("/Height");
        if (width < 1 || height < 1) return;

        try
        {
            using var picture = filter switch
            {
                "/DCTDecode" => new MagickImage(stream.Value, new MagickReadSettings { Format = MagickFormat.Jpeg }),
                // A predictor in the parameters means rows are filtered: those are left alone.
                "/FlateDecode" when !elements.ContainsKey("/DecodeParms") && stream.UnfilteredValue is { } raw && raw.Length == width * height * (gray ? 1 : 3)
                    => new MagickImage(raw, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, gray ? "I" : "RGB")),
                _ => (MagickImage?)null,
            } ?? throw new InvalidDataException();
            if (picture.Width != width || picture.Height != height) return;

            var dpi = Math.Max(width / shown.Width, height / shown.Height) * 72;
            if (dpi > TargetDpi)
            {
                var factor = TargetDpi / dpi;
                picture.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(width * factor)), (uint)Math.Max(1, Math.Round(height * factor))) { IgnoreAspectRatio = true });
            }
            picture.Strip();
            picture.ColorType = gray ? ColorType.Grayscale : ColorType.TrueColor;
            picture.Quality = (uint)Quality.Compact.Webp();
            var jpeg = picture.ToByteArray(MagickFormat.Jpeg);
            if (jpeg.Length >= stream.Length) return;

            stream.Value = jpeg;
            elements.SetName("/Filter", "/DCTDecode");
            elements.Remove("/DecodeParms");
            elements.SetInteger("/Width", (int)picture.Width);
            elements.SetInteger("/Height", (int)picture.Height);
        }
        catch (Exception e) when (e is MagickException or InvalidDataException) { /* a picture that can't be read is kept as it is */ }
    }
}
