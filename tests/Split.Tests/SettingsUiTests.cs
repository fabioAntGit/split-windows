using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Split.App;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>Settings, How to Use, About, Activity and the failure report: what they show, what they change, and captures of each.</summary>
public static class SettingsUiTests
{
    // MARK: Helpers

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var inner in Walk(child)) yield return inner;
    }

    private static List<string> Texts(DependencyObject root) =>
        Walk(root).OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0).ToList();

    private static T Named<T>(DependencyObject root, string name) where T : DependencyObject =>
        Walk(root).OfType<T>().FirstOrDefault(e => AutomationProperties.GetName(e) == name) ?? throw new TestFailure($"no {typeof(T).Name} named “{name}”");

    private static void Up(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = element });

    private static System.Windows.Interop.HwndSource? keySource;

    /// <summary>Raises a key press on an element, as the keyboard would.</summary>
    private static void PressKey(UIElement? element, Key key)
    {
        keySource ??= new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());
        if (element is null) InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        else element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, key) { RoutedEvent = Keyboard.KeyDownEvent, Source = element });
    }

    private static void Press(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    private static Button ButtonLabelled(DependencyObject root, string label) =>
        Walk(root).OfType<SplitButton>().FirstOrDefault(b => b.Content is string text && text == label) ?? throw new TestFailure($"no button “{label}”");

    /// <summary>Chooses an entry of a drop-down by its text.</summary>
    private static void Choose(DropDown dropDown, string text) =>
        Up(Walk(dropDown.Menu).OfType<Border>().First(b => AutomationProperties.GetName(b) == text));

    private static void Choose(Segmented segmented, string text) => Up(Named<Border>(segmented, text));

    /// <summary>Lets the dispatcher run what the clicks queued (the tabs redraw after a change).</summary>
    private static void Pump() => Ui.Settle(0.05);

    private static BitmapSource Picture(AppWindow window, Size client)
    {
        var root = (Grid)window.Content;
        root.Background = Palette.Brush(Palette.WheelSurface);
        var size = new Size(client.Width, client.Height + AppWindow.TitleBarHeight);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width * 2, (int)size.Height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(root);
        return bitmap;
    }

    private static void Save(string folder, string name, BitmapSource image, ref int made)
    {
        Check(Ui.Spread(image) > 4, $"{name}: the image is almost uniform: was the view empty?");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
        made++;
    }

    private static T WithLook<T>(LookColor look, Func<T> body)
    {
        var before = Settings.LookColor;
        Look.Color = look;
        try { return body(); }
        finally { Look.Color = before; }
    }

    // MARK: Suites

    public static void Register()
    {
        Test("Activity", "The texts of the rows: files, status, duration and when", () =>
        {
            Check(ActivityFormat.Files([]) == "No files" && ActivityFormat.Files([@"C:\a\photo.png"]) == "photo.png", "one file or none");
            Check(ActivityFormat.Files([@"C:\a\photo.png", "b.png", "c.png"]) == "photo.png and 2 more", "several files");
            Check(ActivityFormat.Duration(65) == "01:05" && ActivityFormat.Duration(0) == "00:00" && ActivityFormat.Duration(3725) == "1:02" && ActivityFormat.Duration(-3) == "00:00", "durations");
            var queued = new ActivityEntry(Guid.NewGuid(), "Job", [], DateTime.Now, new CancelToken());
            var now = DateTime.Now;
            var running = queued with { State = ActivityState.Running, StartedAt = now.AddSeconds(-75), Fraction = 0.426 };
            Check(ActivityFormat.Status(queued, now) == "Waiting", "queued");
            Check(ActivityFormat.Status(running, now) == "43% · 01:15", $"running: {ActivityFormat.Status(running, now)}");
            Check(ActivityFormat.Status(running with { CancelRequested = true }, now) == "Canceling…", "cancelling");
            var names = Enum.GetValues<ActivityState>().Select(ActivityFormat.StateName).ToList();
            Check(names.SequenceEqual(["Waiting", "Running", "Done", "Done with problems", "Failed", "Canceled"]), string.Join(", ", names));
            var done = running with { State = ActivityState.Succeeded, FinishedAt = now.AddMinutes(-3), StartedAt = now.AddMinutes(-3).AddSeconds(-12) };
            Check(ActivityFormat.When(done, now) == "3 minutes ago · took 00:12", ActivityFormat.When(done, now));
            Check(ActivityFormat.When(done with { FinishedAt = now.AddSeconds(-1) }, now).StartsWith("now"), "just now");
            Check(ActivityFormat.When(done with { FinishedAt = now.AddHours(-1) }, now).StartsWith("1 hour ago"), "an hour ago");
        });

        Test("Activity", "The tab: nothing running, a job with progress and Cancel, finished jobs with their notes, Clear", () => Ui.Run(() =>
        {
            var log = ActivityLog.Shared;
            log.ClearFinished();
            var tab = new ActivityTab();
            tab.Rebuild();
            Check(Texts(tab).Contains("Nothing running") && Texts(tab).Contains("No work has finished since Split opened."), "empty state");

            var token = new CancelToken();
            var id = log.Add("Converting to MP4", [@"C:\clips\long.mov"], token);
            log.Start(id);
            log.Progress(id, 0.25, "long.mov");
            tab.Rebuild();
            Check(Texts(tab).Contains("Running (1)") && Texts(tab).Contains("Converting to MP4") && Texts(tab).Contains("long.mov"), "a running job is listed");
            var bar = Walk(tab).OfType<Bar>().Single();
            tab.Update();
            log.Progress(id, 0.75);
            tab.Update();
            Check(Walk(tab).OfType<Bar>().Single() == bar && Near(bar.Fraction, 0.75, 0.001), "progress moves the bar in place, without redrawing the row (a click on Cancel is never lost)");
            Press(ButtonLabelled(tab, "Cancel"));
            Pump();
            Check(token.IsCancelled && log.Entry(id)!.CancelRequested, "Cancel asks the job to stop");
            tab.Update();
            Check(!ButtonLabelled(tab, "Cancel").IsEnabled, "and can't be asked twice");

            var folder = Path.Combine(Fixtures.Root, "_settings-activity");
            Directory.CreateDirectory(folder);
            var made = Path.Combine(folder, "made.txt");
            File.WriteAllText(made, "x");
            log.Finish(id, ActivityState.Partial, "1 of 2 converted", [made], [new JobFailure("b.mov", new Problem("The file is damaged."))], ["MP4 has no subtitles."]);
            tab.Update();
            var texts = Texts(tab);
            Check(texts.Contains("1 of 2 converted") && texts.Contains("MP4 has no subtitles.") && texts.Contains("The file is damaged."), $"a finished job shows its summary, notes and first problem: {string.Join(" | ", texts)}");
            Check(texts.Any(t => t.StartsWith("long.mov · now")), "and when it ended");
            var labels = Walk(tab).OfType<SplitButton>().Select(b => (string)b.Content).ToList();
            Check(labels.SequenceEqual(["Details…", "Show in File Explorer", "Undo"]), string.Join(" | ", labels));
            Check(Texts(tab).Contains("Finished") && Walk(tab).OfType<Border>().Any(b => AutomationProperties.GetName(b) == "Clear"), "Finished has a Clear link");
            log.ClearFinished();
            tab.Rebuild();
            Check(Texts(tab).Contains("No work has finished since Split opened."), "Clear empties the list");
        }));

        Test("Settings", "The window: 760 × 660 below a title bar of the app's colour, not resizable, in the taskbar, six tabs", () => Ui.Run(() =>
        {
            var window = new SettingsWindow(SettingsTab.General);
            Check(window.Width == 760 && window.Height == 660 + AppWindow.TitleBarHeight, $"{window.Width} × {window.Height}");
            Check(window.ResizeMode == ResizeMode.CanMinimize && window.ShowInTaskbar, "a fixed size that shows in the taskbar");
            Check(!window.AllowsTransparency && window.Background is SolidColorBrush solid && solid.Color == Palette.WheelSurface, "a solid background, no Mica or Acrylic");
            var chrome = WindowChrome.GetWindowChrome(window);
            Check(chrome is { CaptionHeight: AppWindow.TitleBarHeight } && chrome.ResizeBorderThickness == new Thickness(0), "the title bar is drawn by the app");
            Check(window.TitleBar.Background is SolidColorBrush bar && bar.Color != Colors.Transparent, "with its own colour");
            Check(window.CaptionButtons.Count == 2 && AutomationProperties.GetName(window.CaptionButtons[^1]) == "Close", "the window buttons are on the right, Close last");
            Check(window.Title == "Split Settings", window.Title);
            Check(window.TabTitles.SequenceEqual(["General", "Activity", "Shortcuts", "Wheels", "Formats", "Engines"]), string.Join(", ", window.TabTitles));
            var id = ActivityLog.Shared.Add("Counting", [], new CancelToken());
            window.Select(SettingsTab.Activity);
            Pump();
            Check(window.TabTitles[1] == "Activity (1)" && window.Current == SettingsTab.Activity, $"running jobs are counted in the tab: {window.TabTitles[1]}");
            ActivityLog.Shared.Cancel(id);
            ActivityLog.Shared.ClearFinished();
            Pump();
            Check(window.TabTitles[1] == "Activity", "and the count goes away");
            window.Close();
        }));

        Test("Settings", "One window each: asking again brings the open one forward; the Activity entry opens its tab", () => Ui.Run(() =>
        {
            var first = SettingsWindow.Open(SettingsTab.Shortcuts);
            var second = SettingsWindow.Open(SettingsTab.Engines);
            Check(first == second && first.Current == SettingsTab.Engines, "the same window, now on Engines");
            first.WindowState = WindowState.Minimized;
            SettingsWindow.Open();
            Check(first.WindowState == WindowState.Normal, "a minimised window is restored");
            first.Close();
            var again = SettingsWindow.Open(SettingsTab.Activity);
            Check(again != first && again.Current == SettingsTab.Activity, "closed, it opens anew");
            again.Close();
            var help = HelpWindow.Open();
            Check(HelpWindow.Open() == help, "How to Use opens once");
            help.Close();
            var about = AboutWindow.Open();
            Check(AboutWindow.Open() == about, "About opens once");
            about.Close();
            SettingsNavigation.Tab = SettingsTab.General;
        }));

        Test("Settings", "General: every choice is written to the settings, the language rebuilds the window", () => Ui.Run(() =>
        {
            var before = (Settings.Quality, Settings.KeepsMetadata, Settings.UseOfficeApps, Settings.PlaySounds, Settings.PlayWheelSounds, Settings.Language);
            var window = new SettingsWindow(SettingsTab.General);
            try
            {
                var tab = window.Page(SettingsTab.General);
                Choose(Named<Segmented>(tab, "Quality"), "High");
                Check(Settings.Quality == Quality.High, "quality");
                Pump();
                Choose(Named<Segmented>(window.Page(SettingsTab.General), "Quality"), "Compact");
                Check(Settings.Quality == Quality.Compact, "quality again, on the redrawn tab");
                foreach (var (name, read) in new (string, Func<bool>)[]
                {
                    ("Keep metadata (date, location, camera)", () => Settings.KeepsMetadata),
                    ("Use Word, Excel and PowerPoint when installed", () => Settings.UseOfficeApps),
                    ("Sounds when done and on errors", () => Settings.PlaySounds),
                    ("Sound when moving over the wheel", () => Settings.PlayWheelSounds),
                })
                {
                    var toggle = Named<Toggle>(window.Page(SettingsTab.General), name);
                    var was = read();
                    Check(toggle.IsOn == was, $"{name}: shows the setting");
                    toggle.Click();
                    Check(read() == !was, $"{name}: flips it");
                    toggle.Click();
                }
                Check(Named<Toggle>(window.Page(SettingsTab.General), "Start with Windows").IsOn == Split.Windows.LaunchAtLogin.IsEnabled(), "launch at login shows the Run key");
                var texts = Texts(window.Page(SettingsTab.General));
                Check(texts.Contains("Everything happens on this PC"), "it is about this PC");
                Check(texts.Any(t => t.StartsWith("Windows dialogs")), "the language note is about Windows");

                var language = Named<DropDown>(window.Page(SettingsTab.General), "Language");
                Check(Walk(language.Menu).OfType<Border>().Select(b => AutomationProperties.GetName(b)).Where(n => n.Length > 0).Contains("Windows language (English)"),
                      "the system choice names the language it resolves to");
                Choose(language, "Português");
                Pump();
                Check(Localizer.Shared.Language == Language.Pt && window.Title == "Definições do Split", $"the window is in Portuguese: {window.Title}");
                Check(window.TabTitles[0] == "Geral", window.TabTitles[0]);
            }
            finally
            {
                Localizer.Shared.Language = Language.En;
                (Settings.Quality, Settings.KeepsMetadata, Settings.UseOfficeApps, Settings.PlaySounds, Settings.PlayWheelSounds) = (before.Quality, before.KeepsMetadata, before.UseOfficeApps, before.PlaySounds, before.PlayWheelSounds);
                Settings.Language = before.Language;
                window.Close();
            }
        }));

        Test("Settings", "Shortcuts: choosing a taken key swaps the two, and the drag monitor and the help follow", () => Ui.Run(() =>
        {
            var window = new SettingsWindow(SettingsTab.Shortcuts);
            try
            {
                var tab = window.Page(SettingsTab.Shortcuts);
                Check(Texts(tab).Contains("While Dragging") && Texts(tab).Contains("Without Dragging"), "both sections");
                Check(Texts(tab).Any(t => t.Contains("File Explorer")) && Texts(tab).Any(t => t.Contains("notification area")), "the other way in is File Explorer and the tray");
                var options = Walk(Named<DropDown>(tab, "Conversion").Menu).OfType<Border>().Select(b => AutomationProperties.GetName(b)).Where(n => n.Length > 0).ToList();
                Check(options.SequenceEqual(["Shift", "Ctrl", "Alt", "Ctrl + Alt", "Off"]), string.Join(", ", options));
                Choose(Named<DropDown>(tab, "Conversion"), "Ctrl");
                Check(Settings.Modifier(WheelKind.Convert) == ModifierChoice.Ctrl && Settings.Modifier(WheelKind.Tools) == ModifierChoice.Shift, "Ctrl moved to Conversion, Tools got Shift");
                // The gesture reads the settings at every look, so the new keys work at once.
                var gesture = new DragGesture();
                gesture.Update(true, 0, 0, 4, 4, ModKeys.None, 0);
                gesture.Update(true, 50, 50, 4, 4, ModKeys.None, 0.1);
                gesture.Update(true, 50, 50, 4, 4, ModKeys.Ctrl, 0.2);
                var step = gesture.Update(true, 50, 50, 4, 4, ModKeys.None, 0.3);
                Check(step.Trigger == WheelKind.Convert, $"a tap of Ctrl now opens the conversion wheel: {step.Trigger}");
                Pump();
                var help = new HelpWindow();
                var caps = Walk(help).OfType<KeyCap>().Select(k => k.Label).ToList();
                Check(caps.SequenceEqual(["Ctrl", "Shift", "Alt"]), $"How to Use shows the keys: {string.Join(", ", caps)}");
                help.Close();
            }
            finally
            {
                foreach (var kind in WheelKinds.All) ModifierChoices.Set(kind.DefaultModifier(), kind);
                window.Close();
            }
        }));

        Test("Help", "How to Use: three steps, the keys as key caps, the Windows note, Get Started closes", () => Ui.Run(() =>
        {
            var help = new HelpWindow();
            Check(help.Title == "How to Use" && help.Width == 540 && help.ResizeMode == ResizeMode.CanMinimize, "540 wide, fixed, can be minimised");
            var texts = Texts(help);
            foreach (var expected in new[]
            {
                "How to Use Split", "Convert and edit files without leaving what you are doing.", "Start dragging files", "Tap a key once",
                "A wheel appears around the pointer:", "Drop on the slice you want", "The result is saved next to the original. The original is never changed.",
                "Conversion", "Tools", "Actions", "to another format", "Everything happens on this PC, without the Internet.",
            })
                Check(texts.Contains(expected), $"missing “{expected}”");
            Check(texts.Any(t => t.StartsWith("Apps running as administrator")), "the note on administrator apps");
            Check(texts.Any(t => t.Contains("File Explorer")), "Windows wording");
            Check(Walk(help).OfType<KeyCap>().Select(k => k.Label).SequenceEqual(["Shift", "Ctrl", "Alt"]), "Shift, Ctrl, Alt by default");
            ModifierChoices.Set(ModifierChoice.CtrlAlt, WheelKind.Actions);
            try
            {
                var other = new HelpWindow();
                Check(Walk(other).OfType<KeyCap>().Select(k => k.Label).SequenceEqual(["Shift", "Ctrl", "Ctrl", "Alt"]), "Ctrl + Alt is drawn as two keys");
                other.Close();
            }
            finally { foreach (var kind in WheelKinds.All) ModifierChoices.Set(kind.DefaultModifier(), kind); }
            help.Show();
            var start = ButtonLabelled(help, "Get Started");
            Press(start);
            Pump();
            Check(!help.IsVisible, "Get Started closes the window");
        }));

        Test("About", "About Split: the mark, the name, the version and one line; nothing that opens the Internet", () => Ui.Run(() =>
        {
            var about = new AboutWindow();
            var texts = Texts(about);
            Check(texts.Contains("Split") && texts.Contains($"Version {AboutWindow.Version}") && AboutWindow.Version.Length > 0 && !AboutWindow.Version.Contains('+'), $"{string.Join(" | ", texts)}");
            Check(texts.Contains("A local file converter and editor. Everything happens on this PC, without the Internet."), "what the app is");
            Check(!Walk(about).OfType<System.Windows.Documents.Hyperlink>().Any() && !texts.Any(t => t.Contains("http", StringComparison.OrdinalIgnoreCase)), "no links");
            Check(about.Title == "About Split" && about.ResizeMode == ResizeMode.CanMinimize, "a small fixed window that can be minimised");
            about.Close();
        }));

        Test("Settings", "Engines: every program and library, with the winget command and the PowerShell line that adds splitapp", () => Ui.Run(() =>
        {
            var tab = new EnginesTab();
            tab.Rebuild();
            var texts = Texts(tab);
            foreach (var name in new[] { "ffmpeg", "exiftool", "LibreOffice", "Word", "Excel", "PowerPoint", "OCR", "Background removal model", "splitapp" })
                Check(texts.Contains(name), $"no row for {name}");
            Check(texts.Contains("Programs (winget)"), "the programs come from winget");
            var copies = Walk(tab).OfType<SplitButton>().Where(b => (string)b.Content == "Copy Command").ToList();
            var installs = Walk(tab).OfType<SplitButton>().Where(b => (string)b.Content == "Install").ToList();
            var winget = Engines.Path("winget") is not null;
            if (Engines.MissingPackagesCommand is { } command)
            {
                var primary = winget ? installs : copies;
                Check(primary.Any(b => b.ToolTip as string == command && b.Face.Background is SolidColorBrush fill && fill.Color == Palette.Selection),
                      "one primary button installs what's missing (or copies the command without winget)");
                Check(command.StartsWith("winget install -e --id "), command);
            }
            if (Engines.Soffice is null)
                Check(installs.Any(b => b.ToolTip as string == "winget install -e --id TheDocumentFoundation.LibreOffice") == winget, "LibreOffice: Install only where winget is");
            Check(!installs.Any(b => b.ToolTip as string == CommandLineTool.InstallCommand), "splitapp's PATH line is only copied");
            var window = Split.Windows.Terminal.Run("winget install -e --id Gyan.FFmpeg");
            Check(Path.IsPathRooted(window.FileName) && window.FileName.EndsWith(@"\WindowsPowerShell\v1.0\powershell.exe", StringComparison.OrdinalIgnoreCase) && !window.UseShellExecute
                  && window.ArgumentList.SequenceEqual(["-NoProfile", "-Command", "winget install -e --id Gyan.FFmpeg; cmd /c pause"]),
                  $"Install runs the command in a PowerShell window that waits for a key: {window.FileName} {string.Join(" ", window.ArgumentList)}");
            if (!CommandLineTool.IsInstalled)
                Check(copies.Any(b => b.ToolTip as string == CommandLineTool.InstallCommand), "splitapp: the button copies the PowerShell line");
            Check(CommandLineTool.InstallCommand.Contains(CommandLineTool.Folder) && CommandLineTool.InstallCommand.Contains("SetEnvironmentVariable"), "the line adds the program's folder to the user's PATH");
            Check(texts.Contains("Programs (winget)") && texts.Contains("Office Apps") && texts.Contains("Terminal"), "sections");
        }));

        Test("Settings", "Formats: search, the details of a format, chips that wrap, a chip shows its format", () => Ui.Run(() =>
        {
            var tab = new FormatsTab();
            tab.Rebuild();
            tab.Measure(new Size(720, 560));
            tab.Arrange(new Rect(0, 0, 720, 560));
            Check(tab.Chosen == Format.Jpg && Texts(tab).Contains("JPG") && Texts(tab).Contains("Turns into") && Texts(tab).Contains("Made from"), "starts on JPG");
            var targets = Conversions.Targets(Format.Jpg);
            var chips = Walk(tab).OfType<SplitButton>().Where(b => b.Content is StackPanel).ToList();
            Check(chips.Count >= targets.Count, $"a chip for each of the {targets.Count} formats JPG turns into ({chips.Count} chips)");
            var wrap = Walk(tab).OfType<WrapPanel>().First();
            wrap.Measure(new Size(160, double.PositiveInfinity));
            Check(wrap.DesiredSize.Height > wrap.Children.OfType<UIElement>().First().DesiredSize.Height * 1.5 || wrap.Children.Count < 3, "chips flow onto more rows when the width is short");
            tab.Search("pdf");
            var listed = Walk(tab).OfType<Border>().Where(b => b.Tag is Format).Select(b => (Format)b.Tag).ToList();
            Check(listed.Contains(Format.Pdf) && listed.All(f => f.Label().Contains("PDF", StringComparison.OrdinalIgnoreCase) || f.Ext().Contains("pdf")) && listed.Count < Formats.All.Count, $"search keeps only matches: {string.Join(", ", listed)}");
            tab.Choose(Format.Mp4);
            Check(tab.Chosen == Format.Mp4 && Texts(tab).Contains("MP4"), "choosing a format shows it");
            Check(Walk(tab).OfType<Border>().Count(b => b.Tag is Format) == Formats.All.Count, "the search is cleared");
            var pages = Walk(tab).OfType<TextBlock>().Where(t => t.Text.StartsWith("Nothing on this PC")).ToList();
            tab.Choose(Format.Pages);
            Check(Texts(tab).Any(t => t.StartsWith("Nothing on this PC opens Pages files")) || Conversions.Targets(Format.Pages).Count > 0, "a format Split can't turn into anything says so, about this PC");
        }));

        Test("Settings", "Wheels: the preview is 330 at one size whatever the wheel's size; slices move and the arrangement is saved", () => Ui.Run(() =>
        {
            var size = Settings.WheelSize;
            var window = new SettingsWindow(SettingsTab.Wheels);
            try
            {
                var tab = (WheelsTab)window.Page(SettingsTab.Wheels);
                tab.SelectWheel(WheelKind.Tools);
                Pump();
                Check(tab.PreviewElement.Width == 330 && tab.PreviewElement.Height == 330, "a 330 square");
                Settings.WheelSize = WheelSize.ExtraSmall;
                tab.RefreshPreview();
                var picture = Walk(tab.PreviewElement).OfType<Image>().Last();
                Check(picture.Source is BitmapSource { } bitmap && Near(bitmap.Width, WheelView.Picture([new WheelItem("a", "A", null)], "t", "", null, new WheelStyle(WheelSize.ExtraLarge, true)).Width, 1),
                      "the wheel in the preview keeps its size, whatever size the wheel opens at");
                var texts = Texts(tab);
                foreach (var expected in new[] { "Size", "Names under icons", "Color", "Black or white for the wheel, the editors and every window.", "Glass background", "Intensity" })
                    Check(texts.Contains(expected), $"missing “{expected}”");
                Check(ButtonLabelled(tab, "Group") is not null && ButtonLabelled(tab, "Restore Defaults") is not null, "Group and Restore Defaults");
                Check(texts.Any(t => t.Contains("Ctrl-click")) && !texts.Any(t => t.Contains("⌘")), "Ctrl-click selects");

                var ids = tab.ShownIds.ToList();
                Check(ids.Count > 3, $"the tools wheel has slices: {ids.Count}");
                tab.DropSlice(0, 2);
                var moved = tab.ShownIds.ToList();
                Check(moved[2] == ids[0] && moved.Count == ids.Count, "a slice dragged to another place takes it");
                Check(Settings.WheelArrangement("tools").Order.Length > 0, "and the arrangement is saved");
                tab.DropSlice(2, 2);
                Pump();
                Check(tab.Selection.SequenceEqual([ids[0]]), "a drop selects the slice");
                // Every slice answers at its middle.
                for (var count = 1; count <= 12; count++)
                    for (var index = 0; index < count; index++)
                        Check(WheelLayout.SliceAt(ToOffset(WheelsTab.SliceMiddle(index, count)), count, WheelSize.ExtraLarge) == index, $"{count} slices: the middle of {index}");
                tab.ClickPreview(WheelsTab.SliceMiddle(1, tab.ShownIds.Count));
                Pump();
                Check(tab.Selection.SequenceEqual([tab.ShownIds[1]]) || tab.OpenGroupId is not null, "a click on a slice chooses it");
                Press(ButtonLabelled(tab, "Restore Defaults"));
                Pump();
                Check(Settings.WheelArrangement("tools").IsDefault && tab.ShownIds.SequenceEqual(ids), "Restore Defaults puts it all back, and saves nothing");
                Check(!ButtonLabelled(tab, "Restore Defaults").IsEnabled, "and is off when there's nothing to restore");
            }
            finally
            {
                Settings.WheelSize = size;
                Settings.SetWheelArrangement(new WheelArrangement(), "tools");
                window.Close();
            }
        }));

        Test("Settings", "Wheels: select with Ctrl-click, group, open, rename, take out, ungroup; unchecked slices; rows dropped on the wheel", () => Ui.Run(() =>
        {
            var tab = new WheelsTab();
            tab.Rebuild();
            try
            {
                tab.SelectWheel(WheelKind.Actions);
                Pump();
                var all = tab.ShownIds.ToList();
                Check(all.Count == FileActions.All.Count, $"the actions wheel lists {all.Count} slices");
                tab.ClickRow(all[0], control: false);
                tab.ClickRow(all[1], control: true);
                Check(tab.Selection.SetEquals([all[0], all[1]]), "Ctrl-click adds to the selection");
                tab.ClickRow(all[1], control: true);
                Check(tab.Selection.SetEquals([all[0]]), "and takes out");
                tab.ClickRow(all[1], control: true);
                Check(Walk(tab).OfType<SplitButton>().First(b => (string)b.Content == "Group").IsEnabled, "two selected slices can be grouped");
                tab.MakeGroup([all[0], all[1]]);
                Pump();
                var group = tab.OpenGroupId;
                Check(group is not null && tab.Draft.Entry(group!)!.Members!.SequenceEqual([all[0], all[1]]), "the new group holds them, in wheel order, and is open");
                Check(Settings.WheelArrangement("actions").Groups is { Count: 1 }, "the group is saved");
                Check(tab.ShownIds.SequenceEqual([all[0], all[1]]), "the preview shows the group's own wheel");
                var name = Walk(tab).OfType<TextBox>().First();
                name.Text = "Mine";
                Check(Settings.WheelArrangement("actions").Groups![0].Name == "Mine", "renaming is saved");
                Press(ButtonLabelled(tab, "Ungroup"));
                Pump();
                Check(tab.OpenGroupId is null && tab.Draft.Entries.All(e => !e.IsGroup) && Settings.WheelArrangement("actions").Groups is null, "Ungroup dissolves it");

                tab.MakeGroup([all[0], all[1]]);
                Pump();
                tab.CloseGroup();
                Pump();
                Check(tab.Draft.Entries.Any(e => e.IsGroup) && tab.ShownIds.Count == all.Count - 1, "closed, the group is one slice of the wheel");
                tab.SetVisible(all[2], false);
                Check(!tab.ShownIds.Contains(all[2]) && Settings.WheelArrangement("actions").Hidden.Contains(all[2]), "an unchecked slice leaves the wheel");
                var dropped = tab.DropRow(all[2], WheelsTab.SliceMiddle(0, tab.ShownIds.Count + 1));
                Pump();
                Check(dropped && tab.ShownIds.Contains(all[2]) && tab.ShownIds[0] == all[2] && !tab.Draft.Hidden.Contains(all[2]), $"an unchecked row dropped on the wheel appears there: {string.Join(", ", tab.ShownIds)}");
                Check(!tab.DropRow(all[2], new Point(2, 2)), "a row dropped outside the ring goes nowhere");
            }
            finally { Settings.SetWheelArrangement(new WheelArrangement(), "actions"); }
        }));

        Test("Settings", "Wheels list by keyboard: Up and Down select, Shift extends, Alt moves, Enter opens a group; Shift-click selects a range", () => Ui.Run(() =>
        {
            var tab = new WheelsTab();
            tab.Rebuild();
            try
            {
                tab.SelectWheel(WheelKind.Actions);
                Pump();
                var all = tab.ShownIds.ToList();
                Border Row(string id) => Walk(tab).OfType<Border>().First(b => b.Tag as string == id);
                tab.ClickRow(all[0], control: false);
                PressKey(Row(all[0]), Key.Down);
                Pump();
                Check(tab.Selection.SequenceEqual([all[1]]), "Down moves the selection to the next row");
                PressKey(Row(all[1]), Key.Down);
                PressKey(Row(all[2]), Key.Up);
                Pump();
                Check(tab.Selection.SequenceEqual([all[1]]), "and Up back");
                tab.ClickRow(all[0], control: false);
                tab.ClickRow(all[3], control: false, shift: true);
                Check(tab.Selection.SetEquals(all.Take(4)), "Shift-click selects the range");
                tab.ClickRow(all[2], control: false);
                Check(tab.KeyRow(all[2], Key.Down, ModifierKeys.Shift), "Shift+Down is used");
                Pump();
                Check(tab.Selection.SetEquals([all[2], all[3]]), "Shift+Down extends the selection");
                Check(tab.KeyRow(all[3], Key.Up, ModifierKeys.Alt), "Alt+Up is used");
                Pump();
                Check(tab.ShownIds[2] == all[3], "Alt+Up moves the slice up");
                var help = AutomationProperties.GetHelpText(Row(all[0]));
                Check(help.Contains("Move Up") && help.Contains("Alt+Up") && help.Contains("Alt+Down"), $"the row names its keys: {help}");
                tab.MakeGroup([all[0], all[1]]);
                Pump();
                var group = tab.OpenGroupId!;
                tab.CloseGroup();
                Pump();
                PressKey(Row(group), Key.Enter);
                Pump();
                Check(tab.OpenGroupId == group, "Enter opens a group");
            }
            finally { Settings.SetWheelArrangement(new WheelArrangement(), "actions"); }
        }));

        Test("Settings", "Formats list by keyboard: Up and Down move between rows", () => Ui.Run(() =>
        {
            var tab = new FormatsTab();
            tab.Rebuild();
            IEnumerable<Border> Rows() => Walk(tab).OfType<Border>().Where(b => b.Tag is Format);
            var formats = Rows().Select(b => (Format)b.Tag).ToList();
            var at = formats.IndexOf(Format.Jpg);
            PressKey(Rows().ElementAt(at), Key.Down);
            Check(tab.Chosen == formats[at + 1], "Down chooses the next format");
            PressKey(Rows().ElementAt(at + 1), Key.Up);
            Check(tab.Chosen == Format.Jpg, "Up chooses the one before");
            PressKey(Rows().First(), Key.Up);
            PressKey(Rows().First(), Key.Up);
            Check(tab.Chosen == Format.Jpg, "Up on the first row stays put");
        }));

        Test("Settings", "Popup menus by keyboard: Up, Down, Home, End skip separators and disabled lines, Enter chooses, Esc closes; lines are menu items", () => Ui.Run(() =>
        {
            var picked = "";
            var closed = false;
            var menu = PopupMenu.Build([new("One", () => picked = "One"), new(null), new("Two", () => picked = "Two", Enabled: false), new("Three", () => picked = "Three"), new("Four", () => picked = "Four")], () => closed = true);
            var window = new Window { Content = menu, Width = 200, Height = 200 };
            string Lit() => string.Join(",", Walk(menu).OfType<Border>().Where(b => b.Tag is null && b != menu && AutomationProperties.GetName(b).Length > 0 && b.Background is SolidColorBrush { Color.A: > 0 }).Select(b => AutomationProperties.GetName(b)));
            try
            {
                // A key press makes the keyboard the last device.
                PressKey(null, Key.Down);
                window.Show();
                Pump();
                Check(Lit() == "One", $"opened from the keyboard, the first line is lit: {Lit()}");
                PressKey(menu, Key.Down);
                Check(Lit() == "Three", $"Down skips the separator and the disabled line: {Lit()}");
                PressKey(menu, Key.Down);
                PressKey(menu, Key.Down);
                Check(Lit() == "One", $"and wraps: {Lit()}");
                PressKey(menu, Key.Up);
                Check(Lit() == "Four", $"Up wraps back: {Lit()}");
                PressKey(menu, Key.Home);
                Check(Lit() == "One", "Home");
                PressKey(menu, Key.End);
                Check(Lit() == "Four", "End");
                PressKey(menu, Key.Escape);
                Check(closed && picked == "", "Esc closes and chooses nothing");
                closed = false;
                PressKey(menu, Key.Enter);
                Check(closed && picked == "Four", "Enter chooses the lit line and closes");
                picked = "";
                PressKey(menu, Key.Home);
                PressKey(menu, Key.Space);
                Check(picked == "One", "Space chooses too");
                var item = Walk(menu).OfType<Border>().First(b => AutomationProperties.GetName(b) == "Three");
                var peer = UIElementAutomationPeer.CreatePeerForElement(item);
                Check(peer.GetAutomationControlType() == AutomationControlType.MenuItem && peer.GetName() == "Three", "a line is a menu item with its name");
                Check(UIElementAutomationPeer.CreatePeerForElement(menu).GetAutomationControlType() == AutomationControlType.Menu, "and the list is a menu");
            }
            finally { window.Close(); }
        }));

        Test("Settings", "Look and size: the colour redraws open windows at once; the size and names are saved for the next wheel", () => Ui.Run(() =>
        {
            var window = new SettingsWindow(SettingsTab.Wheels);
            var size = (Settings.WheelSize, Settings.WheelNames);
            try
            {
                window.Show();
                var tab = window.Page(SettingsTab.Wheels);
                Choose(Named<Segmented>(tab, "Color"), "White");
                Pump();
                Check(Settings.LookColor == LookColor.White && ((SolidColorBrush)window.Background).Color == Palette.WheelSurface && Palette.IsWhite, "White: the window is white");
                Check(window.TitleBar.Background is SolidColorBrush { Color: var bar } && bar.R > 200, $"and so is its title bar ({bar})");
                Choose(Named<Segmented>(window.Page(SettingsTab.Wheels), "Color"), "Black");
                Pump();
                Check(Settings.LookColor == LookColor.Black && ((SolidColorBrush)window.Background).Color == Color.FromRgb(42, 42, 42), "Black again");
                var slider = Named<StepSlider>(window.Page(SettingsTab.Wheels), "Size");
                Check(slider.Value == (int)Settings.WheelSize, "the slider shows the size");
                slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Left) { RoutedEvent = Keyboard.KeyDownEvent });
                Check(Settings.WheelSize == (WheelSize)Math.Max(0, slider.Value) && slider.Value >= 0, "the size is saved as it changes");
                Named<Toggle>(window.Page(SettingsTab.Wheels), "Names under icons").Click();
                Check(Settings.WheelNames == !size.WheelNames, "names under icons");
                var layout = WheelLayout.WindowSize(Settings.WheelSize);
                Check(new WheelView([new WheelItem("a", "A", null)], "t", "", [], false).Layout.Size == layout, "a wheel opened afterwards has that size");
            }
            finally
            {
                (Settings.WheelSize, Settings.WheelNames) = size;
                Look.Color = LookColor.Black;
                window.Close();
            }
        }));

        Test("Settings", "Look: the glass background is saved, and its intensity can be set only while it is on", () => Ui.Run(() =>
        {
            var before = (Settings.GlassWheel, Settings.GlassIntensity);
            (Settings.GlassWheel, Motion.Transparency) = (false, true);
            var window = new SettingsWindow(SettingsTab.Wheels);
            try
            {
                window.Show();
                var tab = window.Page(SettingsTab.Wheels);
                var toggle = Named<Toggle>(tab, "Glass background");
                var slider = Named<SliderBar>(tab, "Intensity");
                Check(!toggle.IsOn && !slider.IsEnabled, "off: the intensity waits");
                toggle.Click();
                Check(Settings.GlassWheel, "the toggle saves it");
                Check(slider.IsEnabled == !Motion.IncreaseContrast, "on: the intensity can be set");
                slider.Move(0.8);
                Check(Near(Settings.GlassIntensity, 0.8, 0.01), $"the intensity is saved: {Settings.GlassIntensity}");
                Check(Texts(tab).Any(t => t.Contains("Transparency effects")), "the note says when it stays solid");
                Motion.Transparency = false;
                window.Page(SettingsTab.Wheels).Rebuild();
                Check(!Named<SliderBar>(window.Page(SettingsTab.Wheels), "Intensity").IsEnabled, "with Windows' transparency effects off the intensity waits too");
            }
            finally
            {
                Motion.Transparency = null;
                (Settings.GlassWheel, Settings.GlassIntensity) = before;
                window.Close();
            }
        }));

        Test("Errors", "The failure report: explained, with the details to copy; OK first, then Copy Report", () => Ui.Run(() =>
        {
            Check(FailureReport.Window([]) is null, "nothing to report, no window");
            var problem = new Problem("The disk filled up.", "Free some space.", "write: ENOSPC\nsecond line");
            var window = FailureReport.Window([new JobFailure("a.mov", problem), new JobFailure("b.mov", problem)])!;
            var texts = Texts(window);
            Check(texts.Contains("2 files weren’t processed") && texts.Contains("Each file has the explanation and what you can do."), string.Join(" | ", texts));
            Check(Walk(window).OfType<SplitButton>().Select(b => (string)b.Content).SequenceEqual(["OK", "Copy Report"]), "[OK] [Copy Report]");
            var box = Walk(window).OfType<TextBox>().Single();
            Check(box.IsReadOnly && box.Text.Contains("• a.mov") && box.Text.Contains("What to do: Free some space.") && box.Text.Contains("    write: ENOSPC") && box.Text.Contains("    second line"), box.Text);
            Check(window.ResizeMode == ResizeMode.NoResize && window.Topmost, "a fixed window, in front");
            var one = FailureReport.Window([new JobFailure("c.mov", problem)])!;
            Check(Texts(one).Contains("Couldn’t process “c.mov”") && Texts(one).Any(t => t.Contains("Free some space")), "one failure shows its whole explanation");
            window.Close();
            one.Close();
        }));

        Test("Security", "The settings, help and about windows never touch the network", () =>
        {
            var folder = Path.Combine(Hostile.Project, "src", "Split.App", "Settings");
            foreach (var file in Directory.EnumerateFiles(folder, "*.cs"))
            {
                var code = File.ReadAllText(file);
                foreach (var banned in new[] { "HttpClient", "WebClient", "System.Net", "WebView", "Hyperlink", "http://", "https://", "Process.Start", "NavigateUri" })
                    Check(!code.Replace("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "").Replace("http://schemas.microsoft.com/winfx/2006/xaml", "").Contains(banned),
                          $"{Path.GetFileName(file)} mentions {banned}");
            }
        });

        Test("Interface", "Snapshots: every tab of Settings, How to Use, About and the failure report, in black and in white", () => Ui.Run(() =>
        {
            var folder = Path.Combine(Fixtures.Root, "_captures-windows");
            Directory.CreateDirectory(folder);
            var made = 0;
            var log = ActivityLog.Shared;
            var scratch = Path.Combine(Fixtures.Root, "_settings-captures");
            Directory.CreateDirectory(scratch);
            var output = Path.Combine(scratch, "holiday.pdf");
            File.WriteAllText(output, "x");
            var running = log.Add("Converting to MP4", [@"C:\Users\me\Videos\holiday.mov", "beach.mov"], new CancelToken());
            log.Start(running);
            log.Progress(running, 0.42, "2 of 3 · holiday.mov");
            var queued = log.Add("Converting to PDF", [@"C:\Users\me\Documents\report.docx"], new CancelToken());
            var done = log.Add("Converting to PDF", [@"C:\Users\me\Documents\holiday.docx"], new CancelToken());
            log.Finish(done, ActivityState.Succeeded, "1 file converted", [output], notes: ["PDF can’t keep the document’s comments."]);
            var broken = log.Add("Converting to JPG", [@"C:\Users\me\Pictures\a.heic", "b.heic"], new CancelToken());
            log.Finish(broken, ActivityState.Partial, "1 of 2 converted", [], [new JobFailure("b.heic", new Problem("The file is damaged or incomplete.", "Open it to check it."))]);
            try
            {
                foreach (var look in LookColors.All)
                {
                    var tag = look.Raw();
                    WithLook(look, () =>
                    {
                        var window = new SettingsWindow(SettingsTab.General);
                        foreach (var tab in Enum.GetValues<SettingsTab>())
                        {
                            window.Select(tab);
                            if (tab == SettingsTab.Wheels) ((WheelsTab)window.Page(tab)).ClickRow("jpg", control: false);
                            Ui.Settle(0.15);
                            window.Refresh();
                            Save(folder, $"settings-{tab.ToString().ToLowerInvariant()}-{tag}", Picture(window, SettingsWindow.ContentSize), ref made);
                        }
                        window.Close();

                        var wheels = new SettingsWindow(SettingsTab.Wheels);
                        var wheelsTab = (WheelsTab)wheels.Page(SettingsTab.Wheels);
                        wheelsTab.SelectWheel(WheelKind.Tools);
                        Ui.Settle(0.1);
                        var tools = wheelsTab.ShownIds;
                        wheelsTab.MakeGroup([tools[0], tools[1]]);
                        Ui.Settle(0.1);
                        wheels.Refresh();
                        Save(folder, $"settings-wheels-group-{tag}", Picture(wheels, SettingsWindow.ContentSize), ref made);
                        wheelsTab.CloseGroup();
                        Settings.SetWheelArrangement(new WheelArrangement(), "tools");
                        wheels.Close();

                        var help = new HelpWindow();
                        Save(folder, $"help-{tag}", Picture(help, new Size(540, MeasureHeight(help))), ref made);
                        help.Close();
                        var about = new AboutWindow();
                        Save(folder, $"about-{tag}", Picture(about, new Size(340, MeasureHeight(about))), ref made);
                        about.Close();
                        var report = FailureReport.Window([new JobFailure("b.heic", new Problem("The file is damaged or incomplete.", "Open it to check it.", "magick: corrupt image"))])!;
                        Save(folder, $"failure-report-{tag}", Picture(report, new Size(520, MeasureHeight(report))), ref made);
                        report.Close();
                        return 0;
                    });
                }
            }
            finally
            {
                log.Cancel(running);
                log.Cancel(queued);
                log.ClearFinished();
            }
            Check(made == 2 * (6 + 1 + 1 + 1 + 1), $"only {made} captures were made");
        }));
    }

    private static Pt ToOffset(Point at) => new((at.X - WheelsTab.PreviewSide / 2) / (WheelsTab.PreviewSide / WheelLayout.WindowSize(WheelSize.ExtraLarge)),
                                                 (WheelsTab.PreviewSide / 2 - at.Y) / (WheelsTab.PreviewSide / WheelLayout.WindowSize(WheelSize.ExtraLarge)));

    /// <summary>The height a window that sizes to its contents needs below its title bar.</summary>
    private static double MeasureHeight(AppWindow window)
    {
        var body = window.Body!;
        body.Measure(new Size(window.Width, double.PositiveInfinity));
        return body.DesiredSize.Height;
    }
}
