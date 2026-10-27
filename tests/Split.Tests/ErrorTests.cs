using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>
/// Every failure must say what went wrong, why, and what to do.
/// </summary>
public static class ErrorTests
{
    private static int Hr(uint code) => unchecked((int)code);

    public static void Register()
    {
        Test("Errors", "Missing file, empty file, folder, unknown type", () =>
        {
            ExpectError("a file that doesn’t exist", ["no longer where it was", "Drag it again"], () => Conversions.Convert(Fixtures.Url("does-not-exist.png"), Format.Jpg));
            ExpectError("an empty file", ["empty (0 bytes)"], () => Conversions.Convert(Fixtures.Url("empty.txt"), Format.Pdf));
            ExpectError("a folder", ["is a folder", "Create ZIP"], () => Conversions.Convert(Fixtures.Url("A folder"), Format.Zip));
            ExpectError("an unknown extension", ["doesn’t recognize", ".xyz"], () => Conversions.Convert(Fixtures.Url("mystery.xyz"), Format.Pdf));
            ExpectError("an impossible target", ["doesn’t convert JPG to MP3", "it can make"], () => Conversions.Convert(Fixtures.Url("quadrants.jpg"), Format.Mp3));
        });

        Test("Errors", "A folder without write permission", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_readonly");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "photo.jpg");
            if (!File.Exists(file)) File.Copy(Fixtures.Url("quadrants.jpg"), file);
            // Windows has no read-only folders; the way to refuse a save is to deny creating files in it.
            var directory = new DirectoryInfo(folder);
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles, AccessControlType.Deny);
            var security = directory.GetAccessControl();
            security.AddAccessRule(rule);
            directory.SetAccessControl(security);
            try
            {
                ExpectError("a folder where files can't be created", ["isn’t allowed to save", "Downloads"], () => Conversions.Convert(file, Format.Png));
            }
            finally
            {
                security.RemoveAccessRule(rule);
                directory.SetAccessControl(security);
            }
        });

        Test("Errors", "Content that doesn’t match the extension", () =>
        {
            var mismatch = FileCheck.Mismatch(Fixtures.Url("fake.png"));
            Check(mismatch?.Contains("its content is JPEG") == true, $"fake.png: {mismatch ?? "null"}");
            Check(mismatch?.Contains("Change the extension to .jpg") == true, "it should suggest .jpg");
            Check(FileCheck.Mismatch(Fixtures.Url("quadrants.jpg")) is null, "a real JPG is fine");
            var page = FileCheck.Mismatch(Fixtures.Url("page.jpg"));
            Check(page?.Contains("HTML") == true && page.Contains("web page"), $"a web page saved as .jpg: {page ?? "null"}");
        });

        Test("Errors", "The real content is recognized (signatures)", () =>
        {
            var expected = new Dictionary<string, string>
            {
                ["quadrants.png"] = "PNG", ["quadrants.jpg"] = "JPEG", ["animated.gif"] = "GIF", ["report.pdf"] = "PDF", ["quadrants.webp"] = "WebP",
                ["tone.wav"] = "WAV", ["clip.mp4"] = "MP4", ["letter.docx"] = "ZIP", ["clip.mkv"] = "MKV/WebM", ["clip.webm"] = "MKV/WebM",
                ["tone.flac"] = "FLAC", ["tone.ogg"] = "OGG", ["tone.mp3"] = "MP3", ["quadrants.tiff"] = "TIFF", ["quadrants.bmp"] = "BMP",
                ["icon.ico"] = "ICO", ["letter.rtf"] = "RTF", ["page.html"] = "HTML", ["drawing.svg"] = "SVG", ["quadrants.heic"] = "HEIC",
                ["clip.avi"] = "AVI", ["tone.aiff"] = "AIFF", ["clip.wmv"] = "WMA/WMV", ["folder.tar.gz"] = "GZIP", ["clip.mov"] = "MOV",
                ["letter.doc"] = "DOC/XLS/PPT",
            };
            foreach (var (name, type) in expected)
                Check(FileCheck.Sniff(Fixtures.Url(name)) == type, $"{name}: {FileCheck.Sniff(Fixtures.Url(name)) ?? "null"} instead of {type}");
        });

        Test("Errors", "Damaged or protected files", () =>
        {
            var corrupt = Fixtures.Isolated("corrupt.zip", "corrupt");
            ExpectError("a damaged ZIP", ["damaged"], () => ArchiveTools.Extract(corrupt));
            var broken = Fixtures.Isolated("broken.mp4", "broken");
            ExpectError("a video cut off halfway", ["incomplete"], () => Conversions.Convert(broken, Format.Mp3));
            var silent = Fixtures.Isolated("silent.mp4", "silent");
            ExpectError("a silent video to MP3", ["has no sound"], () => Conversions.Convert(silent, Format.Mp3));
        });

        Test("Errors", "A password-protected PDF and normalizing without sound", () => Skip("needs the PDF reader and the quick tools"));

        Test("Errors", "A web page saved as a picture is refused with the real cause", () =>
        {
            ExpectError("a web page saved as .jpg", ["HTML", "web page"], () => Conversions.Convert(Fixtures.Url("page.jpg"), Format.Png));
        });

        Test("Errors", "Engine failures are explained in plain words", () =>
        {
            static string Explained(string stderr, string app = "") => Explain.ToolFailure("The conversion", "ffmpeg", stderr, 1, app).FullText;
            Check(Explained("[mov,mp4] moov atom not found\nInvalid data").Contains("incomplete"), "moov atom");
            Check(Explained("x.mp4: Permission denied").Contains("blocked access"), "permission");
            Check(Explained("Unknown encoder 'libfdk_aac'").Contains("winget install -e --id Gyan.FFmpeg"), "missing encoder");
            Check(Explained("Could not find tag for codec pcm_s16le in stream #1, codec not currently supported in container").Contains("MP4 or MKV"), "codec/container");
            Check(Explained("No space left on device").Contains("disk is full"), "full disk");
            Check(Explained("The process cannot access the file because it is being used by another process.").Contains("open in another app"), "file in use");
            var protectedView = Explained("The file is in Protected View and was blocked from opening.", "Word");
            Check(protectedView.Contains("Protected View") && protectedView.Contains("Word") && protectedView.Contains("Unblock"), $"protected view: {protectedView}");
            Check(Explained("Call was rejected by callee. (0x80010001)", "Word").Contains("didn’t respond"), "timeout");
            Check(Explained("The RPC server is unavailable. (0x800706BA)", "Excel").Contains("Excel quit or froze"), "app quit");
            var unknown = Explain.ToolFailure("The conversion", "ffmpeg", "line 1\nsomething odd happened", 1);
            Check(unknown.Message.Contains("something odd happened") && unknown.Technical?.Contains("code 1") == true, "an unknown error keeps its detail");
        });

        Test("Errors", "System errors are explained", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "x.pdf");
            Check(Explain.Error(new IOException("full", Hr(0x80070070)), file).Message.Contains("disk filled up"), "no space");
            Check(Explain.Error(new UnauthorizedAccessException(), file).Message.Contains("blocked access to “x.pdf”"), "permission");
            Check(Explain.Error(new FileNotFoundException(), file).Message.Contains("no longer where it was"), "file not found");
            Check(Explain.Error(new DirectoryNotFoundException(), file).Message.Contains("no longer where it was"), "folder not found");
            Check(Explain.Error(new IOException("busy", Hr(0x80070020)), file).Message.Contains("“x.pdf” is open in another app"), "sharing violation");
            Check(Explain.Error(new IOException("locked", Hr(0x80070021)), null).Message.Contains("open in another app"), "lock violation, without a file name");
            Check(Explain.Error(new IOException("protected", Hr(0x80070013)), file).Message.Contains("read-only"), "write protected");
            Check(Explain.Error(new IOException("name", Hr(0x8007007B)), file).Message.Contains("doesn’t accept the name"), "invalid name");
            Check(Explain.Error(new PathTooLongException(), file).Message.Contains("too long"), "path too long");
            var generic = Explain.Error(new InvalidOperationException("X failed"));
            Check(generic.Message.Contains("X failed") && generic.Technical?.Contains("InvalidOperationException") == true, "a generic error keeps its detail");
            Check(Explain.Error(Problem.Cancelled).IsCancellation, "a cancellation stays one");
        });

        Test("Errors", "Why a wheel has no options", () =>
        {
            var temp = Path.GetTempPath();
            var mixed = Explain.NoTargets([Fixtures.Url("quadrants.jpg"), Fixtures.Url("clip.mp4")], tools: false).Message;
            Check(mixed.Contains("different kinds") && mixed.Contains("images") && mixed.Contains("video"), $"mixed kinds: {mixed}");
            Check(Explain.NoTargets([Fixtures.Url("A folder")], tools: false).Message.Contains("can’t be converted"), "folder");
            Check(Explain.NoTargets([Fixtures.Url("mystery.xyz")], tools: false).Message.Contains(".xyz"), "extension");
            Check(Explain.NoTargets([Path.Combine(temp, "a.pptx")], tools: false).Message.Contains("PowerPoint"), "a presentation without an app");
            Check(Explain.NoTargets([Path.Combine(temp, "a.xls")], tools: false).Message.Contains("Excel"), "a spreadsheet without an app");
            foreach (var (name, app) in new[] { ("a.numbers", "Numbers"), ("a.pages", "Pages"), ("a.key", "Keynote") })
            {
                var message = Explain.NoTargets([Path.Combine(temp, name)], tools: false).Message;
                Check(message.Contains(app) && message.Contains("only open on a Mac"), $"{name}: {message}");
            }
            var tools = Explain.NoTargets([Fixtures.Url("notes.txt")], tools: true);
            Check(tools.Message.Contains("No tool applies") && tools.Suggestion is not null, "tools");
        });

        Test("Errors", "A missing engine gives the install command", () =>
        {
            ExpectError("a missing program", ["isn’t installed", "winget install -e --id package-x"], () => Engines.Require("program-that-doesnt-exist", "package-x"));
        });

        Test("Errors", "Every message seen in these tests explains itself", () =>
        {
            foreach (var error in ErrorLog.Seen)
            {
                Check(!error.Message.Contains("Exception"), $"raw system message: {error.Message}");
                Check(error.FullText.Length >= 25, $"message too short: {error.FullText}");
            }
            Check(ErrorLog.Seen.Count >= 7, $"too few errors checked ({ErrorLog.Seen.Count})");
        });
    }
}
