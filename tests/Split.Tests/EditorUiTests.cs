using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Split.App;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>The editors' windows, driven without a person: layout, closing, pointer work and captures for a visual review.</summary>
public static class EditorUiTests
{
    /// <summary>A window shown far off screen so that its Skia surface is really drawn.</summary>
    internal static T Shown<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -12000;
        window.Top = 0;
        window.ShowInTaskbar = false;
        window.Show();
        Ui.Settle(0.4);
        return window;
    }

    internal static BitmapSource Picture(Window window)
    {
        var root = (Grid)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        // The window paints its own surface; the picture needs it laid down first.
        var ground = new DrawingVisual();
        using (var dc = ground.RenderOpen()) dc.DrawRectangle(Palette.Brush(Palette.WheelSurface), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(ground);
        bitmap.Render(root);
        return bitmap;
    }

    internal static void Save(string folder, string name, BitmapSource image, ref int made)
    {
        Check(Ui.Spread(image) > 4, $"{name}: the image is almost uniform: was the view empty?");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
        made++;
    }

    internal static T WithLook<T>(LookColor look, Func<T> body)
    {
        var before = Settings.LookColor;
        Look.Color = look;
        try { return body(); }
        finally { Look.Color = before; }
    }

    internal static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null!;
    }

    internal static List<T> FindAll<T>(DependencyObject root)
    {
        var all = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) all.Add(found);
            all.AddRange(FindAll<T>(child));
        }
        return all;
    }

    public static void Register()
    {
        Test("Editors", "Every editor symbol exists, and the editors are listed on the tools wheel", () =>
        {
            foreach (var tool in MarkupTools.All) Check(Icons.Find(tool.Symbol()) is not null, $"the symbol of “{tool.Title()}” has no icon");
            foreach (var symbol in new[] { "rotate.left", "rotate.right", "arrow.left.and.right.righttriangle.left.righttriangle.right", "arrow.up.and.down.righttriangle.up.righttriangle.down", "chevron.down" })
                Check(Icons.Find(symbol) is not null, $"“{symbol}” has no icon");
            Check(EditorLauncher.WindowTitle(EditorTool.Crop, [@"C:\a\photo.jpg"]) == "Crop — photo.jpg", "the window title names the file");
            Check(EditorLauncher.WindowTitle(EditorTool.Adjust, [@"C:\a\1.jpg", @"C:\a\2.png"]) == "Adjust — 2 images", "or the number of images");
        });

        Test("Editors", "A window opens once for the same files, and every editor is an app window", () => Ui.Run(() =>
        {
            var photo = Fixtures.Url("photo.jpg");
            foreach (var tool in new[] { EditorTool.Crop, EditorTool.Annotate, EditorTool.Adjust, EditorTool.Background })
            {
                EditorLauncher.Open(tool, [photo]);
                EditorLauncher.Open(tool, [photo]);
            }
            var windows = Application_OpenEditors();
            Check(windows.Count == 4, $"four windows, not {windows.Count}");
            Check(windows.All(w => w is EditorWindow and AppWindow), "they are app windows");
            Check(windows.All(w => w.ResizeMode == ResizeMode.CanResize && w.MinWidth == 780), "resizable, with the smallest size of the editors");
            foreach (var window in windows) ((EditorWindow)window).CloseNow();
            Check(Application_OpenEditors().Count == 0, "closed");

            // A file that can't be read is explained, and no empty window opens.
            var before = Application_OpenEditors().Count;
            EditorLauncher.Open(EditorTool.Crop, [Fixtures.Url("fake.png").Replace("fake", "missing")]);
            Check(Application_OpenEditors().Count == before, "nothing opens for a missing file");
        }));

        Test("Editors", "Closing with edits asks first; without edits it doesn’t", () => Ui.Run(() =>
        {
            var asked = 0;
            var answer = false;
            var previous = EditorWindow.ConfirmDiscard;
            EditorWindow.ConfirmDiscard = _ => { asked++; return answer; };
            try
            {
                using var picture = Px.Quadrants();
                var crop = Shown(new CropEditor([Fixtures.Url("quadrants.png")], "Crop", picture));
                crop.Close();
                Check(asked == 0 && !crop.IsVisible, "an untouched crop closes without asking");

                crop = Shown(new CropEditor([Fixtures.Url("quadrants.png")], "Crop", picture));
                crop.Model.Aspect = CropAspect.Square;
                Ui.Settle(0.1);
                Check(crop.Layout.Save.IsEnabled, "a changed crop can be saved");
                crop.Close();
                Check(asked == 1 && crop.IsVisible, "a changed crop asks, and “Cancel” keeps the window");
                answer = true;
                crop.Close();
                Check(asked == 2 && !crop.IsVisible, "and “Discard” closes it");

                var annotate = Shown(new AnnotateEditor(Fixtures.Url("quadrants.png"), "Annotate", picture));
                annotate.Model.Commit([Mark.Create(MarkupTool.Redact, [new NPt(0, 0), new NPt(0.2, 0.2)], SKColors.Red, 4)]);
                answer = false;
                annotate.Close();
                Check(asked == 3 && annotate.IsVisible, "marks count as work");
                annotate.Model.Undo();
                annotate.Close();
                Check(asked == 3 && !annotate.IsVisible, "undone, there is nothing to lose");

                var adjust = Shown(new AdjustEditor([Fixtures.Url("quadrants.png")], "Adjust", picture));
                adjust.Model.Adjustments = new Adjustments { Exposure = 1 };
                adjust.Close();
                Check(asked == 4 && adjust.IsVisible, "adjustments count as work");
                answer = true;
                adjust.Close();

                var background = Shown(new BackgroundEditor([Fixtures.Url("quadrants.png")], "Background", picture));
                background.Model.Style = background.Model.Style with { Shadow = 1 };
                background.Close();
                Check(asked == 5 && !background.IsVisible, "the background editor never asks");
            }
            finally { EditorWindow.ConfirmDiscard = previous; }
        }));

        Test("Crop", "The pointer drags the handles, the grid shows while dragging, and the result size follows", () => Ui.Run(() =>
        {
            using var picture = Px.Quadrants();
            var window = Shown(new CropEditor([Fixtures.Url("quadrants.png")], "Crop", picture));
            var canvas = window.Canvas;
            var frame = canvas.Frame;
            Check(frame.Width > 300 && frame.Height > 200, $"the picture is fitted into the work area: {frame}");
            Check(!canvas.ShowsGrid, "no grid at rest");
            canvas.Press(new NPt(frame.MinX + 2, frame.MinY + 2));
            Check(canvas.ShowsGrid, "the grid shows while a handle is held");
            canvas.Drag(new NPt(frame.MinX + frame.Width * 0.25, frame.MinY + frame.Height * 0.25));
            canvas.Release();
            var rect = window.Model.Settings.Rect;
            Check(!canvas.ShowsGrid && Math.Abs(rect.X - 0.25) < 0.02 && Math.Abs(rect.Y - 0.25) < 0.02, $"the corner moved: {rect}");
            Ui.Settle(0.1);
            var shown = FindAll<TextBlock>(window.Layout).Select(t => t.Text).ToList();
            Check(shown.Contains(EditorParts.Dimensions(window.Model.ResultSize.Width, window.Model.ResultSize.Height)) && window.Model.ResultSize.Width < 500, $"the result size is shown: {string.Join(" | ", shown.Where(t => t.Contains('×')))}");
            window.Model.Aspect = CropAspect.Square;
            var square = window.Model.Settings.Rect;
            Check(Math.Abs(square.Width * 600 - square.Height * 400) < 1, "a fixed format keeps its shape");
            canvas.Press(new NPt(frame.MidX, frame.MidY));
            canvas.Drag(new NPt(frame.MidX + 20, frame.MidY));
            canvas.Release();
            Check(window.Model.Settings.Rect.X > square.X, "dragging the middle moves the crop");
            window.CloseNow();
        }));

        Test("Annotate", "Drawing, selecting, moving and typing with the pointer", () => Ui.Run(() =>
        {
            using var picture = Px.Quadrants();
            var window = Shown(new AnnotateEditor(Fixtures.Url("quadrants.png"), "Annotate", picture));
            var canvas = window.Canvas;
            var model = window.Model;
            var frame = canvas.Frame;
            NPt At(double x, double y) => new(frame.MinX + frame.Width * x, frame.MinY + frame.Height * y);
            Check(!window.Layout.Save.IsEnabled, "nothing to save yet");

            model.Tool = MarkupTool.Rectangle;
            canvas.Press(At(0.2, 0.2));
            canvas.Drag(At(0.5, 0.5));
            canvas.Release(At(0.5, 0.5));
            Check(model.Marks.Count == 1 && model.Marks[0].Tool == MarkupTool.Rectangle, "a rectangle was drawn");
            canvas.Press(At(0.7, 0.7));
            canvas.Release(At(0.7, 0.7));
            Check(model.Marks.Count == 1, "a click without dragging leaves no invisible rectangle");

            model.Tool = MarkupTool.Counter;
            canvas.Press(At(0.8, 0.2));
            canvas.Release(At(0.8, 0.2));
            canvas.Press(At(0.8, 0.4));
            canvas.Release(At(0.8, 0.4));
            Check(model.Marks.Count(m => m.Tool == MarkupTool.Counter) == 2 && model.Marks[^1].Number == 2, "numbers count up");

            model.Tool = MarkupTool.Select;
            canvas.Press(At(0.35, 0.2));
            Check(model.Selection == model.Marks[0].Id, "a click on the outline selects the rectangle");
            canvas.Drag(At(0.45, 0.2));
            canvas.Release(At(0.45, 0.2));
            Check(model.Marks[0].Points[0].X is > 0.29 and < 0.31, $"it moved: {model.Marks[0].Points[0]}");
            model.Undo();
            Check(model.Marks[0].Points[0].X is > 0.19 and < 0.21, "one undo puts it back");
            model.Redo();

            model.Tool = MarkupTool.Text;
            canvas.Press(At(0.1, 0.8));
            canvas.Release(At(0.1, 0.8));
            Ui.Settle(0.1);
            Require(window.Typing is not null, "a text box opens where the text goes");
            Check(model.EditingText is not null, "the text mark is left to the text box");
            window.Typing!.Text = "Hello";
            window.FinishText();
            Check(window.Typing is null && model.Marks[^1].Text == "Hello", "Enter keeps the text");
            model.Tool = MarkupTool.Text;
            canvas.Press(At(0.5, 0.8));
            canvas.Release(At(0.5, 0.8));
            var before = model.Marks.Count;
            window.FinishText();
            Check(model.Marks.Count == before - 1, "an empty text takes its mark away");
            Check(window.Layout.Save.IsEnabled, "there is something to save");
            window.CloseNow();
        }));

        Test("Adjust", "Holding “compare” shows the original, and the sliders change the settings", () => Ui.Run(() =>
        {
            using var picture = Px.Quadrants();
            var window = Shown(new AdjustEditor([Fixtures.Url("quadrants.png")], "Adjust", picture));
            Check(window.CompareButton.Visibility == Visibility.Hidden && !window.Layout.Save.IsEnabled, "untouched: no compare button, nothing to save");
            var sliders = FindAll<SliderBar>(window.Layout);
            Check(sliders.Count == 12, $"twelve sliders: {sliders.Count}");
            sliders[0].Move(1);
            window.Model.Pending.Wait();
            Ui.Settle(0.2);
            Check(window.Model.Adjustments.Exposure == 1, "the exposure slider sets the exposure");
            Check(window.Layout.Save.IsEnabled && window.CompareButton.Visibility == Visibility.Visible, "now it can be saved and compared");
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            window.CompareButton.RaiseEvent(down);
            Check(window.Model.ShowOriginal, "pressing shows the original at once");
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            window.CompareButton.RaiseEvent(up);
            Check(!window.Model.ShowOriginal, "and letting go goes back");
            var labels = FindAll<TextBlock>(window.Layout).Select(t => t.Text).ToList();
            Check(labels.Contains("+1.0 EV"), $"the value is shown with its sign and unit: {string.Join(" | ", labels.Where(t => t.Contains("EV")))}");
            Check(ValueSlider.Signed("", 100)(-0.25) == "-25" && ValueSlider.Signed("", 100)(0) == "0" && ValueSlider.Percent(0.5) == "50%", "value formats");
            window.CloseNow();
        }));

        Test("Background", "The kinds of background, the swatches and the sliders", () => Ui.Run(() =>
        {
            using var picture = Px.Quadrants();
            var window = Shown(new BackgroundEditor([Fixtures.Url("quadrants.png")], "Background", picture));
            var swatches = FindAll<Swatch>(window.Layout);
            Check(swatches.Count == 10 && swatches[0].IsSelected, $"ten gradients, the first chosen: {swatches.Count}");
            Check(FindAll<SliderBar>(window.Layout).Count == 3, "margin, corners and shadow");
            window.Model.Style = window.Model.Style with { Kind = BackdropKind.Transparent };
            window.Model.Pending.Wait();
            window.Refresh();
            Ui.Settle(0.2);
            Check(FindAll<Swatch>(window.Layout).Count == 0, "no swatches without a gradient");
            Check(FindAll<TextBlock>(window.Layout).Any(t => t.Text.StartsWith("No background")), "the transparency note");
            window.CloseNow();
        }));

        Test("Background", "The kinds of background are never cut short, in every language", () => Ui.Run(() =>
        {
            using var picture = Px.Quadrants();
            var language = Localizer.Shared.Language;
            try
            {
                foreach (var each in Languages.All)
                {
                    Localizer.Shared.Language = each;
                    var window = Shown(new BackgroundEditor([Fixtures.Url("quadrants.png")], "Background", picture));
                    foreach (var label in FindAll<Segmented>(window.Layout).SelectMany(FindAll<TextBlock>))
                    {
                        var room = label.ActualWidth;
                        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        Check(label.DesiredSize.Width <= room + 0.5, $"{each}: “{label.Text}” is cut to {room:0} of {label.DesiredSize.Width:0}");
                    }
                    window.CloseNow();
                }
            }
            finally { Localizer.Shared.Language = language; }
        }));

        Test("Interface", "Snapshots: the four editors in black and in white", () => Ui.Run(() =>
        {
            var folder = Path.Combine(Fixtures.Root, "_captures-windows");
            Directory.CreateDirectory(folder);
            var made = 0;
            using var photo = EditorImages.Load(Fixtures.Url("photo.jpg"), EditorImages.PreviewSide);
            using var subject = EditorImages.Load(Fixtures.Url("quadrants.png"), EditorImages.PreviewSide);
            foreach (var look in LookColors.All)
            {
                var tag = look.Raw();
                WithLook(look, () =>
                {
                    var crop = Shown(new CropEditor([Fixtures.Url("photo.jpg")], "Crop — photo.jpg", photo));
                    crop.Model.Aspect = CropAspect.R4x5;
                    crop.Canvas.Press(new NPt(crop.Canvas.Frame.MidX, crop.Canvas.Frame.MidY));
                    crop.Canvas.Drag(new NPt(crop.Canvas.Frame.MidX + 30, crop.Canvas.Frame.MidY + 10));
                    crop.Refresh();
                    Ui.Settle(0.3);
                    Save(folder, $"editor-crop-{tag}", Picture(crop), ref made);
                    crop.CloseNow();

                    var annotate = Shown(new AnnotateEditor(Fixtures.Url("photo.jpg"), "Annotate and Cover — photo.jpg", photo));
                    var model = annotate.Model;
                    var color = new SKColor(255, 59, 48);
                    var text = Mark.Create(MarkupTool.Text, [new NPt(0.08, 0.1)], color, 6) with { Text = "Look here" };
                    model.Commit(
                    [
                        Mark.Create(MarkupTool.Arrow, [new NPt(0.3, 0.35), new NPt(0.5, 0.55)], color, 6),
                        Mark.Create(MarkupTool.Rectangle, [new NPt(0.55, 0.3), new NPt(0.85, 0.6)], new SKColor(0, 122, 255), 5),
                        Mark.Create(MarkupTool.Blur, [new NPt(0.1, 0.6), new NPt(0.35, 0.85)], color, 4),
                        Mark.Create(MarkupTool.Pixelate, [new NPt(0.4, 0.65), new NPt(0.6, 0.9)], color, 4),
                        Mark.Create(MarkupTool.Redact, [new NPt(0.65, 0.68), new NPt(0.9, 0.8)], color, 4),
                        Mark.Create(MarkupTool.Highlighter, [new NPt(0.1, 0.25), new NPt(0.3, 0.22), new NPt(0.45, 0.26)], new SKColor(255, 204, 0), 4),
                        Mark.Create(MarkupTool.Counter, [new NPt(0.9, 0.12)], color, 6) with { Number = 1 },
                        text,
                    ]);
                    model.Tool = MarkupTool.Select;
                    model.Selection = model.Marks[1].Id;
                    Ui.Settle(0.3);
                    annotate.Refresh();
                    Ui.Settle(0.3);
                    Save(folder, $"editor-annotate-{tag}", Picture(annotate), ref made);
                    annotate.CloseNow();

                    var adjust = Shown(new AdjustEditor([Fixtures.Url("photo.jpg")], "Adjust — photo.jpg", photo));
                    adjust.Model.Adjustments = new Adjustments { Exposure = 0.3, Contrast = 1.15, Saturation = 1.2, Warmth = 0.2, Vignette = 0.4, Filter = PhotoFilter.Chrome };
                    adjust.Model.Pending.Wait();
                    adjust.Refresh();
                    Ui.Settle(0.5);
                    Save(folder, $"editor-adjust-{tag}", Picture(adjust), ref made);
                    adjust.CloseNow();

                    var background = Shown(new BackgroundEditor([Fixtures.Url("quadrants.png")], "Add Background — quadrants.png", subject));
                    background.Model.Pending.Wait();
                    Ui.Settle(0.4);
                    Save(folder, $"editor-background-{tag}", Picture(background), ref made);
                    background.Model.Style = background.Model.Style with { Kind = BackdropKind.Transparent, Corner = 0.08 };
                    background.Model.Pending.Wait();
                    background.Refresh();
                    Ui.Settle(0.4);
                    Save(folder, $"editor-background-transparent-{tag}", Picture(background), ref made);
                    background.CloseNow();
                    return 0;
                });
            }
            Check(made == 10, $"only {made} captures were made");
        }));
    }

    private static List<Window> Application_OpenEditors() =>
        new[] { "editor:Crop", "editor:Annotate", "editor:Adjust", "editor:Background" }.Select(prefix => SingleWindowsFor(prefix)).OfType<Window>().ToList();

    private static Window? SingleWindowsFor(string prefix)
    {
        var path = Path.GetFullPath(Fixtures.Url("photo.jpg")).ToLowerInvariant();
        var window = SingleWindows.Find($"{prefix}:{path}");
        return window is { IsVisible: true } ? window : null;
    }
}
