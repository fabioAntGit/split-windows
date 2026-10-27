namespace Split;

public enum OfficeApp { Word, Excel, PowerPoint }

/// <summary>What an Office app is asked to save: a PDF, or the file in the format the Core reads.</summary>
public enum OfficeExport { Pdf, Docx, Xlsx }

/// <summary>
/// What the Core needs from Windows. The Core stays free of Windows APIs; <c>Split.Windows</c>
/// implements this and each program (command line, app, tests) sets <see cref="Platform.Current"/> at start.
/// </summary>
public interface IPlatform
{
    /// <summary>The full path of a program, or null when it is not installed. Looks where installers put it, also right after an install.</summary>
    string? FindProgram(string name);

    bool IsOfficeInstalled(OfficeApp app);

    /// <summary>Closes the Office apps a job started, leaving the ones the user already had open.</summary>
    void QuitLaunchedOfficeApps();

    bool CanRemoveBackground { get; }

    /// <summary>The languages Windows can read text in (OCR).</summary>
    IReadOnlyList<string> OcrLanguages { get; }

    /// <summary>Where a Windows shortcut (.lnk) points, or null when the file is not one.</summary>
    string? ResolveShortcut(string path);

    /// <summary>Sends a file or folder to the Recycle Bin, never deleting it for good.</summary>
    void MoveToRecycleBin(string path);

    void SetClipboardText(string text);

    /// <summary>An RTF document as Windows' rich text engine reads it; <paramref name="rtf"/> is the file as ASCII-compatible text.</summary>
    RichDocument ReadRtf(string rtf);

    /// <summary>The text Windows recognises in a picture (PNG bytes), one line per row; empty when it reads nothing or no language is installed.</summary>
    string RecognizeText(byte[] png);

    /// <summary>
    /// Opens <paramref name="source"/> read-only in the Office app (macros off, nothing added to recent files) and saves
    /// <paramref name="output"/> in the format asked. Apps the job starts stay open until <see cref="QuitLaunchedOfficeApps"/>.
    /// </summary>
    void ExportWithOffice(OfficeApp app, string source, string output, OfficeExport export);
}

public static class Platform
{
    private static IPlatform? current;

    public static IPlatform Current
    {
        get => current ?? throw new InvalidOperationException("Platform.Current was not set at start.");
        set => current = value;
    }
}
