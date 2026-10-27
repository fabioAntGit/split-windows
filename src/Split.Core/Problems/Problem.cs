namespace Split;

/// <summary>
/// An error the user can act on: what failed and why (<see cref="Exception.Message"/>), what to do
/// next (<see cref="Suggestion"/>) and the raw tool output for the "Details" view (<see cref="Technical"/>).
/// </summary>
public class Problem(string message, string? suggestion = null, string? technical = null) : Exception(message)
{
    public string? Suggestion { get; } = suggestion;
    public string? Technical { get; } = technical;

    /// <summary>The user stopped the job: not a failure, so no error sound and no "Details…".</summary>
    public bool IsCancellation { get; private init; }

    public static Problem Cancelled => new(L("Canceled."), L("Nothing was saved for this file.")) { IsCancellation = true };

    /// <summary>The same problem with another suggestion.</summary>
    public Problem WithSuggestion(string suggestion) => new(Message, suggestion, Technical) { IsCancellation = IsCancellation };

    /// <summary>Message and suggestion as one paragraph (command line and compact views).</summary>
    public string FullText => string.IsNullOrEmpty(Suggestion) ? Message : Message + " " + Suggestion;
}

/// <summary>A file that failed, with the explanation shown in "Details…" and in the activity list.</summary>
public sealed record JobFailure(string File, Problem Error);
