namespace Split;

/// <summary>Turning raw failures into explanations.</summary>
public static class Explain
{
    /// <summary>Any error → what failed, why, and what to do. Adds the content/extension mismatch when there is one.</summary>
    public static Problem Error(Exception error, string? file = null)
    {
        var result = error as Problem ?? FromException(error, file);
        if (file is not null && FileCheck.Mismatch(file) is { } mismatch && result.Suggestion?.Contains(mismatch) != true)
            result = result.WithSuggestion(L("Likely cause: %@", char.ToLowerInvariant(mismatch[0]) + mismatch[1..]));
        return result;
    }

    /// <summary>Errors of the file system, mapped by the Windows error code they carry.</summary>
    public static Problem FromException(Exception error, string? file)
    {
        var name = file is null ? null : Path.GetFileName(file);
        // The sentence that names the file, or the one that doesn't when there is none.
        string Say(Func<string, string> named, string unnamed) => name is null ? unnamed : named(name);
        var path = (error as FileNotFoundException)?.FileName ?? file;
        var code = error.HResult;
        var technical = $"{error.GetType().Name} 0x{code:X8}: {error.Message}";
        var win32 = (uint)code >> 16 == 0x8007 ? code & 0xFFFF : 0;

        if (error is FileNotFoundException or DirectoryNotFoundException || win32 is 2 or 3)
        {
            var missing = Say(n => L("“%@” is no longer where it was.", n), L("The file is no longer where it was."));
            return new Problem(missing, L("It was moved or deleted while it was being processed. Drag it again."), technical);
        }
        if (error is UnauthorizedAccessException || win32 == 5)
        {
            var blocked = Say(n => L("Windows blocked access to “%@”.", n), L("Windows blocked access to the file."));
            return new Problem(blocked, FileCheck.PrivacyHint(path ?? Path.GetPathRoot(Environment.SystemDirectory)!), technical);
        }
        switch (win32)
        {
            case 19:
                return new Problem(Say(n => L("The disk holding “%@” is read-only.", n), L("The disk is read-only.")),
                                   L("Copy the file to this PC and use the copy."), technical);
            case 32 or 33:
                return new Problem(Say(n => L("“%@” is open in another app.", n), L("The file is open in another app.")),
                                   L("Close it there, or wait for the other app to finish with it, and try again."), technical);
            case 39 or 112:
                return new Problem(L("The disk filled up while working."), L("Free some space and try again."), technical);
            case 123 or 161 or 267:
                return InvalidName(Say, technical);
        }
        if (error is NotSupportedException) return InvalidName(Say, technical);
        if (error is PathTooLongException || win32 == 206)
        {
            return new Problem(Say(n => L("The path to “%@” is too long for Windows.", n), L("The path is too long for Windows.")),
                               L("Move the file closer to the root of the drive, or shorten the folder and file names."), technical);
        }
        // The readers of .NET say a file is damaged by the type of the error, not by a code.
        if (error is EndOfStreamException or InvalidDataException || win32 == 38)
            return new Problem(L("Couldn’t finish: %@", L("the file is damaged, incomplete or not the type its extension says")),
                               L("Open it in its usual app to check it. If it came from the Internet, download it again."), technical);
        if (error is OutOfMemoryException)
            return new Problem(L("Couldn’t finish: %@", L("the PC ran out of memory")), L("Close other apps or process fewer files at a time."), technical);
        return new Problem(L("Couldn’t finish: %@", error.Message), technical: technical);
    }

    private static Problem InvalidName(Func<Func<string, string>, string, string> say, string technical) =>
        new(say(n => L("Windows doesn’t accept the name “%@”.", n), L("Windows doesn’t accept that file name.")),
            L("Names can’t contain < > : \" / \\ | ? * or be a reserved name such as CON or NUL. Rename it and try again."), technical);

    private sealed record Rule(string[] Patterns, Func<string, string> Reason, Func<string> Suggestion);

