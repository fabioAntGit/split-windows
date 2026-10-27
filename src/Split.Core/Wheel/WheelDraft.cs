namespace Split;

public enum DraftMove { Top, Up, Down, Bottom }

/// <summary>
/// One wheel as Settings › Wheels edits it: what sits on the wheel, what each group holds and
/// what is left out. It turns back into the arrangement that is saved.
/// </summary>
public sealed class WheelDraft : IEquatable<WheelDraft>
{
    /// <summary>The top level, in order; a group carries its slices in order.</summary>
    public List<WheelEntry> Entries { get; private set; } = [];

    public HashSet<string> Hidden { get; set; } = [];

    /// <summary>The user's names for groups, as typed.</summary>
    public Dictionary<string, string> Names { get; private set; } = [];

    public WheelDraft() { }

    public WheelDraft(WheelArrangement arrangement, IReadOnlyList<string> ids, IReadOnlyList<WheelGroup> defaultGroups)
    {
        Entries = arrangement.Entries(ids, defaultGroups);
        Hidden = [.. arrangement.Hidden];
        foreach (var group in arrangement.Groups ?? [])
        {
            if (group.Name is { } name) Names[group.Id] = name; else Names.Remove(group.Id);
        }
    }

    /// <summary>A separate draft with the same content, for steps that must not change this one.</summary>
    public WheelDraft Copy() => new() { Entries = Entries.Select(e => e.Copy()).ToList(), Hidden = [.. Hidden], Names = new(Names) };

    /// <summary>
    /// What is saved. Whatever matches the app's defaults is saved as nothing, so the wheel keeps
    /// following them, also when the app adds slices or groups later.
    /// </summary>
    public WheelArrangement Arrangement(IReadOnlyList<string> ids, IReadOnlyList<WheelGroup> defaultGroups)
    {
        var standard = new WheelArrangement().Entries(ids, defaultGroups);
        var groups = Entries.Where(e => e.IsGroup).Select(e => new WheelGroup(e.Id, Cleaned(Names.GetValueOrDefault(e.Id)), [.. e.Members!])).ToList();
        var standardGroups = standard.Where(e => e.IsGroup).Select(e => new WheelGroup(e.Id, null, [.. e.Members!])).ToList();
        var custom = !groups.OrderBy(g => g.Id, StringComparer.Ordinal).SequenceEqual(standardGroups.OrderBy(g => g.Id, StringComparer.Ordinal));
        var order = Entries.Select(e => e.Id).ToArray();
        return new WheelArrangement
        {
            Order = !custom && order.SequenceEqual(standard.Select(e => e.Id)) ? [] : order,
            Hidden = [.. Hidden],
            Groups = custom ? groups : null,
        };
    }

