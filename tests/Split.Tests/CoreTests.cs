using System.IO;
using System.Text;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>The small rules the rest stands on: texts, names, settings, intake, text encodings and the job list.</summary>
public static class CoreTests
{
    public static void Register()
    {
        Test("Core", "Translation tables are read with their comments, escapes and Windows line ends", () =>
        {
            var table = StringsFile.Parse("/* A comment */\r\n// another\r\n\"one\" = \"1\";\r\n\"two \\\"q\\\"\" = \"a\\nb\\\\c\\tz \\U00e9\";\r\n  \"three\"=\"3\";");
            Check(table.Count == 3 && table["one"] == "1" && table["three"] == "3", $"{table.Count} entries");
            Check(table["two \"q\""] == "a\nb\\c\tz é", $"escapes: {table.GetValueOrDefault("two \"q\"")}");
            Check(StringsFile.Parse("\"ok\" = \"1\"; \"broken\" = ").Count == 1, "a damaged table gives what came before the damage");
            Check(Loc.Fill("%@ of %@", [1, 2]) == "1 of 2" && Loc.Fill("%@", [0.5]) == "0.5", "placeholders fill in order, with invariant numbers");
            Check(Loc.Fill("%%", []) == "%%" && Loc.Fill("5%% %@", ["x"]) == "5% x", "a percent sign is written twice");
            Check(Loc.Listed(["A"]) == "A" && Loc.Listed(["A", "B"]) == "A and B" && Loc.Listed(["A", "B", "C"]) == "A, B, and C", "lists read as sentences");
        });

