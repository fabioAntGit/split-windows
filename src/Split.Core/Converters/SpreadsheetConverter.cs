using System.Text;

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

    public IReadOnlyList<string> Convert(string path, Format source, Format target)
    {
        // Office keeps the workbook's own look in a PDF: older formats always go through it (or LibreOffice).
        if (target == Format.Pdf && (source is Format.Xls or Format.Ods || (source == Format.Xlsx && OfficeBridge.CanUseExcel)))
            return [OfficeBridge.SpreadsheetToPdf(path, source)];
        if (target == Format.Xlsx && source is Format.Xls or Format.Ods)
        {
            // The program that reads the workbook writes the XLSX itself: formulas, formatting,
            // merged cells and charts stay, instead of going through a table of values.
            var converted = Output.Url(path, "xlsx");
            var directory = Output.ScratchDirectory();
            try { File.Move(OfficeBridge.SpreadsheetToXlsx(path, source, directory), converted); }
            finally { Output.Discard(directory); }
            return [converted];
        }
        var sheets = Read(path, source);
        if (!sheets.Any(s => s.Rows.Count > 0)) throw new Problem(L("“%@” opened, but every sheet is empty.", Path.GetFileName(path)));
        ReportLosses(sheets, target);
        switch (target)
        {
            case Format.Csv or Format.Tsv:
                var delimiter = target == Format.Csv ? ',' : '\t';
                var filled = sheets.Where(s => s.Rows.Count > 0).ToList();
                var outputs = new List<string>();
                foreach (var sheet in filled)
                {
                    var suffix = filled.Count > 1 ? " - " + sheet.Name : "";
                    var output = Output.Url(path, target.Ext(), suffix);
                    Output.WriteText(output, Csv.Format(sheet.Rows, delimiter));
                    outputs.Add(output);
                }
                return outputs;
            case Format.Xlsx:
                var xlsx = Output.Url(path, "xlsx");
                Xlsx.Write(sheets, xlsx);
                return [xlsx];
            case Format.Html:
                var html = Output.Url(path, "html");
                Output.WriteText(html, Html(sheets, Output.BaseName(path)));
                return [html];
            case Format.Pdf:
                var pdf = Output.Url(path, "pdf");
                // Tables with many columns are printed on their side.
                PdfWriter.Write(Document(sheets), pdf, landscape: sheets.Any(s => s.Rows.Any(r => r.Count > 6)));
                return [pdf];
            default:
                throw new Problem(L("Spreadsheets only convert to XLSX, CSV, TSV, HTML or PDF."));
        }
    }

    /// <summary>Tables of values can't hold formulas or a workbook's look; say so when the source had them.</summary>
    private static void ReportLosses(List<Sheet> sheets, Format target)
    {
        var formulas = sheets.Sum(s => s.Formulas.Count);
        if (formulas > 0 && target != Format.Xlsx)
            Fidelity.Note(L("%@ keeps the results of the %@ formulas, not the formulas themselves. XLSX keeps them.", target.Label(), formulas.ToString()));
        if (target is Format.Csv or Format.Tsv && sheets.Count(s => s.Rows.Count > 0) > 1)
            Fidelity.Note(L("%@ holds one sheet per file, so each sheet became its own file.", target.Label()));
    }

    public static List<Sheet> Read(string path, Format source)
    {
        switch (source)
        {
            case Format.Csv:
                var text = DocumentPipeline.ReadText(path);
                var delimiter = Csv.GuessDelimiter(text);
                return [new Sheet(Output.BaseName(path), Csv.Parse(text, delimiter)) { DecimalComma = delimiter == ';' }];
            case Format.Tsv:
                return [new Sheet(Output.BaseName(path), Csv.Parse(DocumentPipeline.ReadText(path), '\t'))];
            case Format.Xlsx:
                return Xlsx.Read(path);
            default:
                var directory = Output.ScratchDirectory();
                try { return Xlsx.Read(OfficeBridge.SpreadsheetToXlsx(path, source, directory)); }
                finally { Output.Discard(directory); }
        }
    }

    /// <summary>The sheets as printable tables: 10 pt text, the first row in bold, each sheet under its name when there are several.</summary>
    private static RichDocument Document(IReadOnlyList<Sheet> sheets)
    {
        var document = new RichDocument();
        foreach (var sheet in sheets.Where(s => s.Rows.Count > 0))
        {
            if (sheets.Count > 1)
                document.Blocks.Add(new DocParagraph { HeadingLevel = 2, SpacingAfter = 6 }.Add(new DocRun { Text = sheet.Name, Bold = true, Size = 18 }));
            var width = sheet.Rows.Max(r => r.Count);
            var table = new DocTable();
            for (var index = 0; index < sheet.Rows.Count; index++)
            {
                var cells = new List<DocCell>();
                for (var column = 0; column < width; column++)
                {
                    var cell = new DocCell();
                    cell.Blocks.Add(new DocParagraph().Add(new DocRun { Text = column < sheet.Rows[index].Count ? sheet.Rows[index][column] : "", Bold = index == 0, Size = 10 }));
                    cells.Add(cell);
                }
                table.Rows.Add(cells);
            }
            document.Blocks.Add(table);
        }
        return document;
    }

    public static string Html(IReadOnlyList<Sheet> sheets, string title)
    {
        var body = new StringBuilder();
        foreach (var sheet in sheets.Where(s => s.Rows.Count > 0))
        {
            if (sheets.Count > 1) body.Append($"<h2>{Markdown.Escape(sheet.Name)}</h2>");
            var width = sheet.Rows.Max(r => r.Count);
            body.Append("<table>");
            for (var index = 0; index < sheet.Rows.Count; index++)
            {
                var tag = index == 0 ? "th" : "td";
                var padded = sheet.Rows[index].Concat(Enumerable.Repeat("", width - sheet.Rows[index].Count));
                body.Append("<tr>" + string.Concat(padded.Select(cell => $"<{tag}>{Markdown.Escape(cell)}</{tag}>")) + "</tr>");
            }
            body.Append("</table>");
        }
        return $$"""
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>{{Markdown.Escape(title)}}</title>
            <style>body{font:10pt Helvetica,Arial,sans-serif}table{border-collapse:collapse;margin-bottom:1.5em}
            td,th{border:1px solid #999;padding:3px 6px;vertical-align:top}th{background:#eee;text-align:left}</style>
            </head><body>{{body}}</body></html>
            """;
    }
}
