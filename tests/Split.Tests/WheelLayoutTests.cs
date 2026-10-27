using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>Text sizes without a font: a width for every letter and a height for every line, so the layout can be tested anywhere.</summary>
public sealed class StandInMeasurer : ITextMeasurer
{
    private static double LineHeight(double size) => Math.Ceiling(size * 1.2);

    private static double WidthOf(string text, double size, bool bold) => text.Length * size * (bold ? 0.58 : 0.54);

    public double Height(int lines, double fontSize, bool bold) => lines * LineHeight(fontSize);

    public (double Width, double Height) Measure(string text, double fontSize, bool bold, double width)
    {
        var lines = 1;
        var line = "";
        var widest = 0.0;
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && WidthOf(next, fontSize, bold) > width)
            {
                lines++;
                line = word;
            }
            else line = next;
            widest = Math.Max(widest, Math.Min(width, WidthOf(line, fontSize, bold)));
        }
        return (widest, lines * LineHeight(fontSize));
    }
}

/// <summary>The wheel's geometry, hit testing, springs and the drag gesture, all without a window.</summary>
public static class WheelLayoutTests
{
    /// <summary>What a sweep over many wheels found, for the checks that look at the whole.</summary>
    public sealed class Tally
    {
        public int Wheels, Slices, TruncatedAtMedium;
        public List<string> TruncatedAtLargest = [], IconsOnly = [];
    }

    private static Rc IconBox(WheelLayout layout, Pt icon) => new(icon.X - layout.IconBox.Width / 2, icon.Y - layout.IconBox.Height / 2, layout.IconBox.Width, layout.IconBox.Height);

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>Checks one wheel the way it must hold at every size, language and number of slices.</summary>
    public static void CheckWheel(string label, IReadOnlyList<(string Title, string? Symbol)> items, WheelStyle style, ITextMeasurer measurer, Tally tally, bool crowded = false)
    {
        var layout = WheelLayout.Make(items.Select(i => i.Title).ToList(), items.Select(i => i.Symbol).ToList(), style, measurer);
        tally.Wheels++;
        Check(layout.Size == WheelLayout.WindowSize(style.Size), $"{label}: the wheel isn’t the size chosen in Settings");
        for (var index = 0; index < items.Count; index++)
        {
            var slice = layout.Slices[index];
            var title = $"{label}: “{items[index].Title}”";
            if (slice.Icon is { } icon)
            {
                Check(Math.Abs(Hypot(icon.X, icon.Y - layout.IconLift) - layout.IconRadius) < 0.01, $"{title}: the icon is off the ring the others share");
                var box = IconBox(layout, icon);
                Check(layout.Clear(box, index), $"{title}: the icon touches a divider, the hub or the rim");
                if (layout.Style.Names)
                {
                    if (slice.Text is not { } name) Check(false, $"{title}: the name is missing");
                    else Check(name.MaxY <= box.MinY - WheelLayout.SymbolGap + 0.5 && Math.Abs(name.MidX - icon.X) < 0.5, $"{title}: the name isn’t right under its icon");
                }
                else Check(slice.Text is null, $"{title}: names are off, but it shows one");
            }
            else if (slice.Text is null)
            {
                Check(false, $"{title}: a slice without an icon must show its name");
            }
            if (slice.Text is { } text)
            {
                Check(layout.Clear(text, index), $"{title}: the name touches a divider, the hub or the rim");
                if (slice.Truncated) Check(text.Width >= 2 * layout.FontSize - 1, $"{title}: the name has no room left at all");
            }
            var (dx, dy) = (WheelLayout.Pop * Math.Sin(layout.AngleOf(index)), WheelLayout.Pop * Math.Cos(layout.AngleOf(index)));
            List<Rc> boxes = [.. new[] { slice.Text }.OfType<Rc>(), .. slice.Icon is { } at ? [IconBox(layout, at)] : Array.Empty<Rc>()];
            Check(boxes.Select(b => b.OffsetBy(dx, dy)).All(b => new[] { (b.MinX, b.MinY), (b.MaxX, b.MinY), (b.MinX, b.MaxY), (b.MaxX, b.MaxY) }.All(c => Hypot(c.Item1, c.Item2) <= layout.Outer)),
                  $"{title}: leaves the ring when its slice pops out");
            for (var other = index + 1; other < items.Count; other++)
                Check(!layout.Frame(index).Intersects(layout.Frame(other)), $"{title} overlaps “{items[other].Title}”");
            if (style.Names && !crowded)
            {
                if (style.Size == WheelSize.Medium) tally.Slices++;
                if (slice.Truncated && style.Size == WheelSize.Medium) tally.TruncatedAtMedium++;
                if (slice.Truncated && style.Size == WheelSize.ExtraLarge) tally.TruncatedAtLargest.Add(title);
                if (!layout.Style.Names && style.Size >= WheelSize.Medium) tally.IconsOnly.Add(title);
            }
        }
    }

