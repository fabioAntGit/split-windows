using System.Text.RegularExpressions;

namespace Split;

/// <summary>
/// Turns JSON into rows and columns without losing anything: every value gets a column
/// named by its path (<c>address.city</c>, <c>tags[0]</c>), so nested data survives in a flat table.
/// </summary>
public static class JsonTable
{
    public sealed record Table(List<string> Columns, List<JsonValue?[]> Rows);

    public static Table Make(JsonValue value)
    {
        IEnumerable<JsonValue> records = value.Kind == JsonKind.Array ? value.Items : [value];
        var columns = new List<string>();
        var known = new HashSet<string>();
        var rows = new List<Dictionary<string, JsonValue>>();
        foreach (var record in records)
        {
            var cells = new List<(string, JsonValue)>();
            Flatten(record, "", cells);
            if (cells.Count == 0) cells.Add(("value", record));
            var row = new Dictionary<string, JsonValue>();
            foreach (var (path, leaf) in cells)
            {
                var column = path.Length == 0 ? "value" : path;
                if (known.Add(column)) columns.Add(column);
                row[column] = leaf;
            }
            rows.Add(row);
        }
        return new Table(columns, rows.Select(row => columns.Select(c => row.GetValueOrDefault(c)).ToArray()).ToList());
    }

    private static void Flatten(JsonValue value, string path, List<(string, JsonValue)> cells)
    {
        if (value.Kind == JsonKind.Object && value.Members.Count > 0)
        {
            foreach (var member in value.Members) Flatten(member.Value, path.Length == 0 ? member.Key : path + "." + member.Key, cells);
        }
        else if (value.Kind == JsonKind.Array && value.Items.Count > 0)
        {
            for (var index = 0; index < value.Items.Count; index++) Flatten(value.Items[index], path + $"[{index}]", cells);
        }
        else
        {
            // Leaves, and empty objects/arrays (kept as {} / [] so they don't vanish).
            cells.Add((path, value));
        }
    }

