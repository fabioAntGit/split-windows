using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Split;

/// <summary>A cell position: row and column from 0, as in <see cref="Sheet.Rows"/>.</summary>
public readonly record struct SheetCell(int Row, int Column);

public sealed class Sheet(string name, List<List<string>> rows)
{
    public string Name { get; } = name;
    public List<List<string>> Rows { get; } = rows;

    /// <summary>European CSVs (";" separated) write decimals as "1234,56".</summary>
    public bool DecimalComma { get; init; }

    /// <summary>Cells written as text even when they look like numbers or dates (e.g. "007" from JSON).</summary>
    public HashSet<SheetCell> LiteralText { get; init; } = [];

    /// <summary>Formulas read from a workbook; <see cref="Rows"/> holds their last computed values. An empty formula marks a cell that shares another cell's formula.</summary>
    public Dictionary<SheetCell, string> Formulas { get; init; } = [];
}

/// <summary>Writes a package (XLSX, EPUB…) in the order given, into a hidden temp that becomes the result when complete.</summary>
internal static class Zip
{
    /// <param name="stored">Entries kept uncompressed (EPUB wants its mimetype first and stored).</param>
    public static void Write(string output, IEnumerable<(string Path, byte[] Data)> entries, ISet<string>? stored = null)
    {
        var temp = Output.TempSibling(output);
        try
        {
            using (var file = File.Create(temp))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var (path, data) in entries)
                {
                    var entry = archive.CreateEntry(path, stored?.Contains(path) == true ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(data);
                }
            }
        }
        catch
        {
            Output.Discard(temp);
            throw;
        }
        Output.Commit(temp, output);
    }

    public static void Write(string output, IEnumerable<(string Path, string Text)> entries) =>
        Write(output, entries.Select(e => (e.Path, new UTF8Encoding(false).GetBytes(e.Text))));
}

// MARK: - CSV

public static class Csv
{
    /// <summary>European Excel exports use ";": pick whichever separator dominates the first line.</summary>
    public static char GuessDelimiter(string text)
    {
        var header = text.Split('\n')[0];
        int semicolons = header.Count(c => c == ';'), commas = header.Count(c => c == ','), tabs = header.Count(c => c == '\t');
        if (tabs > commas && tabs > semicolons) return '\t';
        return semicolons > commas ? ';' : ',';
    }

    public static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else inQuotes = false;
            }
            else if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                rows.Add(row);
                row = [];
                field.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    public static string Format(IEnumerable<IEnumerable<string>> rows, char delimiter) =>
        string.Join("\n", rows.Select(row => string.Join(delimiter, row.Select(value =>
            value.Contains(delimiter) || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
                ? "\"" + value.Replace("\"", "\"\"") + "\"" : value)))) + "\n";
}

// MARK: - XLSX reader and writer (values, dates, percentages, formulas, header style, column widths)

public static class Xlsx
{
    private static byte[]? Entry(ZipArchive archive, string path)
    {
        if (archive.GetEntry(path) is not { } entry) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.Length == 0 ? null : memory.ToArray();
    }

    /// <summary>One part of a workbook as text, for the checks that read what the writer produced.</summary>
    public static string EntryText(string workbook, string path)
    {
        using var archive = ZipFile.OpenRead(workbook);
        return Entry(archive, path) is { } bytes ? Encoding.UTF8.GetString(bytes) : "";
    }

