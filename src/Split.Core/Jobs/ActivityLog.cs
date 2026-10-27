namespace Split;

/// <summary>Set by the user from the Activity tab; read by the job's thread between steps.</summary>
public sealed class CancelToken
{
    private volatile bool cancelled;

    public bool IsCancelled => cancelled;

    public void Cancel() => cancelled = true;
}

public enum ActivityState { Queued, Running, Succeeded, Partial, Failed, Cancelled }

public static class ActivityStates
{
    public static bool IsActive(this ActivityState state) => state is ActivityState.Queued or ActivityState.Running;
}

/// <summary>One job in the activity list. A snapshot: every change makes a new one.</summary>
public sealed record ActivityEntry(Guid Id, string Title, IReadOnlyList<string> Files, DateTime QueuedAt, CancelToken Token)
{
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public ActivityState State { get; init; } = ActivityState.Queued;
    public double? Fraction { get; init; }
    public string Detail { get; init; } = "";
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Outputs { get; init; } = [];
    public IReadOnlyList<JobFailure> Failures { get; init; } = [];
    /// <summary>What the target formats could not keep (see <see cref="Fidelity"/>).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool CancelRequested { get; init; }
    /// <summary>The results were moved to the Recycle Bin with "Undo".</summary>
    public bool Undone { get; init; }
}

/// <summary>
/// Everything the app was asked to do since it opened: what is waiting, what is running (with
/// progress) and how each job ended. Notifications come and go; this list stays, in
/// Settings › Activity. Jobs report from their own threads, so every change is made under a lock
/// and announced by <see cref="Changed"/> for the interface to pick up on its own thread.
/// </summary>
public sealed class ActivityLog
{
    public static ActivityLog Shared { get; } = new();

    public const int HistoryLimit = 100;

    /// <summary>How "Undo" disposes of a result; the tests swap it so they never fill the real Recycle Bin.</summary>
    public static Action<string> MoveToRecycleBin { get; set; } = path => Platform.Current.MoveToRecycleBin(path);

    public event Action? Changed;

    private readonly object gate = new();
    private List<ActivityEntry> entries = [];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ActivityEntry> Entries
    {
        get { lock (gate) return [.. entries]; }
    }

    public IReadOnlyList<ActivityEntry> Active => Entries.Where(e => e.State.IsActive()).ToList();

    public IReadOnlyList<ActivityEntry> Finished => Entries.Where(e => !e.State.IsActive()).ToList();

    public Guid Add(string title, IReadOnlyList<string> files, CancelToken token, Guid? id = null)
    {
        var entry = new ActivityEntry(id ?? Guid.NewGuid(), title, files, DateTime.Now, token);
        lock (gate)
        {
            entries.Insert(0, entry);
            Trim();
        }
        Changed?.Invoke();
        return entry.Id;
    }

    public void Start(Guid id) => Update(id, e => e with { State = ActivityState.Running, StartedAt = DateTime.Now, Fraction = 0 });

    public void Progress(Guid id, double fraction, string? detail = null) =>
        Update(id, e => e.State != ActivityState.Running ? e : e with { Fraction = fraction, Detail = detail ?? e.Detail });

    public void Finish(Guid id, ActivityState state, string summary, IReadOnlyList<string>? outputs = null,
                       IReadOnlyList<JobFailure>? failures = null, IReadOnlyList<string>? notes = null) =>
        Update(id, e => e with
        {
            State = state, FinishedAt = DateTime.Now, Fraction = null, Detail = "", Summary = summary,
            Outputs = outputs ?? [], Failures = failures ?? [], Notes = notes ?? [],
        });

    /// <summary>
    /// Queued jobs stop before they start; running ones stop at the next safe point
    /// (the engine process is ended and its partial output removed).
    /// </summary>
    public void Cancel(Guid id)
    {
        if (Entry(id) is not { } entry || !entry.State.IsActive()) return;
        entry.Token.Cancel();
        Update(id, e => e with { CancelRequested = true });
        if (entry.State == ActivityState.Queued) Finish(id, ActivityState.Cancelled, L("Canceled before it started."));
    }

    /// <summary>What "Undo" would move to the Recycle Bin: the job's results that are still there, never one of the files the job started from (nor a folder holding one).</summary>
    public static IReadOnlyList<string> Undoable(ActivityEntry entry)
    {
        if (entry.Undone || entry.State.IsActive()) return [];
        return entry.Outputs.Where(output =>
        {
            var exists = File.Exists(output) || Directory.Exists(output);
            return exists && !entry.Files.Any(source => Paths.IsInside(source, output));
        }).ToList();
    }

    /// <summary>Takes a job back by moving its results to the Recycle Bin, where "Restore" can still bring them back. Returns what could not be moved.</summary>
    public List<JobFailure> Undo(Guid id)
    {
        var failures = new List<JobFailure>();
        if (Entry(id) is not { } entry) return failures;
        foreach (var path in Undoable(entry))
        {
            try { MoveToRecycleBin(path); }
            catch (Exception e) { failures.Add(new JobFailure(Path.GetFileName(path), Explain.Error(e, path))); }
        }
        if (failures.Count == 0) Update(id, e => e with { Undone = true });
        return failures;
    }

    public void ClearFinished()
    {
        lock (gate) entries = entries.Where(e => e.State.IsActive()).ToList();
        Changed?.Invoke();
    }

    public ActivityEntry? Entry(Guid id)
    {
        lock (gate) return entries.FirstOrDefault(e => e.Id == id);
    }

    private void Update(Guid id, Func<ActivityEntry, ActivityEntry> change)
    {
        lock (gate)
        {
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            entries[index] = change(entries[index]);
        }
        Changed?.Invoke();
    }

    /// <summary>Called with the lock held.</summary>
    private void Trim()
    {
        var finished = entries.Where(e => !e.State.IsActive()).Skip(HistoryLimit).ToHashSet();
        if (finished.Count > 0) entries = entries.Where(e => !finished.Contains(e)).ToList();
    }
}
