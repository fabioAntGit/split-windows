namespace Split;

/// <summary>
/// How the user arranged one wheel: the order of its slices, the ones left out, and the groups
/// that gather several slices behind one. Slices are kept by identifier, so one the user never
/// placed (a format added later, or an engine installed since) still shows up: in its usual
/// group, or right after the slice it follows by default.
/// </summary>
public sealed class WheelArrangement : IEquatable<WheelArrangement>
{
    /// <summary>The top level of the wheel: slices and groups.</summary>
    public string[] Order { get; init; } = [];

    public HashSet<string> Hidden { get; init; } = [];

    /// <summary>The user's groups; null follows the app's own.</summary>
    public List<WheelGroup>? Groups { get; init; }

    public bool IsDefault => Order.Length == 0 && Hidden.Count == 0 && Groups is null;

    /// <summary>Every item, hidden ones included, in the chosen order. <paramref name="items"/> come in the default order.</summary>
    public List<T> Sorted<T>(IEnumerable<T> items, Func<T, string> id)
    {
        var list = items.ToList();
        if (Order.Length == 0) return list;
        var position = new Dictionary<string, int>();
        for (var i = 0; i < Order.Length; i++) position.TryAdd(Order[i], i);
        var anchor = -1;
        var offset = 0;
        return list.Select((item, index) =>
            {
                if (position.TryGetValue(id(item), out var placed))
                {
                    anchor = placed;
                    offset = 0;
                }
                else
                {
                    offset++;
                }
                return (anchor, offset, index, item);
            })
            .OrderBy(k => k.anchor).ThenBy(k => k.offset).ThenBy(k => k.index)
            .Select(k => k.item).ToList();
    }

    /// <summary>What the wheel shows: the chosen order, without the hidden items.</summary>
    public List<T> Apply<T>(IEnumerable<T> items, Func<T, string> id) => Sorted(items, id).Where(item => !Hidden.Contains(id(item))).ToList();

    /// <summary>
    /// The wheel in order, hidden entries included: each slice, and each group with its slices
    /// in order. <paramref name="ids"/> are the slices on offer, in the default order, and
    /// <paramref name="defaultGroups"/> the app's groups. A group sits where its first slice would;
    /// one with nothing on offer is left out.
    /// </summary>
    public List<WheelEntry> Entries(IReadOnlyList<string> ids, IReadOnlyList<WheelGroup> defaultGroups)
    {
        var offered = ids.ToHashSet();
        var owner = new Dictionary<string, string>();
        var members = new Dictionary<string, List<string>>();
        foreach (var group in Groups ?? defaultGroups)
        {
            if (members.ContainsKey(group.Id)) continue;
            var list = new List<string>();
            foreach (var id in group.Members.Where(id => offered.Contains(id) && !owner.ContainsKey(id)))
            {
                owner[id] = group.Id;
                list.Add(id);
            }
            members[group.Id] = list;
        }
        // A slice the user never placed joins its usual group, if the user kept that group.
        if (Groups is not null)
        {
            var placed = Order.ToHashSet();
            foreach (var group in defaultGroups)
            {
                if (!members.TryGetValue(group.Id, out var list)) continue;
                for (var position = 0; position < group.Members.Length; position++)
                {
                    var id = group.Members[position];
                    if (!offered.Contains(id) || owner.ContainsKey(id) || placed.Contains(id)) continue;
                    var before = group.Members.Take(position).LastOrDefault(list.Contains);
                    list.Insert(before is null ? 0 : list.IndexOf(before) + 1, id);
                    owner[id] = group.Id;
                }
            }
        }
        var skeleton = new List<WheelEntry>();
        var seen = new HashSet<string>();
        foreach (var id in ids)
        {
            if (!owner.TryGetValue(id, out var group)) skeleton.Add(new WheelEntry(id));
            else if (seen.Add(group)) skeleton.Add(new WheelEntry(group, members[group]));
        }
        return Sorted(skeleton, entry => entry.Id);
    }

    public bool Equals(WheelArrangement? other) =>
        other is not null && Order.SequenceEqual(other.Order) && Hidden.SetEquals(other.Hidden)
        && (Groups is null ? other.Groups is null : other.Groups is not null && Groups.SequenceEqual(other.Groups));

    public override bool Equals(object? obj) => Equals(obj as WheelArrangement);

    public override int GetHashCode() => HashCode.Combine(Order.Length, Hidden.Count, Groups?.Count);
}

/// <summary>Slices gathered behind one slice of the wheel, which opens them as a wheel of their own.</summary>
public sealed record WheelGroup(string Id, string? Name, string[] Members)
{
    public bool Equals(WheelGroup? other) => other is not null && Id == other.Id && Name == other.Name && Members.SequenceEqual(other.Members);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Members.Length);
}

/// <summary>One place on a wheel: a slice, or a group and its slices.</summary>
public sealed class WheelEntry(string id, IEnumerable<string>? members = null) : IEquatable<WheelEntry>
{
    public string Id { get; } = id;

    /// <summary>Null for a slice.</summary>
    public List<string>? Members { get; set; } = members?.ToList();

    public bool IsGroup => Members is not null;

    public WheelEntry Copy() => new(Id, Members);

    public bool Equals(WheelEntry? other) =>
        other is not null && Id == other.Id && (Members is null ? other.Members is null : other.Members is not null && Members.SequenceEqual(other.Members));

    public override bool Equals(object? obj) => Equals(obj as WheelEntry);

    public override int GetHashCode() => HashCode.Combine(Id, Members?.Count);

    public override string ToString() => IsGroup ? $"{Id}[{string.Join(",", Members!)}]" : Id;
}
