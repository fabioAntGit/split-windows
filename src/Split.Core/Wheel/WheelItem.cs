namespace Split;

public sealed class WheelItem
{
    /// <summary>Stable across languages, so the user's arrangement of the wheel can find it.</summary>
    public string Id { get; }

    public string Title { get; }

    public string? Symbol { get; }

    /// <summary>One line shown in the centre while the item is highlighted.</summary>
    public string? Detail { get; }

    /// <summary>A group's slices: choosing a group opens them as a wheel of their own.</summary>
    public List<WheelItem> Children { get; }

    public Action<IReadOnlyList<string>> Action { get; }

    public bool IsGroup => Children.Count > 0;

    public WheelItem(string? id, string title, string? symbol, string? detail = null, List<WheelItem>? children = null,
                     Action<IReadOnlyList<string>>? action = null)
    {
        Id = id ?? title;
        Title = title;
        Symbol = symbol;
        Detail = detail;
        Children = children ?? [];
        Action = action ?? (_ => { });
    }
}

/// <summary>Items for the tools wheel: editors first, then one-click tools.</summary>
public static class ToolCatalog
{
    public static List<WheelItem> Items(IReadOnlyList<string> paths)
    {
        var editors = EditorTools.Available(paths).Select(editor =>
            new WheelItem(editor.Raw(), editor.Title(), editor.Symbol(), editor.Detail(), action: chosen => WheelHost.Current.OpenEditor(editor, chosen)));
        var tools = QuickTools.Available(paths).Select(tool =>
            new WheelItem(tool.Raw(), tool.Title(), tool.Symbol(), tool.Detail(), action: chosen =>
            {
                if (tool != QuickTool.CompressToSize) JobRunner.Shared.Tool(tool, chosen);
                // Asked after the wheel closes, so the dialog isn't covered by it.
                else WheelHost.Current.ChooseTargetSize(chosen, bytes => JobRunner.Shared.Tool(tool, chosen, bytes));
            }));
        return [.. editors, .. tools];
    }
}