    public static void Register()
    {
        Test("Wheels", "The wheel’s sizes: radii, centre, window and the scale text follows", () =>
        {
            double[] outer = [146, 166, 186, 210, 236];
            double[] inner = [53, 60, 67, 76, 85];
            foreach (var size in WheelSizes.All)
            {
                var i = (int)size;
                Check(WheelLayout.OuterRadius(size) == outer[i] && WheelLayout.InnerRadius(size) == inner[i], $"{size}: {WheelLayout.OuterRadius(size)}/{WheelLayout.InnerRadius(size)}");
                Check(WheelLayout.WindowSize(size) == 2 * (outer[i] + 12 + 10), $"{size}: window {WheelLayout.WindowSize(size)}");
            }
            var layout = WheelLayout.Make(["A", "B", "C"], ["circle", "circle", "circle"], new WheelStyle(WheelSize.Medium, true), new StandInMeasurer());
            Check(layout.Scale == 1 && layout.TextScale == 1 && layout.Disc == 198 && layout.Size == 416, $"medium: scale {layout.Scale}, disc {layout.Disc}, size {layout.Size}");
            var big = WheelLayout.Make(["A", "B", "C"], ["circle", "circle", "circle"], new WheelStyle(WheelSize.ExtraLarge, true), new StandInMeasurer());
            Check(Math.Abs(big.TextScale - Math.Sqrt(236.0 / 186)) < 1e-9 && big.FontSize > layout.FontSize && big.SymbolSize > layout.SymbolSize, "text and icons grow more slowly than the wheel");
            Check(layout.FontSize == 11 && layout.LabelFontSize == 13 && layout.SymbolSize == 19, $"medium type: {layout.FontSize}/{layout.LabelFontSize}/{layout.SymbolSize}");
        });

        Test("Wheels", "Each slice answers in the right place; the centre and outside don’t", () =>
        {
            foreach (var size in WheelSizes.All)
            {
                var (inner, outer) = (WheelLayout.InnerRadius(size), WheelLayout.OuterRadius(size));
                var disc = outer + WheelLayout.RimMargin;
                for (var count = 1; count <= 16; count++)
                {
                    var step = 2 * Math.PI / count;
                    for (var index = 0; index < count; index++)
                    {
                        foreach (var (angle, radius) in new[] { ((index + 0.5) * step, (inner + outer) / 2), (index * step + 0.03, inner + 2), ((index + 1) * step - 0.03, disc + 10) })
                        {
                            var found = WheelLayout.SliceAt(new Pt(radius * Math.Sin(angle), radius * Math.Cos(angle)), count, size);
                            Check(found == index, $"{size}, {count} slices: the point at {angle * 180 / Math.PI:0}° should be {index}, got {found}");
                        }
                    }
                    Check(WheelLayout.SliceAt(new Pt(0, 0), count, size) is null && WheelLayout.SliceAt(new Pt(0, inner - 20), count, size) is null, $"{size}, {count}: near the centre cancels");
                    Check(WheelLayout.SliceAt(new Pt(0, disc + 40), count, size) is null, $"{size}, {count}: far from the wheel cancels");
                }
                Check(WheelLayout.SliceAt(new Pt(0, 100), 0, size) is null, "no slices, no answer");
                Check(WheelLayout.IsOnHub(new Pt(0, 0), size) && !WheelLayout.IsOnHub(new Pt(0, outer), size) && !WheelLayout.IsOnHub(new Pt(0, inner), size), $"{size}: the hub");
                // Clockwise from 12 o'clock: three o'clock is the second of four.
                Check(WheelLayout.SliceAt(new Pt(outer - 10, -1), 4, size) == 1 && WheelLayout.SliceAt(new Pt(-(outer - 10), 1), 4, size) == 3, $"{size}: slices run clockwise from the top");
            }
        });

        Test("Wheels", "Icons share one ring and names sit under them, clear of the lines, at every size (with a stand-in font)", () =>
        {
            string[] titles = ["PDF", "JPG", "Compress to Size", "Remove Background", "Annotate and Cover", "Organize PDF", "Normalize Volume", "Make Mono", "Trim Silence", "Strip Metadata", "Extract Frames", "Shelf"];
            var tally = new Tally();
            foreach (var size in WheelSizes.All)
            {
                foreach (var names in new[] { true, false })
                {
                    foreach (var count in new[] { 1, 2, 3, 5, 6, 8, 10, 12 })
                    {
                        var style = new WheelStyle(size, names);
                        CheckWheel($"{size}, names {names}, {count} with icons", [.. titles.Take(count).Select(t => (t, (string?)"circle"))], style, new StandInMeasurer(), tally, crowded: count > 8);
                        CheckWheel($"{size}, names {names}, {count} names only", [.. titles.Take(count).Select(t => (t, (string?)null))], style, new StandInMeasurer(), tally, crowded: count > 8);
                    }
                }
            }
            Check(tally.Wheels == 5 * 2 * 8 * 2, $"only {tally.Wheels} wheels were checked");
        });

        Test("Wheels", "A small wheel shortens long names with “…” and keeps the icons in line", () =>
        {
            string[] titles = ["Compress to Size", "Remove Background", "Annotate and Cover", "Organize PDF", "Normalize Volume", "Make Mono", "Trim Silence", "Remove Audio", "Strip Metadata", "Extract Frames"];
            string?[] symbols = [.. titles.Select(_ => "circle")];
            var small = WheelLayout.Make(titles, symbols, new WheelStyle(WheelSize.ExtraSmall, true), new StandInMeasurer());
            var large = WheelLayout.Make(titles, symbols, new WheelStyle(WheelSize.ExtraLarge, true), new StandInMeasurer());
            Check(small.Slices.Any(s => s.Truncated), "ten long names on the smallest wheel should not all fit whole");
            Check(small.Slices.Count(s => s.Truncated) > large.Slices.Count(s => s.Truncated), "a bigger wheel shortens fewer names");
            Check(small.FontSize < large.FontSize && small.SymbolSize < large.SymbolSize, "text and icons follow the size of the wheel");
            Check(small.Slices.Where(s => s.Icon is not null).Select(s => Math.Round(Hypot(s.Icon!.Value.X, s.Icon.Value.Y - small.IconLift) * 100)).Distinct().Count() == 1, "the icons share one ring");
            var iconsOnly = WheelLayout.Make(titles, symbols, new WheelStyle(WheelSize.ExtraSmall, false), new StandInMeasurer());
            Check(iconsOnly.SymbolSize > small.SymbolSize, "without names the icons grow");
            Check(Math.Abs(iconsOnly.IconRadius - (iconsOnly.Inner + iconsOnly.Outer) / 2) < 0.01, "without names the icons sit mid-ring");
            // A wheel too crowded for a name under every icon shows icons only.
            string[] many = [.. Enumerable.Range(0, 16).Select(i => $"Option number {i}")];
            var crowded = WheelLayout.Make(many, [.. many.Select(_ => "circle")], new WheelStyle(WheelSize.ExtraSmall, true), new StandInMeasurer());
            Check(!crowded.Style.Names && crowded.Slices.All(s => s.Icon is not null && s.Text is null), "sixteen icons on the smallest wheel give up the names");
        });

        Test("Wheels", "Liang–Barsky: a segment through a rectangle, past it, along it and inside it", () =>
        {
            var box = new Rc(0, 0, 10, 10);
            Check(WheelLayout.SegmentIntersects(new Pt(-5, 5), new Pt(15, 5), box), "through");
            Check(!WheelLayout.SegmentIntersects(new Pt(-5, 11), new Pt(15, 11), box), "past");
            Check(!WheelLayout.SegmentIntersects(new Pt(11, -5), new Pt(11, 15), box), "beside");
            Check(WheelLayout.SegmentIntersects(new Pt(2, 2), new Pt(3, 3), box), "inside");
            Check(!WheelLayout.SegmentIntersects(new Pt(-5, -5), new Pt(-1, -1), box), "short of it");
            Check(WheelLayout.SegmentIntersects(new Pt(-5, 10), new Pt(15, 10), box), "along an edge");
        });

        Test("Wheels", "Motion: critically damped springs, no bounce, interruptible", () =>
        {
            var spring = new SpringValue { Target = 1 };
            double peak = 0, time = 0;
            while (!spring.IsSettled || time == 0)
            {
                spring.Step(1.0 / 120);
                peak = Math.Max(peak, spring.Value);
                time += 1.0 / 120;
                if (time > 3) break;
            }
            Check(spring.Value == 1, $"the spring should settle on its target ({spring.Value})");
            Check(peak <= 1.001, $"a critical spring doesn’t bounce (peak {peak})");
            Check(time < 0.7, $"it took {time:0.00} s to settle (0.26 s response)");

            var moving = new SpringValue { Target = 1 };
            for (var i = 0; i < 10; i++) moving.Step(1.0 / 120);
            var before = moving.Value;
            moving.Target = 0;
            moving.Step(1.0 / 120);
            Check(Math.Abs(moving.Value - before) < 0.05, $"changing target halfway can’t jump ({before} → {moving.Value})");
            Check(moving.Value >= before - 0.001, "the motion keeps the speed it had before turning");

            var slow = new SpringValue { Target = 1, Response = 0.3 };
            var quick = new SpringValue { Target = 1, Response = 0.15 };
            for (var i = 0; i < 12; i++) { slow.Step(1.0 / 120); quick.Step(1.0 / 120); }
            Check(quick.Value > slow.Value, "a shorter response gets there sooner");
        });

        Test("Wheels", "The drag gesture: a key tapped during a drag opens its wheel; a tap without a drag, or a held key, doesn’t", () =>
        {
            ModKeys Keys(WheelKind kind) => kind switch { WheelKind.Convert => ModKeys.Shift, WheelKind.Tools => ModKeys.Ctrl, _ => ModKeys.Alt };
            WheelKind? Tap(DragGesture gesture, ModKeys keys, double start, double length, bool dragging = true)
            {
                var found = (WheelKind?)null;
                if (dragging)
                {
                    gesture.Update(true, 100, 100, 4, 4, ModKeys.None, start - 0.5, Keys);
                    gesture.Update(true, 140, 100, 4, 4, ModKeys.None, start - 0.4, Keys);
                }
                gesture.Update(dragging, 140, 100, 4, 4, keys, start, Keys);
                found = gesture.Update(dragging, 140, 100, 4, 4, ModKeys.None, start + length, Keys).Trigger;
                return found;
            }
            Check(Tap(new DragGesture(), ModKeys.Shift, 10, 0.1) == WheelKind.Convert, "Shift opens the conversions");
            Check(Tap(new DragGesture(), ModKeys.Ctrl, 10, 0.1) == WheelKind.Tools, "Ctrl opens the tools");
            Check(Tap(new DragGesture(), ModKeys.Alt, 10, 0.1) == WheelKind.Actions, "Alt opens the actions");
            Check(Tap(new DragGesture(), ModKeys.Shift, 10, 0.1, dragging: false) is null, "without a drag, nothing happens");
            Check(Tap(new DragGesture(), ModKeys.Shift, 10, 0.9) is null, "a key held for longer than 0.8 s is not a tap");
            Check(Tap(new DragGesture(), ModKeys.Shift, 10, 0.79) == WheelKind.Convert, "just inside the window still is");
            Check(Tap(new DragGesture(), ModKeys.Ctrl | ModKeys.Alt, 10, 0.1) is null, "a combination nobody has chosen opens nothing");
            Check(new DragGesture().Update(true, 0, 0, 4, 4, ModKeys.None, 0, k => ModKeys.Ctrl | ModKeys.Alt).Trigger is null, "no keys, no trigger");

            // Two keys pressed one after the other count as one tap of both.
            var both = new DragGesture();
            both.Update(true, 100, 100, 4, 4, ModKeys.None, 9, _ => ModKeys.Ctrl | ModKeys.Alt);
            both.Update(true, 200, 100, 4, 4, ModKeys.None, 9.1, _ => ModKeys.Ctrl | ModKeys.Alt);
            both.Update(true, 200, 100, 4, 4, ModKeys.Ctrl, 10, _ => ModKeys.Ctrl | ModKeys.Alt);
            both.Update(true, 200, 100, 4, 4, ModKeys.Ctrl | ModKeys.Alt, 10.05, _ => ModKeys.Ctrl | ModKeys.Alt);
            both.Update(true, 200, 100, 4, 4, ModKeys.Alt, 10.1, _ => ModKeys.Ctrl | ModKeys.Alt);
            Check(both.Update(true, 200, 100, 4, 4, ModKeys.None, 10.15, _ => ModKeys.Ctrl | ModKeys.Alt).Trigger == WheelKind.Convert, "Ctrl + Alt, when chosen, opens the wheel it was given to (the first, here)");

            // The drag ends when the button comes up, once.
            var drag = new DragGesture();
            drag.Update(true, 100, 100, 4, 4, ModKeys.None, 1, Keys);
            Check(!drag.IsDragging, "the pointer hasn’t moved yet");
            drag.Update(true, 103, 102, 4, 4, ModKeys.None, 1.1, Keys);
            Check(!drag.IsDragging, "within the system’s drag distance it is a click");
            drag.Update(true, 110, 100, 4, 4, ModKeys.None, 1.2, Keys);
            Check(drag.IsDragging, "past it, it is a drag");
            Check(drag.Update(false, 110, 100, 4, 4, ModKeys.None, 1.3, Keys).DragEnded && !drag.IsDragging, "the button coming up ends it");
            Check(!drag.Update(false, 110, 100, 4, 4, ModKeys.None, 1.4, Keys).DragEnded, "and only once");
            // A key tapped right after the drop is too late.
            drag.Update(false, 110, 100, 4, 4, ModKeys.Shift, 1.5, Keys);
            Check(drag.Update(false, 110, 100, 4, 4, ModKeys.None, 1.6, Keys).Trigger is null, "no drag, no wheel");

            // The defaults, from the settings: Shift converts, Ctrl opens the tools, Alt the actions.
            foreach (var kind in WheelKinds.All) Check(DragGesture.KeysOf(kind.DefaultModifier()) == Keys(kind), $"{kind}: the default key");
            Check(DragGesture.KeysOf(ModifierChoice.Off) == ModKeys.None && DragGesture.KeysOf(ModifierChoice.CtrlAlt) == (ModKeys.Ctrl | ModKeys.Alt), "Off is no key and Ctrl + Alt is both");
            var settings = new DragGesture();
            settings.Update(true, 100, 100, 4, 4, ModKeys.None, 0);
            settings.Update(true, 200, 100, 4, 4, ModKeys.None, 0.1);
            settings.Update(true, 200, 100, 4, 4, ModKeys.Shift, 0.2);
            Check(settings.Update(true, 200, 100, 4, 4, ModKeys.None, 0.3).Trigger == WheelKind.Convert, "with no choice made, Shift opens the conversions");
        });

        Test("Wheels", "The wheel stays inside the screen's work area; the drag has left only when the pointer is outside the window", () =>
        {
            var area = new Box(0, 0, 1920, 1040);
            Check(WheelPlacement.Origin(area, 960, 520, 400) == (760, 320), "centred on the point");
            Check(WheelPlacement.Origin(area, 10, 10, 400) == (0, 0), "pushed in from the top left corner");
            Check(WheelPlacement.Origin(area, 1915, 1035, 400) == (1520, 640), "pushed in from the bottom right corner");
            Check(WheelPlacement.Origin(new Box(1920, 0, 1280, 1000), 1930, 500, 400) == (1920, 300), "on the second screen");
            var window = new Box(100, 100, 400, 400);
            Check(!WheelPlacement.DragLeft(window, 300, 300) && !WheelPlacement.DragLeft(window, 100, 100) && !WheelPlacement.DragLeft(window, 499, 499), "inside, edges included");
            Check(WheelPlacement.DragLeft(window, 99, 300) && WheelPlacement.DragLeft(window, 500, 300) && WheelPlacement.DragLeft(window, 300, 500) && WheelPlacement.DragLeft(window, 300, 99), "outside on each side");
        });
    }
}
