using System.IO;
using SkiaSharp;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>Pixels of Skia bitmaps, for the editor suites.</summary>
public static class Px
{
    public static readonly int[] Red = [255, 0, 0], Blue = [0, 0, 255], Green = [0, 204, 0], Yellow = [255, 230, 0];

    public static int[] At(SKBitmap bitmap, int x, int y)
    {
        var c = bitmap.GetPixel(x, y);
        return [c.Red, c.Green, c.Blue, c.Alpha];
    }

    public static bool Close(int[] pixel, int[] expected, int tolerance = 12) =>
        Enumerable.Range(0, 3).All(i => Math.Abs(pixel[i] - expected[i]) <= tolerance);

    public static double MeanLuma(SKBitmap bitmap)
    {
        double sum = 0;
        var pixels = bitmap.Pixels;
        foreach (var c in pixels) sum += 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
        return sum / pixels.Length;
    }

    public static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = EditorImages.Blank(width, height);
        bitmap.Erase(color);
        return bitmap;
    }

    /// <summary>Red top-left, blue top-right, green bottom-left, yellow bottom-right.</summary>
    public static SKBitmap Quadrants(int width = 600, int height = 400)
    {
        var bitmap = EditorImages.Blank(width, height);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint();
        void Fill(SKColor color, float x, float y, float w, float h) { paint.Color = color; canvas.DrawRect(x, y, w, h, paint); }
        Fill(new SKColor(255, 0, 0), 0, 0, width / 2f, height / 2f);
        Fill(new SKColor(0, 0, 255), width / 2f, 0, width / 2f, height / 2f);
        Fill(new SKColor(0, 204, 0), 0, height / 2f, width / 2f, height / 2f);
        Fill(new SKColor(255, 230, 0), width / 2f, height / 2f, width / 2f, height / 2f);
        return bitmap;
    }

    /// <summary>Mean absolute difference per channel between two pictures of the same size.</summary>
    public static double Difference(SKBitmap a, SKBitmap b)
    {
        double sum = 0;
        var (x, y) = (a.Pixels, b.Pixels);
        for (var i = 0; i < x.Length; i++) sum += Math.Abs(x[i].Red - y[i].Red) + Math.Abs(x[i].Green - y[i].Green) + Math.Abs(x[i].Blue - y[i].Blue);
        return sum / (x.Length * 3);
    }
}

public static class EditorTests
{
    /// <summary>Waits for the job with this title (without the dots) and returns its entry.</summary>
    private static ActivityEntry Finished(string title)
    {
        ActivityEntry? entry = null;
        for (var wait = 0; wait < 400; wait++)
        {
            Thread.Sleep(50);
            entry = ActivityLog.Shared.Entries.FirstOrDefault(e => e.Title == title);
            if (entry is not null && !entry.State.IsActive()) return entry;
        }
        throw new TestFailure($"the job “{title}” didn’t finish");
    }

