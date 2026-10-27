namespace Split;

/// <summary>Slides only become PDF, through PowerPoint or LibreOffice. Keynote files are recognised but have no targets.</summary>
public sealed class PresentationConverter : IConversionFamily
{
    public IReadOnlyList<Format> Targets(Format source) =>
        source.Group() == FormatGroup.Presentation && source != Format.Key && (Engines.PowerPoint || Engines.Soffice is not null) ? [Format.Pdf] : [];

    public IReadOnlyList<string> Convert(string path, Format source, Format target) => throw Pending.Error();
}