    public WheelEntry? Entry(string id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>The group a slice is in, if any.</summary>
    public string? GroupOf(string id) => Entries.FirstOrDefault(e => e.Members?.Contains(id) == true)?.Id;

    // MARK: Order

    public void MoveEntries(IReadOnlyCollection<int> offsets, int destination) => Entries = Moved(Entries, offsets, destination);

    public void MoveMembers(string group, IReadOnlyCollection<int> offsets, int destination)
    {
        if (Entry(group) is { Members: { } members } entry) entry.Members = Moved(members, offsets, destination);
    }

    /// <summary>One step, or to an end, within the list the slice or group is in.</summary>
    public void Move(string id, DraftMove move)
    {
        if (GroupOf(id) is { } group && Entry(group) is { Members: { } members } entry && members.IndexOf(id) is var from and >= 0)
            entry.Members = Moved(members, [from], Destination(from, move, members.Count));
        else if (Entries.FindIndex(e => e.Id == id) is var at and >= 0)
            MoveEntries([at], Destination(at, move, Entries.Count));
    }

    /// <summary>
    /// Puts a slice or group right before <paramref name="neighbour"/> in the list it is in (the top
    /// level, or its group), or last without one. A wheel shows only what isn't hidden, so a drop on
    /// it is said by the slice that is to follow, and hidden ones keep their places.
    /// </summary>
    public void Place(string id, string? neighbour)
    {
        if (id == neighbour) return;
        if (GroupOf(id) is { } group && Entry(group) is { Members: { } members } entry)
        {
            members.RemoveAll(m => m == id);
            members.Insert(neighbour is not null && members.IndexOf(neighbour) is var at and >= 0 ? at : members.Count, id);
            entry.Members = members;
        }
        else if (Entries.FindIndex(e => e.Id == id) is var from and >= 0)
        {
            var moving = Entries[from];
            Entries.RemoveAt(from);
            Entries.Insert(neighbour is not null && Entries.FindIndex(e => e.Id == neighbour) is var next and >= 0 ? next : Entries.Count, moving);
        }
    }

    // MARK: Groups

    /// <summary>Puts top-level slices in a new group, where the first of them was, and returns its identifier.</summary>
    public string? MakeGroup(IReadOnlyList<string> ids, string? name = null)
    {
        var chosen = Entries.Where(e => !e.IsGroup && ids.Contains(e.Id)).Select(e => e.Id).ToList();
        var at = chosen.Count == 0 ? -1 : Entries.FindIndex(e => e.Id == chosen[0]);
        if (at < 0) return null;
        var used = Entries.Select(e => e.Id).ToHashSet();
        var id = Enumerable.Range(1, int.MaxValue).Select(n => $"group.{n}").First(candidate => !used.Contains(candidate));
        var position = Entries.Take(at).Count(e => !chosen.Contains(e.Id));
        Entries.RemoveAll(e => !e.IsGroup && chosen.Contains(e.Id));
        Entries.Insert(position, new WheelEntry(id, chosen));
        if (name is null) Names.Remove(id); else Names[id] = name;
        return id;
    }

    /// <summary>Moves a top-level slice to the end of a group.</summary>
    public void Add(string id, string group)
    {
        var from = Entries.FindIndex(e => e.Id == id && !e.IsGroup);
        if (from < 0 || Entry(group) is not { IsGroup: true }) return;
        Entries.RemoveAt(from);
        Entry(group)?.Members?.Add(id);
    }

    /// <summary>Takes a slice out of its group and puts it right after the group. A group left empty goes away.</summary>
    public void TakeOut(string id)
    {
        if (GroupOf(id) is not { } group) return;
        var index = Entries.FindIndex(e => e.Id == group);
        if (index < 0) return;
        Entries[index].Members!.RemoveAll(m => m == id);
        Entries.Insert(index + 1, new WheelEntry(id));
        if (Entries[index].Members!.Count == 0) Forget(group, index);
    }

    /// <summary>Dissolves a group: its slices take its place.</summary>
    public void Ungroup(string group)
    {
        var index = Entries.FindIndex(e => e.Id == group);
        if (index < 0 || Entries[index].Members is not { } members) return;
        Forget(group, index);
        Entries.InsertRange(index, members.Select(m => new WheelEntry(m)));
    }

    private void Forget(string group, int index)
    {
        Entries.RemoveAt(index);
        Names.Remove(group);
        Hidden.Remove(group);
    }

    // MARK: Helpers

    private static string? Cleaned(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static int Destination(int from, DraftMove move, int count) => move switch
    {
        DraftMove.Top => 0,
        DraftMove.Up => Math.Max(from - 1, 0),
        DraftMove.Down => Math.Min(from + 2, count),
        _ => count,
    };

    /// <summary>Like moving rows in a list: <paramref name="destination"/> counts the items before the move.</summary>
    private static List<T> Moved<T>(List<T> list, IReadOnlyCollection<int> offsets, int destination)
    {
        var moving = offsets.Order().Select(i => list[i]).ToList();
        var rest = list.Where((_, i) => !offsets.Contains(i)).ToList();
        rest.InsertRange(destination - offsets.Count(o => o < destination), moving);
        return rest;
    }

    public bool Equals(WheelDraft? other) =>
        other is not null && Entries.SequenceEqual(other.Entries) && Hidden.SetEquals(other.Hidden)
        && Names.Count == other.Names.Count && Names.All(pair => other.Names.TryGetValue(pair.Key, out var value) && value == pair.Value);

    public override bool Equals(object? obj) => Equals(obj as WheelDraft);

    public override int GetHashCode() => Entries.Count;
}
