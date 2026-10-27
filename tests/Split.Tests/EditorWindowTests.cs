using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Split.App;
using static Split.Tests.EditorUiTests;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>The windows of Collage, Edit Media and Organize PDF, driven without a person, and their captures.</summary>
public static class EditorWindowTests
{
    private static string Id(EditorTool tool, params string[] paths) =>
        $"editor:{tool}:{string.Join('|', paths.Select(p => Path.GetFullPath(p).ToLowerInvariant()))}";

    private static void Ready(MediaEditor window)
    {
        window.Model.Loaded.Wait();
        window.Model.Details.Wait();
        Ui.Settle(0.5);
    }

    private static void Press(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(Window.GetWindow(element)!);
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    public static void Register()
    {
        Test("Editors", "The symbols of the three editors exist", () =>
        {
            foreach (var layout in CollageLayouts.All) Check(Icons.Find(layout.Symbol()) is not null, $"the symbol of “{layout.Title()}” has no icon");
            foreach (var tool in new[] { EditorTool.Collage, EditorTool.Media, EditorTool.PdfPages }) Check(Icons.Find(tool.Symbol()) is not null, $"the symbol of “{tool.Title()}” has no icon");
            foreach (var symbol in new[] { "play.fill", "pause.fill", "rectangle.fill", "drop", "minus.circle", "trash", "plus.square.on.square", "exclamationmark.triangle", "rotate.left", "rotate.right" })
                Check(Icons.Find(symbol) is not null, $"“{symbol}” has no icon");
            Check(EditorLauncher.WindowTitle(EditorTool.Collage, [@"C:\a\1.jpg", @"C:\a\2.png"]) == "Collage — 2 images", "the collage is named after the number of images");
            Check(EditorLauncher.WindowTitle(EditorTool.PdfPages, [@"C:\a\1.pdf", @"C:\a\2.png"]) == "Organize PDF — 2 files", "the organizer after the number of files");
            Check(EditorLauncher.WindowTitle(EditorTool.Media, [@"C:\a\clip.mp4"]) == "Edit Media — clip.mp4", "a clip after its name");
        });

        Test("Editors", "Each of the three opens once for the same files; a file that can't be read is explained", () => Ui.Run(() =>
        {
            var pictures = new[] { Fixtures.Url("quadrants.png"), Fixtures.Url("quadrants.jpg") };
            var report = Fixtures.Url("report.pdf");
            var clip = Fixtures.Url("clip.mp4");
            for (var round = 0; round < 2; round++)
            {
                EditorLauncher.Open(EditorTool.Collage, pictures);
                EditorLauncher.Open(EditorTool.PdfPages, [report]);
                EditorLauncher.Open(EditorTool.Media, [clip]);
            }
            var windows = new[] { SingleWindows.Find(Id(EditorTool.Collage, pictures)), SingleWindows.Find(Id(EditorTool.PdfPages, report)), SingleWindows.Find(Id(EditorTool.Media, clip)) };
            Require(windows.All(w => w is { IsVisible: true }), "all three are open");
            Check(windows[0] is CollageEditor && windows[1] is PdfOrganizerEditor && windows[2] is MediaEditor, "the right editors");
            Check(windows.All(w => w is AppWindow { ResizeMode: ResizeMode.CanResize } && w.MinWidth == 780), "app windows, resizable, with the smallest size of the others");
            Check(windows[2]!.Width == 1160, $"the media editor is a little wider: {windows[2]!.Width}");
            foreach (var window in windows) ((EditorWindow)window!).CloseNow();
            Check(SingleWindows.Find(Id(EditorTool.Collage, pictures)) is not { IsVisible: true }, "closed");

            var missing = CollageTests.Garbage("fake.png").Replace("fake", "missing");
            EditorLauncher.Open(EditorTool.Collage, [CollageTests.Garbage("fake.png"), missing]);
            EditorLauncher.Open(EditorTool.PdfPages, [missing]);
            Check(SingleWindows.Find(Id(EditorTool.PdfPages, missing)) is not { IsVisible: true }, "nothing opens for a missing file");
        }));

        Test("Collage", "The window: layout choices, spacing, background, the order list", () => Ui.Run(() =>
        {
            var model = new CollageModel([Fixtures.Url("quadrants.png"), Fixtures.Url("quadrants.jpg"), Fixtures.Url("quadrants.bmp")]);
            var window = Shown(new CollageEditor(model, "Collage — 3 images"));
            model.Pending.Wait();
            Ui.Settle(0.2);
            Check(FindAll<Segmented>(window.Layout).Count == 1 && FindAll<SliderBar>(window.Layout).Count == 2, "layout choice, spacing and corners");
            Check(FindAll<Toggle>(window.Layout).Count == 1, "the grid can fill its cells");
            Check(window.OrderRows.Count == 3, "three rows in the order list");
            Check(window.Layout.Save.IsEnabled, "three pictures can be arranged");

            model.Style = model.Style with { Layout = CollageLayout.Row };
            window.Refresh();
            Ui.Settle(0.1);
            Check(FindAll<Toggle>(window.Layout).Count == 0, "a row has no cells to fill");

            model.Style = model.Style with { Background = CollageFill.Gradient };
            window.Refresh();
            Ui.Settle(0.1);
            Check(FindAll<Swatch>(window.Layout).Count == 10, "ten gradients to choose from");
            model.Style = model.Style with { Background = CollageFill.Transparent };
            window.Refresh();
            Ui.Settle(0.1);
            Check(FindAll<Swatch>(window.Layout).Count == 0, "and none otherwise");

            var sliders = FindAll<SliderBar>(window.Layout);
            sliders[0].Move(0.04);
            Check(Near(model.Style.Spacing, 0.04, 1e-9), $"spacing: {model.Style.Spacing}");
            sliders[1].Move(0.2);
            Check(Near(model.Style.Corner, 0.2, 1e-9), $"corners: {model.Style.Corner}");

            window.MoveRow(0, 2);
            Check(model.Paths.Select(Path.GetFileName).SequenceEqual(["quadrants.jpg", "quadrants.bmp", "quadrants.png"]), "dragging a row moves the picture");
            Check(window.OrderRows.Count == 3, "the list keeps its rows");
            window.Remove(Fixtures.Url("quadrants.bmp"));
            Check(model.Paths.Count == 2 && window.OrderRows.Count == 2 && window.Layout.Save.IsEnabled, "removing a picture");
            window.Remove(Fixtures.Url("quadrants.jpg"));
            Check(!window.Layout.Save.IsEnabled, "one picture is not a collage");

            window.Layout.Reset!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(model.Style == new CollageSpec(), "Reset brings the style back (not the pictures)");
            Check(model.Paths.Count == 1, "the pictures stay as they are");
            window.CloseNow();
        }));

        Test("Collage", "Closing asks nothing, and the labels are never cut, in every language", () => Ui.Run(() =>
        {
            var asked = 0;
            var previous = EditorWindow.ConfirmDiscard;
            EditorWindow.ConfirmDiscard = _ => { asked++; return true; };
            var language = Localizer.Shared.Language;
            try
            {
                foreach (var each in Languages.All)
                {
                    Localizer.Shared.Language = each;
                    var window = Shown(new CollageEditor(new CollageModel([Fixtures.Url("quadrants.png"), Fixtures.Url("quadrants.jpg")]), "Collage"));
                    foreach (var label in FindAll<Segmented>(window.Layout).SelectMany(FindAll<TextBlock>))
                    {
                        var room = label.ActualWidth;
                        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var needed = label.DesiredSize.Width - label.Margin.Left - label.Margin.Right;
                        Check(needed <= room + 0.5, $"{each}: “{label.Text}” is cut to {room:0} of {needed:0}");
                    }
                    window.Close();
                    Check(!window.IsVisible, "a collage closes without asking");
                }
            }
            finally
            {
                Localizer.Shared.Language = language;
                EditorWindow.ConfirmDiscard = previous;
            }
            Check(asked == 0, $"asked {asked} times");
        }));

        Test("Organize PDF", "The window: pages as cells, selecting, turning, deleting with the keyboard, dragging to reorder", () => Ui.Run(() =>
        {
            var model = new PdfOrganizerModel([Fixtures.Url("report.pdf"), Fixtures.Url("quadrants.png")]);
            var window = Shown(new PdfOrganizerEditor(model, "Organize PDF — 2 files"));
            model.Pending.Wait();
            Ui.Settle(0.4);
            Check(window.Cells.Count == 4, $"{window.Cells.Count} cells");
            Check(window.Cells.All(c => c.HasThumbnail), "every cell shows its page");
            Check(window.Cells.Select(c => c.Number).SequenceEqual([1, 2, 3, 4]), "numbered in order");
            var texts = FindAll<TextBlock>(window.Layout).Select(t => t.Text).ToList();
            Check(texts.Contains("report.pdf") && texts.Contains("quadrants.png"), "with several files each cell names its file");
            Check(texts.Contains("4") && texts.Contains("none (applies to all)"), $"the totals: {string.Join(" | ", texts.Where(t => t.Length < 30))}");
            Check(window.Layout.Save.IsEnabled, "a document with pages can be saved");

            window.Click(window.Cells[1].Item.Id, extend: false);
            Ui.Settle(0.1);
            Check(window.Cells[1].IsSelected && window.Cells.Count(c => c.IsSelected) == 1, "a click selects one page");
            window.Click(window.Cells[3].Item.Id, extend: true);
            Ui.Settle(0.1);
            Check(window.Cells.Count(c => c.IsSelected) == 2, "Ctrl or Shift adds");

            model.Rotate(90);
            Ui.Settle(0.5);
            Check(model.Items.Count(i => i.Rotation == 90) == 2, "only the selected pages turn");
            Check(model.HasChanges, "turning is a change");

            // Dragging a page over another takes its place.
            var first = window.Cells[0];
            var third = window.Cells[2];
            var firstId = first.Item.Id;
            var grid = (Panel)first.Parent;
            var from = first.TranslatePoint(new Point(20, 20), grid);
            var to = third.TranslatePoint(new Point(20, 20), grid);
            window.Press(first, from);
            window.Drag(new Point(from.X + 2, from.Y));
            Check(window.Cells[0] == first && model.Items[0].Id == firstId, "a few points are not a drag");
            window.Drag(to);
            window.Release(extend: false);
            Ui.Settle(0.2);
            Check(model.Items[2].Id == firstId, $"the page took the place of the one it was dropped on: {string.Join(",", model.Items.Select(i => i.Page))}");
            Check(window.Cells[2].Item.Id == firstId && window.Cells.Select(c => c.Number).SequenceEqual([1, 2, 3, 4]), "the grid follows");
            Check(model.Selection.Count == 2, "dragging doesn’t change the selection");

            Press(window, Key.Delete);
            Ui.Settle(0.2);
            Check(model.Items.Count == 2, $"Delete takes the selected pages away: {model.Items.Count}");
            window.CloseNow();
        }));

        Test("Organize PDF", "Closing with changes asks first; without them it doesn’t", () => Ui.Run(() =>
        {
            var asked = 0;
            var answer = false;
            var previous = EditorWindow.ConfirmDiscard;
            EditorWindow.ConfirmDiscard = _ => { asked++; return answer; };
            try
            {
                var window = Shown(new PdfOrganizerEditor(new PdfOrganizerModel([Fixtures.Url("report.pdf")]), "Organize PDF"));
                window.Close();
                Check(asked == 0 && !window.IsVisible, "untouched pages close without asking");
                window = Shown(new PdfOrganizerEditor(new PdfOrganizerModel([Fixtures.Url("report.pdf")]), "Organize PDF"));
                window.Model.Rotate(90);
                window.Close();
                Check(asked == 1 && window.IsVisible, "turned pages ask, and “Cancel” keeps the window");
                window.Model.Rotate(-90);
                window.Close();
                Check(asked == 1 && !window.IsVisible, "turned back, nothing is lost");
            }
            finally { EditorWindow.ConfirmDiscard = previous; }
        }));

        Test("Organize PDF", "The buttons follow the selection, and a refusal is shown", () => Ui.Run(() =>
        {
            var model = new PdfOrganizerModel([Fixtures.Url("report.pdf")]);
            var window = Shown(new PdfOrganizerEditor(model, "Organize PDF"));
            Ui.Settle(0.2);
            var buttons = FindAll<SplitButton>(window.Layout).GroupBy(b => System.Windows.Automation.AutomationProperties.GetName(b) is { Length: > 0 } name ? name : (string)b.Content).ToDictionary(g => g.Key, g => g.First());
            Check(!buttons["Duplicate the Selected Pages"].IsEnabled && !buttons["Delete the Selected Pages (⌫)"].IsEnabled && !buttons["Save Only the Selected Pages…"].IsEnabled, "nothing selected, nothing to duplicate, delete or extract");
            Check(buttons["Rotate Left"].IsEnabled && buttons["Rotate Right"].IsEnabled, "turning applies to all pages when none is selected");
            model.SelectAll();
            model.RemoveSelected();
            Ui.Settle(0.2);
            Check(FindAll<TextBlock>(window.Layout).Any(t => t.Text.StartsWith("You can’t delete every page")), "deleting every page says why not");
            Check(buttons["Duplicate the Selected Pages"].IsEnabled, "with a selection they work");
            window.CloseNow();
        }));

        Test("Edit Media", "The video window: ready, trimming with the timeline, covering and cropping with the pointer", () => Ui.Run(() =>
        {
            var window = Shown(new MediaEditor(Fixtures.Isolated("clip.mp4", "media-window"), "Edit Media — clip.mp4"));
            Ready(window);
            var model = window.Model;
            Check(model.Ready && model.IsVideo, $"ready: {model.Failure?.FullText}");
            Check(!window.Layout.Save.IsEnabled, "nothing changed, nothing to export");
            Check(window.Clock.StartsWith("0:00.0 / 0:02.0"), $"the clock: {window.Clock}");
            Check(FindAll<Segmented>(window.Layout).Any(), "View, Crop and Cover");

            var timeline = window.Timeline;
            Check(timeline.ActualWidth > 300, $"the timeline has room: {timeline.ActualWidth}");
            timeline.Press(timeline.EndX - 2);
            timeline.Drag(timeline.ActualWidth * 0.75);
            timeline.Release();
            Check(Near(model.Edit.End, 1.5, 0.05) && model.Edit.Start == 0, $"the end handle moved: {model.Edit.End}");
            timeline.Press(timeline.StartX + 2);
            timeline.Drag(timeline.ActualWidth * 0.25);
            timeline.Release();
            Check(Near(model.Edit.Start, 0.5, 0.05), $"the start handle moved: {model.Edit.Start}");
            timeline.Press(timeline.ActualWidth * 0.5);
            timeline.Drag(timeline.ActualWidth * 0.6);
            timeline.Release();
            Check(Near(model.Current, 1.2, 0.05), $"the playhead moved: {model.Current}");
            Ui.Settle(0.2);
            Check(window.Layout.Save.IsEnabled, "a trimmed clip can be exported");
            Check(FindAll<Toggle>(window.Layout).Any(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Quick trim (no re-encoding)" && t.IsVisible), "a cut alone offers the quick trim");

            // Cover with the pointer.
            model.Mode = MediaEditorModel.EditMode.Redact;
            var stage = window.Stage;
            var frame = stage.Frame;
            Check(frame.Width > 200, $"the picture fits the stage: {frame}");
            stage.Press(new NPt(frame.MinX + frame.Width * 0.1, frame.MinY + frame.Height * 0.1));
            stage.Drag(new NPt(frame.MinX + frame.Width * 0.4, frame.MinY + frame.Height * 0.4));
            Check(stage.Draft is not null, "the area being drawn is shown");
            stage.Release(new NPt(frame.MinX + frame.Width * 0.4, frame.MinY + frame.Height * 0.4));
            Check(model.Edit.Masks.Count == 1 && model.Edit.Masks[0].Kind == MaskKind.Blur && Near(model.Edit.Masks[0].Rect.Width, 0.3, 0.01), "an area is covered");
            Check(model.SelectedRedaction == model.Edit.Masks[0].Id, "and chosen");
            stage.Press(new NPt(frame.MinX + frame.Width * 0.8, frame.MinY + frame.Height * 0.8));
            stage.Release(new NPt(frame.MinX + frame.Width * 0.8, frame.MinY + frame.Height * 0.8));
            Check(model.SelectedRedaction is null && model.Edit.Masks.Count == 1, "a click elsewhere chooses nothing and covers nothing");
            Ui.Settle(0.2);
            Check(FindAll<TextBlock>(window.Layout).Any(t => t.Text == "Blurred Area"), "the covered area is listed");

            // Crop.
            model.Mode = MediaEditorModel.EditMode.Crop;
            stage.Press(new NPt(frame.MinX + 2, frame.MinY + 2));
            Check(stage.ShowsGrid, "the thirds grid shows while a handle is held");
            stage.Drag(new NPt(frame.MinX + frame.Width * 0.25, frame.MinY + frame.Height * 0.25));
            stage.Release(new NPt(frame.MinX + frame.Width * 0.25, frame.MinY + frame.Height * 0.25));
            Check(model.Edit.Crop is { } crop && Near(crop.X, 0.25, 0.02) && Near(crop.Y, 0.25, 0.02), $"the corner moved: {model.Edit.Crop}");
            Ui.Settle(0.2);
            Check(FindAll<SplitButton>(window.Layout).Any(b => b.Content as string == "No Crop" && b.Visibility == Visibility.Visible), "“No Crop” shows with a crop");

            var asked = 0;
            var previous = EditorWindow.ConfirmDiscard;
            EditorWindow.ConfirmDiscard = _ => { asked++; return false; };
            try
            {
                window.Close();
                Check(asked == 1 && window.IsVisible, "an edited clip asks before closing");
            }
            finally { EditorWindow.ConfirmDiscard = previous; }
            window.CloseNow();
        }));

        Test("Edit Media", "Turning starts the areas over; the speed, mute and the formats follow the file", () => Ui.Run(() =>
        {
            var window = Shown(new MediaEditor(Fixtures.Isolated("clip.mp4", "media-window2"), "Edit Media — clip.mp4"));
            Ready(window);
            var model = window.Model;
            model.Edit.Crop = new NBox(0.1, 0.1, 0.5, 0.5);
            model.Edit.Masks.Add(new MaskRegion(new NBox(0, 0, 0.2, 0.2), MaskKind.Black));
            var turn = FindAll<SplitButton>(window.Layout).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Rotate 90° Right");
            turn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(model.Edit.Turns == 1 && model.Edit.Crop is null && model.Edit.Masks.Count == 0, "a turn forgets the crop and the covers");
            Check(model.DisplaySize == (240, 320), "the stage is portrait now");
            var left = FindAll<SplitButton>(window.Layout).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Rotate 90° Left");
            left.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(model.Edit.Turns == 0, "and back");

            var mute = FindAll<Toggle>(window.Layout).First(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Mute");
            var fade = FindAll<Toggle>(window.Layout).First(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Fade In");
            mute.Click();
            Ui.Settle(0.1);
            Check(model.Edit.Mute && !fade.IsEnabled && window.Player.IsMuted, "muted: no fades, and the player is silent");
            mute.Click();
            Check(!model.Edit.Mute && fade.IsEnabled, "and back");

            var formats = FindAll<DropDown>(window.Layout).First(d => System.Windows.Automation.AutomationProperties.GetName(d) == "Save As");
            var items = FindAll<TextBlock>(formats.Menu).Select(t => t.Text).ToList();
            Check(items.SequenceEqual(["Same as the Original", "MP4", "Animated GIF", "Audio Only (M4A)", "Audio Only (MP3)"]), $"a video can become: {string.Join(" | ", items)}");

            var speeds = FindAll<DropDown>(window.Layout).First(d => System.Windows.Automation.AutomationProperties.GetName(d) == "Speed");
            Check(FindAll<TextBlock>(speeds.Menu).Select(t => t.Text).SequenceEqual(["0.25×", "0.5×", "0.75×", "Normal", "1.25×", "1.5×", "2×", "4×"]), "the speeds");
            window.CloseNow();
        }));

        Test("Edit Media", "Playing: the play button, space, and the loop inside the trim", () => Ui.Run(() =>
        {
            var window = Shown(new MediaEditor(Fixtures.Isolated("clip.mp4", "media-play"), "Edit Media — clip.mp4"));
            Ready(window);
            var model = window.Model;
            model.Edit.Start = 0.5;
            model.Edit.End = 1.0;
            window.TogglePlay();
            Check(model.IsPlaying, "it plays");
            Check(model.Current >= 0.5 - 1e-9, $"from the start of the trim: {model.Current}");
            Ui.Settle(1.2);
            Check(model.Current >= 0.4 && model.Current <= 1.05, $"and stays inside the trim: {model.Current}");
            window.TogglePlay();
            Check(!model.IsPlaying, "it pauses");
            window.CloseNow();
        }));

        Test("Edit Media", "The sound window: a waveform, no picture tools, WAV as an output", () => Ui.Run(() =>
        {
            var window = Shown(new MediaEditor(Fixtures.Isolated("tone.wav", "media-sound"), "Edit Media — tone.wav"));
            Ready(window);
            Check(window.Model.Ready && !window.Model.IsVideo, "ready");
            Check(!FindAll<Segmented>(window.Layout).Any(), "no View, Crop, Cover for a sound");
            var formats = FindAll<DropDown>(window.Layout).First(d => System.Windows.Automation.AutomationProperties.GetName(d) == "Save As");
            Check(FindAll<TextBlock>(formats.Menu).Select(t => t.Text).SequenceEqual(["Same as the Original", "Audio Only (M4A)", "Audio Only (MP3)", "WAV"]), "a sound can be saved as M4A, MP3 or WAV");
            Check(FindAll<ValueSlider>(window.Layout).Count == 1 && !FindAll<Toggle>(window.Layout).Any(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Mute"), "volume, but no mute (an empty file)");
            window.CloseNow();
        }));

        Test("Edit Media", "A file that can't be edited shows why instead of an empty stage", () => Ui.Run(() =>
        {
            var window = Shown(new MediaEditor(CollageTests.Garbage("fake.png"), "Edit Media — fake.png"));
            window.Model.Loaded.Wait();
            Ui.Settle(0.4);
            Check(window.Model.Failure is not null && !window.Layout.Save.IsEnabled, "nothing to export");
            Check(FindAll<TextBlock>(window).Any(t => t.Text == window.Model.Failure!.Message), "the explanation is on the stage");
            window.CloseNow();
        }));

        Test("Edit Media", "The labels are never cut, in every language", () => Ui.Run(() =>
        {
            var language = Localizer.Shared.Language;
            try
            {
                foreach (var each in Languages.All)
                {
                    Localizer.Shared.Language = each;
                    var window = Shown(new MediaEditor(Fixtures.Isolated("clip.mp4", "media-languages"), "Edit Media"));
                    Ready(window);
                    foreach (var label in FindAll<Segmented>(window.Layout).SelectMany(FindAll<TextBlock>).Concat(FindAll<SplitButton>(window.Layout).SelectMany(FindAll<TextBlock>)))
                    {
                        var room = label.ActualWidth;
                        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var needed = label.DesiredSize.Width - label.Margin.Left - label.Margin.Right;
                        Check(needed <= room + 0.5, $"{each}: “{label.Text}” is cut to {room:0} of {needed:0}");
                    }
                    window.CloseNow();
                }
            }
            finally { Localizer.Shared.Language = language; }
        }));

        Test("Interface", "Snapshots: Collage, Organize PDF and Edit Media in black and in white", () => Ui.Run(() =>
        {
            var folder = Path.Combine(Fixtures.Root, "_captures-windows");
            Directory.CreateDirectory(folder);
            var made = 0;
            foreach (var look in LookColors.All)
            {
                var tag = look.Raw();
                WithLook(look, () =>
                {
                    var model = new CollageModel([Fixtures.Url("quadrants.png"), Fixtures.Url("quadrants.jpg"), Fixtures.Url("quadrants.bmp"), Fixtures.Url("quadrants.tiff"), Fixtures.Url("quadrants.webp")]);
                    model.Style = model.Style with { Background = CollageFill.Gradient, Gradient = 1, Corner = 0.08, Spacing = 0.03 };
                    var collage = Shown(new CollageEditor(model, "Collage — 5 images"));
                    model.Pending.Wait();
                    collage.Refresh();
                    Ui.Settle(0.5);
                    Save(folder, $"editor-collage-{tag}", Picture(collage), ref made);
                    collage.CloseNow();

                    var pdf = new PdfOrganizerModel([Fixtures.Url("report.pdf"), Fixtures.Url("quadrants.png")]);
                    var pages = Shown(new PdfOrganizerEditor(pdf, "Organize PDF — 2 files"));
                    pdf.Pending.Wait();
                    Ui.Settle(0.4);
                    pdf.Toggle(pdf.Items[1].Id, extend: false);
                    pdf.Rotate(90);
                    Ui.Settle(0.6);
                    Save(folder, $"editor-pdf-{tag}", Picture(pages), ref made);
                    pages.CloseNow();

                    var video = Shown(new MediaEditor(Fixtures.Isolated("clip.mp4", "media-capture"), "Edit Media — clip.mp4"));
                    Ready(video);
                    video.Model.Edit.Start = 0.3;
                    video.Model.Edit.End = 1.6;
                    video.Model.Edit.Masks.Add(new MaskRegion(new NBox(0.1, 0.1, 0.3, 0.3), MaskKind.Black));
                    video.Model.Edit.Masks.Add(new MaskRegion(new NBox(0.55, 0.5, 0.3, 0.3), MaskKind.Blur));
                    video.Model.Mode = MediaEditorModel.EditMode.Redact;
                    video.Seek(0.9);
                    video.Refresh();
                    Ui.Settle(0.7);
                    Save(folder, $"editor-video-{tag}", Picture(video), ref made);
                    video.CloseNow();

                    var audio = Shown(new MediaEditor(Fixtures.Isolated("tone.wav", "media-capture2"), "Edit Media — tone.wav"));
                    Ready(audio);
                    audio.Model.Edit.Start = 0.5;
                    audio.Model.Edit.End = 2.5;
                    audio.Model.Edit.FadeIn = true;
                    audio.Seek(1.2);
                    audio.Refresh();
                    Ui.Settle(0.5);
                    Save(folder, $"editor-audio-{tag}", Picture(audio), ref made);
                    audio.CloseNow();
                    return 0;
                });
            }
            Check(made == 8, $"only {made} captures were made");
        }));
    }
}
