namespace Split;

public enum OfficeApp { Word, Excel, PowerPoint }

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

    /// <summary>The faces the system finds in a grey picture (one byte per pixel, row after row), in its pixels.</summary>
    IReadOnlyList<PixelBox> FindFaces(byte[] gray, int width, int height);
}

/// <summary>A rectangle of whole pixels.</summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height);

public static class Platform
{
    private static IPlatform? current;

    public static IPlatform Current
    {
        get => current ?? throw new InvalidOperationException("Platform.Current was not set at start.");
        set => current = value;
    }
}
