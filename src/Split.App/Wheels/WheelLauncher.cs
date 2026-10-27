namespace Split.App;

/// <summary>What the wheel window needs to open: the slices for these files, and how it was asked for.</summary>
public sealed record WheelRequest(WheelKind Kind, IReadOnlyList<WheelItem> Items, string Title, string Subtitle,
                                  IReadOnlyList<string> Paths, bool ClickMode);

/// <summary>
/// Where every wheel starts: from the tray's file chooser, "Open with", the shelf and (while dragging) the
/// drag monitor. It decides what the wheel offers, or explains why there is nothing to offer; showing the
/// wheel itself is done by <see cref="Present"/>, which the wheel window sets.
/// </summary>
public static class WheelLauncher
{
    /// <summary>Opens a wheel for the request. Until the wheel window exists it says so.</summary>
    public static Action<WheelRequest> Present { get; set; } = _ => Hud.Current.ShowNotice(Pending.Error());

    public static void Show(WheelKind kind, IReadOnlyList<string> paths, bool clickMode)
    {
        paths = Intake.Resolve(paths);
        var items = WheelCatalog.Items(kind, paths);
        if (items.Count == 0)
        {
            // Say why there's nothing to choose, instead of an empty wheel.
            var allHidden = WheelCatalog.Offered(kind, paths).Count > 0;
            Hud.Current.ShowNotice(allHidden ? Explain.AllHidden(kind.Title()) : Explain.NoTargets(paths, tools: kind == WheelKind.Tools));
            return;
        }
        var subtitle = paths.Count == 1 ? Path.GetFileName(paths[0]) : L("%@ files", paths.Count);
        Present(new WheelRequest(kind, items, kind.Title(), subtitle, paths, clickMode));
    }

    /// <summary>"Open with Split" and files sent to a running Split: the conversions if there are any, otherwise the tools.</summary>
    public static void Open(IReadOnlyList<string> paths)
    {
        paths = Intake.Resolve(paths);
        Show(WheelCatalog.Items(WheelKind.Convert, paths).Count == 0 ? WheelKind.Tools : WheelKind.Convert, paths, clickMode: true);
    }
}

/// <summary>The windows that more than one place opens. The code that builds each one sets its entry here; until then they say so.</summary>
public static class AppWindows
{
    private static void NotYet() => Hud.Current.ShowNotice(Pending.Error());

    public static Action ShowSettings { get; set; } = NotYet;

    public static Action ShowActivity { get; set; } = NotYet;

    public static Action ShowHelp { get; set; } = NotYet;

    public static Action ShowAbout { get; set; } = NotYet;
}
