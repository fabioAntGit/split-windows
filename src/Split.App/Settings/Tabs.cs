using System.Windows;
using System.Windows.Controls;
using Split.Windows;

namespace Split.App;

/// <summary>Language, quality, metadata, sounds, starting with Windows and the Office apps.</summary>
public sealed class GeneralTab : TabPage
{
    private string? loginError;

    protected override UIElement Build()
    {
        var languages = Languages.All.Select(l => l == Split.Language.System ? $"{l.NativeName()} ({l.Resolved().NativeName()})" : l.NativeName()).ToList();
        var language = new DropDown(languages, Languages.All.ToList().IndexOf(Localizer.Shared.Language),
                                    i => Localizer.Shared.Language = Languages.All[i], L("Language"), 260);

        var quality = new Segmented(Qualities.All.Select(q => q.Label()).ToList(), Qualities.All.ToList().IndexOf(Settings.Quality), i =>
        {
            Settings.Quality = Qualities.All[i];
            Dispatcher.BeginInvoke(Rebuild);
        }, L("Quality"), 260);
        var metadata = new Toggle(Settings.KeepsMetadata, L("Keep metadata (date, location, camera)"));
        metadata.Changed += on => Settings.KeepsMetadata = on;
        var office = new Toggle(Settings.UseOfficeApps, L("Use Word, Excel and PowerPoint when installed"));
        office.Changed += on => Settings.UseOfficeApps = on;
        var sounds = new Toggle(Settings.PlaySounds, L("Sounds when done and on errors"));
        sounds.Changed += on => Settings.PlaySounds = on;
        var wheelSounds = new Toggle(Settings.PlayWheelSounds, L("Sound when moving over the wheel"));
        wheelSounds.Changed += on => Settings.PlayWheelSounds = on;
        var login = new Toggle(LaunchAtLogin.IsEnabled(), L("Start with Windows"));
        login.Changed += on => SetLogin(on, login);

        var generalRows = new List<UIElement>
        {
            Kit.Row(Kit.Text(L("Sounds when done and on errors")), sounds),
            Kit.Row(Kit.Text(L("Sound when moving over the wheel")), wheelSounds),
            Kit.Row(Kit.Text(L("Start with Windows")), login),
        };
        if (loginError is not null) generalRows.Add(Kit.Row(Kit.Text(loginError, TextSize.Subheadline, secondary: true), height: 30));

        var privacy = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
        var lines = new StackPanel { Width = 600 };
        lines.Children.Add(Kit.Text(L("Everything happens on this PC"), 13, weight: FontWeights.SemiBold));
        lines.Children.Add(Kit.Text(L("Split doesn’t use the Internet: it sends no files, has no accounts and collects no usage statistics."), 11, secondary: true));
        var shield = Kit.Symbol("lock.shield", 18);
        shield.Margin = new Thickness(0, 0, 12, 0);
        shield.VerticalAlignment = VerticalAlignment.Top;
        privacy.Children.Add(shield);
        privacy.Children.Add(lines);

        return Form(
            Kit.Section(L("Language"), Kit.Group(Kit.Row(Kit.Text(L("Language")), language)), L("Windows dialogs, such as Open, always use the language of Windows.")),
            Kit.Section(L("Conversion"), Kit.Group(
                Kit.Row(Kit.Text(L("Quality")), quality),
                Kit.Row(Kit.Text(L("Keep metadata (date, location, camera)")), metadata),
                Kit.Row(Kit.Text(L("Use Word, Excel and PowerPoint when installed")), office)),
                L("Office exports PDFs with the exact layout and the file never leaves this PC, but Word and PowerPoint follow their own rules: they may load pictures or templates the document fetches from the Internet. When off, only Split’s own converters are used, and Split removes anything remote first.")),
            Kit.Section(L("General"), Kit.Group([.. generalRows])),
            Kit.Section(null, Kit.Group(privacy)));
    }

    private void SetLogin(bool enabled, Toggle toggle)
    {
        try
        {
            LaunchAtLogin.Set(enabled);
            loginError = null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            loginError = L("Windows refused: %@", e.Message);
            toggle.IsOn = LaunchAtLogin.IsEnabled();
            Dispatcher.BeginInvoke(Rebuild);
        }
    }
}

