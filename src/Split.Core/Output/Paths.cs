namespace Split;

/// <summary>File names the way the rest of the app reads them: a name that starts with a dot has no extension.</summary>
public static class Paths
{
    /// <summary>"photo.JPG" → "JPG" (without the dot, as written); empty when there is none.</summary>
    public static string Extension(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[(dot + 1)..];
    }

    public static string WithoutExtension(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    /// <summary>Same file, whatever the spelling: absolute, without "." and "..".</summary>
    public static string Standard(string path) => Path.GetFullPath(path);

    public static bool IsFolder(string path) => Directory.Exists(path);

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var inner = Path.TrimEndingDirectorySeparator(Standard(path));
        var outer = Path.TrimEndingDirectorySeparator(Standard(folder));
        return inner.Equals(outer, StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
