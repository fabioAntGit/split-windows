using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Split.Tests;

// A small test harness: no framework, so every suite registers itself with Test() and the
// runner (Program.cs) executes them with the same output and exit code everywhere.

/// <summary>Stops the test: raised by Require when later steps depend on the failed one.</summary>
public sealed class TestFailure(string message) : Exception(message);

/// <summary>Stops the test and marks it skipped (a missing sample or tool), never passed.</summary>
public sealed class TestSkipped(string reason) : Exception(reason);

public sealed record TestCase(string Suite, string Name, Action Body);

public static class Harness
{
    public static readonly List<TestCase> Tests = [];
    public static int Checks;
    public static List<string> Failures = [];

    public static void Test(string suite, string name, Action body) => Tests.Add(new TestCase(suite, name, body));

    /// <summary>Records a failure but lets the test continue, so one run shows every problem.</summary>
    public static void Check(bool condition, string message, [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        Checks++;
        if (!condition) Failures.Add($"{message}  [{Path.GetFileName(file)}:{line}]");
    }

    /// <summary>Stops the test: later steps depend on this one.</summary>
    public static void Require(bool condition, string message, [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        Checks++;
        if (!condition) throw new TestFailure($"{message}  [{Path.GetFileName(file)}:{line}]");
    }

    public static bool Near(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

    /// <summary>Stops the test without failing it; the runner counts and lists it apart.</summary>
    public static void Skip(string reason) => throw new TestSkipped(reason);

    /// <summary>The body must throw an explained error mentioning every fragment (case-insensitive).</summary>
    public static Problem? ExpectError(string label, string[] contains, Action body, [CallerLineNumber] int line = 0)
    {
        Checks++;
        try
        {
            body();
            Failures.Add($"{label}: should have failed with an explained error, but it worked  [line {line}]");
            return null;
        }
        catch (Exception error) when (error is not TestFailure and not TestSkipped)
        {
            var explained = Explain.Error(error);
            var text = (explained.FullText + " " + (explained.Technical ?? "")).ToLowerInvariant();
            var missing = contains.Where(fragment => !text.Contains(fragment.ToLowerInvariant())).ToList();
            if (missing.Count > 0)
                Failures.Add($"{label}: the message doesn’t mention [{string.Join(", ", missing)}] → “{explained.FullText}”  [line {line}]");
            ErrorLog.Seen.Add(explained);
            return explained;
        }
    }
}

public static class ErrorLog
{
    public static readonly List<Problem> Seen = [];
}

/// <summary>Reading back what the tests wrote.</summary>
public static class Files
{
    public static string Sha(string path)
    {
        if (Directory.Exists(path))
        {
            var items = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .Select(item => Path.GetRelativePath(path, item)).Order(StringComparer.Ordinal);
            return string.Concat(items.Select(item => item + Sha(Path.Combine(path, item))));
        }
        return File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "-";
    }

    public static string Text(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    public static List<string> List(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).Where(item => !Path.GetFileName(item).StartsWith('.'))
            .Order(StringComparer.Ordinal).ToList();
}

/// <summary>Where the project's own files are, for the checks that read the code itself.</summary>
public static partial class Hostile
{
    public static string Project { get; set; } = "";

    /// <summary>Every C# file of the app, in all its folders.</summary>
    public static List<string> Sources() =>
        Directory.EnumerateFiles(Path.Combine(Project, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Order(StringComparer.Ordinal).ToList();
}
