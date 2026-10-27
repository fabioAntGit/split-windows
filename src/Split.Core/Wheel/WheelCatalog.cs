namespace Split;

/// <summary>
/// The kinds of file the conversion wheel is arranged for. PDF has its own arrangement:
/// it turns into pictures and text, while the other documents turn into each other.
/// </summary>
public enum SourceKind { Image, Pdf, Document, Spreadsheet, Presentation, Audio, Video, Subtitle, Archive, Data }

public static class SourceKinds
{
    public static readonly IReadOnlyList<SourceKind> All = Enum.GetValues<SourceKind>();

    public static SourceKind Of(Format format) => format.Group() switch
    {
        FormatGroup.Image => SourceKind.Image,
        FormatGroup.Document => format == Format.Pdf ? SourceKind.Pdf : SourceKind.Document,
        FormatGroup.Spreadsheet => SourceKind.Spreadsheet,
        FormatGroup.Presentation => SourceKind.Presentation,
        FormatGroup.Audio => SourceKind.Audio,
        FormatGroup.Video => SourceKind.Video,
        FormatGroup.Subtitle => SourceKind.Subtitle,
        FormatGroup.Archive => SourceKind.Archive,
        _ => SourceKind.Data,
    };

    public static string Raw(this SourceKind kind) => kind.ToString().ToLowerInvariant();

    public static string Title(this SourceKind kind) => kind switch
    {
        SourceKind.Image => L("Images"),
        SourceKind.Pdf => "PDF",
        SourceKind.Document => L("Documents"),
        SourceKind.Spreadsheet => L("Spreadsheets"),
        SourceKind.Presentation => L("Presentations"),
        SourceKind.Audio => L("Audio"),
        SourceKind.Video => L("Video"),
        SourceKind.Subtitle => L("Subtitles"),
        SourceKind.Archive => L("Archives"),
        _ => L("Data"),
    };

    public static IEnumerable<Format> Formats(this SourceKind kind) => Split.Formats.All.Where(f => Of(f) == kind);
}

/// <summary>
/// Each wheel keeps one arrangement, and the conversion wheel one per kind of file, since an
/// image and a spreadsheet offer different formats.
/// </summary>
public sealed record WheelScope(WheelKind Kind, SourceKind? Source = null)
{
    public static WheelScope Convert(SourceKind source) => new(WheelKind.Convert, source);

    public static readonly WheelScope Tools = new(WheelKind.Tools);

    public static readonly WheelScope Actions = new(WheelKind.Actions);

    /// <summary>The arrangement used for these files; null when the conversion wheel can't place them.</summary>
    public static WheelScope? For(WheelKind kind, IReadOnlyList<string> paths) => kind switch
    {
        WheelKind.Convert => paths.Count > 0 && Formats.Detect(paths[0]) is { } format ? Convert(SourceKinds.Of(format)) : null,
        WheelKind.Tools => Tools,
        _ => Actions,
    };

    public string Key => Kind switch
    {
        WheelKind.Convert => "convert." + Source!.Value.Raw(),
        WheelKind.Tools => "tools",
        _ => "actions",
    };

    public WheelArrangement Arrangement
    {
        get => Settings.WheelArrangement(Key);
        set => Settings.SetWheelArrangement(value, Key);
    }
}

/// <summary>A slice as Settings lists it, before any file is chosen, with what it does.</summary>
public sealed record WheelChoice(string Id, string Title, string? Symbol, string Explanation);

/// <summary>
/// The groups the wheels start with: variants of one task behind a single slice, so a wheel
/// stays short. Each one can be renamed, changed or dissolved in Settings › Wheels.
/// </summary>
public enum StandardGroup
{
    Compress, Background, Pdf, Sound,
    /// <summary>On the video wheel: every audio format, so the video formats stay in view.</summary>
    Audio,
}

public static class StandardGroups
{
    public static readonly IReadOnlyList<StandardGroup> All = Enum.GetValues<StandardGroup>();

