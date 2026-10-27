using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Split.App;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>The wheel itself, drawn and driven without a person: pointer, keys, groups, the screen reader's view, the window and its rules.</summary>
public static class WheelUiTests
{
    private static readonly string[] Samples =
    [
        "quadrants.jpg", "quadrants.png", "animated.gif", "drawing.svg", "clip.mp4", "tone.mp3", "report.pdf", "letter.docx",
        "sales.xlsx", "prices.csv", "folder.zip", "notes.txt", "page.html", "book.epub", "subs.srt", "data.json",
    ];

    private static WheelView Wheel(int count, bool clickMode = true, bool forWindow = false) =>
        new(Enumerable.Range(0, count).Select(i => new WheelItem(null, $"Option {i + 1}", "circle", $"Detail {i + 1}")),
            "Test", "file.txt", [Fixtures.Url("notes.txt")], clickMode, forWindow: forWindow);

    /// <summary>A real window around the view that is never shown, so the view has a place on screen to report.</summary>
    private static Window Host(WheelView view, bool show = false)
    {
        var window = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Content = view, Width = view.Side, Height = view.Side, Left = -20000, Top = -20000 };
        if (show) window.Show();
        else _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        view.Measure(new Size(view.Side, view.Side));
        view.Arrange(new Rect(0, 0, view.Side, view.Side));
        return window;
    }

    /// <summary>Clockwise from 12 o'clock, like the wheel.</summary>
    private static Point At(WheelView view, double angle, double radius) => new(view.Center.X + radius * Math.Sin(angle), view.Center.Y - radius * Math.Cos(angle));

    private static Point Middle(WheelView view, int index) => At(view, (index + 0.5) * view.Layout.Step, (view.Layout.Inner + view.Layout.Outer) / 2);

    private static List<WheelItem> Group() =>
    [
        new(null, "Option 1", "circle"),
        new("group", "Group", "square.stack", children: [.. Enumerable.Range(0, 3).Select(i => new WheelItem(null, $"Option {i + 1}", "circle"))]),
        new(null, "Option 3", "circle"),
    ];

    private static (int Red, int Green, int Blue) Colour(BitmapSource image, WheelView view, double angle, double radius, double scale = 2)
    {
        var point = At(view, angle, radius);
        var pixel = Ui.PixelAt(image, (int)(point.X * scale), (int)(point.Y * scale));
        return (pixel[2], pixel[1], pixel[0]);
    }

    private static bool Close((int Red, int Green, int Blue) a, (int, int, int) b, int tolerance) =>
        Math.Abs(a.Red - b.Item1) <= tolerance && Math.Abs(a.Green - b.Item2) <= tolerance && Math.Abs(a.Blue - b.Item3) <= tolerance;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    private sealed class RecordingHost(List<FileAction> asked) : IWheelHost
    {
        public void OpenEditor(EditorTool tool, IReadOnlyList<string> paths) { }
        public void Perform(FileAction action, IReadOnlyList<string> paths) => asked.Add(action);
        public void ChooseTargetSize(IReadOnlyList<string> paths, Action<long> chosen) { }
    }

    private sealed class NoticeHud : IHud
    {
        public List<Problem> Notices { get; } = [];
        public void ShowProgress(string title, string item, int index, int count, Guid? jobId = null) { }
        public void UpdateProgress(double fraction, string? detail = null) { }
        public void ShowDone(IReadOnlyList<string> outputs, string message, IReadOnlyList<JobFailure>? failures = null, int total = 1, IReadOnlyList<string>? notes = null, Guid? jobId = null) { }
        public void ShowMessage(string message, string detail = "", bool isError = false) { }
        public void ShowNotice(Problem problem) => Notices.Add(problem);
    }

    public static void Register()
    {
        Test("Interface", "Wheel: each slice answers in the right place; the centre and outside don’t", () => Ui.Run(() =>
        {
            for (var count = 1; count <= 16; count++)
            {
                var view = Wheel(count);
                var layout = view.Layout;
                for (var index = 0; index < count; index++)
                {
                    var start = index * layout.Step;
                    foreach (var (angle, radius) in new[] { (start + layout.Step / 2, (layout.Inner + layout.Outer) / 2), (start + 0.03, layout.Inner + 2), (start + layout.Step - 0.03, layout.Disc + 10) })
                        Check(view.IndexAt(At(view, angle, radius)) == index, $"{count} slices: the point at {angle * 180 / Math.PI:0}° should be {index}, got {view.IndexAt(At(view, angle, radius))}");
                }
                Check(view.IndexAt(view.Center) is null, $"{count} slices: the centre should cancel");
                Check(view.IndexAt(At(view, 0.3, layout.Inner - 20)) is null, $"{count} slices: near the centre should cancel");
                Check(view.IndexAt(At(view, 0.3, layout.Disc + 40)) is null, $"{count} slices: far from the wheel should cancel");
            }
            // The preview in Settings answers from the size alone, and agrees with the wheel.
            foreach (var size in WheelSizes.All)
            {
                foreach (var count in new[] { 1, 2, 5, 8, 13 })
                {
                    var view = new WheelView(Enumerable.Range(0, count).Select(i => new WheelItem(i.ToString(), $"Item {i}", null)), "", "", [], true, new WheelStyle(size, true));
                    for (var step = 0; step < 48; step++)
                    {
                        var angle = step * Math.PI / 24 + 0.01;
                        foreach (var radius in new[] { WheelLayout.InnerRadius(size) - 10, WheelLayout.InnerRadius(size) + 6, WheelLayout.OuterRadius(size) - 6 })
                        {
                            var found = WheelLayout.SliceAt(new Pt(radius * Math.Sin(angle), radius * Math.Cos(angle)), count, size);
                            Check(found == view.IndexAt(At(view, angle, radius)), $"{size}, {count} slices, {step * 360 / 48}°: the preview finds {found}, the wheel {view.IndexAt(At(view, angle, radius))}");
                        }
                    }
                }
            }
        }));

        Test("Interface", "Wheel: the highlighted slice takes the selection where the pointer finds it, on black and on white", () => Ui.Run(() =>
        {
            var color = Settings.LookColor;
            try
            {
                foreach (var (look, selected, surface) in new[] { (LookColor.Black, (80, 80, 80), (42, 42, 42)), (LookColor.White, (206, 206, 206), (245, 245, 245)) })
                {
                    Look.Color = look;
                    var view = Wheel(6);
                    view.PressKey(Key.Right);
                    view.PressKey(Key.Right);
                    Require(view.Highlighted == 1, "two right arrows should highlight slice 1");
                    Ui.Settle(0.6);
                    var image = Ui.Render(view, new Size(view.Side, view.Side), null);
                    var radius = view.Layout.Inner + 12;
                    var inside = Colour(image, view, 1.5 * view.Layout.Step, radius);
                    var neighbour = Colour(image, view, 2.5 * view.Layout.Step, radius);
                    Check(Close(inside, selected, 8), $"{look}: slice 1 should be {selected}, it is {inside}");
                    Check(Close(neighbour, surface, 30), $"{look}: slice 2 should keep the wheel's colour ({neighbour})");
                    // The platter sits inside its slice: a little in from the divider, and short of the rim.
                    var middle = view.Layout.Inner + 40;
                    var nearDivider = Colour(image, view, view.Layout.Step + 2.5 / middle, middle);
                    var inDivider = Colour(image, view, view.Layout.Step + 8 / middle, middle);
                    var pastRim = Colour(image, view, 1.5 * view.Layout.Step, view.Layout.Outer + 8);
                    Check(Close(nearDivider, surface, 30) && Close(inDivider, selected, 12), $"{look}: the platter should start between 2.5 and 8 pt from the divider ({nearDivider}, {inDivider})");
                    Check(Close(pastRim, surface, 30), $"{look}: the platter shouldn’t reach the rim ({pastRim})");
                }
            }
            finally { Look.Color = color; }
        }));

        Test("Interface", "Wheel: icons share one ring and names sit under them, clear of the lines, at every size and language", () => Ui.Run(() =>
        {
            var tally = new WheelLayoutTests.Tally();
            try
            {
                foreach (var language in new[] { Language.En, Language.Pt, Language.Fr, Language.Es })
                {
                    Localizer.Shared.Language = language;
                    var sets = new List<(string Name, List<(string Title, string? Symbol)> Items, bool Crowded)>();
                    foreach (var sample in Samples.Where(s => File.Exists(Fixtures.Url(s))))
                    {
                        string[] paths = [Fixtures.Url(sample)];
                        List<(string, string?)> Of(IEnumerable<WheelItem> items) => [.. items.Select(i => (i.Title, i.Symbol))];
                        var convert = WheelCatalog.Items(WheelKind.Convert, paths);
                        if (convert.Count > 0) sets.Add(($"{sample} convert", Of(convert), false));
                        var tools = WheelCatalog.Items(WheelKind.Tools, paths);
                        if (tools.Count > 0) sets.Add(($"{sample} tools", Of(tools), false));
                        foreach (var group in tools.Where(t => t.IsGroup)) sets.Add(($"{sample} {group.Title}", Of(group.Children), false));
                    }
                    sets.Add(("actions", [.. FileActions.All.Select(a => (a.Title(), (string?)a.Symbol()))], false));
                    // The crowded cases: every tool ungrouped.
                    sets.Add(("all tools", [.. QuickTools.All.Select(t => (t.Title(), (string?)t.Symbol()))], true));
                    sets.Add(("editors", [.. EditorTools.All.Select(t => (t.Title(), (string?)t.Symbol()))], true));
                    foreach (var size in WheelSizes.All)
                    {
                        foreach (var names in new[] { true, false })
                        {
                            foreach (var (name, items, crowded) in sets.Where(s => s.Items.Count > 0))
                                WheelLayoutTests.CheckWheel($"{language.Raw()}, {size.Label()}, names {(names ? "on" : "off")}, {name}", items, new WheelStyle(size, names), WheelView.Text, tally, crowded);
                        }
                    }
                }
            }
            finally { Localizer.Shared.Language = Language.En; }
            Check(tally.Wheels > 1000, $"only {tally.Wheels} wheels were checked");
            Check(tally.TruncatedAtMedium * 20 <= tally.Slices, $"at the usual size few names should be shortened: {tally.TruncatedAtMedium} of {tally.Slices}");
            Check(tally.TruncatedAtLargest.Count == 0, $"on the largest wheel every name shows whole: {string.Join(", ", tally.TruncatedAtLargest.Take(6))}");
            Check(tally.IconsOnly.Count == 0, $"from the usual size up, every wheel has room for its names: {string.Join(", ", tally.IconsOnly.Take(6))}");
        }));

        Test("Interface", "Wheel: a small wheel shortens long names with “…” and keeps the icons in line", () => Ui.Run(() =>
        {
            string[] titles = ["Compress to Size", "Remove Background", "Annotate and Cover", "Organize PDF", "Normalize Volume", "Make Mono", "Trim Silence", "Remove Audio", "Strip Metadata", "Extract Frames"];
            string?[] symbols = [.. titles.Select(_ => "circle")];
            var small = WheelLayout.Make(titles, symbols, new WheelStyle(WheelSize.ExtraSmall, true), WheelView.Text);
            var large = WheelLayout.Make(titles, symbols, new WheelStyle(WheelSize.ExtraLarge, true), WheelView.Text);
            Check(small.Slices.Any(s => s.Truncated), "ten long names on the smallest wheel should not all fit whole");
            Check(small.Slices.Count(s => s.Truncated) > large.Slices.Count(s => s.Truncated), "a bigger wheel shortens fewer names");
            Check(small.FontSize < large.FontSize && small.SymbolSize < large.SymbolSize, "text and icons follow the size of the wheel");
            var rings = small.Slices.Where(s => s.Icon is not null).Select(s => Math.Round(Math.Sqrt(s.Icon!.Value.X * s.Icon.Value.X + Math.Pow(s.Icon.Value.Y - small.IconLift, 2)) * 100)).Distinct().Count();
            Check(rings == 1, "the icons share one ring");
            var iconsOnly = WheelLayout.Make(titles, symbols, new WheelStyle(WheelSize.ExtraSmall, false), WheelView.Text);
            Check(iconsOnly.SymbolSize > small.SymbolSize, "without names the icons grow");
            Check(Math.Abs(iconsOnly.IconRadius - (iconsOnly.Inner + iconsOnly.Outer) / 2) < 0.01, "without names the icons sit mid-ring");
            // The drawn name really ends in an ellipsis: a cut-short box is narrower than the whole name.
            var index = small.Slices.ToList().FindIndex(s => s.Truncated);
            if (index >= 0 && small.Slices[index].Text is { } box)
            {
                var whole = WpfTextMeasurer.Format(titles[index], small.FontSize, WpfTextMeasurer.Weight(false), Colors.Black, box.Width);
                Check(whole.Height > box.Height + 1, $"“{titles[index]}” is marked shortened but fits its box");
            }
        }));

        Test("Interface", "Wheel: keyboard (arrows, Return, Esc) and mouse (press highlights, release confirms)", () => Ui.Run(() =>
        {
            var view = Wheel(5);
            using var _ = new HostScope(Host(view));
            var performed = new List<string>();
            var cancelled = 0;
            view.OnPerform = (item, _) => performed.Add(item.Title);
            view.OnCancel = () => cancelled++;

            view.PressKey(Key.Return);
            Check(performed.Count == 0, "Return without a highlight does nothing");
            view.PressKey(Key.Right);
            Check(view.Highlighted == 0, "→ starts at the first slice");
            view.PressKey(Key.Up);
            Check(view.Highlighted == 1, "↑ moves clockwise");
            view.PressKey(Key.Left);
            view.PressKey(Key.Left);
            Check(view.Highlighted == 4, $"← wraps round to the last slice ({view.Highlighted})");
            view.PressKey(Key.Return);
            Check(performed.SequenceEqual(["Option 5"]), $"Return confirms the highlighted one: {string.Join(",", performed)}");
            view.PressKey(Key.Space);
            Check(performed.Count == 2, "Space confirms too");
            view.PressKey(Key.Escape);
            Check(cancelled == 1, "Esc cancels");

            view.Press(Middle(view, 2));
            Check(view.Highlighted == 2 && performed.Count == 2, "pressing highlights but doesn’t confirm yet");
            view.Dragged(Middle(view, 3));
            Check(view.Highlighted == 3, "dragging moves the highlight");
            view.Release(Middle(view, 3));
            Check(performed.Last() == "Option 4", $"releasing confirms where it is released: {string.Join(",", performed)}");
            view.Press(view.Center);
            view.Release(view.Center);
            Check(performed.Count == 3 && cancelled == 2, "clicking the centre closes the wheel and does nothing");
            var outside = new Point(4, 4);
            view.Press(outside);
            view.Release(outside);
            Check(cancelled == 3, "clicking outside the wheel closes it");
            view.Press(Middle(view, 1));
            view.Release(view.Center);
            Check(performed.Count == 3 && cancelled == 3, "pressing a slice and releasing on the centre does nothing");
            view.Hover(Middle(view, 1));
            Check(view.Highlighted == 1, "hovering highlights");
            view.Leave();
            Check(view.Highlighted is null, "leaving takes the highlight away");
            view.PressKey(Key.Back);
            Check(cancelled == 3, "Backspace outside a group does nothing");
        }));

        Test("Interface", "Wheel: the screen reader sees each slice as a button that can be invoked", () => Ui.Run(() =>
        {
            var view = Wheel(4);
            // Far off screen: a slice can only say where it is on a screen once its window has been shown.
            using var _ = new HostScope(Host(view, show: true));
            var performed = new List<string>();
            view.OnPerform = (item, _) => performed.Add(item.Title);
            var peer = UIElementAutomationPeer.CreatePeerForElement(view);
            var children = peer.GetChildren();
            Require(children.Count == 4, $"4 slices → {children.Count} elements");
            Check(children.Select(c => c.GetName()).SequenceEqual(["Option 1", "Option 2", "Option 3", "Option 4"]), $"labels: {string.Join(",", children.Select(c => c.GetName()))}");
            Check(children.All(c => c.GetAutomationControlType() == AutomationControlType.Button), "each slice is a button");
            Check(children[2].GetHelpText() == "Detail 3", "the detail is the help text");
            Check(children[2].GetBoundingRectangle().Width > 0, "each slice has a place on screen");
            ((IInvokeProvider)children[2].GetPattern(PatternInterface.Invoke)!).Invoke();
            Check(performed.SequenceEqual(["Option 3"]), $"invoking the third slice performs it: {string.Join(",", performed)}");
            Check(peer.GetAutomationControlType() == AutomationControlType.Group && peer.GetName().Contains("Test"), "the wheel is a named group");
            view.PressKey(Key.Right);
            Check(children[0].HasKeyboardFocus(), "the highlighted slice is the focused one");
        }));

        Test("Interface", "Wheel: a group opens as a wheel of its own; Esc, Backspace and the centre go back", () => Ui.Run(() =>
        {
            var view = new WheelView(Group(), "Tools", "file.txt", [Fixtures.Url("notes.txt")], true);
            using var _ = new HostScope(Host(view));
            var performed = new List<string>();
            var cancelled = 0;
            view.OnPerform = (item, _) => performed.Add(item.Title);
            view.OnCancel = () => cancelled++;

            var size = view.Side;
            view.Press(Middle(view, 1));
            view.Release(Middle(view, 1));
            Check(performed.Count == 0 && view.IsInGroup && view.Items.Count == 3 && view.Title == "Group", "clicking a group opens it");
            Check(view.ParentTitle == "Tools" && view.Side == size, "the open group knows its wheel, and the wheel keeps its size");
            var children = UIElementAutomationPeer.CreatePeerForElement(view).GetChildren();
            Check(children.Count == 4 && children[0].GetName() == L("Back to %@", "Tools"), "the screen reader gets a Back button first");
            view.Press(Middle(view, 2));
            view.Release(Middle(view, 2));
            Check(performed.SequenceEqual(["Option 3"]), $"a slice of the group is performed: {string.Join(",", performed)}");

            view.PressKey(Key.Escape);
            Check(!view.IsInGroup && view.Items.Count == 3 && view.Highlighted == 1 && cancelled == 0, "Esc goes back to the wheel, onto the group");
            view.PressKey(Key.Return);
            Check(view.IsInGroup, "Return opens the highlighted group");
            view.PressKey(Key.Back);
            Check(!view.IsInGroup, "Backspace goes back");
            view.PressKey(Key.Space);
            Check(view.IsInGroup, "Space opens it too");
            view.Press(view.Center);
            view.Release(view.Center);
            Check(!view.IsInGroup && cancelled == 0, "clicking the centre of a group goes back");
            view.PressKey(Key.Escape);
            Check(cancelled == 1, "at the top, Esc closes the wheel");
            ((IInvokeProvider)children[0].GetPattern(PatternInterface.Invoke)!).Invoke();
            Check(!view.IsInGroup && cancelled == 1, "…and does nothing once back");
        }));

        Test("Interface", "Wheel: every change is drawn at once, also going back without moving the pointer", () => Ui.Run(() =>
        {
            var view = new WheelView(Group().Take(2), "Tools", "", [], true);
            using var _ = new HostScope(Host(view));
            var group = Middle(view, 1);
            view.Press(group);
            view.Release(group);
            Ui.Settle(0.3);
            var drawn = view.RedrawCount;
            view.Press(view.Center);
            view.Release(view.Center);
            Check(!view.IsInGroup && view.RedrawCount > drawn, $"clicking the centre redraws the wheel it goes back to ({drawn} → {view.RedrawCount} draws)");
            view.Press(group);
            view.Release(group);
            drawn = view.RedrawCount;
            view.Hover(view.Center);
            Check(view.HubHighlighted && view.RedrawCount > drawn, "pointing at the centre of a group lights it up");
            view.StopAnimating();
        }));

        Test("Interface", "Wheel: resting a drag on a group opens it, and on the centre goes back", () => Ui.Run(() =>
        {
            var view = new WheelView(Group().Take(2), "Tools", "", [], false) { IsLive = true };
            using var _ = new HostScope(Host(view));
            Check(view.DragMoved(Middle(view, 1)), "a group takes a drag");
            Check(view.Highlighted == 1 && !view.IsInGroup, "a drag highlights the group without opening it yet");
            Ui.Settle(WheelView.SpringDelay + 0.3);
            Check(view.IsInGroup && view.Items.Count == 3, "after a moment on the group, it opens");
            view.DragMoved(view.Center);
            Ui.Settle(WheelView.SpringDelay + 0.3);
            Check(!view.IsInGroup, "after a moment on the centre, it goes back");
            // Moving on before the moment is up opens nothing.
            view.DragMoved(Middle(view, 1));
            Ui.Settle(0.2);
            view.DragMoved(Middle(view, 0));
            Ui.Settle(WheelView.SpringDelay + 0.2);
            Check(!view.IsInGroup, "a drag that moves on in time doesn’t open the group it passed");
            Check(!view.DragMoved(new Point(2, 2)), "outside the wheel, a drag isn’t taken");
            view.StopAnimating();
        }));

        Test("Interface", "Wheel: files dropped on a group open it for clicking; dropped on a slice they are performed", () => Ui.Run(() =>
        {
            var view = new WheelView(Group().Take(2), "Tools", "", [Fixtures.Url("notes.txt")], false) { IsLive = true };
            using var _ = new HostScope(Host(view));
            var performed = new List<string>();
            var needsClicks = 0;
            view.OnPerform = (item, paths) => performed.Add($"{item.Title}:{paths.Count}");
            view.OnNeedsClicks = () => needsClicks++;
            Check(!view.Dropped([Fixtures.Url("notes.txt")]), "nothing under the pointer takes nothing");
            view.DragMoved(Middle(view, 1));
            Check(view.Dropped([Fixtures.Url("notes.txt"), Fixtures.Url("page.html")]), "a group takes the drop");
            Ui.Settle(0.2);
            Check(view.ClickMode && view.IsInGroup && needsClicks == 1 && view.Paths.Count == 2 && performed.Count == 0, "the wheel is clicked from here on, the files kept");
            view.DragMoved(Middle(view, 0));
            Check(view.Dropped(null), "a slice of the group takes the drop");
            Ui.Settle(0.2);
            Check(performed.SequenceEqual(["Option 1:2"]), $"it is performed with the dropped files: {string.Join(",", performed)}");
            view.StopAnimating();
        }));

        Test("Interface", "Wheel: the window is clear, on top, a drop target, and never takes the focus until the wheel is clicked", () => Ui.Run(() =>
        {
            var window = new WheelWindow(new WheelController { Headless = true });
            var style = window.ExtendedStyle;
            Check((style & WheelWindow.NoActivate) != 0 && (style & WheelWindow.ToolWindow) != 0, "the window is no-activate and a tool window");
            Check(window.AllowsTransparency && window.Topmost && window.AllowDrop && !window.ShowInTaskbar && !window.ShowActivated, "clear, on top, a drop target, off the taskbar, not activated");
            window.SetExStyle(WheelWindow.NoActivate, false);
            Check((window.ExtendedStyle & WheelWindow.NoActivate) == 0, "clicking the wheel lets the window take the keyboard");
            window.Close();
            foreach (var size in WheelSizes.All)
            {
                var view = new WheelView([], "", "", [], false, new WheelStyle(size, true), forWindow: true);
                Check(view.Side == WheelLayout.WindowSize(size) + 2 * WheelView.ShadowMargin, $"{size}: the window is the wheel plus room for its shadow");
            }
        }));

        Test("Interface", "Wheel: a drag probes, a second tap with the wheel up switches in place, text and empty offers go quietly", () => Ui.Run(() =>
        {
            var controller = new WheelController { Headless = true };
            var notices = new NoticeHud();
            var (hud, present) = (Hud.Current, WheelLauncher.Present);
            Hud.Current = notices;
            WheelLauncher.Present = controller.Present;
            try
            {
                string[] photo = [Fixtures.Url("photo.jpg")];
                controller.BeginDrag(WheelKind.Convert);
                Check(controller.State == WheelState.Probing && controller.WaitingForFiles, "a tap during a drag puts the window under the pointer and waits for files");
                controller.BeginDrag(WheelKind.Tools);
                Check(controller.ProbeKind == WheelKind.Tools && controller.WaitingForFiles, "a second tap while waiting changes which wheel will open");
                controller.NotAFileDrag();
                Check(controller.State == WheelState.Hidden && !controller.WaitingForFiles, "text arrived instead of files: the window goes quietly");

                controller.BeginDrag(WheelKind.Convert);
                controller.FileDragEntered(photo);
                var view = controller.View;
                Require(controller.State == WheelState.Shown && view is { ClickMode: false } && view.Title == WheelKind.Convert.Title(), "files arrive: the conversions open");
                Check(!controller.WaitingForFiles, "no more waiting for files");
                Check(view!.Items.Select(i => i.Id).SequenceEqual(WheelCatalog.Items(WheelKind.Convert, photo).Select(i => i.Id)), "with the slices for these files");

                controller.BeginDrag(WheelKind.Tools);
                Check(controller.State == WheelState.Shown && controller.View is { } switched && switched != view && switched.Title == WheelKind.Tools.Title() && switched.Paths.SequenceEqual(photo),
                      "a second tap with the wheel up opens the other wheel for the same files at once");
                Check(!controller.WaitingForFiles, "…without waiting for files to enter again: the drag is already inside");
                controller.NotAFileDrag();
                controller.FileDragEntered(["C:\\other.txt"]);
                Check(controller.State == WheelState.Shown && controller.View!.Title == WheelKind.Tools.Title() && controller.View.Paths.SequenceEqual(photo), "the drag entering again changes nothing");
                controller.Escape();
                Check(controller.State == WheelState.Hidden, "Esc during the drag closes the wheel");

                // Nothing on offer: said so, and the window goes.
                controller.BeginDrag(WheelKind.Convert);
                controller.FileDragEntered([Fixtures.Url("mystery.xyz")]);
                Check(controller.State == WheelState.Hidden && notices.Notices.Count == 1, "files nothing converts say why, and no wheel opens");

                // A probe nothing enters leaves by itself.
                controller.BeginDrag(WheelKind.Convert);
                Ui.Settle(WheelController.NotAFileDragAfter + 0.2);
                Check(controller.State == WheelState.Hidden, "no files within a moment: not a file drag");

                // The button coming up closes a drag's wheel, but not one that is being clicked.
                controller.BeginDrag(WheelKind.Convert);
                controller.FileDragEntered(photo);
                controller.DragEnded();
                Ui.Settle(WheelController.CloseAfterDrag + 0.2);
                Check(controller.State == WheelState.Hidden, "the button came up: the wheel closes");
                WheelLauncher.Show(WheelKind.Tools, photo, clickMode: true);
                Check(controller.State == WheelState.Shown && controller.View!.ClickMode, "opened from the menu, the wheel is clicked");
                controller.DragEnded();
                controller.Escape();
                Ui.Settle(WheelController.CloseAfterDrag + 0.2);
                Check(controller.State == WheelState.Shown, "a clicked wheel stays open when a drag ends, and Esc from the monitor leaves it to the keyboard");
                controller.View!.PressKey(Key.Escape);
                Check(controller.State == WheelState.Hidden, "its own Esc closes it");
                WheelLauncher.Show(WheelKind.Convert, [Fixtures.Url("mystery.xyz")], clickMode: true);
                Check(controller.State == WheelState.Hidden, "nothing to offer, nothing opens");
            }
            finally
            {
                Hud.Current = hud;
                WheelLauncher.Present = present;
                controller.Hide(animated: false);
            }
        }));

        Test("Interface", "Wheel: the probe is a clear window under the pointer, the wheel’s size, in the work area, and it goes when no files come", () => Ui.Run(() =>
        {
            var controller = new WheelController();
            try
            {
                controller.BeginDrag(WheelKind.Convert);
                var window = controller.Window;
                Require(window is { IsVisible: true } && controller.State == WheelState.Probing, "the window is up as soon as the key is tapped");
                Check((window!.ExtendedStyle & WheelWindow.NoActivate) != 0, "…without taking the focus");
                GetWindowRect(window.Handle, out var rect);
                var placed = controller.Placed;
                Check(rect.Left == (int)placed.X && rect.Top == (int)placed.Y, $"it is where it was placed: {rect.Left},{rect.Top} and {placed.X},{placed.Y}");
                var side = (WheelLayout.WindowSize(Settings.WheelSize) + 2 * WheelView.ShadowMargin) * placed.Scale;
                Check(Math.Abs(rect.Right - rect.Left - side) <= 1 && Math.Abs(rect.Bottom - rect.Top - side) <= 1, $"it is the size of the wheel and its shadow ({rect.Right - rect.Left} px, {side:0})");
                Ui.Settle(WheelController.NotAFileDragAfter + 0.25);
                Check(controller.State == WheelState.Hidden && !window.IsVisible, "no files: the window is gone");
            }
            finally { controller.Hide(animated: false); }
        }));

        Test("Interface", "Wheel: a slice is performed with the files; the actions go to the app, the shelf among them", () => Ui.Run(() =>
        {
            var controller = new WheelController { Headless = true };
            var (host, present, hud) = (WheelHost.Current, WheelLauncher.Present, Hud.Current);
            var notices = new NoticeHud();
            WheelLauncher.Present = controller.Present;
            Hud.Current = notices;
            try
            {
                var done = new List<string>();
                var item = new WheelItem("test", "Test", "circle", action: paths => done.AddRange(paths));
                controller.Present(new WheelRequest(WheelKind.Tools, [item], "Tools", "photo.jpg", [Fixtures.Url("photo.jpg")], ClickMode: true));
                controller.View!.Choose(0);
                Check(controller.State == WheelState.Hidden && done.SequenceEqual([Fixtures.Url("photo.jpg")]), "choosing closes the wheel and runs the slice with the files");

                var actions = WheelCatalog.Items(WheelKind.Actions, [Fixtures.Url("notes.txt")]);
                Check(actions.Select(a => a.Id).SequenceEqual(["shelf", "copy", "copyPaths", "move", "terminal"]), "the five actions");
                var asked = new List<FileAction>();
                WheelHost.Current = new RecordingHost(asked);
                foreach (var action in actions) action.Action([Fixtures.Url("notes.txt")]);
                Check(asked.SequenceEqual(FileActions.All), "each slice asks the app for its own action");

                // The app does the clipboard itself, with nothing to complain about.
                WheelHost.Current = new AppWheelHost();
                new AppWheelHost().Perform(FileAction.Copy, [Fixtures.Url("notes.txt")]);
                Check(notices.Notices.Count == 0 && System.Windows.Clipboard.GetFileDropList().Count == 1, "an action the app does says nothing");
            }
            finally
            {
                WheelHost.Current = host;
                WheelLauncher.Present = present;
                Hud.Current = hud;
            }
        }));

        Test("Interface", "Wheel: pictures and snapshots in black and in white, every size", () => Ui.Run(() =>
        {
            var folder = Path.Combine(Fixtures.Root, "_captures-windows");
            Directory.CreateDirectory(folder);
            var made = 0;
            var color = Settings.LookColor;
            void Save(string name, BitmapSource image)
            {
                Check(Ui.Spread(image) > 8, $"{name}: the image is almost uniform: was the view empty?");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(folder, name + ".png"));
                encoder.Save(stream);
                made++;
            }
            BitmapSource Shot(WheelView view)
            {
                Ui.Settle(0.5);
                return Ui.Render(view, new Size(view.Side, view.Side), Color.FromRgb(89, 89, 89));
            }
            try
            {
                foreach (var look in LookColors.All)
                {
                    Look.Color = look;
                    var name = look.Raw();
                    string[] photo = [Fixtures.Url("quadrants.jpg")];
                    var convert = new WheelView(WheelCatalog.Items(WheelKind.Convert, photo), WheelKind.Convert.Title(), "quadrants.jpg", photo, true, forWindow: true);
                    Save($"wheel-convert-{name}", Shot(convert));
                    var tools = new WheelView(WheelCatalog.Items(WheelKind.Tools, photo), WheelKind.Tools.Title(), "quadrants.jpg", photo, true, forWindow: true);
                    Save($"wheel-tools-{name}", Shot(tools));
                    tools.PressKey(Key.Right);
                    tools.PressKey(Key.Right);
                    Save($"wheel-tools-highlight-{name}", Shot(tools));
                    var group = tools.Items.FindIndex(i => i.IsGroup);
                    if (group >= 0)
                    {
                        tools.OpenGroup(group);
                        tools.PressKey(Key.Right);
                        Save($"wheel-group-{name}", Shot(tools));
                    }
                    else Check(false, "the photo tools have no group");
                    var actions = new WheelView(WheelCatalog.Items(WheelKind.Actions, photo), WheelKind.Actions.Title(), "2 files", photo, true, forWindow: true);
                    Save($"wheel-actions-{name}", Shot(actions));
                    string[] video = [Fixtures.Url("clip.mp4")];
                    var busy = new WheelView(WheelCatalog.Items(WheelKind.Tools, video), WheelKind.Tools.Title(), "clip.mp4", video, true, forWindow: true);
                    Save($"wheel-video-tools-{name}", Shot(busy));
                    foreach (var (size, names) in new[] { (WheelSize.ExtraSmall, true), (WheelSize.ExtraSmall, false), (WheelSize.Small, true), (WheelSize.Medium, true), (WheelSize.Large, true), (WheelSize.ExtraLarge, true) })
                    {
                        var all = QuickTools.All.Select(t => new WheelItem(null, t.Title(), t.Symbol()));
                        var wheel = new WheelView(all, WheelKind.Tools.Title(), "", [], true, new WheelStyle(size, names), forWindow: true);
                        Save($"wheel-{size}-{(names ? "names" : "icons")}-{name}", Shot(wheel));
                    }
                }
            }
            finally { Look.Color = color; }
            Check(made == 2 * 12, $"only {made} captures were made");
        }));
    }

    /// <summary>Closes the window that hosts a view when the test is done.</summary>
    private sealed class HostScope(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
