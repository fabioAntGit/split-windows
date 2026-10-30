using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Split.App;
using Split.Windows;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>The interface, tested without a person: elements built on a thread of their own, colour maths, and captures saved for a visual review.</summary>
public static class UiTests
{
    public static void Register()
    {
        Test("Interface", "Every symbol used exists", () =>
        {
            var names = new HashSet<string>();
            var literal = new Regex("(?:Icons\\.Block\\(|Symbol\\()\\s*\"([^\"]+)\"");
            foreach (var file in Hostile.Sources().Where(f => f.Contains($"{Path.DirectorySeparatorChar}Split.App{Path.DirectorySeparatorChar}")))
            {
                foreach (Match match in literal.Matches(File.ReadAllText(file))) names.Add(match.Groups[1].Value);
            }
            foreach (var name in Icons.Names.Keys) names.Add(name);
            QuickTools.All.ToList().ForEach(tool => names.Add(tool.Symbol()));
            EditorTools.All.ToList().ForEach(tool => names.Add(tool.Symbol()));
            FileActions.All.ToList().ForEach(action => names.Add(action.Symbol()));
            StandardGroups.All.Select(group => group.Symbol()).OfType<string>().ToList().ForEach(symbol => names.Add(symbol));
            // The tray menu's own symbols.
            TrayMenu.Build(() => { }).Select(entry => entry.Symbol).OfType<string>().ToList().ForEach(symbol => names.Add(symbol));
            Check(names.Count >= 40, $"only {names.Count} symbols found: did the search stop working?");
            foreach (var name in names.Order(StringComparer.Ordinal))
                Check(Icons.Find(name) is not null, $"the symbol “{name}” has no icon in the font");
            Check(Icons.Find("checkmark.circle.fill") is { Filled: true } && Icons.Find("checkmark.circle") is { Filled: false }, "“.fill” picks the filled form");
        });

        Test("Interface", "Black and white: text on the selection ≥ 4.5:1, controls ≥ 3:1, ink on the highlighted slice ≥ 4.5:1", () =>
        {
            var color = Settings.LookColor;
            try
            {
                foreach (var look in LookColors.All)
                {
                    Look.Color = look;
                    var surface = Palette.WheelSurface;
                    Check(Palette.Contrast(Palette.Selection, Palette.OnSelection) >= 4.5, $"{look}: text on the selection: {Palette.Contrast(Palette.Selection, Palette.OnSelection):0.00}");
                    Check(Palette.Contrast(Palette.Accent, surface) >= 3, $"{look}: accent against the window");
                    Check(Palette.Contrast(Palette.ControlTint, surface) >= 3, $"{look}: control tint against the window");
                    var selection = Palette.Luminance(Palette.Selection);
                    Check(selection > 0.95 || selection < 0.01, $"{look}: the selection is white or black");
                    Check(Palette.Contrast(Palette.WheelInk, surface) >= 4.5, $"{look}: names on the wheel");
                    // The highlighted slice is a veil of the ink over the surface, judged as laid on it.
                    var slice = Palette.Over(Palette.WheelSelection, surface);
                    Check(Palette.Contrast(Palette.WheelInk, slice) >= 4.5, $"{look}: names on the highlighted slice: {Palette.Contrast(Palette.WheelInk, slice):0.00}");
                    Check(Palette.Contrast(slice, surface) >= 1.3, $"{look}: the highlighted slice against the wheel: {Palette.Contrast(slice, surface):0.00}");
                    Check(Palette.Contrast(Palette.Primary, surface) >= 4.5 && Palette.Contrast(Palette.Secondary, surface) >= 4.5, $"{look}: text on the window");
                }
                Look.Color = LookColor.Black;
                Check(Palette.Luminance(Palette.WheelSurface) is > 0.015 and < 0.04, "black is a soft black, not pitch black");
                Check(Palette.WheelSurface == Color.FromRgb(42, 42, 42) && Palette.Selection == Colors.White && Palette.OnSelection == Color.FromRgb(10, 10, 10), "the dark values");
                Look.Color = LookColor.White;
                Check(Palette.WheelSurface == Color.FromRgb(245, 245, 245) && Palette.Selection == Color.FromRgb(20, 20, 20) && Palette.WheelInk == Color.FromRgb(26, 26, 26), "the light values");
            }
            finally { Look.Color = color; }
        });

        Test("Interface", "The logo: six slices apart from each other around an open centre; the tray icon is drawn in the taskbar's colour", () => Ui.Run(() =>
        {
            const double cx = 100, cy = 100, radius = 80, step = 2 * Math.PI / LogoGeometry.Slices;
            // Angles run anticlockwise from three o'clock, as in the outline; the screen's y points down.
            Point At(double angle, double r) => new(cx + r * Math.Cos(angle), cy - r * Math.Sin(angle));
            for (var index = 0; index < LogoGeometry.Slices; index++)
            {
                var path = Logo.Slice(index, cx, cy, radius);
                var middle = Math.PI / 2 - (index + 0.5) * step;
                var border = Math.PI / 2 - index * step;
                Check(path.FillContains(At(middle, 50)), $"slice {index} misses its own middle");
                Check(!path.FillContains(At(border, 50)) && !path.FillContains(At(border - step, 50)), $"slice {index} reaches over a gap");
                Check(!path.FillContains(new Point(cx, cy)) && !path.FillContains(At(middle, 20)), $"slice {index} closes the centre");
                Check(!path.FillContains(At(middle, 81)) && path.FillContains(At(middle, 78)), $"slice {index} doesn’t end at the rim");
            }
            foreach (var (pixels, light) in new[] { (16, false), (24, true), (32, false) })
            {
                var bytes = Logo.TrayIcon(pixels, light);
                Check(bytes.Length > 100 && bytes[2] == 1 && bytes[4] == 1, "the tray icon is an .ico with one image");
                var frame = new IconBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                Check(frame.PixelWidth == pixels && frame.PixelHeight == pixels, $"the icon is {pixels} pixels");
                var rgba = new byte[pixels * pixels * 4];
                new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(rgba, pixels * 4, 0);
                byte[] Pixel(double angle, double share)
                {
                    var point = new Point(pixels / 2.0 + share * pixels / 2 * Math.Cos(angle), pixels / 2.0 - share * pixels / 2 * Math.Sin(angle));
                    var offset = ((int)point.Y * pixels + (int)point.X) * 4;
                    return rgba[offset..(offset + 4)];
                }
                var chosen = Pixel(Math.PI / 2 - 0.5 * step, 0.65);
                var other = Pixel(Math.PI / 2 - 3.5 * step, 0.65);
                Check(chosen[3] > 240 && other[3] is > 40 and < 140, $"{pixels}px: the chosen slice is solid ({chosen[3]}), the others faint ({other[3]})");
                Check(chosen[0] == (light ? 0 : 255), $"{pixels}px: {(light ? "black on a light" : "white on a dark")} taskbar");
                Check(Pixel(Math.PI / 2 - 0.5 * step, 0.05)[3] == 0, $"{pixels}px: the centre is open");
            }
        }));

        Test("Interface", "The application icon: every size Windows asks for, and the file in the project has them", () => Ui.Run(() =>
        {
            var drawn = Logo.AppIcon(Logo.AppIconSizes);
            var frames = new IconBitmapDecoder(new MemoryStream(drawn), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            Check(frames.Select(frame => frame.PixelWidth).SequenceEqual([16, 20, 24, 32, 40, 48, 64, 256]), "sizes 16, 20, 24, 32, 40, 48, 64 and 256");
            Check(frames.All(frame => frame.PixelHeight == frame.PixelWidth), "square images");
            var shipped = File.ReadAllBytes(Path.Combine(Hostile.Project, "src", "Split.App", "Split.ico"));
            var shippedSizes = new IconBitmapDecoder(new MemoryStream(shipped), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Select(frame => frame.PixelWidth);
            Check(shippedSizes.SequenceEqual(Logo.AppIconSizes), "Split.ico in the project has the same sizes (tools/MakeIcon draws it)");
        }));

        Test("Interface", "HUD: states, texts, notes and plurals", () =>
        {
            var hud = NotificationHud.Shared;
            hud.IsHeadless = true;
            var model = hud.Model;
            string[] output = [Fixtures.Url("quadrants.png")];
            hud.ShowProgress("Converting", "photo.heic", 1, 4);
            Check(model.State == HudState.Progress && model.Detail == "2 of 4 · photo.heic" && model.Fraction == 0.25, $"progress: {model.Detail}");
            hud.UpdateProgress(0.5);
            Check(model.Fraction == 0.5, "progress should move on");
            var job = Guid.NewGuid();
            hud.ShowDone(output, "1 file converted", jobId: job);
            Check(model.State == HudState.Success && model.Detail.Contains("quadrants.png") && model.Detail.Contains("next to the original"), model.Detail);
            Check(model.UndoJob == job, "a finished job can be undone from the card");
            hud.UpdateProgress(0.9);
            Check(model.Fraction == null, "late progress can’t reopen a result");
            hud.ShowDone(output, "1 file converted", notes: ["JPG has no transparency."]);
            Check(model.State == HudState.Warning && model.Detail.Contains("JPG has no transparency."), $"notes are shown: {model.Detail}");
            Check(model.UndoJob == null, "without a job there is nothing to undo");
            var problem = new Problem("The disk filled up.", "Free some space.");
            hud.ShowDone(output, "1 of 3 converted", [new("a.mov", problem), new("b.mov", problem)], total: 3);
            Check(model.State == HudState.Warning && model.Detail.Contains("a.mov") && model.Detail.Contains("1 more"), $"warning: {model.Detail}");
            hud.ShowDone([], "Nothing converted", [new("a.mov", problem)], total: 1);
            Check(model.State == HudState.Failure && model.Detail.Contains("Free some space"), $"a single failure shows the suggestion: {model.Detail}");
            hud.ShowNotice(new Problem("Folders can’t be converted.", "Use Create ZIP."));
            Check(model.State == HudState.Warning && model.Detail == "Use Create ZIP.", "a notice, without the error sound");
            hud.ShowMessage("Done", "Detail");
            Check(model.State == HudState.Info && model.Title == "Done", "a message is information");
            hud.ShowError(new Problem("It broke.", "Try again.", "tool said no"));
            Check(model.State == HudState.Failure && model.Failures.Count == 1, "an error with technical details can show them");
            hud.Hide();
            var verbs = new[] { JobRunner.Phrase(1, (L("1 file converted"), L("%@ files converted"))), JobRunner.Phrase(3, (L("1 file converted"), L("%@ files converted"))) };
            Check(verbs.SequenceEqual(["1 file converted", "3 files converted"]), $"plurals: {string.Join(" / ", verbs)}");
        });

        Test("Interface", "Sounds: short and soft, starting and ending in silence, the wheel’s tick the faintest", () =>
        {
            foreach (var (name, samples, longest, loudest) in new[] { ("done", Sounds.DoneDrop, 0.15, 0.4), ("error", Sounds.ErrorDrops, 0.25, 0.4), ("wheel", Sounds.WheelTick, 0.06, 0.15) })
            {
                Check(samples.Length > 0 && samples.Length / (double)SoundOut.Rate <= longest, $"{name}: {samples.Length * 1000 / SoundOut.Rate} ms");
                Check(samples.Max(Math.Abs) <= loudest, $"{name}: peak {samples.Max(Math.Abs):0.00}");
                Check(Math.Abs(samples[0]) < 0.01 && Math.Abs(samples[^1]) < 0.01, $"{name}: no click at either end");
            }
            List<(float[], int)> playing = [([0.5f, 0.5f, 0.5f], 0), ([0.75f, 0.75f], 0)];
            var mixed = SoundOut.Mix(playing, 2);
            Check(mixed.SequenceEqual([1f, 1f]), $"sounds that overlap add up, kept within full scale: {string.Join(", ", mixed)}");
            Check(playing.Count == 1 && playing[0].Item2 == 2, "the one that ended is taken out, the other goes on where it was");
        });

        Test("Windows", "Sounds: quick ones all reach the device together, which rests once they end", () =>
        {
            Require(SoundOut.Prepare().Wait(TimeSpan.FromSeconds(10)), "the audio graph opens");
            if (SoundOut.Available != true) Skip("this PC has no audio output");
            var before = SoundOut.Mixed;
            var started = System.Diagnostics.Stopwatch.StartNew();
            SoundOut.Play(new float[Sounds.WheelTick.Length]);
            while (SoundOut.Mixed == before && started.ElapsedMilliseconds < 2000) Thread.Sleep(1);
            Check(started.ElapsedMilliseconds < 150, $"the first one reaches the device in {started.ElapsedMilliseconds} ms");
            Thread.Sleep(300);
            before = SoundOut.Mixed;
            started.Restart();
            SoundOut.Play(new float[Sounds.WheelTick.Length]);
            while (SoundOut.Mixed == before && started.ElapsedMilliseconds < 2000) Thread.Sleep(1);
            Check(started.ElapsedMilliseconds < 50, $"with the stream open, the next in {started.ElapsedMilliseconds} ms");
            Thread.Sleep(300);
            before = SoundOut.Mixed;
            for (var i = 0; i < 3; i++) SoundOut.Play(new float[Sounds.WheelTick.Length]);
            var until = DateTime.UtcNow.AddSeconds(2);
            while (SoundOut.Mixed - before < Sounds.WheelTick.Length && DateTime.UtcNow < until) Thread.Sleep(10);
            Check(SoundOut.Mixed - before >= Sounds.WheelTick.Length, $"the device took them: {SoundOut.Mixed - before} samples");
            Thread.Sleep(300);
            Check(SoundOut.Mixed - before < 2 * Sounds.WheelTick.Length, $"played over each other, not one after the other: {SoundOut.Mixed - before} samples");
            until = DateTime.UtcNow.AddSeconds(5);
            while (SoundOut.IsRunning && DateTime.UtcNow < until) Thread.Sleep(50);
            Check(!SoundOut.IsRunning, "the audio stream closes when there is nothing to hear, so the PC can sleep");
        });

        Test("Interface", "HUD: the card sits in the corner beside the tray, wherever the taskbar is", () =>
        {
            var work = new Box(0, 0, 1920, 1040);
            var bottom = HudPlacement.Frame(work, ScreenEdge.Bottom, 360, 110, HudCard.Inset);
            Check(bottom.Right - HudCard.Inset == 1920 - 12 && bottom.Bottom - HudCard.Inset == 1040 - 8, $"bottom taskbar: 12 from the right and 8 from the bottom ({bottom})");
            var top = HudPlacement.Frame(new Box(0, 40, 1920, 1040), ScreenEdge.Top, 360, 110, HudCard.Inset);
            Check(top.Y + HudCard.Inset == 40 + 8 && top.Right - HudCard.Inset == 1920 - 12, $"top taskbar: under it, on the right ({top})");
            var left = HudPlacement.Frame(new Box(60, 0, 1860, 1080), ScreenEdge.Left, 360, 110, HudCard.Inset);
            Check(left.X + HudCard.Inset == 60 + 12 && left.Bottom - HudCard.Inset == 1080 - 8, $"left taskbar: at its bottom ({left})");
            var right = HudPlacement.Frame(new Box(0, 0, 1860, 1080), ScreenEdge.Right, 360, 110, HudCard.Inset);
            Check(right.Right - HudCard.Inset == 1860 - 12 && right.Bottom - HudCard.Inset == 1080 - 8, $"right taskbar: next to it ({right})");
            Check(HudPlacement.SlideDirection(ScreenEdge.Left) == -1 && HudPlacement.SlideDirection(ScreenEdge.Bottom) == 1, "it slides in from the side it sits on");
        });

        Test("Interface", "HUD: the window never takes the focus, and nothing is drawn in the margin around the card", () => Ui.Run(() =>
        {
            var window = NotificationHud.Shared.MakeWindow();
            var style = window.ExtendedStyle;
            Check((style & HudWindow.NoActivate) != 0 && (style & HudWindow.ToolWindow) != 0, "the window is no-activate and a tool window");
            Check(window.AllowsTransparency && window.Topmost && !window.ShowInTaskbar && !window.ShowActivated, "clear, on top, off the taskbar, not activated");
            Check(window.Width == HudCard.CardWidth + 2 * HudCard.Inset, $"the window is the card plus its margin ({window.Width})");

            var model = new HudModel { State = HudState.Success, Title = "3 files converted", Detail = "The results are next to the originals." };
            var card = new HudCard(model, Ui.NoActions);
            var image = Ui.Render(card, new Size(HudCard.CardWidth + 2 * HudCard.Inset, 110), null);
            var corner = Ui.PixelAt(image, 1, 1);
            Check(corner[3] == 0, $"nothing is drawn in the margin around the card (it would be cut off): {string.Join(",", corner)}");
            Check(HudCard.Inset >= 6, "room for the close button on the corner");
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Check(card.DesiredSize.Width == 360, $"the card is 344 wide inside its margin: {card.DesiredSize.Width}");
        }));

        Test("Interface", "HUD: the buttons fit the card, and Open gives way first", () => Ui.Run(() =>
        {
            var model = new HudModel { State = HudState.Success, Title = "t", Detail = "d", Outputs = ["a.png"], UndoJob = Guid.NewGuid() };
            var card = new HudCard(model, Ui.NoActions);
            // With Undo beside it, "Open" would not fit in 344: it gives way.
            Check(Ui.Buttons(card).SequenceEqual(["Show in File Explorer", "Undo"]), $"Open gives way to Undo: {string.Join(" | ", Ui.Buttons(card))}");
            model.UndoJob = null;
            model.Notify();
            Check(Ui.Buttons(card).SequenceEqual(["Show in File Explorer", "Open"]), $"a single result: {string.Join(" | ", Ui.Buttons(card))}");
            model.UndoJob = Guid.NewGuid();
            model.Outputs = ["a.png", "b.png"];
            model.Notify();
            Check(Ui.Buttons(card).SequenceEqual(["Show in File Explorer", "Undo"]), "several results have no Open");
            model.State = HudState.Warning;
            model.Failures = [new("a.mov", new Problem("x"))];
            model.UndoJob = null;
            model.Notify();
            Check(Ui.Buttons(card).SequenceEqual(["Details…", "Show in File Explorer"]), $"with failures: {string.Join(" | ", Ui.Buttons(card))}");
            model.State = HudState.Progress;
            model.JobId = Guid.NewGuid();
            model.Notify();
            Check(Ui.Buttons(card).SequenceEqual(["Cancel", "Show Activity"]), "progress can be cancelled");
            model.JobId = null;
            model.Notify();
            Check(Ui.Buttons(card).SequenceEqual(["Show Activity"]), "progress of something that can’t be cancelled");
        }));

        Test("Interface", "Buttons: the selection’s colours, a hairline edge, a tool that turns to the selection", () => Ui.Run(() =>
        {
            var color = Settings.LookColor;
            try
            {
                foreach (var look in LookColors.All)
                {
                    Look.Color = look;
                    var primary = new SplitButton("Go", ButtonKind.Primary);
                    var secondary = new SplitButton("Go");
                    var tool = new SplitButton("T", ButtonKind.Tool, selected: true);
                    Check(((SolidColorBrush)primary.Face.Background).Color == Palette.Selection && ((SolidColorBrush)primary.Foreground).Color == Palette.OnSelection, $"{look}: primary");
                    Check(((SolidColorBrush)secondary.Face.Background).Color == Palette.With(Palette.Primary, 0.1), $"{look}: secondary fill");
                    Check(((SolidColorBrush)tool.Face.Background).Color == Palette.Selection && tool.Width == 30, $"{look}: the chosen tool");
                    Check(primary.MinHeight >= 28 && secondary.MinHeight >= 28 && new SplitButton("x", compact: true).MinHeight == 24, $"{look}: heights");
                    secondary.IsEnabled = false;
                    Check(secondary.Opacity == 0.4, $"{look}: a disabled button is at 0.4");
                }
            }
            finally { Look.Color = color; }
        }));

        Test("Shelf", "The same file is held once, files that are gone disappear, and the window floats without taking the focus", () => Ui.Run(() =>
        {
            var model = new ShelfModel();
            var quadrants = Fixtures.Url("quadrants.png");
            model.Add([quadrants, quadrants.ToUpperInvariant(), Fixtures.Url("notes.txt")]);
            Check(model.Items.Count == 2, $"the same file is held once: {model.Items.Count}");
            model.Add([Fixtures.Url("does-not-exist.txt")]);
            model.Prune();
            Check(model.Items.Count == 2, "files that are gone disappear from the shelf");
            model.Remove(quadrants);
            Check(model.Items.Count == 1, "a file can be taken off");

            var window = Shelf.Shared.MakeWindow();
            Check(window.Topmost && !window.ShowInTaskbar && window.WindowStyle == WindowStyle.ToolWindow && window.Title == "Shelf", "a small window on top, off the taskbar");
            Check(window.MinWidth == 240 && window.MinHeight == 220 && window.Width == 280 && window.Height == 380, "280 × 380, at least 240 × 220");
            var handle = new DragAllHandle(() => []);
            Check(handle.Cursor == System.Windows.Input.Cursors.Hand && System.Windows.Automation.AutomationProperties.GetName(handle) == "Drag All", "“Drag All” has a hand and a name for the narrator");
            Check(!Shelf.Shared.IsVisible, "not shown until asked");
        }));

        Test("Shelf", "It opens against the right edge of the screen under the pointer, at that screen's scale", () => Ui.Run(() =>
        {
            var window = Shelf.Shared.MakeWindow();
            window.Opacity = 0;
            window.Show();
            Shelf.Place(window);
            var (work, scale) = Desktop.MonitorAtCursor();
            var (left, top) = (window.Left * scale, window.Top * scale);
            window.Hide();
            Check(Near(left + 280 * scale, work.Right - 20 * scale, 1.5), $"20 from the right edge: {left + 280 * scale} against {work.Right - 20 * scale}");
            Check(Near(top + 190 * scale, work.Y + work.Height / 2, 1.5), $"centred from top to bottom: {top + 190 * scale} against {work.Y + work.Height / 2}");
        }));

        Test("Tray", "The menu: Convert, Tools, Actions, Shelf, Activity, the keys, Settings, How to Use, About, Quit", () =>
        {
            var entries = TrayMenu.Build(() => { });
            var titles = entries.Select(entry => entry.Title).ToList();
            string?[] expected =
            [
                "Convert Files…", "Tools…", "Actions…", "Open the Shelf", "Activity…", null,
                "While dragging, tap: Shift conversion  ·  Ctrl tools  ·  Alt actions", null,
                "Settings…", "How to Use", "About Split", null, "Quit Split",
            ];
            Check(titles.SequenceEqual(expected), $"the menu reads: {string.Join(" | ", titles.Select(t => t ?? "—"))}");
            Check(!entries[6].Enabled && entries.Where((e, i) => i != 6 && !e.IsSeparator).All(e => e.Enabled && e.Run is not null), "only the hint is disabled; every other line does something");
            Check(entries.Where(e => e.Symbol is not null).All(e => Icons.Find(e.Symbol!) is not null), "every symbol of the menu has an icon");

            var id = ActivityLog.Shared.Add("A job", [], new CancelToken());
            try { Check(TrayMenu.Build(() => { })[4].Title == "Activity (1 running)…", "the running jobs are counted"); }
            finally { ActivityLog.Shared.Cancel(id); ActivityLog.Shared.ClearFinished(); }

            ModifierChoices.Set(ModifierChoice.Off, WheelKind.Actions);
            try { Check(TrayMenu.Build(() => { })[6].Title!.EndsWith("Off actions"), $"the hint follows the keys chosen: {TrayMenu.Build(() => { })[6].Title}"); }
            finally { ModifierChoices.Set(WheelKind.Actions.DefaultModifier(), WheelKind.Actions); }
        });

        Test("Windows", "A second start hands its files to the first, which gets them together with its own; the pipe is the user’s own", () =>
        {
            var name = "SplitTest-" + Guid.NewGuid().ToString("N");
            using var first = new SingleInstance(name);
            using var second = new SingleInstance(name);
            Require(first.IsPrimary && !second.IsPrimary, "only the first start owns the instance");
            var arrivals = new List<List<string>>();
            var arrived = new ManualResetEventSlim();
            first.Listen(paths => { lock (arrivals) arrivals.Add([.. paths]); arrived.Set(); });
            first.Receive(["d.txt"]);
            Check(SingleInstance.Send(["a.txt", "b.txt"], name) && SingleInstance.Send(["b.txt", "c.txt"], name), "both starts reach the first");
            Check(arrived.Wait(3000), "the files arrive");
            Thread.Sleep(600);
            lock (arrivals)
            {
                Check(arrivals.Count == 1, $"arrivals within 300 ms are handed over together ({arrivals.Count} batches)");
                Check(arrivals.Count > 0 && arrivals[0].Count == 4, $"each file once: {string.Join(", ", arrivals.SelectMany(a => a).Select(Path.GetFileName))}");
            }
            Check(!SingleInstance.Send(["x"], name + "-nobody"), "with no Split running, nothing answers");
        });

        Test("Windows", "Launch at login is a value of the user’s Run key, and nothing else", () =>
        {
            var name = "SplitTest-" + Guid.NewGuid().ToString("N");
            try
            {
                Check(!LaunchAtLogin.IsEnabled(name), "off by default");
                LaunchAtLogin.Set(true, name, @"C:\Program Files\Split\Split.exe");
                Check(LaunchAtLogin.IsEnabled(name), "on");
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                Check(key?.GetValue(name) as string == "\"C:\\Program Files\\Split\\Split.exe\"", "the program’s path is quoted");
                LaunchAtLogin.Set(false, name);
                Check(!LaunchAtLogin.IsEnabled(name), "off again");
            }
            finally { LaunchAtLogin.Set(false, name); }
        });

        Test("Interface", "Snapshots: the HUD in each state, the shelf and the tray menu, in black and in white", () => Ui.Run(() =>
        {
            var folder = Path.Combine(Fixtures.Root, "_captures-windows");
            Directory.CreateDirectory(folder);
            var made = 0;
            var color = Settings.LookColor;
            try
            {
                string[] files = [.. Directory.EnumerateFiles(Fixtures.Root).Where(f => !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.Ordinal)];
                foreach (var look in LookColors.All)
                {
                    Look.Color = look;
                    var states = new (string Name, HudState State, string Title, string Detail, double? Fraction)[]
                    {
                        ("progress", HudState.Progress, "Converting to PDF", "2 of 4 · report.docx", 0.42),
                        ("waiting", HudState.Progress, "Converting to MP4", "clip.mov", null),
                        ("success", HudState.Success, "3 files converted", "The results are next to the originals.", null),
                        ("warning", HudState.Warning, "2 of 3 converted", "c.mov: the file is damaged or incomplete.\n…and 1 more with problems.", null),
                        ("error", HudState.Failure, "The disk filled up", "Free some space (for example, empty the Recycle Bin) and try again.", null),
                        ("info", HudState.Info, "Moved to the Recycle Bin", "File Explorer’s “Restore” brings the files back if you need them again.", null),
                    };
                    foreach (var (name, state, title, detail, fraction) in states)
                    {
                        var model = new HudModel
                        {
                            State = state, Title = title, Detail = detail, Fraction = fraction,
                            Outputs = state == HudState.Success ? ["a.png"] : [], UndoJob = state == HudState.Success ? Guid.NewGuid() : null,
                            JobId = state == HudState.Progress ? Guid.NewGuid() : null,
                            Failures = state == HudState.Warning ? [new("c.mov", new Problem("damaged"))] : [],
                        };
                        if (state == HudState.Warning) model.Outputs = ["a.png"];
                        var card = new HudCard(model, Ui.NoActions);
                        Ui.Settle(0.4);
                        Capture(folder, $"hud-{name}-{look.Raw()}", card, new Size(HudCard.CardWidth + 2 * HudCard.Inset + 20, 150), ref made);
                    }
                    // The close button shows while the pointer is over a result.
                    var hovered = new HudModel { State = HudState.Success, Title = "1 file converted", Detail = "“photo.png” is next to the original.", Outputs = ["a.png"], Hovering = true };
                    Capture(folder, $"hud-hover-{look.Raw()}", new HudCard(hovered, Ui.NoActions), new Size(HudCard.CardWidth + 2 * HudCard.Inset + 20, 150), ref made);

                    var shelf = new ShelfModel();
                    shelf.Add(files.Take(3));
                    Capture(folder, $"shelf-{look.Raw()}", new ShelfView(shelf), new Size(280, 350), ref made);
                    Capture(folder, $"shelf-empty-{look.Raw()}", new ShelfView(new ShelfModel()), new Size(280, 350), ref made);

                    var entries = TrayMenu.Build(() => { });
                    var menu = new ContextMenu();
                    MenuLook.Fill(menu, entries);
                    Check(menu.Items.Count == entries.Count && menu.Items[6] is MenuItem { IsEnabled: false }, "each line of the menu is there, the hint disabled");
                    // A menu can't be put in another element, so it is opened and drawn through a brush.
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
                    menu.IsOpen = true;
                    Ui.Settle(0.3);
                    var (width, height) = (Math.Ceiling(menu.ActualWidth), Math.Ceiling(menu.ActualHeight));
                    Capture(folder, $"tray-menu-{look.Raw()}", new Border { Background = new VisualBrush(menu) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top } }, new Size(width, height), ref made);
                    menu.IsOpen = false;
                }
            }
            finally { Look.Color = color; }
            Check(made == 20, $"only {made} captures were made");
        }));
    }

    private static void Capture(string folder, string name, FrameworkElement element, Size size, ref int made)
    {
        var image = Ui.Render(element, size, Colors.Gray);
        Check(Ui.Spread(image) > 4, $"{name}: the image is almost uniform: was the view empty?");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
        made++;
    }
}

/// <summary>Runs interface code on a thread of its own with a dispatcher, and draws elements to pixels.</summary>
public static class Ui
{
    public static readonly HudActions NoActions = new(() => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });

