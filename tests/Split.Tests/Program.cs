using System.IO;
using System.Diagnostics;
using Split.Tests;
using static Split.Tests.Harness;

// Test runner: `test.ps1 [filter]` runs every suite (or those whose "suite name" contains the filter).
Console.OutputEncoding = System.Text.Encoding.UTF8;

var filter = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();

// Samples and outputs live under build/test-run, found from the solution file upwards.
var repository = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(repository, "Split.sln")))
    repository = Path.GetDirectoryName(repository) ?? throw new InvalidOperationException("Split.sln not found above the test binary");
var root = Path.Combine(repository, "build", "test-run");
Directory.CreateDirectory(root);

// Sample generation, the suite registrations and the Integrity checks slot in here.

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
    catch (Exception e) { Failures.Add($"stopped: {e.Message}"); }
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