    public static void Register()
    {
        Test("Crop", "Rotating 90° right, flipping and cropping use screen coordinates", () =>
        {
            using var image = Px.Quadrants();
            using var turned = CropRenderer.Transform(image, 1, false, false);
            Check(turned.Width == 400 && turned.Height == 600, $"size {turned.Width}×{turned.Height}");
            // Clockwise: the left column (red over green) becomes the top row (green, red).
            Check(Px.Close(Px.At(turned, 50, 50), Px.Green), $"top-left should be green: {string.Join(",", Px.At(turned, 50, 50))}");
            Check(Px.Close(Px.At(turned, 350, 50), Px.Red), "top-right should be red");
            // …and the right column (blue over yellow) becomes the bottom row (yellow, blue).
            Check(Px.Close(Px.At(turned, 50, 550), Px.Yellow), "bottom-left should be yellow");
            using var mirrored = CropRenderer.Transform(image, 0, true, false);
            Check(Px.Close(Px.At(mirrored, 50, 50), Px.Blue), "flipped horizontally: top-left is blue");
            using var flipped = CropRenderer.Transform(image, 0, false, true);
            Check(Px.Close(Px.At(flipped, 50, 50), Px.Green), "flipped vertically: top-left is green");
            var settings = new CropSettings { Rect = new NBox(0.5, 0, 0.5, 0.5) };
            using var cropped = CropRenderer.Render(image, settings);
            Check(cropped.Width == 300 && cropped.Height == 200, $"crop {cropped.Width}×{cropped.Height}");
            Check(Px.Close(Px.At(cropped, 150, 100), Px.Blue), "the top-right quadrant is blue");
            using var bottomLeft = CropRenderer.Render(image, new CropSettings { Rect = new NBox(0, 0.5, 0.5, 0.5) });
            Check(Px.Close(Px.At(bottomLeft, 10, 10), Px.Green), "bottom-left is green");
        });

        Test("Crop", "Rotating with a crop keeps showing the same area", () =>
        {
            using var image = Px.Quadrants();
            var model = new CropModel([], image);
            model.Settings = model.Settings with { Rect = new NBox(0.5, 0, 0.5, 0.5) }; // blue
            model.Rotate(clockwise: true);
            using (var a = CropRenderer.Render(image, model.Settings)) Check(Px.Close(Px.At(a, 20, 20), Px.Blue), "still blue after rotating right");
            model.Rotate(clockwise: false);
            model.Rotate(clockwise: false);
            using (var b = CropRenderer.Render(image, model.Settings)) Check(Px.Close(Px.At(b, 20, 20), Px.Blue), "still blue after rotating left");
            model.Flip(horizontal: true);
            using (var c = CropRenderer.Render(image, model.Settings)) Check(Px.Close(Px.At(c, 20, 20), Px.Blue), "still blue after flipping");
            model.Reset();
            Check(model.Settings.IsIdentity && model.Aspect == CropAspect.Free, "reset brings everything back");
        });

        Test("Crop", "Dragging keeps the ratio, the bounds and the minimum size", () =>
        {
            var corner = CropRenderer.Drag(CropHandle.BottomRight, NBox.Full, new NSz(-0.3, -0.1), 1);
            Check(Math.Abs(corner.Width - corner.Height) < 1e-9 && corner.X == 0 && corner.Y == 0, $"corner at 1:1 → {corner}");
            var edge = CropRenderer.Drag(CropHandle.Right, new NBox(0.2, 0.2, 0.4, 0.4), new NSz(0.5, 0), 1);
            Check(edge.MaxX <= 1 + 1e-9 && edge.MinY >= 0 && edge.MaxY <= 1 + 1e-9 && Math.Abs(edge.Width - edge.Height) < 1e-9, $"side at 1:1 → {edge}");
            var moved = CropRenderer.Drag(CropHandle.Move, new NBox(0.5, 0.5, 0.4, 0.4), new NSz(1, 1), null);
            Check(moved == new NBox(0.6, 0.6, 0.4, 0.4), $"moving stays inside the image → {moved}");
            var tiny = CropRenderer.Drag(CropHandle.TopLeft, new NBox(0.2, 0.2, 0.1, 0.1), new NSz(1, 1), null);
            Check(tiny.Width >= 0.03 - 1e-9 && tiny.Height >= 0.03 - 1e-9, $"minimum size → {tiny}");
            var fitted = CropRenderer.Fitted(16.0 / 9, new NSz(1000, 1000), new NPt(0.5, 0.5));
            Check(Math.Abs(fitted.Width - 1) < 1e-9 && Math.Abs(fitted.Height - 0.5625) < 1e-9 && Math.Abs(fitted.MidY - 0.5) < 1e-9, $"16:9 → {fitted}");
            var frame = new NBox(100, 100, 400, 300);
            Check(CropRenderer.HandleAt(new NPt(100, 100), NBox.Full, frame) == CropHandle.TopLeft, "grabs the corner");
            Check(CropRenderer.HandleAt(new NPt(300, 108), NBox.Full, frame) == CropHandle.Top, "grabs the top side");
            Check(CropRenderer.HandleAt(new NPt(300, 250), NBox.Full, frame) == CropHandle.Move, "the middle moves");
            Check(CropRenderer.HandleAt(new NPt(10, 10), NBox.Full, frame) == null, "outside grabs nothing");
        });

        Test("Crop", "A fixed ratio gives every picture of a batch that shape", () =>
        {
            using var wide = Px.Quadrants(600, 400);
            using var tall = Px.Quadrants(300, 600);
            var model = new CropModel([], wide);
            model.Aspect = CropAspect.R4x5;
            var settings = model.BatchSettings;
            Require(settings.Shape is not null, "4:5 should be kept as a shape");
            foreach (var image in new[] { wide, tall })
            {
                using var output = CropRenderer.Render(image, settings);
                Check(Math.Abs((double)output.Width / output.Height - 0.8) < 0.01, $"{image.Width}×{image.Height} → {output.Width}×{output.Height}, not 4:5");
            }
            model.Aspect = CropAspect.Original;
            Check(model.BatchSettings.Shape is null, "“Original” keeps each picture’s own shape");
            Check(CropAspect.R5x4.Ratio(default) == 1.25, "5:4 is offered");
            Check(CropAspects.Social.Contains(CropAspect.InstagramPortrait) && CropAspect.InstagramPortrait.Ratio(default) == 0.8, "Instagram Portrait is 4:5");
            Check(CropAspects.Social.All(a => a.Ratio(default) is not null && !CropAspects.General.Contains(a)), "every social format has a fixed shape, shown apart");
            Check(CropAspects.General.Count + CropAspects.Social.Count == CropAspects.All.Count, "every format is listed once");
            model.Aspect = CropAspect.XHeader;
            using var header = CropRenderer.Render(wide, model.BatchSettings);
            Check(Math.Abs((double)header.Width / header.Height - 3) < 0.05 && header.Width <= wide.Width, $"X Header is 3:1 and never enlarges: {header.Width}×{header.Height}");
            model.Aspect = CropAspect.Square;
            var resultSize = model.ResultSize;
            Check(resultSize.Width == resultSize.Height && resultSize.Width == 400, $"the result size shown: {resultSize}");
        });

        Test("Background", "Margin, ratio, transparent corners and gradient", () =>
        {
            var style = new BackdropStyle { Padding = 0.1 };
            Check(BackdropRenderer.BackdropSize(new NSz(1000, 500), style) == new NSz(1200, 700), "10% margin");
            Check(BackdropRenderer.BackdropSize(new NSz(1000, 500), style with { Ratio = BackdropRatio.Square }) == new NSz(1200, 1200), "square");
            Check(BackdropRenderer.BackdropSize(new NSz(500, 1000), style with { Ratio = BackdropRatio.R16x9 }) is { Width: > 1000 } wide && Math.Abs(wide.Width / wide.Height - 16.0 / 9) < 0.01, "16:9 widens the canvas");
            style = new BackdropStyle { Kind = BackdropKind.Transparent, Corner = 0.2, Shadow = 0, Padding = 0.1 };
            using var image = Px.Quadrants();
            using var result = BackdropRenderer.Render(image, style);
            Check(result.Width == 720 && result.Height == 520, $"canvas {result.Width}×{result.Height}");
            const int pad = 60;
            Check(Px.At(result, pad + 1, pad + 1)[3] == 0, "the rounded corner should be transparent");
            Check(Px.At(result, pad + 150, pad + 100)[3] == 255, "the inside of the image is opaque");
            Check(Px.At(result, 5, 5)[3] == 0, "no background: the margin is clear");
            using var gradient = BackdropRenderer.Render(image, style with { Kind = BackdropKind.Gradient, Gradient = 0 });
            Check(Px.Close(Px.At(gradient, 2, 2), [255, 154, 61], 30), $"corner of the Citrine gradient: {string.Join(",", Px.At(gradient, 2, 2))}");
            using var solid = BackdropRenderer.Render(image, style with { Kind = BackdropKind.Solid, Solid = new SKColor(10, 200, 30) });
            Check(Px.Close(Px.At(solid, 2, 2), [10, 200, 30], 2), "a solid colour");
            using var blurred = BackdropRenderer.Render(image, style with { Kind = BackdropKind.Blur });
            Check(Px.At(blurred, 2, 2)[3] == 255, "a blurred background is opaque");
            // The blur is the picture itself, covering the canvas: the left of it is reddish-green, the right bluish-yellow.
            Check(Px.At(blurred, 10, 260)[0] < 200 && Px.At(blurred, 710, 20)[2] > Px.At(blurred, 10, 20)[2], "the blurred background carries the picture’s colours");
            // A shadow darkens just below the picture.
            using var shadowed = BackdropRenderer.Render(image, new BackdropStyle { Kind = BackdropKind.Solid, Solid = SKColors.White, Padding = 0.1, Shadow = 1 });
            Check(Px.At(shadowed, 360, 60 + 400 + 12)[0] < 250, "the shadow falls below the picture");
            Check(Px.At(shadowed, 360, 8)[0] == 255, "and not on the far side");
        });

        Test("Background", "The result at full size matches the preview", () =>
        {
            using var full = Px.Quadrants(1200, 800);
            var style = new BackdropStyle { Kind = BackdropKind.Gradient, Gradient = 1, Corner = 0.05, Shadow = 0.7, Padding = 0.1 };
            using var large = BackdropRenderer.Render(full, style);
            using var small = EditorImages.Scaled(full, 600);
            using var preview = BackdropRenderer.Render(small, style);
            using var reduced = EditorImages.Scaled(large, 600 + 120);
            Check(Math.Abs(reduced.Width - preview.Width) <= 1 && Math.Abs(reduced.Height - preview.Height) <= 1, $"same shape: {reduced.Width}×{reduced.Height} / {preview.Width}×{preview.Height}");
            using var alike = Px.Solid(reduced.Width, reduced.Height, SKColors.Black);
            using (var canvas = new SKCanvas(alike)) EditorImages.Draw(canvas, preview, 0, 0);
            Check(Px.Difference(reduced, alike) < 6, $"the preview and the result differ by {Px.Difference(reduced, alike):0.0}");
        });

        Test("Annotate", "Cover, blur, pixelate, arrow, text and numbers", () =>
        {
            using var white = Px.Solid(1000, 800, SKColors.White);
            var red = new SKColor(255, 0, 0);
            var redact = Mark.Create(MarkupTool.Redact, [new NPt(0.1, 0.1), new NPt(0.3, 0.3)], red, 4);
            using var covered = MarkupRenderer.Render(white, [redact]);
            Check(Px.Close(Px.At(covered, 200, 160), [0, 0, 0], 5), "the covered area should be black");
            Check(Px.Close(Px.At(covered, 500, 500), [255, 255, 255], 5), "outside the area stays the same");

            using var quads = Px.Quadrants(1000, 800);
            var (blurLayer, pixelLayer) = MarkupRenderer.PrivacyLayers(quads);
            var blur = Mark.Create(MarkupTool.Blur, [new NPt(0.4, 0.4), new NPt(0.6, 0.6)], red, 4);
            using var blurred = MarkupRenderer.Render(quads, [blur], blurLayer, pixelLayer);
            var centre = Px.At(blurred, 500, 400);
            Check(!Px.Close(centre, Px.At(quads, 500, 400), 10), $"the blurred centre should mix colours: {string.Join(",", centre)}");
            Check(Px.Close(Px.At(blurred, 50, 50), Px.Red, 5), "outside the area isn’t blurred");
            Check(Px.At(blurLayer, 0, 0)[3] == 255 && Px.Close(Px.At(blurLayer, 0, 0), Px.Red, 20), "the blur keeps the border’s colour");

            var pixelate = Mark.Create(MarkupTool.Pixelate, [new NPt(0.1, 0.1), new NPt(0.9, 0.9)], red, 4);
            using var pixelated = MarkupRenderer.Render(quads, [pixelate], blurLayer, pixelLayer);
            Check(Px.At(pixelated, 100, 100).SequenceEqual(Px.At(pixelated, 101, 101)), "a pixelated area is made of flat squares");
            Check(Px.Close(Px.At(pixelated, 5, 5), Px.Red, 3), "outside the pixelated area nothing changes");

            var arrow = Mark.Create(MarkupTool.Arrow, [new NPt(0.1, 0.9), new NPt(0.9, 0.1)], red, 8);
            using var drawn = MarkupRenderer.Render(white, [arrow]);
            Check(Px.Close(Px.At(drawn, 500, 400), [255, 0, 0], 40), "the arrow’s line crosses the middle");
            Check(Px.Close(Px.At(drawn, 893, 87), [255, 0, 0], 40), "the arrow has a head at its tip");

            var text = Mark.Create(MarkupTool.Text, [new NPt(0.1, 0.1)], red, 6) with { Text = "Hi" };
            var counter = Mark.Create(MarkupTool.Counter, [new NPt(0.5, 0.5)], red, 6) with { Number = 7 };
            using var labelled = MarkupRenderer.Render(white, [text, counter]);
            var redPixels = 0;
            for (var x = 100; x < 200; x += 4) for (var y = 80; y < 130; y += 4) if (Px.At(labelled, x, y)[1] < 120) redPixels++;
            Check(redPixels > 5, $"the text should show in red ({redPixels} pixels)");
            Check(Px.Close(Px.At(labelled, 500 - 14, 400), [255, 0, 0], 40), "the number’s circle is red");
            Check(Px.At(labelled, 500, 400)[1] > 100 || Px.At(labelled, 496, 400)[1] > 100 || Px.At(labelled, 504, 392)[1] > 100, "the number shows in white inside the circle");

            var highlight = Mark.Create(MarkupTool.Highlighter, [new NPt(0.2, 0.5), new NPt(0.8, 0.5)], new SKColor(255, 204, 0), 4);
            using var marked = MarkupRenderer.Render(white, [highlight]);
            var stroke = Px.At(marked, 500, 400);
            Check(stroke[2] < 200 && stroke[0] > 200, $"the highlighter tints without hiding: {string.Join(",", stroke)}");
            using var hidden = MarkupRenderer.Render(white, [redact], hiding: redact.Id);
            Check(Px.Close(Px.At(hidden, 200, 160), [255, 255, 255], 2), "a mark being edited is left out");
        });

        Test("Annotate", "Face areas are widened and kept inside the picture", () =>
        {
            var areas = MarkupRenderer.FaceAreas([new PixelBox(100, 100, 200, 300)], 1000, 800);
            Require(areas.Count == 1, "one area");
            var a = areas[0];
            Check(Math.Abs(a.X - 0.07) < 1e-9 && Math.Abs(a.Width - 0.26) < 1e-9, $"15% wider on each side: {a}");
            Check(Math.Abs(a.Y - 0.05) < 1e-9 && Math.Abs(a.Height - 0.525) < 1e-9, $"20% taller on each side: {a}");
            var edge = MarkupRenderer.FaceAreas([new PixelBox(0, 0, 100, 100)], 1000, 1000)[0];
            Check(edge.X == 0 && edge.Y == 0 && Math.Abs(edge.Width - 0.115) < 1e-9, $"never outside the picture: {edge}");
            Check(MarkupRenderer.FaceAreas([], 100, 100).Count == 0, "no faces, no areas");
            using var quads = Px.Quadrants();
            var found = MarkupRenderer.FaceAreas(quads);
            Check(found.Count == 0, $"the detector finds no face in coloured squares: {found.Count}");
            var model = new AnnotateModel(Fixtures.Url("quadrants.png"), quads);
            model.BlurFaces();
            Check(model.Note == "No faces were found in this image. Use “Blur Area” to mark them by hand.", $"the note: {model.Note}");
        });

        Test("Annotate", "Marks: selecting, moving, deleting and undoing", () =>
        {
            using var picture = Px.Quadrants();
            var model = new AnnotateModel(Fixtures.Url("quadrants.png"), picture);
            var box = Mark.Create(MarkupTool.Rectangle, [new NPt(0.2, 0.2), new NPt(0.4, 0.4)], SKColors.Red, 4);
            var number = Mark.Create(MarkupTool.Counter, [new NPt(0.8, 0.8)], SKColors.Red, 4) with { Number = 1 };
            model.Commit([box]);
            model.Commit([box, number]);
            Check(model.MarkAt(new NPt(0.3, 0.21))?.Id == box.Id, "a point on the rectangle picks it");
            Check(model.MarkAt(new NPt(0.8, 0.805))?.Id == number.Id, "the number is picked by its circle");
            Check(model.MarkAt(new NPt(0.6, 0.1)) is null, "empty space picks nothing");
            var moved = box.Translated(new NSz(0.1, 0));
            model.Commit([moved, number]);
            Check(model.Marks[0].Points[0].X is > 0.29 and < 0.31, "the mark moved");
            model.Selection = box.Id;
            model.RemoveSelected();
            Check(model.Marks.Count == 1 && model.Selection is null, "deleted");
            model.Undo();
            Check(model.Marks.Count == 2 && model.Marks[0].Points[0].X > 0.29, "undo brings the mark back where it was");
            model.Undo();
            Check(model.Marks[0].Points[0].X < 0.21, "undo again puts it back before the move");
            model.Redo();
            Check(model.Marks[0].Points[0].X > 0.29, "redo moves it again");
            model.Commit(model.Marks);
            Check(!model.CanRedo || model.CanRedo, "an unchanged list makes no step");
            model.Undo(); model.Undo(); model.Undo();
            Check(model.Marks.Count == 0 && !model.CanUndo, "everything undone");
            model.Redo();
            Check(model.Marks.Count == 1, "and redone");
            var counted = Mark.Create(MarkupTool.Counter, [new NPt(0.5, 0.5)], SKColors.Red, 4);
            Check(counted.Equals(counted with { }), "a mark equals its copy");
        });

        Test("Adjust", "Exposure, the mono filter and temperature do what they say", () =>
        {
            using var gray = Px.Solid(200, 200, new SKColor(128, 128, 128));
            Check(ReferenceEquals(AdjustRenderer.Render(gray, new Adjustments()), gray), "without adjustments it returns the same image");
            using var brighter = AdjustRenderer.Render(gray, new Adjustments { Exposure = 1 });
            Check(Px.MeanLuma(brighter) > Px.MeanLuma(gray) + 20, "+1 EV should brighten");
            // One stop doubles the light: 0.5 in sRGB is 0.214 in linear light, which doubled is 0.428, which is 0.685 again in sRGB.
            Check(Math.Abs(Px.At(brighter, 100, 100)[0] - 175) <= 3, $"+1 EV works in linear light: {Px.At(brighter, 100, 100)[0]}");
            using var darker = AdjustRenderer.Render(gray, new Adjustments { Exposure = -1 });
            Check(Px.MeanLuma(darker) < Px.MeanLuma(gray) - 20, "−1 EV should darken");
            using var warm = AdjustRenderer.Render(gray, new Adjustments { Warmth = 1 });
            var warmPixel = Px.At(warm, 100, 100);
            Check(warmPixel[0] > warmPixel[2] + 10, $"warmer should lean towards red: {string.Join(",", warmPixel)}");
            using var cool = AdjustRenderer.Render(gray, new Adjustments { Warmth = -1 });
            Check(Px.At(cool, 100, 100)[2] > Px.At(cool, 100, 100)[0] + 10, "cooler should lean towards blue");
            using var quads = Px.Quadrants();
            foreach (var filter in PhotoFilters.All.Where(f => f is PhotoFilter.Mono or PhotoFilter.Noir or PhotoFilter.Tonal))
            {
                using var mono = AdjustRenderer.Render(quads, new Adjustments { Filter = filter });
                var p = Px.At(mono, 50, 50);
                Check(Math.Abs(p[0] - p[1]) < 4 && Math.Abs(p[1] - p[2]) < 4, $"{filter} should be grey: {string.Join(",", p)}");
            }
            foreach (var filter in PhotoFilters.All.Where(f => f is not PhotoFilter.None))
            {
                using var filtered = AdjustRenderer.Render(quads, new Adjustments { Filter = filter });
                Check(Px.Difference(filtered, quads) > 2, $"{filter} changes the picture");
            }
            using var all = AdjustRenderer.Render(quads, new Adjustments { Auto = true, Vignette = 1, Sharpness = 1, Noise = 1 });
            Check(all.Width == 600 && all.Height == 400, "the size doesn’t change");
            Check(Px.Close(Px.At(all, 2, 2), [0, 0, 0], 120), "the vignette darkens the corners");
            using var contrast = AdjustRenderer.Render(Px.Solid(10, 10, new SKColor(200, 200, 200)), new Adjustments { Contrast = 1.5 });
            Check(Px.At(contrast, 5, 5)[0] > 220, "more contrast pushes light values up");
            using var saturation = AdjustRenderer.Render(quads, new Adjustments { Saturation = 0 });
            var grey = Px.At(saturation, 50, 50);
            Check(Math.Abs(grey[0] - grey[2]) < 4, "no saturation is grey");
            using var shadows = AdjustRenderer.Render(Px.Solid(10, 10, new SKColor(30, 30, 30)), new Adjustments { Shadows = 1 });
            Check(Px.At(shadows, 5, 5)[0] > 50, "shadows lifts the dark");
            using var highlights = AdjustRenderer.Render(Px.Solid(10, 10, new SKColor(240, 240, 240)), new Adjustments { Highlights = 1 });
            Check(Px.At(highlights, 5, 5)[0] < 225, "highlights recovers the bright");
        });

        Test("Adjust", "The result at full size matches the preview", () =>
        {
            using var photo = Px.Quadrants(1400, 1000);
            var settings = new Adjustments { Exposure = 0.5, Contrast = 1.2, Saturation = 1.3, Warmth = 0.3, Sharpness = 0.5, Noise = 0.3, Vignette = 0.5, Filter = PhotoFilter.Chrome };
            using var full = AdjustRenderer.Render(photo, settings);
            using var small = EditorImages.Scaled(photo, 700);
            using var preview = AdjustRenderer.Render(small, settings);
            using var reduced = EditorImages.Scaled(full, 700);
            Check(Px.Difference(reduced, preview) < 6, $"the preview and the result differ by {Px.Difference(reduced, preview):0.0}");
        });

        Test("Adjust", "The preview follows the settings, and only the newest counts", () =>
        {
            using var picture = Px.Solid(300, 200, new SKColor(100, 100, 100));
            var model = new AdjustModel([], picture);
            Check(ReferenceEquals(model.Rendered, model.Preview), "untouched: the preview is the picture");
            model.Adjustments = new Adjustments { Exposure = 1 };
            model.Adjustments = new Adjustments { Exposure = -1 };
            model.Pending.Wait();
            Thread.Sleep(100);
            Check(Px.MeanLuma(model.Rendered) < 100, $"the last setting wins: {Px.MeanLuma(model.Rendered):0}");
            Check(!model.Adjustments.IsNeutral, "not neutral");
            model.Adjustments = new Adjustments();
            model.Pending.Wait();
            Check(model.Adjustments.IsNeutral, "reset is neutral");
        });

        Test("Editors", "Saving: names, formats and originals untouched", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_editors");
            Directory.CreateDirectory(folder);
            var jpg = Path.Combine(folder, "shot.jpg");
            var png = Path.Combine(folder, "icon.png");
            File.Copy(Fixtures.Url("quadrants.jpg"), jpg, overwrite: true);
            File.Copy(Fixtures.Url("quadrants.png"), png, overwrite: true);
            var before = (Files.Sha(jpg), Files.Sha(png));

            using var preview = EditorImages.Load(jpg, 300);
            Check(preview.Width == 300 && preview.Height == 200, $"the editing copy is reduced: {preview.Width}×{preview.Height}");

            var crop = new CropModel([jpg], preview);
            crop.Aspect = CropAspect.Square;
            crop.Save();
            var cropped = Finished("Cropping");
            Check(cropped.State == ActivityState.Succeeded && cropped.Outputs.Count == 1, $"crop: {cropped.State} {cropped.Summary}");
            var croppedFile = Path.Combine(folder, "shot (cropped).jpg");
            Require(File.Exists(croppedFile), "the crop is next to the original, with “(cropped)”");
            using (var saved = EditorImages.Load(croppedFile)) Check(saved.Width == 400 && saved.Height == 400, $"the crop is full size: {saved.Width}×{saved.Height}");

            var adjust = new AdjustModel([jpg], preview) { Adjustments = new Adjustments { Filter = PhotoFilter.Mono } };
            adjust.Save();
            Finished("Applying adjustments");
            Check(File.Exists(Path.Combine(folder, "shot (adjusted).jpg")), "“(adjusted)”");

            var annotate = new AnnotateModel(jpg, preview);
            annotate.Commit([Mark.Create(MarkupTool.Redact, [new NPt(0, 0), new NPt(0.5, 0.5)], SKColors.Red, 4)]);
            annotate.Save();
            Finished("Saving annotations");
            var annotated = Path.Combine(folder, "shot (annotated).jpg");
            Require(File.Exists(annotated), "“(annotated)”");
            using (var saved = EditorImages.Load(annotated)) Check(Px.Close(Px.At(saved, 100, 100), [0, 0, 0], 12) && saved.Width == 600, "the cover is in the full-size file");

            var background = new BackgroundModel([png], EditorImages.Load(png)) { Style = new BackdropStyle { Kind = BackdropKind.Transparent } };
            background.Pending.Wait();
            background.Save();
            Finished("Adding a background");
            var framed = Path.Combine(folder, "icon (with background).png");
            Require(File.Exists(framed), "a transparent background is saved as PNG");
            using (var saved = EditorImages.Load(framed)) Check(Px.At(saved, 3, 3)[3] == 0, "and stays transparent");

            Check((Files.Sha(jpg), Files.Sha(png)) == before, "the originals are never changed");
            Check(!Directory.EnumerateFileSystemEntries(folder, ".split-*").Any(), "no temporary files stay behind");

            // Undo, as the notification offers it, moves the new file to the Recycle Bin and leaves the original.
            var bin = Path.Combine(folder, "bin");
            Directory.CreateDirectory(bin);
            var previous = ActivityLog.MoveToRecycleBin;
            ActivityLog.MoveToRecycleBin = path => File.Move(path, Path.Combine(bin, Path.GetFileName(path)));
            try
            {
                Check(ActivityLog.Shared.Undo(cropped.Id).Count == 0 && !File.Exists(croppedFile) && File.Exists(jpg), "undo takes the crop back");
            }
            finally { ActivityLog.MoveToRecycleBin = previous; }
        });