    public static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is TestSkipped or TestFailure) throw failure;
        if (failure is not null) throw new TestFailure(failure.ToString());
    }

    /// <summary>Lets animations and timers run for a moment.</summary>
    public static void Settle(double seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>The element on a backdrop (clear when null), at twice the size so captures can be read.</summary>
    public static BitmapSource Render(FrameworkElement element, Size size, Color? backdrop)
    {
        var root = new Grid { Width = size.Width, Height = size.Height, Background = backdrop is { } c ? new SolidColorBrush(c) : null };
        root.Children.Add(element);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width * 2, (int)size.Height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(root);
        root.Children.Clear();
        return bitmap;
    }

    public static byte[] PixelAt(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    /// <summary>Standard deviation of luminance over a coarse sample: about 0 means an empty view.</summary>
    public static double Spread(BitmapSource image)
    {
        var width = image.PixelWidth;
        var data = new byte[width * image.PixelHeight * 4];
        image.CopyPixels(data, width * 4, 0);
        var values = new List<double>();
        for (var i = 0; i < data.Length; i += 4 * 7) values.Add(0.299 * data[i + 2] + 0.587 * data[i + 1] + 0.114 * data[i]);
        var mean = values.Average();
        return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
    }

    /// <summary>The labels of the buttons on a card, left to right.</summary>
    public static List<string> Buttons(DependencyObject root)
    {
        var labels = new List<string>();
        void Walk(DependencyObject node)
        {
            if (node is SplitButton { Content: string label }) labels.Add(label);
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Walk(child);
        }
        Walk(root);
        return labels;
    }
}
