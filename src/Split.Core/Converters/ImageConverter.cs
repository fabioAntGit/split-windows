namespace Split;

/// <summary>Pictures. WebP and JPEG XL are written by the app itself; HEIC can be read but not written.</summary>
public sealed class ImageConverter : IConversionFamily
{
    public static readonly Format[] StillTargets =
        [Format.Jpg, Format.Png, Format.Webp, Format.Jxl, Format.Avif, Format.Tiff, Format.Bmp, Format.Gif, Format.Ico, Format.Pdf];

    public IReadOnlyList<Format> Targets(Format source) =>
        source.Group() != FormatGroup.Image ? [] : source == Format.Gif ? [.. StillTargets, Format.Mp4, Format.Webm] : StillTargets;

    public IReadOnlyList<string> Convert(string path, Format source, Format target) => throw Pending.Error();
}
