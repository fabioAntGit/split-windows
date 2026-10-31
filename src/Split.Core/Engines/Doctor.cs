namespace Split;

/// <summary>Which engines are installed (<c>splitapp doctor</c>, Settings › Engines).</summary>
public static class Doctor
{
    public static string Report()
    {
        static string Line(string name, bool ok, string detail) => $"{(ok ? "✓" : "✗")} {name} — {detail}";
        var ocr = Platform.Current.OcrLanguages;
        string[] lines =
        [
            Line("ffmpeg", Engines.Path("ffmpeg") is not null, L("audio, video, GIF, subtitles (winget install -e --id Gyan.FFmpeg)")),
            Line("exiftool", Engines.Path("exiftool") is not null, L("removing image metadata (winget install -e --id OliverBetz.ExifTool)")),
            Line("Word", Engines.Word, L("DOCX/DOC → PDF with the exact layout")),
            Line("Excel", Engines.Excel, L("XLS/XLSX → PDF and XLSX")),
            Line("PowerPoint", Engines.PowerPoint, "PPTX/PPT → PDF"),
            Line("LibreOffice", Engines.Soffice is not null, L("an alternative for ODS/ODP/PPT (winget install -e --id TheDocumentFoundation.LibreOffice)")),
            L("✓ Windows — Magick, PDFium and Windows OCR"),
            Line("OCR", ocr.Count > 0, ocr.Count > 0 ? L("text in scanned PDFs: %@", Listed(ocr)) : L("no language for reading text in scanned PDFs is installed in Windows")),
            (Tools.CanRemoveBackground ? "✓ " : "✗ ") + L("Background removal model — remove background"),
        ];
        return string.Join("\n", lines);
    }
}

/// <summary>
/// The <c>splitapp</c> command in a terminal: the folder holding <c>splitapp.exe</c> (next to the app)
/// on the user's PATH, which one PowerShell command adds without an administrator.
/// </summary>
public static class CommandLineTool
{
    public static string Folder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static bool IsInstalled
    {
        get
        {
            var folder = Folder;
            return new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
                .SelectMany(target => (Environment.GetEnvironmentVariable("Path", target) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                .Any(entry => Path.TrimEndingDirectorySeparator(entry.Trim()).Equals(folder, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>For the user to paste in PowerShell; the folder is quoted, and adding it twice does nothing.</summary>
    public static string InstallCommand
    {
        get
        {
            var folder = "'" + Folder.Replace("'", "''") + "'";
            return $"$d = {folder}; $p = [Environment]::GetEnvironmentVariable('Path', 'User'); " +
                   "if (($p -split ';') -notcontains $d) { [Environment]::SetEnvironmentVariable('Path', (\"$p\".TrimEnd(';') + ';' + $d).TrimStart(';'), 'User') }";
        }
    }
}
