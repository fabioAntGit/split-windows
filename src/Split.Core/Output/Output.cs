namespace Split;

/// <summary>Output naming: results are saved beside the originals, which stay untouched.</summary>
public static class Output
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string BaseName(string source)
    {
        var name = Paths.WithoutExtension(source);
        return name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static string Url(string source, string ext, string suffix = "", string? directory = null) =>
        Unique(Path.Combine(directory ?? Path.GetDirectoryName(Path.GetFullPath(source))!, BaseName(source) + suffix + "." + ext));

    public static string Folder(string source, string suffix) =>
        Unique(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, BaseName(source) + suffix), isDirectory: true);

    /// <summary>
    /// A name Windows accepts: no <c>&lt;&gt;:"/\|?*</c> or control characters, not one of the reserved
    /// device names (CON, NUL…, even with an extension), and no dots or spaces at the end.
    /// </summary>
    public static string SafeName(string name)
    {
        var clean = string.Concat(name.Select(c => c < ' ' || "<>:\"/\\|?*".Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (clean.Length == 0) return "_";
        var stem = clean.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem) ? "_" + clean : clean;
    }

    /// <summary>Names handed out but not written yet: parallel jobs must not pick the same output.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);

    public static string Unique(string path, bool isDirectory = false)
    {
        lock (Reserved)
        {
            var directory = Path.GetDirectoryName(path) ?? "";
            var name = SafeName(Path.GetFileName(path));
            var url = Path.Combine(directory, name);
            bool Taken(string candidate) => File.Exists(candidate) || Directory.Exists(candidate) || Reserved.Contains(candidate);
            if (!Taken(url))
            {
                Reserved.Add(url);
                return url;
            }
            var stem = name;
            var ext = "";
            if (!isDirectory)
            {
                if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    ext = "tar.gz";
                    stem = name[..^7];
                }
                else if (Paths.Extension(name) is { Length: > 0 } found)
                {
                    ext = found;
                    stem = Paths.WithoutExtension(name);
                }
            }
            for (var index = 2; ; index++)
            {
                var candidate = Path.Combine(directory, ext.Length == 0 ? $"{stem} {index}" : $"{stem} {index}.{ext}");
                if (Taken(candidate)) continue;
                Reserved.Add(candidate);
                return candidate;
            }
        }
    }

    /// <summary>Hidden-looking temp name next to the final output; renamed only when the engine succeeds.</summary>
    public static string TempSibling(string final) =>
        Path.Combine(Path.GetDirectoryName(final) ?? "", $".split-{Guid.NewGuid().ToString()[..8]}-{Path.GetFileName(final)}");

    public static void Commit(string temp, string final)
    {
        if (!File.Exists(temp) && !Directory.Exists(temp))
            throw new Problem(L("The engine finished without errors but didn’t create the output file."),
                              L("This usually happens when the source has no usable content (for example, a video without pictures). Try another format."));
        if (Directory.Exists(temp)) Directory.Move(temp, final); else File.Move(temp, final);
    }

    public static void Discard(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        lock (Reserved) Reserved.Remove(path);
    }

    public static string ScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"split-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Text results are written like every other result: hidden temp first, renamed when complete.</summary>
    public static void WriteText(string final, string text)
    {
        var temp = TempSibling(final);
        try { File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false)); }
        catch { Discard(temp); throw; }
        Commit(temp, final);
    }
}
