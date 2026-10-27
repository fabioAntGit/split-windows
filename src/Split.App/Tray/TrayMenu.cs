using Microsoft.Win32;

namespace Split.App;

/// <summary>One line of the tray menu; a line without a title is a separator.</summary>
public sealed record MenuEntry(string? Title, string? Symbol = null, Action? Run = null, bool Enabled = true)
{
    public bool IsSeparator => Title is null;
}

/// <summary>
/// The menu of the tray icon: short, with the settings in their own window. It is built again each
/// time it opens, so the shelf's wording, the running count and the keys are always current.
/// </summary>
public static class TrayMenu
{
    public static List<MenuEntry> Build(Action quit)
    {
        var running = ActivityLog.Shared.Active.Count;
        var keys = string.Join("  ·  ", WheelKinds.All.Select(kind => $"{Settings.Modifier(kind).Label()} {kind.Title().ToLowerInvariant()}"));
        return
        [
            new(L("Convert Files…"), "arrow.triangle.2.circlepath", () => ChooseFiles(WheelKind.Convert)),
            new(L("Tools…"), "wrench.and.screwdriver", () => ChooseFiles(WheelKind.Tools)),
            new(L("Actions…"), "square.and.arrow.up", () => ChooseFiles(WheelKind.Actions)),
            new(Shelf.Shared.IsVisible ? L("Close the Shelf") : L("Open the Shelf"), "tray", Shelf.Shared.Toggle),
            new(running == 0 ? L("Activity…") : L("Activity (%@ running)…", running), "list.bullet.rectangle", () => AppWindows.ShowActivity()),
            new(null),
            new(L("While dragging, tap: %@", keys), Enabled: false),
            new(null),
            new(L("Settings…"), "gearshape", () => AppWindows.ShowSettings()),
            new(L("How to Use"), "questionmark.circle", () => AppWindows.ShowHelp()),
            new(L("About Split"), "info.circle", () => AppWindows.ShowAbout()),
            new(null),
            new(L("Quit Split"), Run: quit),
        ];
    }

    /// <summary>Asks for files, then opens the wheel for them in click mode.</summary>
    private static void ChooseFiles(WheelKind kind)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = kind.ChoosePrompt() };
        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0) WheelLauncher.Show(kind, dialog.FileNames, clickMode: true);
    }
}
