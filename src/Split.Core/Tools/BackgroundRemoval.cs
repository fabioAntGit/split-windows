using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Split;

/// <summary>
/// Cutting out the subject of a picture with a segmentation model (IS-Net, 1024×1024) that runs on the processor
/// through the ONNX runtime. Nothing leaves the PC: the model is a file that ships with the app.
/// </summary>
internal static class BackgroundRemoval
{
    private const int Side = 1024;

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "models", "isnet-general-use.onnx");

    public static string Remove(string path)
    {
        if (!File.Exists(ModelPath)) throw new Problem(L("Removing the background needs the model that ships with Split."), L("Reinstall Split to get it back."));
        var format = Formats.Detect(path) ?? throw Explain.NoTargets([path], tools: true);
        using var frames = ImageConverter.Open(path, format);
        using var picture = frames.Images[0].Clone();
        // The mask is drawn on the picture as it is seen.
        picture.AutoOrient();
        JobProgress.CheckCancelled();

        var mask = Mask(picture);
        if (mask is null)
        {
            throw new Problem(L("No main subject was found in “%@”.", Path.GetFileName(path)),
                              L("It works best with people, animals and objects that stand out from the background."));
        }
        using var cutout = Cut(picture, mask);
        var output = Output.Url(path, "png", " (" + L("no background") + ")");
        try { ImageConverter.WriteSingle(cutout, null, output, Format.Png, Format.Png, faithful: false); }
        catch
        {
            Output.Discard(output);
            throw;
        }
        return output;
    }

    /// <summary>The subject as 0-255 per pixel at the picture's size, or null when the model finds none.</summary>
    private static byte[]? Mask(IMagickImage<ushort> picture)
    {
        using var small = picture.Clone();
        small.Alpha(AlphaAction.Off);
        small.Resize(new MagickGeometry(Side, Side) { IgnoreAspectRatio = true });
        var rgb = small.GetPixels().ToByteArray(PixelMapping.RGB) ?? throw new InvalidDataException();

        // As the model was trained: scaled by the brightest value, minus the mean of each channel.
        float[] mean = [0.485f, 0.456f, 0.406f];
        var peak = Math.Max((int)rgb.Max(), 1);
        var input = new DenseTensor<float>([1, 3, Side, Side]);
        for (var i = 0; i < Side * Side; i++)
        {
            for (var c = 0; c < 3; c++) input[0, c, i / Side, i % Side] = (float)rgb[i * 3 + c] / peak - mean[c];
        }

        float[] raw;
        using (var session = new InferenceSession(ModelPath))
        {
            var name = session.InputMetadata.Keys.First();
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
            raw = results[0].AsTensor<float>().ToArray();
        }
        float low = raw.Min(), high = raw.Max();
        if (high < 0.5f || high - low < 1e-6f) return null;

        var scaled = new byte[Side * Side];
        var inside = 0;
        for (var i = 0; i < scaled.Length; i++)
        {
            var value = (raw[i] - low) / (high - low);
            scaled[i] = (byte)Math.Round(value * 255);
            if (value >= 0.5f) inside++;
        }
        // A handful of pixels is noise, not a subject.
        if (inside < scaled.Length * 0.002) return null;

        using var small8 = new MagickImage(scaled, new PixelReadSettings(Side, Side, StorageType.Char, "I"));
        small8.Resize(new MagickGeometry(picture.Width, picture.Height) { IgnoreAspectRatio = true });
        return small8.GetPixels().ToByteArray(PixelMapping.RGB)!.Where((_, index) => index % 3 == 0).ToArray();
    }

    /// <summary>The picture with the mask as its transparency.</summary>
    private static MagickImage Cut(IMagickImage<ushort> picture, byte[] mask)
    {
        using var flat = picture.Clone();
        flat.Alpha(AlphaAction.Off);
        var rgb = flat.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var rgba = new byte[mask.Length * 4];
        for (var i = 0; i < mask.Length; i++)
        {
            rgba[i * 4] = rgb[i * 3];
            rgba[i * 4 + 1] = rgb[i * 3 + 1];
            rgba[i * 4 + 2] = rgb[i * 3 + 2];
            rgba[i * 4 + 3] = mask[i];
        }
        var cut = new MagickImage(rgba, new PixelReadSettings(picture.Width, picture.Height, StorageType.Char, PixelMapping.RGBA));
        if (picture.GetColorProfile() is { } profile) cut.SetProfile(profile);
        return cut;
    }
}
