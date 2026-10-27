namespace Split;

/// <summary>
/// Files as they arrive (drags, "Open with", the command line). Shortcuts and symbolic links are
/// followed to the file they point at, so the work happens on the real file and its result lands
/// next to it; the same file reached twice is only handled once.
/// </summary>
public static class Intake
{
    /// <summary>Shortcuts can point at other shortcuts; a few hops is plenty and stops a loop.</summary>
    public const int MaxHops = 8;

    public static List<string> Resolve(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return paths.Select(Resolve).Where(seen.Add).ToList();
    }

    public static string Resolve(string path)
    {
        var current = Paths.Standard(path);
        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (Target(current) is not { } next || next.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = next;
        }
        return current;
    }

    /// <summary>One hop: where a shortcut or a symbolic link points, or null for an ordinary file.</summary>
    private static string? Target(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            // A dangling link stays as it is; the file check then explains that it is missing.
            var resolved = info.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
            return resolved is not null && (File.Exists(resolved) || Directory.Exists(resolved)) ? Paths.Standard(resolved) : null;
        }
        if (!Paths.Extension(path).Equals("lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        return Platform.Current.ResolveShortcut(path) is { } target ? Paths.Standard(target) : null;
    }
}
