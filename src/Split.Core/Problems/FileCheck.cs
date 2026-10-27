namespace Split;

/// <summary>Checks before a job starts, and the real type of a file from its first bytes.</summary>
public static class FileCheck
{
    private const int WriteProtected = unchecked((int)0x80070013);

    /// <summary>Problems that would make any job fail, explained before an engine runs.</summary>
    public static void Preflight(string path, bool allowFolders = false, bool waitForDownload = true)
    {
        var name = Path.GetFileName(path);
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            throw new Problem(L("“%@” is no longer where it was.", name),
                              L("It was moved, renamed or deleted after you chose it. Drag it again from where it is now."));
        if (isDirectory && !allowFolders && !IsPackage(path))
            throw new Problem(L("“%@” is a folder, not a file.", name),
                              L("Folders can’t be converted. To compress one, use “Create ZIP” in the tools wheel."));
        if (waitForDownload) RequireLocalCopy(path);
        if (!IsReadable(path, isDirectory))
            throw new Problem(L("Windows doesn’t let Split read “%@”.", name), PrivacyHint(path));
        if (!isDirectory && Size(path) == 0)
            throw new Problem(L("“%@” is empty (0 bytes): there is nothing to process.", name),
                              L("The download or export probably failed. Download or export the file again."));
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        CheckWritable(folder);
        CheckSpace(folder, Size(path));
    }

    /// <summary>A folder that is one document (an RTFD), which converts like a file.</summary>
    public static bool IsPackage(string path) =>
        Directory.Exists(path) && Paths.Extension(path).Equals("rtfd", StringComparison.OrdinalIgnoreCase);

    public static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool IsReadable(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory) _ = Directory.EnumerateFileSystemEntries(path).Any();
            else File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>OneDrive placeholders are downloaded first (reading one starts it); nothing can use them otherwise.</summary>
    public static void RequireLocalCopy(string path)
    {
        // The cloud-file flags (RecallOnOpen, RecallOnDataAccess) have no names in FileAttributes.
        const FileAttributes online = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
        if (!File.Exists(path) || (File.GetAttributes(path) & online) == 0) return;
        var download = Task.Run(() =>
        {
            using var file = File.OpenRead(path);
            file.CopyTo(Stream.Null);
        });
        try { if (download.Wait(TimeSpan.FromSeconds(60))) return; }
        catch (AggregateException) { return; }
        throw new Problem(L("“%@” is in OneDrive and hasn’t been downloaded to this PC yet.", Path.GetFileName(path)),
                          L("The download was requested but took more than a minute. Wait until the cloud icon next to the file in File Explorer turns into a green check and try again."));
    }

