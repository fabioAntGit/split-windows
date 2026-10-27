using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Split;

public sealed class ArchiveTools : IConversionFamily
{
    public IReadOnlyList<Format> Targets(Format source) => source.Group() == FormatGroup.Archive ? [Format.Zip, Format.Tar, Format.Tgz] : [];

    public IReadOnlyList<string> Convert(string path, Format source, Format target)
    {
        var workspace = Output.ScratchDirectory();
        try
        {
            Unpack(path, workspace);
            var items = Directory.EnumerateFileSystemEntries(workspace).Where(item => Path.GetFileName(item) != "__MACOSX").ToList();
            var output = Output.Url(path, target.Ext());
            Pack(items, target, output);
            return [output];
        }
        finally { Output.Discard(workspace); }
    }

    private static Problem Blocked(string key) =>
        Explain.ToolFailure(L("Extracting the archive"), "archive", $"illegal path: {key}", 1);

    /// <summary>
    /// Extracts every entry under <paramref name="directory"/>. A path that would land anywhere else
    /// (<c>..</c>, a drive, an absolute path, a stream name) stops the extraction. Links are never
    /// created: once moved next to the archive they would give a hostile archive a way out.
    /// </summary>
    public static void Unpack(string path, string directory)
    {
        var root = Path.GetFullPath(directory);

        void Entry(string? key, bool isDirectory, string? link, Func<Stream> open)
        {
            JobProgress.CheckCancelled();
            if (key is null) return;
            var slashed = key.Replace('\\', '/');
            var relative = slashed.TrimStart('/');
            if (relative.Length == 0) return;
            if (slashed.StartsWith('/') || relative.Contains(':') || Path.IsPathRooted(relative)) throw Blocked(key);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!Paths.IsInside(target, root)) throw Blocked(key);
            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                return;
            }
            if (!string.IsNullOrEmpty(link))
            {
                var resolved = Path.GetFullPath(Path.IsPathRooted(link) ? link : Path.Combine(Path.GetDirectoryName(target)!, link.Replace('/', '\\')));
                Fidelity.Note(Paths.IsInside(resolved, root)
                    ? L("Shortcuts in the archive were left out: Windows can’t recreate them.")
                    : L("Shortcuts in the archive that pointed outside it were left out, for safety."));
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = open();
            using var output = File.Create(target);
            input.CopyTo(output);
        }

