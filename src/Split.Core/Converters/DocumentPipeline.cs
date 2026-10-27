namespace Split;

/// <summary>Documents. Pages files are recognised but have no targets.</summary>
public sealed class DocumentPipeline : IConversionFamily
{
    public static readonly Format[] TextTargets = [Format.Pdf, Format.Docx, Format.Odt, Format.Rtf, Format.Txt, Format.Html, Format.Epub];

    public IReadOnlyList<Format> Targets(Format source) => source.Group() != FormatGroup.Document ? [] : source switch
    {
        Format.Pdf => [Format.Png, Format.Jpg, Format.Txt, Format.Docx, Format.Rtf, Format.Html, Format.Epub],
        Format.Pages => [],
        _ => TextTargets,
    };

    public IReadOnlyList<string> Convert(string path, Format source, Format target) => throw Pending.Error();
}