/// <summary>The key that opens each wheel while files are dragged.</summary>
public sealed class ShortcutsTab : TabPage
{
    protected override UIElement Build()
    {
        var rows = WheelKinds.All.Select(wheel => Kit.Row(Kit.Text(wheel.Title()), new DropDown(
            ModifierChoices.All.Select(c => c.Label()).ToList(), ModifierChoices.All.ToList().IndexOf(Settings.Modifier(wheel)), i =>
            {
                ModifierChoices.Set(ModifierChoices.All[i], wheel);
                Dispatcher.BeginInvoke(Rebuild);
            }, wheel.Title(), 160))).ToArray<UIElement>();
        var without = Kit.Text(L("Use “Open with Split” in File Explorer (under “Show more options” on Windows 11), or the menu of the Split icon in the notification area (Convert, Tools, Actions)."), 12, secondary: true);
        without.Margin = new Thickness(12);
        return Form(
            Kit.Section(L("While Dragging"), Kit.Group(rows),
                        L("Start dragging files and, without letting go, tap the key once. The wheel appears around the pointer. Each key opens one wheel only: choosing one that is taken swaps the two.")),
            Kit.Section(L("Without Dragging"), Kit.Group(without)));
    }
}

/// <summary>What helps Split along: the programs it uses, the apps and libraries, and the terminal command.</summary>
public sealed class EnginesTab : TabPage
{
    protected override UIElement Build()
    {
        var sections = new List<UIElement>();
        if (Engines.MissingPackagesCommand is { } command)
        {
            sections.Add(Kit.Section(null, Kit.Group(CommandRow(L("Install everything that is missing"),
                Engines.Path("winget") is null ? L("Install App Installer from the Microsoft Store first, then paste the command in PowerShell.") : L("Paste the command in PowerShell or Windows Terminal."),
                command))));
        }
        sections.Add(Kit.Section(L("Programs (winget)"), Kit.Group(
            Row("ffmpeg", L("Audio, video, GIF and subtitles"), Engines.Path("ffmpeg") is not null, "winget install -e --id Gyan.FFmpeg"),
            Row("exiftool", L("Removing image metadata"), Engines.Path("exiftool") is not null, "winget install -e --id OliverBetz.ExifTool"),
            Row("LibreOffice", L("An alternative for ODS, ODP and PPT"), Engines.Soffice is not null, "winget install -e --id TheDocumentFoundation.LibreOffice", optional: true)),
            L("Installed once with winget. After that they work without the Internet.")));
        sections.Add(Kit.Section(L("Office Apps"), Kit.Group(
            Row("Word", L("DOCX → PDF with the exact layout"), Engines.Word, null, optional: true),
            Row("Excel", L("XLS/XLSX → PDF and XLSX"), Engines.Excel, null, optional: true),
            Row("PowerPoint", L("PPTX/PPT → PDF"), Engines.PowerPoint, null, optional: true))));
        var ocr = Platform.Current.OcrLanguages;
        var included = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 10) };
        var tick = Kit.Symbol("checkmark.circle.fill", 15);
        tick.Margin = new Thickness(0, 0, 10, 0);
        included.Children.Add(tick);
        included.Children.Add(Kit.Text(L("Image and PDF libraries that come with Split, and Windows’ text recognition"), 12));
        sections.Add(Kit.Section(L("Included with Split and Windows"), Kit.Group(
            included,
            Row("OCR", ocr.Count > 0 ? L("Text in scanned PDFs: %@", Listed(ocr)) : L("Text in scanned PDFs"), ocr.Count > 0, null, optional: true),
            Row(L("Background removal model"), L("Removing the background of pictures"), Tools.CanRemoveBackground, null, optional: true)),
            L("Libraries and models that run on this PC, with no Internet connection.")));
        sections.Add(Kit.Section(L("Terminal"), Kit.Group(
            Row("splitapp", L("Conversions and tools from Terminal"), CommandLineTool.IsInstalled, CommandLineTool.InstallCommand, optional: true)),
            L("The command adds the folder of this copy of the app to your PATH, so “splitapp convert pdf file.docx” works in a new terminal window.")));
        return Form([.. sections]);
    }

    private static UIElement Row(string name, string purpose, bool ok, string? install, bool optional = false)
    {
        var icon = Kit.Symbol(ok ? "checkmark.circle.fill" : optional ? "minus.circle" : "xmark.circle.fill", 15, secondary: !ok && optional);
        icon.Margin = new Thickness(0, 0, 10, 0);
        var text = new StackPanel();
        text.Children.Add(Kit.Text(name, 13, weight: FontWeights.Medium));
        text.Children.Add(Kit.Text(ok ? purpose : optional ? L("Optional · %@", purpose) : L("Missing · %@", purpose), 11, secondary: true));
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(icon);
        left.Children.Add(text);
        UIElement? copy = null;
        if (!ok && install is not null)
        {
            var button = new SplitButton(L("Copy Command"), compact: true) { ToolTip = install };
            button.Click += (_, _) => Platform.Current.SetClipboardText(install);
            copy = button;
        }
        return Kit.Row(left, copy, 44);
    }

    private static UIElement CommandRow(string title, string detail, string command)
    {
        var text = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        text.Children.Add(Kit.Text(title, 13, weight: FontWeights.Medium));
        var code = Kit.Text(command, 11);
        code.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        text.Children.Add(code);
        text.Children.Add(Kit.Text(detail, 11, secondary: true));
        var copy = new SplitButton(L("Copy Command"), ButtonKind.Primary);
        copy.Click += (_, _) => Platform.Current.SetClipboardText(command);
        return Kit.Row(text, copy, 56);
    }
}

