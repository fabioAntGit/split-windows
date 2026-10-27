namespace Split;

/// <summary>The text pieces of the activity rows (Settings › Activity), kept apart from the views so they can be checked.</summary>
public static class ActivityFormat
{
    public static string Files(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return L("No files");
        var first = Path.GetFileName(paths[0]);
        return paths.Count == 1 ? first : L("%@ and %@ more", first, (paths.Count - 1).ToString());
    }

    public static string Status(ActivityEntry entry, DateTime now) => entry.State switch
    {
        ActivityState.Queued => L("Waiting"),
        ActivityState.Running when entry.CancelRequested => L("Canceling…"),
        ActivityState.Running => L("%@ · %@", $"{(int)Math.Round((entry.Fraction ?? 0) * 100)}%", Duration((now - (entry.StartedAt ?? now)).TotalSeconds)),
        _ => StateName(entry.State),
    };

    public static string StateName(ActivityState state) => state switch
    {
        ActivityState.Queued => L("Waiting"),
        ActivityState.Running => L("Running"),
        ActivityState.Succeeded => L("Done"),
        ActivityState.Partial => L("Done with problems"),
        ActivityState.Failed => L("Failed"),
        _ => L("Canceled"),
    };

    /// <summary>"01:05", or "1:05" (hours and minutes) from an hour on.</summary>
    public static string Duration(double seconds)
    {
        var total = (long)Math.Max(0, seconds);
        return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}" : $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>"3 minutes ago · took 00:12".</summary>
    public static string When(ActivityEntry entry, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var text = Ago(current - (entry.FinishedAt ?? current));
        if (entry.StartedAt is { } start) text += " · " + L("took %@", Duration(((entry.FinishedAt ?? current) - start).TotalSeconds));
        return text;
    }

    private static string Ago(TimeSpan span)
    {
        var seconds = (long)Math.Max(0, span.TotalSeconds);
        var (n, minutes, hours, days) = (seconds, seconds / 60, seconds / 3600, seconds / 86400);
        return seconds switch
        {
            < 5 => L("now"),
            < 60 => L("%@ seconds ago", n.ToString()),
            < 120 => L("1 minute ago"),
            < 3600 => L("%@ minutes ago", minutes.ToString()),
            < 7200 => L("1 hour ago"),
            < 86400 => L("%@ hours ago", hours.ToString()),
            < 172800 => L("1 day ago"),
            _ => L("%@ days ago", days.ToString()),
        };
    }
}