    public static string Raw(this StandardGroup group) => "group." + group.ToString().ToLowerInvariant();

    public static StandardGroup? FromRaw(string raw) => All.Cast<StandardGroup?>().FirstOrDefault(g => g!.Value.Raw() == raw);

    public static WheelScope Scope(this StandardGroup group) => group == StandardGroup.Audio ? WheelScope.Convert(SourceKind.Video) : WheelScope.Tools;

    public static string Title(this StandardGroup group) => group switch
    {
        StandardGroup.Compress => L("Compress"),
        StandardGroup.Background => L("Background"),
        StandardGroup.Pdf => "PDF",
        StandardGroup.Sound => L("Sound"),
        _ => L("Audio"),
    };

    /// <summary>The conversion wheel shows names only.</summary>
    public static string? Symbol(this StandardGroup group) => group switch
    {
        StandardGroup.Compress => QuickTool.Compress.Symbol(),
        StandardGroup.Background => EditorTool.Background.Symbol(),
        StandardGroup.Pdf => EditorTool.PdfPages.Symbol(),
        StandardGroup.Sound => "waveform",
        _ => null,
    };

    public static string[] Members(this StandardGroup group) => group switch
    {
        StandardGroup.Compress => [QuickTool.Compress.Raw(), QuickTool.CompressToSize.Raw(), QuickTool.Zip.Raw()],
        StandardGroup.Background => [QuickTool.RemoveBackground.Raw(), EditorTool.Background.Raw()],
        StandardGroup.Pdf => [EditorTool.PdfPages.Raw(), QuickTool.CombinePdf.Raw(), QuickTool.SplitPages.Raw()],
        StandardGroup.Sound => [QuickTool.Normalize.Raw(), QuickTool.Mono.Raw(), QuickTool.TrimSilence.Raw(), QuickTool.Mute.Raw()],
        _ => Formats.All.Where(f => f.Group() == FormatGroup.Audio).Select(f => f.Raw()).ToArray(),
    };
}

/// <summary>What each wheel offers, and in which order.</summary>
public static class WheelCatalog
{
    /// <summary>The slices for these files: in the user's order, without the hidden ones, and grouped.</summary>
    public static List<WheelItem> Items(WheelKind kind, IReadOnlyList<string> paths)
    {
        var offered = Offered(kind, paths);
        return WheelScope.For(kind, paths) is { } scope ? Arranged(offered, scope.Arrangement, DefaultGroups(scope)) : offered;
    }

    /// <summary>
    /// Lays <paramref name="offered"/> out as <paramref name="arrangement"/> says. A group shows as one slice only
    /// when it still holds two or more (otherwise its slice stands on its own), and a wheel that
    /// would hold nothing but one group shows that group's slices straight away.
    /// </summary>
    public static List<WheelItem> Arranged(IReadOnlyList<WheelItem> offered, WheelArrangement arrangement, IReadOnlyList<WheelGroup> defaultGroups)
    {
        var byId = new Dictionary<string, WheelItem>();
        foreach (var item in offered) byId.TryAdd(item.Id, item);
        var names = new Dictionary<string, string>();
        foreach (var group in arrangement.Groups ?? []) if (group.Name is { } name) names.TryAdd(group.Id, name);
        var result = new List<WheelItem>();
        foreach (var entry in arrangement.Entries(offered.Select(i => i.Id).ToList(), defaultGroups).Where(e => !arrangement.Hidden.Contains(e.Id)))
        {
            if (entry.Members is not { } members)
            {
                if (byId.TryGetValue(entry.Id, out var item)) result.Add(item);
                continue;
            }
            var children = members.Where(m => !arrangement.Hidden.Contains(m)).Select(m => byId.GetValueOrDefault(m)).OfType<WheelItem>().ToList();
            if (children.Count > 1)
            {
                result.Add(new WheelItem(entry.Id, GroupTitle(entry.Id, names.GetValueOrDefault(entry.Id), children[0].Title),
                                         GroupSymbol(entry.Id, children[0].Symbol), string.Join(", ", children.Select(c => c.Title)), children));
            }
            else
            {
                result.AddRange(children);
            }
        }
        return result.Count == 1 && result[0].IsGroup ? result[0].Children : result;
    }

