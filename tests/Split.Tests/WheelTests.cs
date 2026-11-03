using System.IO;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>
/// The user's arrangement of the wheels: order, hidden slices and what Settings lists. What needs the
/// wheel's geometry or drawing (finding the slice under the pointer, the preview picture) waits for the wheel itself.
/// </summary>
public static class WheelTests
{
    private static WheelItem Item(string id, string title) => new(id, title, null);

    private static List<string> Flat(IEnumerable<WheelItem> items) => items.SelectMany(i => i.IsGroup ? i.Children : [i]).Select(i => i.Id).ToList();

    private static List<string> Ids(IEnumerable<WheelItem> items) => items.Select(i => i.Id).ToList();

    private static List<string> Ids(IEnumerable<WheelEntry> entries) => entries.Select(e => e.Id).ToList();

    public static void Register()
    {
        Test("Wheels", "Options are short, unique and explained", () =>
        {
            string[] samples = ["quadrants.jpg", "quadrants.png", "animated.gif", "drawing.svg", "clip.mp4", "tone.mp3", "report.pdf", "letter.docx",
                                "sales.xlsx", "prices.csv", "folder.zip", "notes.txt", "page.html", "book.epub", "subs.srt", "data.json"];
            foreach (var sample in samples)
            {
                string[] paths = [Fixtures.Url(sample)];
                foreach (var kind in WheelKinds.All)
                {
                    var items = WheelCatalog.Offered(kind, paths);
                    var limit = items.All(i => i.Title.Length <= 7) ? 16 : 14;
                    Check(items.Count <= limit, $"{sample}, {kind} wheel: {items.Count} options are too many to read at a glance");
                    Check(items.Select(i => i.Title).Distinct().Count() == items.Count, $"{sample}, {kind} wheel: repeated titles");
                    foreach (var item in items)
                    {
                        Check(item.Title.Length is > 0 and <= 22, $"{sample}, {kind} wheel: the title “{item.Title}” is empty or too long");
                        Check(!string.IsNullOrEmpty(item.Detail), $"{sample}, {kind} wheel: “{item.Title}” has no explanation");
                    }
                }
            }
            foreach (var kind in WheelKinds.All)
                Check(kind.Title().Length > 0 && kind.Summary().Length > 0 && kind.ChoosePrompt().Length > 0, $"{kind}: title, summary and prompt");
        });

        Test("Wheels", "An arrangement moves and hides slices; one never placed sits after its neighbour", () =>
        {
            static List<string> Sort(WheelArrangement arrangement, params string[] items) => arrangement.Sorted(items, id => id);
            Check(Sort(new WheelArrangement(), "a", "b", "c").SequenceEqual(["a", "b", "c"]), "no arrangement keeps the default order");
            Check(Sort(new WheelArrangement { Order = ["c", "a", "b"] }, "a", "b", "c", "d").SequenceEqual(["c", "d", "a", "b"]),
                  "“d” follows “c” by default, so it stays right after it");
            Check(Sort(new WheelArrangement { Order = ["b", "a"] }, "x", "a", "y", "b", "z").SequenceEqual(["x", "b", "z", "a", "y"]),
                  "items never placed keep their default neighbours");
            Check(Sort(new WheelArrangement { Order = ["gone", "b", "a"] }, "a", "b").SequenceEqual(["b", "a"]), "slices that no longer exist are ignored");
            Check(new WheelArrangement { Order = ["b", "a"], Hidden = ["a"] }.Apply(["a", "b", "c"], id => id).SequenceEqual(["b", "c"]), "hidden slices are left out");
        });

        Test("Wheels", "Without an arrangement every wheel keeps its default order, with the app's groups", () => WithoutArrangements(() =>
        {
            foreach (var format in Formats.All)
            {
                var path = Path.Combine(Path.GetTempPath(), "sample." + format.Ext());
                var items = WheelCatalog.Items(WheelKind.Convert, [path]);
                var targets = Conversions.Targets(format).Select(f => f.Raw()).ToList();
                var flat = Flat(items);
                Check(flat.ToHashSet().SetEquals(targets) && flat.Count == targets.Count, $"{format.Label()}: slices went missing or doubled");
                Check(flat.Where(id => !id.StartsWith("group.", StringComparison.Ordinal)).SequenceEqual(targets.Where(flat.Contains)), $"{format.Label()}: the wheel changed order");
                if (format.Group() == FormatGroup.Video)
                {
                    var audio = items.FirstOrDefault(i => i.Id == StandardGroup.Audio.Raw());
                    Check((audio?.Children.Count ?? 0) > 1 && items.Count < targets.Count, $"{format.Label()}: the audio formats should sit behind one slice");
                }
                else
                {
                    Check(!items.Any(i => i.IsGroup), $"{format.Label()}: only the video wheel groups formats");
                }
            }
            var photo = Fixtures.Url("photo.jpg");
            Check(Ids(WheelCatalog.Items(WheelKind.Actions, [photo])).SequenceEqual(FileActions.All.Select(a => a.Raw())), "actions changed order");
            var tools = WheelCatalog.Items(WheelKind.Tools, [photo]);
            var offered = Ids(ToolCatalog.Items([photo]));
            Check(Flat(tools).ToHashSet().SetEquals(offered), $"tools went missing: {string.Join(", ", offered.Except(Flat(tools)))}");
            Check(tools.Count < offered.Count, "grouping should make the tools wheel shorter");
            var compress = tools.FirstOrDefault(i => i.Id == StandardGroup.Compress.Raw());
            Check(compress?.Children.Select(c => c.Id).SequenceEqual(["compress", "compress-to", "zip"]) == true,
                  $"compressing sits behind one slice: {string.Join(", ", compress?.Children.Select(c => c.Id) ?? [])}");
            Check(compress?.Title == L("Compress") && compress?.Detail?.Contains(QuickTool.Zip.Title()) == true, "the group is named and lists its slices");
        }));

        Test("Wheels", "A group shows as one slice only with two or more slices; a lone group opens straight away", () =>
        {
            var items = new[] { "a", "b", "c", "d" }.Select(id => Item(id, id.ToUpperInvariant())).ToList();
            WheelGroup[] groups = [new("g", null, ["b", "c"])];
            var grouped = WheelCatalog.Arranged(items, new WheelArrangement(), groups);
            Check(Ids(grouped).SequenceEqual(["a", "g", "d"]) && Ids(grouped[1].Children).SequenceEqual(["b", "c"]), $"b and c sit behind g: {string.Join(",", Ids(grouped))}");
            Check(grouped[1].Title == "B", "a group with no name of its own takes its first slice's name");
            var one = WheelCatalog.Arranged(items, new WheelArrangement { Hidden = ["c"] }, groups);
            Check(Ids(one).SequenceEqual(["a", "b", "d"]), $"a group left with one slice shows that slice: {string.Join(",", Ids(one))}");
            var gone = WheelCatalog.Arranged(items, new WheelArrangement { Hidden = ["g"] }, groups);
            Check(Ids(gone).SequenceEqual(["a", "d"]), $"hiding a group hides its slices: {string.Join(",", Ids(gone))}");
            var lone = WheelCatalog.Arranged(items.Skip(1).Take(2).ToList(), new WheelArrangement(), groups);
            Check(Ids(lone).SequenceEqual(["b", "c"]), $"a wheel with nothing but one group shows its slices: {string.Join(",", Ids(lone))}");
            var named = WheelCatalog.Arranged(items, new WheelArrangement { Groups = [new("g", "Mine", ["c", "b"])] }, groups);
            Check(named[1].Title == "Mine" && Ids(named[1].Children).SequenceEqual(["c", "b"]), "the user's name and order win");
        });

        Test("Wheels", "Arrangement entries: groups sit where their first slice would, new slices join their usual group", () =>
        {
            WheelGroup[] groups = [new("g", null, ["b", "c", "x"])];
            var entries = new WheelArrangement().Entries(["a", "b", "c", "d"], groups);
            Check(entries.SequenceEqual([new WheelEntry("a"), new("g", ["b", "c"]), new("d")]), string.Join(" ", entries));
            Check(Ids(new WheelArrangement().Entries(["a", "d"], groups)).SequenceEqual(["a", "d"]), "a group with nothing on offer is left out");
            // The user kept g with only c in it, before "b" existed: b joins g, before c as in the app's order.
            var saved = new WheelArrangement { Order = ["d", "g", "a"], Groups = [new("g", null, ["c"])] };
            var later = saved.Entries(["a", "b", "c", "d"], groups);
            Check(later.SequenceEqual([new WheelEntry("d"), new("g", ["b", "c"]), new("a")]), string.Join(" ", later));
            // A slice the user placed outside the group stays there.
            var outside = new WheelArrangement { Order = ["b", "g", "a", "d"], Groups = [new("g", null, ["c"])] };
            Check(outside.Entries(["a", "b", "c", "d"], groups).First().Equals(new WheelEntry("b")), "a slice taken out of a group stays out");
            // The user dissolved every group: nothing comes back.
            var none = new WheelArrangement { Groups = [] };
            Check(none.Entries(["a", "b", "c", "d"], groups).All(e => !e.IsGroup), "dissolved groups stay dissolved");
        });

        Test("Wheels", "Settings drafts: group, add, take out, ungroup, rename and move, saved as little as possible", () =>
        {
            string[] ids = ["a", "b", "c", "d", "e"];
            WheelGroup[] groups = [new("g", null, ["c", "d"])];
            var draft = new WheelDraft(new WheelArrangement(), ids, groups);
            Check(Ids(draft.Entries).SequenceEqual(["a", "b", "g", "e"]), string.Join(",", Ids(draft.Entries)));
            Check(draft.Arrangement(ids, groups).IsDefault, "an untouched draft saves nothing");

            var made = draft.MakeGroup(["e", "a"]);
            Require(made is not null, "no group was made");
            Check(Ids(draft.Entries).SequenceEqual([made!, "b", "g"]) && draft.Entry(made!)!.Members!.SequenceEqual(["a", "e"]),
                  $"the new group takes the first slice's place: {string.Join(" ", draft.Entries)}");
            draft.Add("b", made!);
            Check(draft.Entry(made!)!.Members!.SequenceEqual(["a", "e", "b"]) && draft.GroupOf("b") == made, "b joins the end of the group");
            draft.Move("b", DraftMove.Top);
            Check(draft.Entry(made!)!.Members!.SequenceEqual(["b", "a", "e"]), $"moving inside a group: {string.Join(",", draft.Entry(made!)!.Members!)}");
            draft.Names[made!] = "  Mine  ";
            var saved = draft.Arrangement(ids, groups);
            Check(saved.Groups?.First(g => g.Id == made).Name == "Mine", "names are saved trimmed");
            var expected = draft.Copy();
            expected.Names[made!] = "Mine";
            Check(new WheelDraft(saved, ids, groups).Equals(expected), "the draft comes back as it was saved");

            draft.TakeOut("a");
            Check(Ids(draft.Entries).SequenceEqual([made!, "a", "g"]), $"a taken out goes right after its group: {string.Join(",", Ids(draft.Entries))}");
            draft.TakeOut("b");
            draft.TakeOut("e");
            Check(draft.Entry(made!) is null && !draft.Names.ContainsKey(made!), "a group left empty goes away");
            draft.Ungroup("g");
            Check(Ids(draft.Entries).SequenceEqual(["e", "b", "a", "c", "d"]) && !draft.Entries.Any(e => e.IsGroup), string.Join(",", Ids(draft.Entries)));
            var dissolved = draft.Arrangement(ids, groups);
            Check(dissolved.Groups?.Count == 0, "dissolving the app's group is remembered");

            var plain = new WheelDraft(new WheelArrangement(), ids, groups);
            plain.Move("e", DraftMove.Top);
            var moved = plain.Arrangement(ids, groups);
            Check(moved.Order.SequenceEqual(["e", "a", "b", "g"]) && moved.Groups is null, $"moving alone keeps following the app's groups: {string.Join(",", moved.Order)}");
            plain.Move("e", DraftMove.Bottom);
            Check(plain.Arrangement(ids, groups).IsDefault, "moved back, it saves nothing again");
        });

        Test("Wheels", "Dragging on the preview puts a slice right before the one it was dropped in front of", () =>
        {
            string[] ids = ["a", "b", "c", "d", "e"];
            WheelGroup[] groups = [new("g", null, ["c", "d"])];
            var draft = new WheelDraft(new WheelArrangement(), ids, groups) { Hidden = ["b"] };
            // The wheel shows a, g and e; b is hidden and keeps its place among them.
            draft.Place("e", "a");
            Check(Ids(draft.Entries).SequenceEqual(["e", "a", "b", "g"]), $"e dropped where a was: {string.Join(",", Ids(draft.Entries))}");
            draft.Place("e", null);
            Check(Ids(draft.Entries).SequenceEqual(["a", "b", "g", "e"]), $"dropped on the last slice, it goes last: {string.Join(",", Ids(draft.Entries))}");
            draft.Place("g", "a");
            Check(Ids(draft.Entries).SequenceEqual(["g", "a", "b", "e"]) && draft.Entry("g")!.Members!.SequenceEqual(["c", "d"]), $"a group moves as one: {string.Join(" ", draft.Entries)}");
            draft.Place("d", "c");
            Check(draft.Entry("g")!.Members!.SequenceEqual(["d", "c"]) && draft.Entries.Count == 4, $"inside a group, slices stay in it: {string.Join(" ", draft.Entries)}");
            var before = draft.Copy();
            draft.Place("a", "a");
            Check(draft.Equals(before), "dropped on itself, nothing moves");
            var saved = draft.Arrangement(ids, groups);
            Check(new WheelDraft(saved, ids, groups).Entries.SequenceEqual(draft.Entries), "the new order is what is saved");
        });

        Test("Wheels", "Every slice and format says what it does; conversions that wait for a program say which", () =>
        {
            var scopes = new List<WheelScope> { WheelScope.Tools, WheelScope.Actions };
            scopes.AddRange(SourceKinds.All.Select(WheelScope.Convert));
            foreach (var scope in scopes)
            {
                foreach (var choice in WheelCatalog.Choices(scope))
                {
                    Check(choice.Explanation.Length > 20, $"{scope.Key} › {choice.Title}: no explanation");
                    Check(!choice.Explanation.Contains("for ."), $"{scope.Key} › {choice.Title}: offered for nothing");
                }
            }
            foreach (var format in Formats.All) Check(format.Summary().EndsWith('.'), $"{format.Label()}: no summary");
            static string? Waits(string program) => Engines.Path(program) is null ? program : null;
            Check(Conversions.MissingProgram(Format.Jpg, Format.Png) is null && Conversions.MissingProgram(Format.Docx, Format.Pdf) is null, "pictures and documents need no program");
            Check(Conversions.MissingProgram(Format.Mp3, Format.Wav) == Waits("ffmpeg") && Conversions.MissingProgram(Format.Gif, Format.Mp4) == Waits("ffmpeg"), "sound and video wait for ffmpeg");
            Check(Conversions.MissingProgram(Format.Png, Format.Webp) is null && Conversions.MissingProgram(Format.Png, Format.Jxl) is null, "WebP and JPEG XL are written by the app itself");
            Check(Conversions.MissingProgram(Format.Gif, Format.Png) is null && Conversions.MissingProgram(Format.Srt, Format.Vtt) is null, "a GIF to a picture and subtitles need nothing");
        });

        Test("Wheels", "Settings lists every slice a file can show, in the order the wheel uses", () =>
        {
            foreach (var source in SourceKinds.All)
            {
                var choices = WheelCatalog.Choices(WheelScope.Convert(source)).Select(c => c.Id).ToList();
                Check(choices.ToHashSet().Count == choices.Count, $"{source.Raw()}: a format is listed twice");
                foreach (var format in source.Formats())
                {
                    var targets = Conversions.Targets(format).Select(f => f.Raw()).ToList();
                    Check(targets.All(choices.Contains), $"{format.Label()}: {string.Join(", ", targets.Except(choices))} missing from Settings");
                    Check(choices.Where(targets.Contains).SequenceEqual(targets), $"{format.Label()}: Settings lists {string.Join(",", choices.Where(targets.Contains))}, the wheel shows {string.Join(",", targets)}");
                }
            }
            Check(WheelCatalog.ArrangeableKinds.Contains(SourceKind.Image) && WheelCatalog.ArrangeableKinds.Contains(SourceKind.Video), "images and video can be arranged");
            Check(!WheelCatalog.ArrangeableKinds.Contains(SourceKind.Subtitle), "subtitles offer one format at a time, so there is nothing to arrange");
            foreach (var scope in new[] { WheelScope.Tools, WheelScope.Actions })
            {
                var ids = WheelCatalog.Choices(scope).Select(c => c.Id).ToList();
                Check(ids.ToHashSet().Count == ids.Count, $"{scope.Key}: two slices share an identifier");
            }
        });

        Test("Wheels", "Slice identifiers are the same in every language", () =>
        {
            try
            {
                WheelScope[] scopes = [WheelScope.Tools, WheelScope.Actions, WheelScope.Convert(SourceKind.Image), WheelScope.Convert(SourceKind.Document)];
                List<string> Identifiers() => scopes.SelectMany(s => WheelCatalog.Choices(s).Select(c => c.Id)).ToList();
                var english = Identifiers();
                foreach (var language in new[] { Language.Pt, Language.Fr, Language.Es })
                {
                    Localizer.Shared.Language = language;
                    Check(Identifiers().SequenceEqual(english), $"{language.Raw()}: identifiers changed with the language");
                }
            }
            finally { Localizer.Shared.Language = Language.En; }
        });

        Test("Wheels", "PDF moved to the top comes first on every image wheel; hiding everything is explained", () => WithoutArrangements(() =>
        {
            var scope = WheelScope.Convert(SourceKind.Image);
            var order = WheelCatalog.Choices(scope).Select(c => c.Id).Where(id => id != "pdf").Prepend("pdf").ToArray();
            scope.Arrangement = new WheelArrangement { Order = order };
            foreach (var name in new[] { "photo.jpg", "quadrants.png", "animated.gif" })
            {
                var items = Ids(WheelCatalog.Items(WheelKind.Convert, [Fixtures.Url(name)]));
                Check(items.FirstOrDefault() == "pdf", $"{name}: PDF should be first, got {string.Join(",", items.Take(3))}");
                Check(items.ToHashSet().SetEquals(Conversions.Targets(Formats.Detect(Fixtures.Url(name))!.Value).Select(f => f.Raw())), $"{name}: slices went missing");
            }
            Check(Ids(WheelCatalog.Items(WheelKind.Convert, [Fixtures.Url("report.pdf")])).FirstOrDefault() != "pdf", "the PDF wheel has its own arrangement");
            scope.Arrangement = new WheelArrangement { Order = order, Hidden = ["pdf", "webp"] };
            var shown = Ids(WheelCatalog.Items(WheelKind.Convert, [Fixtures.Url("photo.jpg")]));
            Check(!shown.Contains("pdf") && !shown.Contains("webp") && shown.FirstOrDefault() == "png", $"hidden slices still show: {string.Join(",", shown)}");
            scope.Arrangement = new WheelArrangement { Hidden = [.. WheelCatalog.Choices(scope).Select(c => c.Id)] };
            Check(WheelCatalog.Items(WheelKind.Convert, [Fixtures.Url("photo.jpg")]).Count == 0, "every slice is hidden");
            Check(WheelCatalog.Offered(WheelKind.Convert, [Fixtures.Url("photo.jpg")]).Count > 0, "the wheel still knows what it could offer");
            var message = Explain.AllHidden(WheelKind.Convert.Title());
            Check(message.Message.Contains(WheelKind.Convert.Title()) && message.Suggestion?.Contains("Settings › Wheels") == true,
                  $"the notice should name the wheel and where to show the slices: {message.FullText}");
        }));

        Test("Wheels", "The preview finds the slice under the pointer where the wheel does", () => Ui.Run(() =>
        {
            foreach (var size in WheelSizes.All)
            {
                foreach (var count in new[] { 1, 2, 5, 8, 13 })
                {
                    var items = Enumerable.Range(0, count).Select(i => new WheelItem(i.ToString(), $"Item {i}", null));
                    var view = new App.WheelView(items, "", "", [], true, new WheelStyle(size, true));
                    for (var step = 0; step < 48; step++)
                    {
                        var angle = step * Math.PI / 24 + 0.01;
                        foreach (var radius in new[] { WheelLayout.InnerRadius(size) - 10, WheelLayout.InnerRadius(size) + 6, WheelLayout.OuterRadius(size) - 6 })
                        {
                            var found = WheelLayout.SliceAt(new Pt(radius * Math.Sin(angle), radius * Math.Cos(angle)), count, size);
                            var at = new System.Windows.Point(view.Center.X + radius * Math.Sin(angle), view.Center.Y - radius * Math.Cos(angle));
                            Check(found == view.IndexAt(at), $"{size}, {count} slices, {step * 360 / 48}°: the preview finds {found}, the wheel {view.IndexAt(at)}");
                            if (radius > WheelLayout.InnerRadius(size)) Check(found == (int)(angle / (2 * Math.PI / count)) % count, $"{size}, {count} slices, {step * 360 / 48}°: not the slice the angle is in");
                        }
                    }
                    Check(WheelLayout.IsOnHub(new Pt(0, 0), size) && !WheelLayout.IsOnHub(new Pt(0, WheelLayout.OuterRadius(size)), size), $"{size}: the hub");
                }
            }
        }));

        Test("Wheels", "The preview picture lights the chosen slice with a light veil inside its slice", () => Ui.Run(() =>
        {
            var look = Settings.LookColor;
            try
            {
                App.Look.Color = LookColor.Black;
                var items = Enumerable.Range(0, 6).Select(i => new WheelItem(i.ToString(), $"Item {i}", null)).ToList();
                var image = App.WheelView.Picture(items, "Wheel", "", highlighted: 2, style: new WheelStyle());
                var layout = WheelLayout.Make(items.Select(i => i.Title).ToList(), items.Select(i => i.Symbol).ToList(), new WheelStyle(), App.WheelView.Text);
                var scale = image.PixelWidth / (layout.Size);
                double Brightness(int slice, double? radius = null)
                {
                    var angle = (slice + 0.5) * layout.Step;
                    var r = radius ?? layout.Inner + 16;
                    var pixel = Ui.PixelAt(image, (int)((layout.Size / 2 + r * Math.Sin(angle)) * scale), (int)((layout.Size / 2 - r * Math.Cos(angle)) * scale));
                    return Math.Max(pixel[0], Math.Max(pixel[1], pixel[2])) / 255.0;
                }
                Check(Brightness(2) > Brightness(0) + 0.1 && Brightness(2) < 0.6, $"the chosen slice should be a soft veil, brightness {Brightness(2):0.00}");
                Check(Brightness(0) < 0.3, $"the other slices stay dark, brightness {Brightness(0):0.00}");
                var pastRim = Brightness(2, layout.Outer + 8);
                Check(pastRim < Brightness(2) - 0.1, $"the veil stays inside the slice, short of the rim, brightness {pastRim:0.00}");
                Check(Ui.PixelAt(image, 1, 1)[3] == 0, "the picture is clear round the disc");
            }
            finally { App.Look.Color = look; }
        }));

        Test("Wheels", "Glass: what is behind the wheel shows through its disc, tinted; without glass the disc is solid", () => Ui.Run(() =>
        {
            if (App.Motion.IncreaseContrast) Skip("high contrast is on, so the wheel is always solid");
            var (look, glass, intensity) = (Settings.LookColor, Settings.GlassWheel, Settings.GlassIntensity);
            try
            {
                App.Motion.Transparency = true;
                App.Look.Color = LookColor.Black;
                var items = Enumerable.Range(0, 6).Select(i => new WheelItem(i.ToString(), $"Item {i}", null)).ToList();
                var red = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                                                                           Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 4).SelectMany(b => b).ToArray(), 8);
                var layout = WheelLayout.Make(items.Select(i => i.Title).ToList(), items.Select(i => i.Symbol).ToList(), new WheelStyle(), App.WheelView.Text);
                // Past the first slice, between it and the rim, where nothing else is drawn.
                byte[] Disc(System.Windows.Media.Imaging.BitmapSource image)
                {
                    var (scale, r, angle) = (image.PixelWidth / layout.Size, (layout.Outer + layout.Disc) / 2, layout.Step / 2);
                    return Ui.PixelAt(image, (int)((layout.Size / 2 + r * Math.Sin(angle)) * scale), (int)((layout.Size / 2 - r * Math.Cos(angle)) * scale));
                }
                (Settings.GlassWheel, Settings.GlassIntensity) = (false, 0);
                var solid = Disc(App.WheelView.Picture(items, "Wheel", "", style: new WheelStyle(), backdrop: red));
                Check(solid[2] == solid[1] && solid[2] < 80, $"without glass the backdrop is ignored: {solid[2]},{solid[1]},{solid[0]}");
                Settings.GlassWheel = true;
                var clear = Disc(App.WheelView.Picture(items, "Wheel", "", style: new WheelStyle(), backdrop: red));
                Check(clear[2] > clear[1] + 30 && clear[3] == 255, $"with glass the red shows through: {clear[2]},{clear[1]},{clear[0]}");
                Settings.GlassIntensity = 1;
                var strong = Disc(App.WheelView.Picture(items, "Wheel", "", style: new WheelStyle(), backdrop: red));
                Check(strong[2] < clear[2] - 10, $"more intensity, less of it: {strong[2]} against {clear[2]}");
                var none = Disc(App.WheelView.Picture(items, "Wheel", "", style: new WheelStyle()));
                Check(none[3] > 150, $"with nothing captured the tint alone still makes a disc: alpha {none[3]}");
                App.Motion.Transparency = false;
                var off = Disc(App.WheelView.Picture(items, "Wheel", "", style: new WheelStyle(), backdrop: red));
                Check(off[2] == off[1], $"with Windows' transparency effects off it stays solid: {off[2]},{off[1]},{off[0]}");
            }
            finally
            {
                App.Motion.Transparency = null;
                App.Look.Color = look;
                (Settings.GlassWheel, Settings.GlassIntensity) = (glass, intensity);
            }
        }));
    }

    /// <summary>Runs <paramref name="body"/> with no saved arrangement, putting back whatever was there before.</summary>
    private static void WithoutArrangements(Action body)
    {
        var scopes = new List<WheelScope> { WheelScope.Tools, WheelScope.Actions };
        scopes.AddRange(SourceKinds.All.Select(WheelScope.Convert));
        var saved = scopes.Select(scope => scope.Arrangement).ToList();
        foreach (var scope in scopes) scope.Arrangement = new WheelArrangement();
        try { body(); }
        finally
        {
            for (var i = 0; i < scopes.Count; i++) scopes[i].Arrangement = saved[i];
        }
    }
}
