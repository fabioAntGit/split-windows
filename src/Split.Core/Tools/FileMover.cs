namespace Split;

/// <summary>"Move To…": the files go into the folder chosen; a name already taken there gets a number, nothing is overwritten.</summary>
public static class FileMover
{
    public static (List<string> Moved, List<JobFailure> Failures) Move(IReadOnlyList<string> paths, string destination)
    {
        var moved = new List<string>();
        var failures = new List<JobFailure>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            var isFolder = Directory.Exists(path);
            if (isFolder && Paths.IsInside(destination, path))
            {
                failures.Add(new JobFailure(name, new Problem(L("A folder can’t be moved into itself."))));
                continue;
            }
            if (Paths.Standard(destination).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetDirectoryName(Paths.Standard(path).TrimEnd(Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(new JobFailure(name, new Problem(L("It is already in that folder."))));
                continue;
            }
            var target = Output.Unique(Path.Combine(destination, name), isFolder);
            try
            {
                if (!isFolder) File.Move(path, target);
                else if (string.Equals(Path.GetPathRoot(Paths.Standard(path)), Path.GetPathRoot(Paths.Standard(target)), StringComparison.OrdinalIgnoreCase)) Directory.Move(path, target);
                // Another drive: the folder is copied there and removed from here, which Directory.Move cannot do.
                else Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(path, target);
                moved.Add(target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Output.Discard(target);
                failures.Add(new JobFailure(name, Explain.Error(e, path)));
            }
        }
        return (moved, failures);
    }
}
