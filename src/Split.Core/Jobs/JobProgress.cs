namespace Split;

/// <summary>
/// Progress and cancellation of the job running in the current flow of work. Engines report
/// progress and check for cancellation without knowing which job (or test) runs them.
/// </summary>
public static class JobProgress
{
    private sealed record Context(Action<double> Report, CancelToken? Token);

    private static readonly AsyncLocal<Context?> Current = new();

    public static T Run<T>(Action<double> handler, CancelToken? cancel, Func<T> body)
    {
        var outer = Current.Value;
        Current.Value = new Context(handler, cancel);
        try { return body(); }
        finally { Current.Value = outer; }
    }

    public static void Report(double fraction) => Current.Value?.Report(Math.Min(Math.Max(fraction, 0), 1));

    public static bool IsCancelled => Current.Value?.Token?.IsCancelled ?? false;

    /// <summary>Long in-process work (page loops, frame loops) calls this between steps.</summary>
    public static void CheckCancelled()
    {
        if (IsCancelled) throw Problem.Cancelled;
    }
}

/// <summary>What a conversion could not carry over, told to the user instead of dropped in silence.</summary>
/// <remarks>
/// Converters call <see cref="Note"/> while they work, in the flow of work running the job; the job
/// runner collects the notes of each file and shows them with the result and in the activity list.
/// </remarks>
public static class Fidelity
{
    private static readonly AsyncLocal<List<string>?> Notes = new();

    public static (T Result, List<string> Notes) Collect<T>(Func<T> body)
    {
        var outer = Notes.Value;
        var mine = new List<string>();
        Notes.Value = mine;
        try { return (body(), mine); }
        finally { Notes.Value = outer; }
    }

    /// <summary>Records one limitation of the target format, once per file.</summary>
    public static void Note(string text)
    {
        if (Notes.Value is { } list && !list.Contains(text)) list.Add(text);
    }
}
