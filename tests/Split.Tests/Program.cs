using System.IO;
using System.Diagnostics;
using Split;
using Split.Tests;
using Split.Windows;
using static Split.Tests.Harness;

// Test runner: `test.ps1 [filter]` runs every suite (or those whose "suite name" contains the filter).
Console.OutputEncoding = System.Text.Encoding.UTF8;

var filter = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();

// Samples and outputs live under build/test-run, found from the solution file upwards.
var repository = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(repository, "Split.sln")))
    repository = Path.GetDirectoryName(repository) ?? throw new InvalidOperationException("Split.sln not found above the test binary");
Hostile.Project = repository;
var root = Path.Combine(repository, "build", "test-run");
if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
Directory.CreateDirectory(root);

// Before anything reads the settings or the language: the tests never touch the user's own.
Settings.StorePath = Path.Combine(root, "settings.json");
// The translation tables come from the source tree; the messages the tests read are English.
Localizer.TablesDirectory = Path.Combine(repository, "Resources", "Localization");
Platform.Current = new WindowsPlatform();
// The picture library takes its security policy before any test reads or writes a picture.
Imaging.Initialize();
// Office apps stay out of the default run.
Settings.UseOfficeApps = false;
Settings.KeepsMetadata = true;
Settings.PlaySounds = false;
Hud.Current = new HeadlessHud();
if (Localizer.Shared.Language != Language.En) Localizer.Shared.Language = Language.En;

Console.WriteLine($"Generating samples in {root}…");
try
{
    Fixtures.Create(root, Path.Combine(repository, "tests", "Fixtures"));
}
catch (Exception e)
{
    Console.WriteLine($"✗ Couldn’t generate the samples: {Explain.Error(e).FullText}");
    return 2;
}
var originals = Files.List(root).Where(item => !Path.GetFileName(item).StartsWith('_') && Path.GetFileName(item) != "settings.json")
    .ToDictionary(item => item, Files.Sha);

LanguageTests.Register();
WheelTests.Register();
ErrorTests.Register();
CoreTests.Register();
UiTests.Register();
WheelLayoutTests.Register();
WheelUiTests.Register();
ConversionTests.Register();
FidelityTests.Register();
DataTests.Register();
SecurityTests.Register();
SettingsUiTests.Register();
DocumentTests.Register();
SecurityAudit.Register();

Test("Integrity", "Originals are never changed", () =>
{
    foreach (var (path, hash) in originals) Check(Files.Sha(path) == hash, $"{Path.GetFileName(path)} was changed");
});

Test("Integrity", "No temporary files are left behind", () =>
{
    var leftovers = Directory.EnumerateFileSystemEntries(root, ".split-*", SearchOption.AllDirectories).ToList();
    Check(leftovers.Count == 0, $"forgotten temporary files: {string.Join(", ", leftovers.Take(5))}");
    var scratch = Directory.EnumerateFileSystemEntries(Path.GetTempPath(), "split-*").ToList();
    Check(scratch.Count == 0, $"forgotten work folders in {Path.GetTempPath()}: {string.Join(", ", scratch.Take(5))}");
});

var passed = 0;
var failed = new List<(string Name, List<string> Failures)>();
var skipped = new List<(string Name, string Reason)>();
var clock = Stopwatch.StartNew();
foreach (var test in Tests.Where(t => filter == null || $"{t.Suite} {t.Name}".ToLowerInvariant().Contains(filter)))
{
    Failures = [];
    var title = $"{test.Suite} › {test.Name}";
    var begin = Stopwatch.StartNew();
    var skip = (string?)null;
    try { test.Body(); }
    catch (TestSkipped s) { skip = s.Message; }
    catch (Exception e) { Failures.Add($"stopped: {Explain.Error(e).FullText}"); }
    var time = $"{begin.Elapsed.TotalSeconds:0.0} s";
    if (skip != null)
    {
        skipped.Add((title, skip));
        Console.WriteLine($"○ {title} (skipped: {skip})");
    }
    else if (Failures.Count == 0)
    {
        passed++;
        Console.WriteLine($"✓ {title} ({time})");
    }
    else
    {
        failed.Add((title, Failures));
        Console.WriteLine($"✗ {title} ({time})");
        foreach (var f in Failures) Console.WriteLine($"    · {f}");
    }
}

if (skipped.Count > 0)
{
    Console.WriteLine("\nSkipped:");
    foreach (var (name, reason) in skipped) Console.WriteLine($"  ○ {name}: {reason}");
}
Console.WriteLine($"\n{passed} tests passed, {failed.Count} failed, {skipped.Count} skipped, {Checks} checks in {(int)clock.Elapsed.TotalSeconds} s.");
return failed.Count == 0 ? 0 : 1;