    /// <summary>The text a cell shows. Numbers keep their literal; empty containers stay visible.</summary>
    public static string Text(JsonValue? value) => value switch
    {
        null => "",
        { Kind: JsonKind.Null } => "",
        { Kind: JsonKind.String or JsonKind.Number } => value.Text,
        { Kind: JsonKind.Bool } => value.Flag ? "true" : "false",
        { Kind: JsonKind.Object } => "{}",
        _ => "[]",
    };
}

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

    public IReadOnlyList<string> Convert(string path, Format source, Format target)
    {
        if (source == Format.Json) return FromJson(path, target);
        if (source == Format.Xml)
        {
            var value = XmlBridge.Json(XmlBridge.Parse(File.ReadAllBytes(path)));
            var output = Output.Url(path, "json");
            Output.WriteText(output, JsonWriter.String(value) + "\n");
            return [output];
        }
        return [ToJson(path, source)];
    }

    public static JsonValue Read(string path)
    {
        // JSON is UTF-8 by definition, but files saved by older Windows tools are not always.
        var data = TextDecoding.Utf8(File.ReadAllBytes(path)).Data;
        try
        {
            return JsonParser.Parse(data);
        }
        catch (JsonParser.Failure failure)
        {
            throw new Problem(L("“%@” is not valid JSON: %@ (line %@, column %@).", Path.GetFileName(path), failure.Message, failure.Line.ToString(), failure.Column.ToString()),
                              L("Open it in a text editor and fix that spot, or export it again from the app that created it."));
        }
    }

    private static IReadOnlyList<string> FromJson(string path, Format target)
    {
        var value = Read(path);
        var output = Output.Url(path, target.Ext());
        switch (target)
        {
            case Format.Yaml:
                Output.WriteText(output, YamlWriter.Document(value));
                break;
            case Format.Xml:
                Output.WriteText(output, XmlBridge.Document(value));
                break;
            case Format.Csv or Format.Tsv or Format.Xlsx or Format.Html:
                var table = JsonTable.Make(value);
                List<string> header = table.Columns;
                var body = table.Rows.Select(row => row.Select(JsonTable.Text).ToList()).ToList();
                if (table.Rows.Any(row => row.Any(cell => cell is { Kind: JsonKind.Null })))
                    Fidelity.Note(L("Empty cells stand for JSON null: tables have no separate value for it."));
                List<List<string>> rows = [header, .. body];
                switch (target)
                {
                    case Format.Csv or Format.Tsv:
                        // Values are written exactly as text, so a number like 1.50 keeps its trailing zero.
                        Output.WriteText(output, Csv.Format(rows, target == Format.Csv ? ',' : '\t'));
                        break;
                    case Format.Xlsx:
                        Xlsx.Write([new Sheet(Output.BaseName(path), rows) { LiteralText = LiteralCells(table) }], output);
                        break;
                    default:
                        Output.WriteText(output, SpreadsheetConverter.Html([new Sheet(Output.BaseName(path), rows)], Output.BaseName(path)));
                        break;
                }
                break;
            default:
                throw new Problem(L("JSON converts to CSV, TSV, XLSX, HTML, XML or YAML."));
        }
        return [output];
    }

    /// <summary>Text cells from JSON stay text in Excel even when they look like numbers or dates ("007", "2024-01-01").</summary>
    private static HashSet<SheetCell> LiteralCells(JsonTable.Table table)
    {
        var cells = new HashSet<SheetCell>();
        for (var r = 0; r < table.Rows.Count; r++)
            for (var c = 0; c < table.Rows[r].Length; c++)
                if (table.Rows[r][c] is { Kind: JsonKind.String }) cells.Add(new SheetCell(r + 1, c));
        return cells;
    }

    private static string ToJson(string path, Format source)
    {
        var sheets = SpreadsheetConverter.Read(path, source).Where(s => s.Rows.Count > 0).ToList();
        if (sheets.Count == 0) throw new Problem(L("“%@” opened, but every sheet is empty.", Path.GetFileName(path)));
        var documents = sheets.Select(Records).ToList();
        var value = documents.Count == 1 ? documents[0] : JsonValue.Object(sheets.Zip(documents, (sheet, document) => (sheet.Name, document)));
        if (sheets.Any(s => s.Formulas.Count > 0)) Fidelity.Note(L("JSON keeps the results of formulas, not the formulas themselves."));
        var output = Output.Url(path, "json");
        Output.WriteText(output, JsonWriter.String(value) + "\n");
        return output;
    }

    /// <summary>One object per row, keyed by the header row. Text from CSV stays text unless it is plainly a number or true/false, so "007" or "1,5" are never changed.</summary>
    public static JsonValue Records(Sheet sheet)
    {
        if (sheet.Rows.Count == 0) return JsonValue.Array([]);
        var header = sheet.Rows[0];
        var keys = new List<string>();
        var seen = new Dictionary<string, int>();
        for (var index = 0; index < header.Count; index++)
        {
            var key = header[index].Length == 0 ? Xlsx.ColumnName(index) : header[index];
            if (seen.TryGetValue(key, out var count))
            {
                seen[key] = count + 1;
                key += $" ({count + 1})";
            }
            else seen[key] = 1;
            keys.Add(key);
        }
        var width = Math.Max(keys.Count, sheet.Rows.Max(r => r.Count));
        while (keys.Count < width) keys.Add(Xlsx.ColumnName(keys.Count));
        return JsonValue.Array(sheet.Rows.Skip(1).Select(row =>
            JsonValue.Object(keys.Select((key, column) => (key, column < row.Count ? Scalar(row[column]) : JsonValue.String(""))))));
    }

    public static JsonValue Scalar(string text)
    {
        if (text is "TRUE" or "true") return JsonValue.Bool(true);
        if (text is "FALSE" or "false") return JsonValue.Bool(false);
        return IsCanonicalNumber(text) ? JsonValue.Number(text) : JsonValue.String(text);
    }

    /// <summary>JSON's own number grammar, without leading zeros: "12", "-3.5", "1e9". Not "007", "1,5" or "+4".</summary>
    public static bool IsCanonicalNumber(string text) =>
        text.Length <= 30 && Regex.IsMatch(text, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?\z");
}