    // Ordered: the first match explains the failure. Patterns match engine output, which is in English.
    private static readonly Rule[] Rules =
    [
        new(["no space left on device", "not enough space on the disk"], _ => L("the disk is full"),
            () => L("Free some space (for example, empty the Recycle Bin) and try again.")),
        new(["being used by another process", "sharing violation"], _ => L("the file is open in another app"),
            () => L("Close it there, or wait for the other app to finish with it, and try again.")),
        new(["operation not permitted", "permission denied", "not authorized to access", "access is denied"],
            _ => L("Windows blocked access to the file or folder"),
            () => L("Check that you can read and write it (Properties › Security). If Controlled folder access is on, allow Split in Windows Security, or copy the file to another folder.")),
        new(["no such file or directory", "cannot find the file", "cannot find the path"],
            _ => L("the file is no longer where it was (moved, deleted, or its disk was disconnected)"),
            () => L("Drag it again from where it is now.")),
        new(["protected view", "blocked from opening", "mark of the web"],
            app => app.Length == 0 ? L("Office blocked the file as unsafe (Protected View)") : L("%@ blocked the file as unsafe (Protected View)", app),
            () => L("Right-click the file, choose Properties, tick “Unblock” and try again.")),
        new(["call was rejected by callee", "0x80010001", "0x8001010a"],
            app => app.Length == 0 ? L("the app didn’t respond in time") : L("%@ didn’t respond in time", app),
            () => L("There is usually a window waiting for an answer (for example, asking to convert or update the document). Open the app, close that dialog and try again.")),
        new(["rpc server is unavailable", "0x800706ba", "0x800706be", "0x80010108"],
            app => app.Length == 0 ? L("the app quit or froze during the export") : L("%@ quit or froze during the export", app),
            () => L("Open the app yourself, check that the document opens without errors and try again.")),
        new(["password", "encrypted", "passphrase", "is protected"], _ => L("the file is password-protected"),
            () => L("Remove the password in the app it came from (or use 7-Zip for archives) and try again.")),
        new(["path contains '..'", "cannot extract through symlink", "illegal path", "path is absolute"],
            _ => L("the archive tries to write files outside the destination folder (a trick used by malicious archives), so the extraction was blocked"),
            () => L("Don’t open this archive unless you trust whoever sent it. If you do, ask them to create it again.")),
        new(["moov atom not found"], _ => L("the video is incomplete: the recording was interrupted or the download didn’t finish"),
            () => L("Download or copy the video again. If it comes from a recording that stopped halfway, it may not be recoverable.")),
        new(["unknown encoder", "encoder not found", "unrecognized option"], _ => L("the installed ffmpeg doesn’t have the encoder it needs"),
            () => L("Reinstall the full ffmpeg with: winget install -e --id Gyan.FFmpeg --force")),
        new(["could not find tag for codec", "not currently supported in container", "codec not currently supported"],
            _ => L("the target format doesn’t accept this file’s codec"),
            () => L("Choose MP4 or MKV, which accept almost every codec.")),
        new(["does not contain any stream", "matches no streams", "output file is empty", "stream specifier"],
            _ => L("the file doesn’t have the track it needs (for example, a video without sound or audio without a picture)"),
            () => L("Check in a media player that the file has the track you want to use.")),
        new(["decoder (codec", "unsupported codec", "no decoder", "could not find codec parameters", "unknown codec"],
            _ => L("the file uses a codec ffmpeg can’t read"),
            () => L("It may come from a camera or app with its own format. Export it to MP4 (H.264) in that app first.")),
        new(["invalid data found when processing input", "end of file", "invalid nal", "error while decoding", "corrupt",
             "truncated", "damaged", "unexpected end", "not a valid", "file format error", "end-of-central-directory",
             "unrecognized archive format", "couldn't read pkzip", "bad zipfile", "zipfile is empty", "could not process file",
             "unsupported image format", "unsupported color conversion"],
            _ => L("the file is damaged, incomplete or not the type its extension says"),
            () => L("Open it in its usual app to check it. If it came from the Internet, download it again.")),
        new(["not divisible by 2"], _ => L("the video has odd dimensions and the encoder needs even ones"),
            () => L("Crop 1 pixel off in the video editor (Crop) and try again.")),
        new(["cannot allocate memory", "out of memory", "not enough memory", "insufficient memory"], _ => L("the PC ran out of memory"),
            () => L("Close other apps or process fewer files at a time.")),
    ];

