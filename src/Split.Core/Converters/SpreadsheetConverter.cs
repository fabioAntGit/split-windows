namespace Split;

/// <summary>Tables. Old workbooks (XLS, ODS) need Excel or LibreOffice to be read; Numbers files are recognised but have no targets.</summary>
public sealed class SpreadsheetConverter : IConversionFamily
{
    public static readonly Format[] TableTargets = [Format.Xlsx, Format.Csv, Format.Tsv, Format.Html, Format.Pdf];

    public IReadOnlyList<Format> Targets(Format source) => source switch
    {
        Format.Csv or Format.Tsv or Format.Xlsx => TableTargets,
        Format.Xls or Format.Ods => Engines.Excel || Engines.Soffice is not null ? TableTargets : [],
        _ => [],
    };

    public IReadOnlyList<string> Convert(string path, Format source, Format target) => throw Pending.Error();
}
