using System.Collections.Concurrent;

namespace Split;

/// <summary>
/// Runs work off the interface thread, several files at a time when that's safe. Every job is
/// listed in the activity log from the moment it is asked for, and reported through the HUD.
/// </summary>
public sealed class JobRunner
{
    public static JobRunner Shared { get; } = new();

    /// <summary>One job at a time; files inside a job may run in parallel.</summary>
    private readonly BlockingCollection<Action> queue = [];

    private JobRunner() => new Thread(() => { foreach (var job in queue.GetConsumingEnumerable()) job(); })
    {
        IsBackground = true,
        Name = "split.jobs",
    }.Start();

    public void Convert(IReadOnlyList<string> paths, Format target)
    {
        var sources = paths.Select(Formats.Detect).OfType<Format>().ToHashSet();
        Each(L("Converting to %@…", target.Label()), (L("1 file converted"), L("%@ files converted")), paths,
             Parallelism(sources, target), path => Conversions.Convert(path, target));
    }

    public void Tool(QuickTool tool, IReadOnlyList<string> paths, long? targetBytes = null)
    {
        if (tool.IsBatch() || tool == QuickTool.ReadQr)
        {
            Single(tool.Title() + "…", paths.Count == 1 ? Path.GetFileName(paths[0]) : L("%@ files", paths.Count), paths, () =>
            {
                var result = tool.Run(paths);
                if (tool != QuickTool.ReadQr || result.Message is not { } text) return result.Outputs;
                Platform.Current.SetClipboardText(text);
                Hud.Current.ShowMessage(L("Code text copied"), text);
                return null;
            });
            return;
        }
        var sources = paths.Select(Formats.Detect).OfType<Format>().ToHashSet();
        Each(tool.Title() + "…", (L("1 file processed"), L("%@ files processed")), paths,
             Parallelism(sources, null), path => tool.Run([path], targetBytes).Outputs);
    }

    /// <summary>
    /// One unit of work with one result (editors saving, batch tools).
    /// Returning null means the work already reported its own outcome.
    /// </summary>
    public void Single(string title, string item, IReadOnlyList<string> files, Func<IReadOnlyList<string>?> work)
    {
        var (id, token) = Enqueue(title, files);
        queue.Add(() =>
        {
            if (!Begin(id, token, title, item, 1)) return;
            var name = Plain(title);
            try
            {
                foreach (var file in files) FileCheck.Preflight(file, allowFolders: true);
                var (outputs, notes) = JobProgress.Run(fraction =>
                {
                    Hud.Current.UpdateProgress(fraction);
                    ActivityLog.Shared.Progress(id, fraction);
                }, token, () => Fidelity.Collect(work));
                Platform.Current.QuitLaunchedOfficeApps();
                var message = L("%@ — done", name);
                ActivityLog.Shared.Finish(id, ActivityState.Succeeded, message, outputs, notes: notes);
                if (outputs is not null) Hud.Current.ShowDone(outputs, message, notes: notes, jobId: id);
            }
            catch (Exception error)
            {
                Platform.Current.QuitLaunchedOfficeApps();
                var explained = Explain.Error(error, files.Count == 1 ? files[0] : null);
                var failure = new JobFailure(files.Count == 1 ? Path.GetFileName(files[0]) : "", explained);
                if (explained.IsCancellation)
                {
                    ActivityLog.Shared.Finish(id, ActivityState.Cancelled, L("%@ — canceled", name));
                    Hud.Current.ShowMessage(L("Canceled"), name);
                    return;
                }
                var message = L("Couldn’t finish: %@", name);
                ActivityLog.Shared.Finish(id, ActivityState.Failed, message, failures: [failure]);
                Hud.Current.ShowDone([], message, [failure]);
            }
        });
    }

    /// <summary>
    /// Images and documents are CPU-light per file: run a few at once. ffmpeg already uses
    /// every core, and Office apps can only handle one document at a time.
    /// </summary>
    private static int Parallelism(HashSet<Format> sources, Format? target)
    {
        var usesOfficeApps = sources.Any(f => f.Group() is FormatGroup.Presentation or FormatGroup.Spreadsheet
                                              || f is Format.Pages or Format.Doc or Format.Docx or Format.Odt)
            || target == Format.Pages;
        if (usesOfficeApps) return 1;
        var media = sources.Any(f => f.Group() is FormatGroup.Audio or FormatGroup.Video) || target?.Group() == FormatGroup.Video;
        return media ? 2 : Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
    }

    /// <summary>"1 file converted" / "3 files converted": <paramref name="verb"/> holds both translated forms, the second with a placeholder for the count (or for "2 of 3").</summary>
    public static string Phrase(int count, (string One, string Many) verb) => count == 1 ? verb.One : Fill(verb.Many, [count]);