        Test("Core", "Output names stay beside the original, never collide, and Windows accepts them", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_names");
            Directory.CreateDirectory(folder);
            var source = Path.Combine(folder, "photo.tar.gz");
            Check(Output.BaseName(source) == "photo", $"base name of a .tar.gz: {Output.BaseName(source)}");
            var first = Output.Url(source, "zip");
            var second = Output.Url(source, "zip");
            Check(Path.GetFileName(first) == "photo.zip" && Path.GetFileName(second) == "photo 2.zip", $"two results never share a name: {Path.GetFileName(first)}, {Path.GetFileName(second)}");
            Check(Path.GetFileName(Output.Unique(Path.Combine(folder, "a.tar.gz"))) == "a.tar.gz" && Path.GetFileName(Output.Unique(Path.Combine(folder, "a.tar.gz"))) == "a 2.tar.gz", "a second .tar.gz keeps its double extension");
            File.WriteAllText(Path.Combine(folder, "taken.txt"), "x");
            Check(Path.GetFileName(Output.Unique(Path.Combine(folder, "TAKEN.txt"))) == "TAKEN 2.txt", "names are compared without regard to case");
            Check(Output.SafeName("a:b?.txt") == "a_b_.txt" && Output.SafeName("CON.txt") == "_CON.txt" && Output.SafeName("nul") == "_nul", "invalid characters and reserved names");
            Check(Output.SafeName("name. . ") == "name" && Output.SafeName("...") == "_" && Output.SafeName("COM10.txt") == "COM10.txt", "dots and spaces at the end");
            var temp = Output.TempSibling(first);
            Check(Path.GetFileName(temp).StartsWith(".split-") && Path.GetDirectoryName(temp) == folder, "the temp file sits beside the result");
            ExpectError("committing nothing", ["didn’t create the output file"], () => Output.Commit(temp, first));
        });

        Test("Core", "Settings are kept in a JSON file, replaced as a whole, with defaults for what is missing", () =>
        {
            var previous = Settings.StorePath;
            var path = Path.Combine(Fixtures.Root, "_settings", "settings.json");
            try
            {
                Settings.StorePath = path;
                Check(Settings.Quality == Quality.Balanced && Settings.KeepsMetadata && Settings.WheelSize == WheelSize.Medium && Settings.LookColor == LookColor.Black, "defaults");
                Check(Settings.Modifier(WheelKind.Convert) == ModifierChoice.Shift && Settings.Modifier(WheelKind.Actions) == ModifierChoice.Alt, "default keys");
                Settings.Quality = Quality.High;
                Settings.WheelSize = WheelSize.ExtraLarge;
                Settings.LastTargetSize = "5 MB";
                Settings.SetWheelArrangement(new WheelArrangement { Order = ["b", "a"], Hidden = ["z", "y"], Groups = [new("g", "Mine", ["a"])] }, "tools");
                ModifierChoices.Set(ModifierChoice.Ctrl, WheelKind.Convert);
                Settings.StorePath = path; // reads the file again
                Check(Settings.Quality == Quality.High && Settings.WheelSize == WheelSize.ExtraLarge && Settings.LastTargetSize == "5 MB", "values come back from the file");
                Check(Settings.WheelArrangement("tools").Equals(new WheelArrangement { Order = ["b", "a"], Hidden = ["y", "z"], Groups = [new("g", "Mine", ["a"])] }), "an arrangement comes back as it was saved");
                Check(Settings.Modifier(WheelKind.Convert) == ModifierChoice.Ctrl && Settings.Modifier(WheelKind.Tools) == ModifierChoice.Shift, "choosing a taken key swaps the two");
                Settings.SetWheelArrangement(new WheelArrangement(), "tools");
                Check(Settings.WheelArrangement("tools").IsDefault && !Files.Text(path).Contains("wheel.tools"), "the default arrangement is saved as nothing");
                Check(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "no temp file is left beside the settings");
                File.WriteAllText(path, "{ not json");
                Settings.StorePath = path;
                Check(Settings.Quality == Quality.Balanced, "a damaged file counts as empty");
                File.WriteAllText(path, "{\"quality\": 3, \"wheelSize\": \"big\"}");
                Settings.StorePath = path;
                Check(Settings.Quality == Quality.Balanced && Settings.WheelSize == WheelSize.Medium, "values of the wrong type count as missing");
            }
            finally { Settings.StorePath = previous; }
        });

        Test("Core", "Intake follows shortcuts and handles each file once", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_intake");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "Target.txt");
            File.WriteAllText(target, "x");
            var shortcut = Path.Combine(folder, "shortcut.lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic made = shell.CreateShortcut(shortcut);
            made.TargetPath = target;
            made.Save();
            Check(Intake.Resolve(shortcut) == target, $"a shortcut → {Intake.Resolve(shortcut)}");
            Check(Intake.Resolve(target.ToLowerInvariant()) == target.ToLowerInvariant(), "a plain file stays as it is");
            Check(Intake.Resolve([shortcut, target, target.ToUpperInvariant()]).Count == 1, "the same file reached three ways is handled once");
            var notAShortcut = Path.Combine(folder, "gone.lnk");
            File.WriteAllText(notAShortcut, "not a shortcut");
            Check(Intake.Resolve(notAShortcut) == notAShortcut, "a file that only looks like a shortcut stays");
        });

        Test("Core", "Intake follows links, at most eight hops, and keeps a dangling one", () =>
        {
            // Junctions need no privilege, unlike symbolic links, and are followed the same way.
            var folder = Path.Combine(Fixtures.Root, "_links");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "Target");
            Directory.CreateDirectory(target);
            void Junction(string link, string to) =>
                ProcessRunner.RunChecked("cmd.exe", ["/c", "mklink", "/J", link, to], "Creating a test link");
            var previous = target;
            var chain = new List<string>();
            for (var hop = 1; hop <= Intake.MaxHops + 1; hop++)
            {
                var next = Path.Combine(folder, $"hop{hop}");
                Junction(next, previous);
                chain.Add(next);
                previous = next;
            }
            Check(Intake.Resolve(chain[0]) == target, $"a link → {Intake.Resolve(chain[0])}");
            Check(Intake.Resolve(chain[Intake.MaxHops - 1]) == target, "eight hops reach the target");
            Check(Intake.Resolve(chain[Intake.MaxHops]) == chain[0], $"and no more: {Path.GetFileName(Intake.Resolve(chain[Intake.MaxHops]))}");
            var gone = Path.Combine(folder, "gone");
            Directory.CreateDirectory(gone);
            var dangling = Path.Combine(folder, "dangling");
            Junction(dangling, gone);
            Directory.Delete(gone);
            Check(Intake.Resolve(dangling) == dangling, "a dangling link stays, so the missing file is explained");
            foreach (var link in chain.Append(dangling)) Directory.Delete(link);
        });

        Test("Core", "Text in older encodings is read as text", () =>
        {
            const string text = "café, São Paulo — ação";
            Check(TextDecoding.Decode(Encoding.UTF8.GetBytes(text)).Text == text, "UTF-8");
            Check(TextDecoding.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)]).Text == text, "UTF-8 with a mark");
            Check(TextDecoding.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]).Text == text, "UTF-16 with a mark");
            Check(TextDecoding.Decode(Encoding.Unicode.GetBytes("plain text of the sort Notepad writes")).Text == "plain text of the sort Notepad writes", "UTF-16 without a mark");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Check(TextDecoding.Decode(Encoding.GetEncoding(1252).GetBytes("café, ação")).Text == "café, ação", "Windows-1252");
            Check(TextDecoding.Decode(Encoding.GetEncoding(10000).GetBytes("café, ação")).Text == "café, ação", "the old Apple encoding");
            var (data, wasUtf8) = TextDecoding.Utf8(Encoding.GetEncoding(1252).GetBytes("é"));
            Check(!wasUtf8 && Encoding.UTF8.GetString(data) == "é", "other encodings are rewritten as UTF-8");
        });

        Test("Core", "Jobs run in the background, list every file, explain failures and keep the originals", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_jobs");
            Directory.CreateDirectory(folder);
            string[] paths = [Path.Combine(folder, "a.txt"), Path.Combine(folder, "b.txt"), Path.Combine(folder, "c.txt")];
            foreach (var path in paths) File.WriteAllText(path, "x");
            JobRunner.Shared.Each("Testing…", (L("1 file converted"), L("%@ files converted")), paths, 2, path =>
            {
                JobProgress.Report(0.5);
                if (path.EndsWith("b.txt")) throw new IOException("busy", unchecked((int)0x80070020));
                Fidelity.Note("a note");
                Fidelity.Note("a note");
                return [path + ".out"];
            });
            ActivityEntry? entry = null;
            for (var wait = 0; wait < 200 && entry?.State.IsActive() != false; wait++)
            {
                Thread.Sleep(50);
                entry = ActivityLog.Shared.Entries.FirstOrDefault(e => e.Title == "Testing");
            }
            Require(entry is not null && !entry.State.IsActive(), "the job didn't finish");
            Check(entry!.State == ActivityState.Partial && entry.Summary == "2 of 3 files converted", $"{entry.State}: {entry.Summary}");
            Check(entry.Failures.Count == 1 && entry.Failures[0].File == "b.txt" && entry.Failures[0].Error.Message.Contains("open in another app"), "the failure is explained");
            Check(entry.Notes.SequenceEqual(["a note"]), $"notes are kept once: {string.Join("|", entry.Notes)}");
            Check(entry.Outputs.Count == 2, "the results of the files that worked");
            Check(ActivityLog.Undoable(entry).Count == 0, "results that were never written can't be undone");

            // Undo moves only results, never a source.
            var made = Path.Combine(folder, "made.txt");
            File.WriteAllText(made, "x");
            var recycleBin = Path.Combine(folder, "bin");
            Directory.CreateDirectory(recycleBin);
            var previous = ActivityLog.MoveToRecycleBin;
            ActivityLog.MoveToRecycleBin = path => File.Move(path, Path.Combine(recycleBin, Path.GetFileName(path)));
            try
            {
                var id = ActivityLog.Shared.Add("Undo test", [paths[0]], new CancelToken());
                ActivityLog.Shared.Finish(id, ActivityState.Succeeded, "done", [made, paths[0]]);
                var finished = ActivityLog.Shared.Entry(id)!;
                Check(ActivityLog.Undoable(finished).SequenceEqual([made]), "undo never touches a source file");
                Check(ActivityLog.Shared.Undo(id).Count == 0 && !File.Exists(made) && File.Exists(Path.Combine(recycleBin, "made.txt")), "the result went to the bin");
                Check(ActivityLog.Shared.Entry(id)!.Undone && ActivityLog.Undoable(ActivityLog.Shared.Entry(id)!).Count == 0, "a job is undone once");
                var queued = ActivityLog.Shared.Add("Queued", [], new CancelToken());
                ActivityLog.Shared.Cancel(queued);
                Check(ActivityLog.Shared.Entry(queued)!.State == ActivityState.Cancelled, "a queued job is cancelled before it starts");
            }
            finally { ActivityLog.MoveToRecycleBin = previous; }
            Check(paths.All(File.Exists), "the originals are untouched");
        });
    }
}
