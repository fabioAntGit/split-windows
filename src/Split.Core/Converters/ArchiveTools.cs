namespace Split;

public sealed class ArchiveTools : IConversionFamily
{
    public IReadOnlyList<Format> Targets(Format source) => source.Group() == FormatGroup.Archive ? [Format.Zip, Format.Tar, Format.Tgz] : [];

    public IReadOnlyList<string> Convert(string path, Format source, Format target) => throw Pending.Error();
}
