namespace Split;

/// <summary>JSON → CSV, TSV, XLSX, HTML, XML and YAML; XML and tables (CSV, TSV, XLSX and what Excel reads) → JSON.</summary>
public sealed class JsonConverter : IConversionFamily
{
    public static readonly Format[] DataTargets = [Format.Csv, Format.Tsv, Format.Xlsx, Format.Html, Format.Xml, Format.Yaml];

    private static readonly SpreadsheetConverter Spreadsheets = new();

    public IReadOnlyList<Format> Targets(Format source) =>
        source == Format.Json ? DataTargets
        : source == Format.Xml ? [Format.Json]
        : source.Group() == FormatGroup.Spreadsheet && Spreadsheets.Targets(source).Count > 0 ? [Format.Json]
        : [];

    public IReadOnlyList<string> Convert(string path, Format source, Format target) => throw Pending.Error();
}