    public static List<Sheet> Read(string path)
    {
        var invalid = new Problem(L("“%@” isn’t a valid XLSX.", Path.GetFileName(path)),
                                  FileCheck.Mismatch(path) ?? L("It lacks the internal structure of an Excel workbook. It may be damaged or password-protected."));
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (Entry(archive, "xl/workbook.xml") is not { } workbookData) throw invalid;
            var shared = Entry(archive, "xl/sharedStrings.xml") is { } sst ? SharedStrings(sst) : [];
            var relationships = Entry(archive, "xl/_rels/workbook.xml.rels") is { } rels ? Relationships(rels) : [];
            var styles = Entry(archive, "xl/styles.xml") is { } stylesData ? Styles(stylesData) : [];
            var (names, date1904) = Workbook(workbookData);
            var sheets = new List<Sheet>();
            for (var index = 0; index < names.Count; index++)
            {
                var target = relationships.GetValueOrDefault(names[index].Relationship) ?? $"worksheets/sheet{index + 1}.xml";
                target = target.StartsWith('/') ? target[1..] : "xl/" + target;
                if (Entry(archive, target) is not { } data) continue;
                var (rows, formulas) = Worksheet(data, shared, styles, date1904);
                sheets.Add(new Sheet(names[index].Name, rows) { Formulas = formulas });
            }
            return sheets;
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
        {
            // A DOCTYPE (entities to fetch or to expand without limit) never belongs in a workbook.
            throw new Problem(invalid.Message, invalid.Suggestion, e.Message);
        }
    }

    // MARK: Number formats

    public enum CellKind { General, Date, DateTime, Time, Percent }

    /// <summary>Built-in format ids from ECMA-376 §18.8.30 plus custom codes.</summary>
    public static CellKind Kind(int numFmtId, string? code)
    {
        switch (numFmtId)
        {
            case 14 or 15 or 16 or 17: return CellKind.Date;
            case 18 or 19 or 20 or 21 or 45 or 46 or 47: return CellKind.Time;
            case 22: return CellKind.DateTime;
            case 9 or 10: return CellKind.Percent;
        }
        if (code is null) return CellKind.General;
        // Ignore quoted text, escapes and [colour]/[locale] blocks before looking for date tokens.
        var cleaned = Regex.Replace(Regex.Replace(Regex.Replace(code, "\"[^\"]*\"", ""), @"\\.", ""), @"\[[^\]]*\]", "").ToLowerInvariant();
        if (cleaned.Contains('%')) return CellKind.Percent;
        var hasDate = cleaned.Contains('y') || cleaned.Contains('d') || (cleaned.Contains('m') && !cleaned.Contains('h'));
        var hasTime = cleaned.Contains('h') || cleaned.Contains('s');
        if (hasDate && hasTime) return CellKind.DateTime;
        if (hasDate) return CellKind.Date;
        return hasTime ? CellKind.Time : CellKind.General;
    }

    private static readonly DateTime Epoch1899 = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DateTime DateFromSerial(double serial, bool date1904) =>
        (date1904 ? Epoch1904 : Epoch1899).AddSeconds(Math.Round(serial * 86_400, MidpointRounding.AwayFromZero));

    public static double SerialFrom(DateTime date) => (DateTime.SpecifyKind(date, DateTimeKind.Utc) - Epoch1899).TotalSeconds / 86_400;

    /// <summary>A number as text: the shortest form that reads back as the same value, always with a decimal point.</summary>
    private static string Number(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    /// <summary>Cell text as it should appear in CSV/HTML: ISO dates, percentages with "%".</summary>
    public static string Display(string value, CellKind kind, bool date1904)
    {
        if (kind == CellKind.General || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return value;
        var iso = CultureInfo.InvariantCulture;
        switch (kind)
        {
            case CellKind.Date: return DateFromSerial(number, date1904).ToString("yyyy-MM-dd", iso);
            case CellKind.DateTime: return DateFromSerial(number, date1904).ToString("yyyy-MM-dd HH:mm", iso);
            case CellKind.Time: return DateFromSerial(number % 1, false).ToString("HH:mm:ss", iso);
            case CellKind.Percent:
                var percent = number * 100;
                var text = percent == Math.Round(percent) ? ((long)percent).ToString(iso) : Regex.Replace(percent.ToString("0.00", iso), "0+$", "");
                return text + "%";
            default: return value;
        }
    }

    // MARK: Writing helpers

    public static string ColumnName(int index)
    {
        var n = index + 1;
        var name = "";
        while (n > 0)
        {
            name = (char)(65 + (n - 1) % 26) + name;
            n = (n - 1) / 26;
        }
        return name;
    }

    public static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference.ToUpperInvariant())
        {
            if (c is < 'A' or > 'Z') break;
            index = index * 26 + (c - 64);
        }
        return Math.Max(0, index - 1);
    }