    /// <summary>
    /// A command-line engine failed: map its stderr to a reason the user understands.
    /// <paramref name="what"/> names the work as the subject of a sentence ("Converting to WebP").
    /// </summary>
    public static Problem ToolFailure(string what, string tool, string stderr, int status, string app = "")
    {
        var lines = stderr.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var technical = string.Join("\n", new[] { L("%@ ended with code %@", tool, status) }.Concat(lines.TakeLast(8)));
        var lower = stderr.ToLowerInvariant();
        if (Rules.FirstOrDefault(r => r.Patterns.Any(lower.Contains)) is { } rule)
            return new Problem(L("%@ failed: %@.", what, rule.Reason(app)), rule.Suggestion(), technical);
        if (status is 9 or 15 or 137)
            return new Problem(L("%@ was stopped by the system.", what), L("It may have run out of memory. Close other apps and try again."), technical);
        var message = lines.Count > 0 ? L("%@ failed: %@", what, lines[^1][..Math.Min(160, lines[^1].Length)]) : L("%@ failed.", what);
        return new Problem(message, L("Try another target format. If it happens again, look at the technical details."), technical);
    }

    /// <summary>Why a wheel would be empty for these files.</summary>
    public static Problem NoTargets(IReadOnlyList<string> paths, bool tools)
    {
        var folders = paths.Where(p => Directory.Exists(p) && !FileCheck.IsPackage(p)).ToList();
        if (folders.Count > 0 && !tools)
        {
            return new Problem(folders.Count == paths.Count ? L("Folders can’t be converted.") : L("Some of these are folders, and folders can’t be converted."),
                               L("To compress a folder, use “Create ZIP” in the tools wheel."));
        }
        var unknown = paths.Where(p => Formats.Detect(p) is null && !Directory.Exists(p)).ToList();
        if (unknown.Count > 0)
        {
            var first = unknown[0];
            return Paths.Extension(first).Length == 0
                ? new Problem(L("Split doesn’t recognize files without an extension (“%@”).", Path.GetFileName(first)),
                              L("Add the right extension to the name (for example .jpg or .pdf)."))
                : new Problem(L("Split doesn’t recognize .%@ files (“%@”).", Paths.Extension(first).ToLowerInvariant(), Path.GetFileName(first)),
                              L("This format isn’t supported yet."));
        }
        var formats = paths.Select(Formats.Detect).OfType<Format>().ToList();
        var groups = formats.Select(f => f.Group()).Distinct().ToList();
        if (groups.Count > 1)
        {
            var names = Listed(groups.Select(GroupName).OrderBy(n => n, StringComparer.Ordinal).ToList());
            return new Problem(tools ? L("The files are of different kinds (%@) and no tool works for all of them.", names)
                                     : L("The files are of different kinds (%@) and no format works for all of them.", names),
                               L("Drag files of one kind at a time."));
        }
        if (formats.Count > 0 && !tools)
        {
            var format = formats[0];
            if (format is Format.Pages or Format.Numbers or Format.Key)
            {
                return new Problem(L("%@ files only open on a Mac.", format.Label()),
                                   L("Open it on a Mac and export it as a PDF or an Office file, then use that file here."));
            }
            switch (format.Group())
            {
                case FormatGroup.Presentation:
                    return new Problem(L("Converting %@ needs PowerPoint or LibreOffice, and neither is installed.", format.Label()),
                                       L("Install LibreOffice: winget install -e --id TheDocumentFoundation.LibreOffice"));
                case FormatGroup.Spreadsheet:
                    return new Problem(L("Reading %@ needs Excel or LibreOffice, and neither is installed.", format.Label()),
                                       L("Install LibreOffice: winget install -e --id TheDocumentFoundation.LibreOffice"));
            }
        }
        if (tools)
        {
            return new Problem(paths.Count == 1 ? L("No tool applies to this file.") : L("No tool applies to this combination of files."),
                               L("For example, “Join Videos” needs at least two videos, and “Split PDF” only takes PDFs."));
        }
        return new Problem(L("There are no conversions for these files."), L("Try the tools wheel."));
    }

    /// <summary>The wheel has slices for these files, but the user hid every one of them.</summary>
    public static Problem AllHidden(string wheel) =>
        new(L("Everything the “%@” wheel offers for these files is hidden.", wheel), L("Show the slices again in Settings › Wheels."));

    public static string GroupName(FormatGroup group) => group switch
    {
        FormatGroup.Image => L("images"),
        FormatGroup.Document => L("documents"),
        FormatGroup.Spreadsheet => L("spreadsheets"),
        FormatGroup.Presentation => L("presentations"),
        FormatGroup.Audio => L("audio"),
        FormatGroup.Video => L("video"),
        FormatGroup.Subtitle => L("subtitles"),
        FormatGroup.Archive => L("archives"),
        _ => L("data"),
    };
}