/// <summary>What is waiting or running right now (with progress and a way to cancel), and how every job of this session ended.</summary>
public sealed class ActivityTab : TabPage
{
    private sealed class Active(TextBlock status, Bar bar)
    {
        public readonly TextBlock Status = status;
        public readonly Bar Bar = bar;
    }

    private readonly Dictionary<Guid, Active> rows = [];
    private string signature = "";

    private static string Shape() => string.Join(";", ActivityLog.Shared.Entries.Select(e => $"{e.Id:N}{(int)e.State}{e.CancelRequested}{e.Undone}"));

    /// <summary>Redraws only when jobs came, went or changed state; progress alone moves the bars in place, so a click is never lost to a redraw.</summary>
    public void Update()
    {
        if (Shape() != signature) { Rebuild(); return; }
        Tick();
    }

    /// <summary>The clock of each running job, and its progress.</summary>
    public void Tick()
    {
        foreach (var (id, row) in rows.ToList())
        {
            if (ActivityLog.Shared.Entry(id) is not { } entry) continue;
            row.Status.Text = ActivityFormat.Status(entry, DateTime.Now);
            row.Bar.Set(entry.Fraction ?? 0);
        }
    }

    protected override UIElement Build()
    {
        var log = ActivityLog.Shared;
        signature = Shape();
        rows.Clear();
        var active = log.Active;
        var finished = log.Finished;

        UIElement running;
        if (active.Count == 0)
        {
            var text = new StackPanel();
            text.Children.Add(Kit.Text(L("Nothing running"), 13, weight: FontWeights.Medium));
            text.Children.Add(Kit.Text(L("When you drop files on a wheel, the work shows here until it finishes."), 11, secondary: true));
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 10) };
            var icon = Kit.Symbol("checkmark.circle", 18, secondary: true);
            icon.Margin = new Thickness(0, 0, 10, 0);
            line.Children.Add(icon);
            line.Children.Add(text);
            running = Kit.Group(line);
        }
        else
        {
            running = Kit.Group([.. active.Select(ActiveRow)]);
        }

        UIElement list;
        if (finished.Count == 0)
        {
            var none = Kit.Text(L("No work has finished since Split opened."), 12, secondary: true);
            none.Margin = new Thickness(12);
            list = Kit.Group(none);
        }
        else
        {
            list = Kit.Group([.. finished.Select(FinishedRow)]);
        }
        UIElement? clear = finished.Count == 0 ? null : Kit.Link(L("Clear"), () => { log.ClearFinished(); Dispatcher.BeginInvoke(Rebuild); });
        return Form(
            Kit.Section(active.Count == 0 ? L("Running") : L("Running (%@)", active.Count.ToString()), running),
            Kit.Section(L("Finished"), list, L("The list covers this session and empties when Split quits. New files are always saved next to the originals."), clear));
    }

    private UIElement ActiveRow(ActivityEntry entry)
    {
        var status = Kit.Text(ActivityFormat.Status(entry, DateTime.Now), 11, secondary: true, wrap: false);
        var cancel = new SplitButton(L("Cancel"), compact: true) { IsEnabled = !entry.CancelRequested, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => ActivityLog.Shared.Cancel(entry.Id);
        var title = Kit.Text(entry.Title, 13, weight: FontWeights.SemiBold, wrap: false);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var top = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        status.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(status);
        right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);
        top.Children.Add(title);
        var bar = new Bar(entry.Fraction ?? 0) { Margin = new Thickness(0, 6, 0, 0), Visibility = entry.State == ActivityState.Running ? Visibility.Visible : Visibility.Collapsed };
        var detail = Kit.Text(entry.Detail.Length == 0 ? ActivityFormat.Files(entry.Files) : entry.Detail, 11, secondary: true, wrap: false);
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.Margin = new Thickness(0, 6, 0, 0);
        var stack = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };
        stack.Children.Add(top);
        stack.Children.Add(bar);
        stack.Children.Add(detail);
        rows[entry.Id] = new Active(status, bar);
        return stack;
    }

    private UIElement FinishedRow(ActivityEntry entry)
    {
        var symbol = entry.State switch
        {
            ActivityState.Succeeded => "checkmark.circle.fill",
            ActivityState.Partial => "exclamationmark.triangle",
            ActivityState.Failed => "xmark.octagon",
            _ => "stop.circle",
        };
        var icon = Icons.Block(symbol, 16, entry.State == ActivityState.Succeeded ? Palette.Accent : Palette.Secondary);
        icon.Width = 20;
        icon.VerticalAlignment = VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetName(icon, ActivityFormat.StateName(entry.State));

        var text = new StackPanel();
        text.Children.Add(Kit.Text(entry.Summary, 12.5, weight: FontWeights.Medium));
        var files = Kit.Text(ActivityFormat.Files(entry.Files) + " · " + ActivityFormat.When(entry), 11, secondary: true, wrap: false);
        files.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(files);
        foreach (var note in entry.Notes) text.Children.Add(Note("info.circle", note));
        if (entry.Undone) text.Children.Add(Note("trash", L("Undone: the new files are in the Recycle Bin.")));
        if (entry.Failures.FirstOrDefault() is { } first && entry.State != ActivityState.Cancelled)
            text.Children.Add(Kit.Text(first.Error.Message, 11, secondary: true));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        void Add(string label, Action run, string? tip = null)
        {
            var button = new SplitButton(label, compact: true) { Margin = new Thickness(6, 0, 0, 0), ToolTip = tip };
            button.Click += (_, _) => run();
            buttons.Children.Add(button);
        }
        if (entry.Failures.Count > 0) Add(L("Details…"), () => FailureReport.Show(entry.Failures));
        var existing = entry.Outputs.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
        if (existing.Count > 0) Add(L("Show in File Explorer"), () => Explorer.Reveal(existing));
        if (ActivityLog.Undoable(entry).Count > 0) Add(L("Undo"), () =>
        {
            var failures = ActivityLog.Shared.Undo(entry.Id);
            if (failures.Count > 0) FailureReport.Show(failures);
        }, L("Move the new files to the Recycle Bin"));

        var grid = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(10, 0, 8, 0);
        grid.Children.Add(text);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return grid;
    }

    private static UIElement Note(string symbol, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var icon = Kit.Symbol(symbol, 12, secondary: true);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 5, 0);
        row.Children.Add(icon);
        var label = Kit.Text(text, 11, secondary: true);
        label.MaxWidth = 380;
        row.Children.Add(label);
        return row;
    }
}