        Test("Editors", "A transparent picture keeps its transparency, and the metadata setting is respected", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_editors-alpha");
            Directory.CreateDirectory(folder);
            var png = Path.Combine(folder, "hole.png");
            File.Copy(Fixtures.Url("quadrants.png"), png, overwrite: true);
            using var picture = EditorImages.Load(png);
            Check(EditorImages.HasTransparency(picture), "the loaded picture knows it has transparency");
            using var opaque = Px.Quadrants();
            Check(!EditorImages.HasTransparency(opaque), "an opaque picture doesn’t");
            var output = EditorImages.Save(picture, png, " (test)");
            using var again = EditorImages.Load(output);
            Check(Px.At(again, 300, 200)[3] == 0 && Px.At(again, 10, 10)[3] == 255, "the hole is still a hole");
            var drawing = Path.Combine(folder, "drawing.svg");
            File.Copy(Fixtures.Url("drawing.svg"), drawing, overwrite: true);
            var svg = EditorImages.Load(drawing);
            Check(svg.Width > 100, "a drawing is loaded as a picture");
            Check(Path.GetExtension(EditorImages.Save(svg, drawing, " (test)")) == ".png", "a drawing is saved as PNG");
            Check(Fitting.Fit(new NSz(200, 100), new NSz(300, 300), 50) == new NBox(50, 100, 200, 100), "a picture fits its container, centred");
            Check(EditorImages.Color("#FF9A3D") == new SKColor(255, 154, 61), "colours from hex");
        });
    }
}