        try
        {
            if (FileCheck.Sniff(path) == "GZIP")
            {
                // A compressed tar is read as a stream: the archive reader opens one layer only.
                using var stream = File.OpenRead(path);
                using var reader = ReaderFactory.OpenReader(stream);
                while (reader.MoveToNextEntry())
                    Entry(reader.Entry.Key, reader.Entry.IsDirectory, reader.Entry.LinkTarget, reader.OpenEntryStream);
            }
            else
            {
                var isZip = FileCheck.Sniff(path) == "ZIP";
                using var archive = ArchiveFactory.OpenArchive(path);
                foreach (var entry in archive.Entries)
                {
                    // Zip files mark a link in the Unix mode kept in the upper half of the attributes.
                    var link = entry.LinkTarget;
                    if (isZip && string.IsNullOrEmpty(link) && entry.Attrib is { } attributes && ((attributes >> 16) & 0xF000) == 0xA000) link = "?";
                    Entry(entry.Key, entry.IsDirectory, link, entry.OpenEntryStream);
                }
            }
        }
        catch (Exception e) when (e is not Problem and not IOException and not UnauthorizedAccessException && !JobProgress.IsCancelled)
        {
            // The library describes a damaged or locked archive in its own words; the rules map those to ours.
            var words = e.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ? e.Message : "damaged archive: " + e.Message;
            throw Explain.ToolFailure(L("Extracting the archive"), "archive", words, 1);
        }
    }

    /// <summary>Extracts next to the archive; a single top-level folder is lifted out.</summary>
    public static IReadOnlyList<string> Extract(string path)
    {
        if (Formats.Detect(path) is not { } source || source.Group() != FormatGroup.Archive)
            throw new Problem(L("“%@” isn’t a ZIP, TAR, RAR or 7z archive.", Path.GetFileName(path)), FileCheck.Mismatch(path));
        var destination = Output.Folder(path, "");
        // Beside the archive, so the results can be moved into place on the same drive.
        var workspace = Output.TempSibling(destination);
        Directory.CreateDirectory(workspace);
        try
        {
            Unpack(path, workspace);
            var items = Directory.EnumerateFileSystemEntries(workspace)
                .Where(item => Path.GetFileName(item) is not ("__MACOSX" or ".DS_Store")).ToList();
            if (items.Count == 0)
            {
                Output.Discard(destination);
                throw new Problem(L("“%@” opened, but there is nothing inside.", Path.GetFileName(path)));
            }
            if (items.Count == 1)
            {
                Output.Discard(destination);
                var single = items[0];
                var target = Output.Unique(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(single)), Directory.Exists(single));
                if (Directory.Exists(single)) Directory.Move(single, target); else File.Move(single, target);
                return [target];
            }
            Directory.CreateDirectory(destination);
            foreach (var item in items)
            {
                var target = Path.Combine(destination, Path.GetFileName(item));
                if (Directory.Exists(item)) Directory.Move(item, target); else File.Move(item, target);
            }
            return [destination];
        }
        finally { Output.Discard(workspace); }
    }

    /// <summary>Creates a zip/tar/tar.gz with the items' names relative to their folder.</summary>
    public static void Pack(IReadOnlyList<string> items, Format format, string output)
    {
        if (items.Count == 0) throw new Problem(L("There are no files to put in the archive."));
        string? staging = null;
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(items[0]))!;
            if (!items.All(item => Paths.Standard(Path.GetDirectoryName(Path.GetFullPath(item))!).Equals(Paths.Standard(parent), StringComparison.OrdinalIgnoreCase)))
            {
                staging = Output.ScratchDirectory();
                foreach (var item in items)
                {
                    var copy = Output.Unique(Path.Combine(staging, Path.GetFileName(item)), Directory.Exists(item));
                    if (Directory.Exists(item)) CopyDirectory(item, copy); else File.Copy(item, copy);
                }
                parent = staging;
            }
            var names = staging is null ? items.Select(Path.GetFileName).ToList() : Directory.EnumerateFileSystemEntries(staging).Select(Path.GetFileName).ToList();
            var temp = Output.TempSibling(output);
            try
            {
                switch (format)
                {
                    case Format.Zip:
                        using (var file = File.Create(temp))
                        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                            foreach (var name in names!) AddToZip(zip, Path.Combine(parent, name!), name!);
                        break;
                    case Format.Tar or Format.Tgz:
                        using (var file = File.Create(temp))
                        using (Stream stream = format == Format.Tgz ? new GZipStream(file, CompressionLevel.Optimal) : file)
                        using (var tar = new TarWriter(stream, TarEntryFormat.Pax))
                            foreach (var name in names!) AddToTar(tar, Path.Combine(parent, name!), name!);
                        break;
                    default:
                        throw new Problem(L("Split only creates ZIP, TAR and TAR.GZ archives (not %@).", format.Label()), L("RAR and 7z are closed formats or need their own apps."));
                }
            }
            catch
            {
                Output.Discard(temp);
                throw;
            }
            Output.Commit(temp, output);
        }
        finally
        {
            if (staging is not null) Output.Discard(staging);
        }
    }

    private static bool Skipped(string path) =>
        // Archives made on Apple systems carry these folders and files of their own; they are not content.
        Path.GetFileName(path) is ".DS_Store" or "__MACOSX" || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static IEnumerable<string> Children(string directory) =>
        Directory.EnumerateFileSystemEntries(directory).Where(child => !Skipped(child)).Order(StringComparer.Ordinal);

    private static void AddToZip(ZipArchive zip, string path, string name)
    {
        if (Skipped(path)) return;
        if (Directory.Exists(path))
        {
            zip.CreateEntry(name + "/");
            foreach (var child in Children(path)) AddToZip(zip, child, name + "/" + Path.GetFileName(child));
        }
        else zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
    }

    private static void AddToTar(TarWriter tar, string path, string name)
    {
        if (Skipped(path)) return;
        tar.WriteEntry(path, name);
        if (Directory.Exists(path))
            foreach (var child in Children(path)) AddToTar(tar, child, name + "/" + Path.GetFileName(child));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var child in Directory.EnumerateFileSystemEntries(from))
        {
            if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) continue;
            var target = Path.Combine(to, Path.GetFileName(child));
            if (Directory.Exists(child)) CopyDirectory(child, target); else File.Copy(child, target);
        }
    }
}