    public static List<WheelGroup> DefaultGroups(WheelScope scope) =>
        StandardGroups.All.Where(g => g.Scope() == scope).Select(g => new WheelGroup(g.Raw(), null, g.Members())).ToList();

    /// <summary>The user's name for a group, else the app's, else the name of its first slice.</summary>
    public static string GroupTitle(string id, string? name, string? first)
    {
        if (name?.Trim() is { Length: > 0 } trimmed) return trimmed;
        return StandardGroups.FromRaw(id)?.Title() ?? first ?? L("New Group");
    }

    public static string? GroupSymbol(string id, string? first) => StandardGroups.FromRaw(id) is { } standard ? standard.Symbol() : first;

    /// <summary>Everything that applies to these files, in the default order.</summary>
    public static List<WheelItem> Offered(WheelKind kind, IReadOnlyList<string> paths) => kind switch
    {
        WheelKind.Convert => Conversions.Targets(paths).Select(format =>
            new WheelItem(format.Raw(), format.Label(), null, L("Convert to %@", format.Label()), action: chosen => JobRunner.Shared.Convert(chosen, format))).ToList(),
        WheelKind.Tools => ToolCatalog.Items(paths),
        _ => FileActions.All.Select(action =>
            new WheelItem(action.Raw(), action.Title(), action.Symbol(), action.Detail(), action: chosen => WheelHost.Current.Perform(action, chosen))).ToList(),
    };

    /// <summary>Every slice that can appear on a wheel, in the default order.</summary>
    public static List<WheelChoice> Choices(WheelScope scope)
    {
        switch (scope.Kind)
        {
            case WheelKind.Convert:
                var source = scope.Source!.Value;
                var formats = source.Formats().ToList();
                return Merged<Format>(formats.Select(f => Conversions.Targets(f))).Select(target =>
                {
                    var from = formats.Where(f => Conversions.Targets(f).Contains(target)).Select(f => f.Label()).ToList();
                    return new WheelChoice(target.Raw(), target.Label(), null, target.Summary() + " " + L("Offered for %@.", Listed(from)));
                }).ToList();
            case WheelKind.Tools:
                return EditorTools.All.Select(t => new WheelChoice(t.Raw(), t.Title(), t.Symbol(), t.Explanation()))
                    .Concat(QuickTools.All.Where(t => t != QuickTool.RemoveBackground || Tools.CanRemoveBackground)
                        .Select(t => new WheelChoice(t.Raw(), t.Title(), t.Symbol(), t.Explanation()))).ToList();
            default:
                return FileActions.All.Select(a => new WheelChoice(a.Raw(), a.Title(), a.Symbol(), a.Explanation())).ToList();
        }
    }

    /// <summary>The kinds of file worth arranging: those that offer more than one format at a time.</summary>
    public static List<SourceKind> ArrangeableKinds =>
        SourceKinds.All.Where(source => source.Formats().Any(f => Conversions.Targets(f).Count > 1)).ToList();

    /// <summary>
    /// One list with every item of <paramref name="lists"/>, keeping each list's order: an item not seen yet goes
    /// right after the one it follows in its list, or first when it leads its list.
    /// </summary>
    public static List<T> Merged<T>(IEnumerable<IEnumerable<T>> lists) where T : notnull
    {
        var result = new List<T>();
        foreach (var list in lists.Select(l => l.ToList()))
        {
            for (var index = 0; index < list.Count; index++)
            {
                if (result.Contains(list[index])) continue;
                var position = index == 0 ? 0 : result.IndexOf(list[index - 1]) is var found and >= 0 ? found + 1 : result.Count;
                result.Insert(position, list[index]);
            }
        }
        return result;
    }
}