    public static string XmlEscape(string text) =>
        new(Markdown.Escape(text).Where(c => c >= ' ' || c is '\t' or '\n' or '\r').ToArray());

    public static bool IsNumber(string value)
    {
        if (value.Length == 0 || value.Length >= 16 || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
        if (value.StartsWith('0') && value.Length > 1 && !value.StartsWith("0.", StringComparison.Ordinal)) return false;
        return value.All(c => "0123456789.-+eE".Contains(c));
    }

    /// <summary>Style ids in the stylesheet written below.</summary>
    private enum Style { Normal = 0, Header = 1, Date = 2, DateTime = 3, Percent = 4 }

    /// <summary>How a CSV cell is stored: number ('n'), formula ('f') or text ('t').</summary>
    private readonly record struct Cell(char Type, string Text, Style Style = Style.Normal);

    private static Cell CellOf(string value, bool decimalComma)
    {
        var trimmed = value.Trim(' ', '\t');
        if (trimmed.Length > 1 && trimmed.StartsWith('=')) return new Cell('f', trimmed[1..]);
        if (IsNumber(trimmed)) return new Cell('n', trimmed);
        if (decimalComma && Regex.IsMatch(trimmed, @"^-?[0-9]+,[0-9]+\z")) return new Cell('n', trimmed.Replace(',', '.'));
        if (trimmed.EndsWith('%'))
        {
            var body = trimmed[..^1].Trim().Replace(',', '.');
            if (double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && IsNumber(body))
                return new Cell('n', Number(number / 100), Style.Percent);
        }
        if (ParseDate(trimmed) is { } date) return new Cell('n', Number(SerialFrom(date)), trimmed.Contains(':') ? Style.DateTime : Style.Date);
        return new Cell('t', value);
    }

    private static readonly string[] DatePatterns =
        ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "dd/MM/yyyy", "dd-MM-yyyy", "dd/MM/yyyy HH:mm"];