    /// <summary>Per-file work with a shared progress banner and an explained list of failures.</summary>
    public void Each(string title, (string One, string Many) verb, IReadOnlyList<string> paths, int width, Func<string, IReadOnlyList<string>> work)
    {
        var (id, token) = Enqueue(title, paths);
        queue.Add(() =>
        {
            if (!Begin(id, token, title, paths.Count > 0 ? Path.GetFileName(paths[0]) : "", paths.Count)) return;
            var gate = new object();
            var outputs = new IReadOnlyList<string>[paths.Count];
            Array.Fill(outputs, []);
            var notes = new List<string>();
            var failures = new Dictionary<int, JobFailure>();
            var fractions = new double[paths.Count];
            int finished = 0, skipped = 0, cursor = 0;
            var lastUpdate = DateTime.MinValue;

            // Called with the lock held.
            void Publish(string? current)
            {
                var overall = fractions.Sum() / Math.Max(paths.Count, 1);
                var now = DateTime.UtcNow;
                if ((now - lastUpdate).TotalSeconds <= 0.1 && current is null) return;
                lastUpdate = now;
                var detail = paths.Count > 1 ? L("%@ of %@ done", finished, paths.Count) + (current is null ? "" : " · " + current) : current;
                Hud.Current.UpdateProgress(overall, detail);
                ActivityLog.Shared.Progress(id, overall, detail);
            }

            void Worker()
            {
                while (true)
                {
                    int index;
                    lock (gate) index = cursor++;
                    if (index >= paths.Count) return;
                    // Cancelling stops new files from starting; the ones running stop at their next check.
                    if (token.IsCancelled)
                    {
                        lock (gate) skipped++;
                        continue;
                    }
                    var path = paths[index];
                    lock (gate) Publish(Path.GetFileName(path));
                    try
                    {
                        var (result, fileNotes) = JobProgress.Run(fraction =>
                        {
                            lock (gate)
                            {
                                fractions[index] = fraction;
                                Publish(null);
                            }
                        }, token, () => Fidelity.Collect(() => work(path)));
                        lock (gate)
                        {
                            outputs[index] = result;
                            foreach (var note in fileNotes.Where(note => !notes.Contains(note))) notes.Add(note);
                        }
                    }
                    catch (Exception error)
                    {
                        var explained = Explain.Error(error, path);
                        lock (gate)
                        {
                            if (explained.IsCancellation) skipped++;
                            else failures[index] = new JobFailure(Path.GetFileName(path), explained);
                        }
                    }
                    lock (gate)
                    {
                        fractions[index] = 1;
                        finished++;
                        Publish(null);
                    }
                }
            }

            // Real threads: the files must overlap, which a parallel loop doesn't promise.
            Task.WaitAll(Enumerable.Range(0, Math.Min(width, paths.Count)).Select(_ => Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning)).ToArray());
            Platform.Current.QuitLaunchedOfficeApps();

            var ordered = failures.Keys.Order().Select(key => failures[key]).ToList();
            var produced = outputs.SelectMany(o => o).ToList();
            var ok = paths.Count - ordered.Count - skipped;
            if (skipped > 0)
            {
                var canceled = ok == 0 ? L("Canceled") : L("Canceled after %@", Phrase(ok, verb));
                ActivityLog.Shared.Finish(id, ActivityState.Cancelled, canceled, produced, ordered);
                Hud.Current.ShowMessage(canceled, Plain(title));
                return;
            }
            string message;
            if (ordered.Count == 0) message = Phrase(ok, verb);
            else if (ok == 0) message = paths.Count == 1 ? L("Couldn’t process “%@”", Path.GetFileName(paths[0])) : L("No file was processed");
            else message = Fill(verb.Many, [L("%@ of %@", ok, paths.Count)]);
            var state = ordered.Count == 0 ? ActivityState.Succeeded : ok == 0 ? ActivityState.Failed : ActivityState.Partial;
            ActivityLog.Shared.Finish(id, state, message, produced, ordered, notes);
            Hud.Current.ShowDone(produced, message, ordered, paths.Count, notes, id);
        });
    }

    // MARK: Activity bookkeeping

    /// <summary>Lists the job as waiting straight away, so it is visible even before it starts.</summary>
    private static (Guid Id, CancelToken Token) Enqueue(string title, IReadOnlyList<string> files)
    {
        var token = new CancelToken();
        return (ActivityLog.Shared.Add(Plain(title), files, token), token);
    }

    /// <summary>Marks the job as running, unless it was cancelled while it waited.</summary>
    private static bool Begin(Guid id, CancelToken token, string title, string item, int count)
    {
        if (token.IsCancelled) return false;
        ActivityLog.Shared.Start(id);
        Hud.Current.ShowProgress(title, item, 0, count, id);
        return true;
    }

    /// <summary>"Converting to MP4…" → "Converting to MP4".</summary>
    public static string Plain(string title) => title.EndsWith('…') ? title[..^1] : title;
}
