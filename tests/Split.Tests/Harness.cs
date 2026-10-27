using System.IO;
using System.Runtime.CompilerServices;

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

    // ExpectError arrives with the explained errors (Explain) in a later phase.
}