    /// <summary>Whether a file can be created in the folder, found out by creating one that disappears when closed.</summary>
    public static void CheckWritable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".split-probe-{Guid.NewGuid():N}");
            File.Open(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            File.Delete(probe);
        }
        catch (IOException e) when (e.HResult == WriteProtected)
        {
            throw new Problem(L("The folder “%@” is on a read-only disk (%@).", Path.GetFileName(folder), VolumeName(folder)),
                              L("CDs, write-protected cards and disks mounted as read-only can only be read. Copy the file to this PC and use the copy."));
        }
        catch (UnauthorizedAccessException)
        {
            throw new Problem(L("Split isn’t allowed to save in the folder “%@”, where the result would go.", Path.GetFileName(folder)),
                              PrivacyHint(folder) + " " + L("Or copy the file to another folder, such as Downloads."));
        }
    }

    public static void CheckSpace(string folder, long needed)
    {
        long available;
        try { available = new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return; }
        const long margin = 50_000_000;
        if (available <= 0 || available >= needed + margin) return;
        throw new Problem(L("There isn’t enough space on “%@”: %@ left, and the result may need about %@.", VolumeName(folder),
                            ByteCount.Format(available), ByteCount.Format(needed)),
                          L("Free some space (for example, empty the Recycle Bin) and try again."));
    }

    private static string VolumeName(string folder)
    {
        try
        {
            var label = new DriveInfo(Path.GetPathRoot(folder)!).VolumeLabel;
            if (label.Length > 0) return label;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        return L("external disk");
    }

    /// <summary>Where to grant access, when Windows is protecting the folder.</summary>
    public static string PrivacyHint(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var protectedFolders = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                       Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                       Path.Combine(profile, "Downloads") };
        if (protectedFolders.Any(folder => Paths.IsInside(path, folder)))
            return L("Windows may be protecting this folder. Open Windows Security › Virus & threat protection › Ransomware protection › Controlled folder access and allow Split.");
        return L("Check in File Explorer (Properties › Security) that you can read and write it, or allow Split through Controlled folder access in Windows Security.");
    }

    // MARK: Content sniffing

    /// <summary>The real content type, from the first bytes (independent of the extension).</summary>
    public static string? Sniff(string path)
    {
        byte[] bytes;
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[512];
            bytes = bytes[..file.Read(bytes, 0, bytes.Length)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (bytes.Length < 4) return null;

        bool Has(byte[] signature, int offset = 0) => bytes.Length >= offset + signature.Length && bytes.AsSpan(offset, signature.Length).SequenceEqual(signature);
        bool Ascii(string text, int offset = 0) => Has(System.Text.Encoding.UTF8.GetBytes(text), offset);

        if (Has([0xFF, 0xD8, 0xFF])) return "JPEG";
        if (Has([0x89, 0x50, 0x4E, 0x47])) return "PNG";
        if (Ascii("GIF8")) return "GIF";
        if (Ascii("%PDF")) return "PDF";
        if (Ascii("RIFF") && Ascii("WEBP", 8)) return "WebP";
        if (Ascii("RIFF") && Ascii("WAVE", 8)) return "WAV";
        if (Ascii("RIFF") && Ascii("AVI ", 8)) return "AVI";
        if (Ascii("FORM") && (Ascii("AIFF", 8) || Ascii("AIFC", 8))) return "AIFF";
        if (Ascii("ftyp", 4) && bytes.Length >= 12)
        {
            var brand = System.Text.Encoding.UTF8.GetString(bytes, 8, 4);
            if (brand is "heic" or "heix" or "hevc" or "mif1" or "msf1") return "HEIC";
            if (brand.StartsWith("avi", StringComparison.Ordinal)) return "AVIF";
            if (brand.StartsWith("M4A", StringComparison.Ordinal) || brand.StartsWith("M4B", StringComparison.Ordinal)) return "M4A";
            return brand == "qt  " ? "MOV" : "MP4";
        }
        if (Has([0x50, 0x4B, 0x03, 0x04]) || Has([0x50, 0x4B, 0x05, 0x06])) return "ZIP";
        if (Ascii("ID3")) return "MP3";
        if (Ascii("fLaC")) return "FLAC";
        if (Ascii("OggS")) return "OGG";
        if (Has([0x1A, 0x45, 0xDF, 0xA3])) return "MKV/WebM";
        if (Has([0x30, 0x26, 0xB2, 0x75])) return "WMA/WMV";
        if (Has([0x49, 0x49, 0x2A, 0x00]) || Has([0x4D, 0x4D, 0x00, 0x2A])) return "TIFF";
        if (Has([0x00, 0x00, 0x01, 0x00])) return "ICO";
        if (Ascii("Rar!")) return "RAR";
        if (Has([0x37, 0x7A, 0xBC, 0xAF])) return "7z";
        if (Has([0x1F, 0x8B])) return "GZIP";
        if (Has([0xD0, 0xCF, 0x11, 0xE0])) return "DOC/XLS/PPT";
        if (Ascii("{\\rtf")) return "RTF";
        if (Ascii("BM") && bytes.Length > 14) return "BMP";
        if (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0) return "MP3";
        var text = System.Text.Encoding.UTF8.GetString(bytes).ToLowerInvariant();
        if (text.Contains("<!doctype html") || text.Contains("<html")) return "HTML";
        return text.Contains("<svg") ? "SVG" : null;
    }

    private static readonly Dictionary<Format, string[]> Expected = new()
    {
        [Format.Jpg] = ["JPEG"], [Format.Png] = ["PNG"], [Format.Gif] = ["GIF"], [Format.Webp] = ["WebP"], [Format.Heic] = ["HEIC", "AVIF"],
        [Format.Avif] = ["AVIF", "HEIC"], [Format.Tiff] = ["TIFF"], [Format.Bmp] = ["BMP"], [Format.Ico] = ["ICO", "PNG"], [Format.Svg] = ["SVG"],
        [Format.Pdf] = ["PDF"], [Format.Docx] = ["ZIP"], [Format.Xlsx] = ["ZIP"], [Format.Pptx] = ["ZIP"], [Format.Epub] = ["ZIP"],
        [Format.Odt] = ["ZIP"], [Format.Ods] = ["ZIP"], [Format.Odp] = ["ZIP"], [Format.Pages] = ["ZIP"], [Format.Numbers] = ["ZIP"],
        [Format.Key] = ["ZIP"], [Format.Zip] = ["ZIP"],
        [Format.Doc] = ["DOC/XLS/PPT", "RTF"], [Format.Xls] = ["DOC/XLS/PPT"], [Format.Ppt] = ["DOC/XLS/PPT"],
        [Format.Rtf] = ["RTF"], [Format.Mp3] = ["MP3"], [Format.M4a] = ["M4A", "MP4"], [Format.Wav] = ["WAV"], [Format.Flac] = ["FLAC"],
        [Format.Ogg] = ["OGG"], [Format.Opus] = ["OGG"], [Format.Aiff] = ["AIFF"], [Format.Wma] = ["WMA/WMV"], [Format.Wmv] = ["WMA/WMV"],
        [Format.Mp4] = ["MP4", "MOV", "M4A"], [Format.M4v] = ["MP4", "MOV"], [Format.Mov] = ["MOV", "MP4"], [Format.Mkv] = ["MKV/WebM"],
        [Format.Webm] = ["MKV/WebM"], [Format.Avi] = ["AVI"], [Format.Rar] = ["RAR"], [Format.SevenZip] = ["7z"], [Format.Tgz] = ["GZIP"],
    };

    private static readonly Dictionary<string, string> ExtensionFor = new()
    {
        ["JPEG"] = "jpg", ["PNG"] = "png", ["GIF"] = "gif", ["PDF"] = "pdf", ["WebP"] = "webp", ["WAV"] = "wav", ["AVI"] = "avi", ["AIFF"] = "aiff",
        ["HEIC"] = "heic", ["AVIF"] = "avif", ["M4A"] = "m4a", ["MOV"] = "mov", ["MP4"] = "mp4", ["ZIP"] = "zip", ["MP3"] = "mp3", ["FLAC"] = "flac",
        ["OGG"] = "ogg", ["MKV/WebM"] = "mkv", ["TIFF"] = "tiff", ["RAR"] = "rar", ["7z"] = "7z", ["RTF"] = "rtf", ["BMP"] = "bmp", ["HTML"] = "html",
        ["SVG"] = "svg",
    };

    /// <summary>"The file is called .png but holds a JPEG" — the most common cause of baffling failures.</summary>
    public static string? Mismatch(string path)
    {
        if (Formats.Detect(path) is not { } format || Sniff(path) is not { } actual) return null;
        Format[] textFormats = [Format.Txt, Format.Md, Format.Html, Format.Csv, Format.Tsv, Format.Srt, Format.Vtt];
        var isText = textFormats.Contains(format);
        if (isText)
        {
            if (actual is "HTML" or "SVG" or "RTF") return null;
        }
        else if (!Expected.TryGetValue(format, out var allowed) || allowed.Contains(actual))
        {
            return null;
        }
        var text = L("The file has the extension .%@, but its content is %@.", Paths.Extension(path).ToLowerInvariant(), actual);
        if (actual == "HTML" && !isText) text += " " + L("You probably saved the web page instead of the file.");
        if (ExtensionFor.TryGetValue(actual, out var ext) && actual != "HTML") text += " " + L("Change the extension to .%@ and try again.", ext);
        return text;
    }
}