    /// <summary>ISO (2024-03-31, 2024-03-31 14:05) and day-first dates (31/03/2024).</summary>
    public static DateTime? ParseDate(string text)
    {
        if (text.Length is < 8 or > 19 || !char.IsNumber(text[0])) return null;
        foreach (var pattern in DatePatterns)
        {
            if (pattern.Length != text.Length && !(pattern.Contains("'T'") && pattern.Length - 2 == text.Length)) continue;
            if (DateTime.TryParseExact(text, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date.ToString(pattern, CultureInfo.InvariantCulture) == text) return date;
        }
        return null;
    }

    private const string XmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    public static void Write(IReadOnlyList<Sheet> sheets, string output)
    {
        var usedNames = new HashSet<string>();
        var sheetEntries = new StringBuilder();
        var relationships = new StringBuilder();
        var overrides = new StringBuilder();
        var parts = new List<(string Path, string Text)>();
        for (var index = 0; index < sheets.Count; index++)
        {
            var name = new string(sheets[index].Name.Where(c => !"[]:*?/\\".Contains(c)).Take(31).ToArray());
            if (name.Length == 0 || usedNames.Contains(name)) name = $"Folha{index + 1}";
            usedNames.Add(name);
            sheetEntries.Append($"<sheet name=\"{XmlEscape(name)}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>");
            relationships.Append($"<Relationship Id=\"rId{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index + 1}.xml\"/>");
            overrides.Append($"<Override PartName=\"/xl/worksheets/sheet{index + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            parts.Add(($"xl/worksheets/sheet{index + 1}.xml", XmlHeader + WorksheetXml(sheets[index])));
        }
        var stylesId = sheets.Count + 1;
        var entries = new List<(string Path, string Text)>
        {
            ("[Content_Types].xml", XmlHeader + $"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>{overrides}</Types>"),
            ("_rels/.rels", XmlHeader + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            // fullCalcOnLoad: formulas written from CSV have no cached value, so the spreadsheet app computes them on open.
            ("xl/workbook.xml", XmlHeader + $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>{sheetEntries}</sheets><calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/></workbook>"),
            ("xl/_rels/workbook.xml.rels", XmlHeader + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{relationships}<Relationship Id=\"rId{stylesId}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>"),
            ("xl/styles.xml", XmlHeader + Stylesheet),
        };
        entries.AddRange(parts);
        Zip.Write(output, entries);
    }

    private const string Stylesheet =
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
        + "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"yyyy\\-mm\\-dd\"/><numFmt numFmtId=\"165\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm\"/></numFmts>"
        + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
        + "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
        + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>"
        + "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>"
        + "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border></borders>"
        + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
        + "<cellXfs count=\"5\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
        + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>"
        + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "<xf numFmtId=\"10\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>"
        + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

    /// <summary>A first row of text over rows with numbers or dates reads as a header.</summary>
    private static bool HasHeader(List<List<string>> rows, bool decimalComma)
    {
        if (rows.Count < 2 || !rows[0].Any(v => v.Length > 0)) return false;
        return rows[0].All(value => CellOf(value, decimalComma).Type == 't' || value.Length == 0);
    }

    private static string WorksheetXml(Sheet sheet)
    {
        var header = HasHeader(sheet.Rows, sheet.DecimalComma);
        var xml = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        if (header)
            xml.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        var columns = sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Count);
        if (columns > 0)
        {
            xml.Append("<cols>");
            for (var column = 0; column < columns; column++)
            {
                var longest = sheet.Rows.Take(500).Max(r => r.Count > column ? r[column].Length : 0);
                var width = Math.Min(60, Math.Max(8, longest * 1.1 + 2));
                xml.Append($"<col min=\"{column + 1}\" max=\"{column + 1}\" width=\"{width.ToString("0.0", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            }
            xml.Append("</cols>");
        }
        xml.Append("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            xml.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < sheet.Rows[r].Count; c++)
            {
                var value = sheet.Rows[r][c];
                if (value.Length == 0) continue;
                var reference = ColumnName(c) + (r + 1);
                if ((header && r == 0) || sheet.LiteralText.Contains(new SheetCell(r, c)))
                {
                    var style = header && r == 0 ? $" s=\"{(int)Style.Header}\"" : "";
                    xml.Append($"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(value)}</t></is></c>");
                    continue;
                }
                var cell = CellOf(value, sheet.DecimalComma);
                xml.Append(cell.Type switch
                {
                    'n' => $"<c r=\"{reference}\"{(cell.Style == Style.Normal ? "" : $" s=\"{(int)cell.Style}\"")}><v>{cell.Text}</v></c>",
                    'f' => $"<c r=\"{reference}\"><f>{XmlEscape(cell.Text)}</f></c>",
                    _ => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(cell.Text)}</t></is></c>",
                });
            }
            xml.Append("</row>");
        }
        xml.Append("</sheetData></worksheet>");
        return xml.ToString();
    }

    // MARK: XML parsers

    private static MemoryStream Stream(byte[] data) => new(data);

    private static List<string> SharedStrings(byte[] data)
    {
        var strings = new List<string>();
        var current = new StringBuilder();
        bool inText = false, inPhonetic = false;
        XmlEvents.Read(Stream(data),
            (name, _) =>
            {
                switch (name)
                {
                    case "si": current.Clear(); break;
                    case "t": inText = !inPhonetic; break;
                    case "rPh": inPhonetic = true; break;
                }
            },
            text => { if (inText) current.Append(text); },
            name =>
            {
                switch (name)
                {
                    case "si": strings.Add(current.ToString()); break;
                    case "t": inText = false; break;
                    case "rPh": inPhonetic = false; break;
                }
            });
        return strings;
    }

    private static (List<(string Name, string Relationship)> Sheets, bool Date1904) Workbook(byte[] data)
    {
        var sheets = new List<(string, string)>();
        var date1904 = false;
        XmlEvents.Read(Stream(data), (name, attributes) =>
        {
            string? Attribute(string key) => attributes.FirstOrDefault(a => a.Name == key).Value;
            if (name == "workbookPr") date1904 = Attribute("date1904") is "1" or "true";
            if (name != "sheet") return;
            var relationship = attributes.FirstOrDefault(a => a.Name == "r:id" || a.Name.EndsWith(":id", StringComparison.Ordinal)).Value ?? "";
            sheets.Add((Attribute("name") ?? $"Folha{sheets.Count + 1}", relationship));
        }, _ => { }, _ => { });
        return (sheets, date1904);
    }

    private static Dictionary<string, string> Relationships(byte[] data)
    {
        var targets = new Dictionary<string, string>();
        XmlEvents.Read(Stream(data), (name, attributes) =>
        {
            var id = attributes.FirstOrDefault(a => a.Name == "Id").Value;
            var target = attributes.FirstOrDefault(a => a.Name == "Target").Value;
            if (name == "Relationship" && id is not null && target is not null) targets[id] = target;
        }, _ => { }, _ => { });
        return targets;
    }

    /// <summary>Reads cellXfs → number format kind, so date serials can be shown as dates.</summary>
    private static List<CellKind> Styles(byte[] data)
    {
        var custom = new Dictionary<int, string>();
        var kinds = new List<CellKind>();
        var inCellXfs = false;
        XmlEvents.Read(Stream(data), (name, attributes) =>
        {
            string? Attribute(string key) => attributes.FirstOrDefault(a => a.Name == key).Value;
            switch (name)
            {
                case "numFmt":
                    if (int.TryParse(Attribute("numFmtId"), out var fmt) && Attribute("formatCode") is { } code) custom[fmt] = code;
                    break;
                case "cellXfs": inCellXfs = true; break;
                case "xf" when inCellXfs:
                    var id = int.TryParse(Attribute("numFmtId"), out var parsed) ? parsed : 0;
                    kinds.Add(Kind(id, custom.GetValueOrDefault(id)));
                    break;
            }
        }, _ => { }, name => { if (name == "cellXfs") inCellXfs = false; });
        return kinds;
    }

    private static (List<List<string>> Rows, Dictionary<SheetCell, string> Formulas) Worksheet(byte[] data, List<string> shared, List<CellKind> styles, bool date1904)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var column = 0;
        var cellType = "";
        var cellStyle = 0;
        var value = new StringBuilder();
        var collecting = false;
        string? formula = null;
        var collectingFormula = false;
        var formulas = new Dictionary<SheetCell, string>();
        XmlEvents.Read(Stream(data),
            (name, attributes) =>
            {
                string? Attribute(string key) => attributes.FirstOrDefault(a => a.Name == key).Value;
                switch (name)
                {
                    case "row":
                        var target = (int.TryParse(Attribute("r"), out var r) ? r : rows.Count + 1) - 1;
                        while (rows.Count < target) rows.Add([]);
                        row = [];
                        break;
                    case "c":
                        cellType = Attribute("t") ?? "";
                        cellStyle = int.TryParse(Attribute("s"), out var s) ? s : 0;
                        column = Attribute("r") is { } reference ? ColumnIndex(reference) : row.Count;
                        value.Clear();
                        formula = null;
                        break;
                    case "f":
                        formula = "";
                        collectingFormula = true;
                        break;
                    case "v" or "t":
                        collecting = true;
                        break;
                }
            },
            text =>
            {
                if (collectingFormula) formula += text;
                else if (collecting) value.Append(text);
            },
            name =>
            {
                switch (name)
                {
                    case "v" or "t": collecting = false; break;
                    case "f": collectingFormula = false; break;
                    case "c":
                        if (formula is not null) formulas[new SheetCell(rows.Count, column)] = formula;
                        var raw = value.ToString();
                        var text = raw;
                        switch (cellType)
                        {
                            case "s": text = int.TryParse(raw, out var at) && at >= 0 && at < shared.Count ? shared[at] : ""; break;
                            case "b": text = raw == "1" ? "TRUE" : "FALSE"; break;
                            case "" or "n": text = Display(raw, cellStyle < styles.Count ? styles[cellStyle] : CellKind.General, date1904); break;
                        }
                        while (row.Count < column) row.Add("");
                        if (row.Count == column) row.Add(text); else row[column] = text;
                        break;
                    case "row": rows.Add(row); break;
                }
            });
        while (rows.Count > 0 && rows[^1].All(v => v.Length == 0)) rows.RemoveAt(rows.Count - 1);
        return (rows, formulas);
    }
}
