using System.IO;
using SkiaSharp;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>The collage: its layouts, the picture it draws, and saving it through a job.</summary>
public static class CollageTests
{
    private static readonly HashSet<Guid> Seen = [];

    /// <summary>The next finished job with this title that an earlier call didn't return.</summary>
    internal static ActivityEntry Finished(string title)
    {
        for (var wait = 0; wait < 600; wait++)
        {
            Thread.Sleep(50);
            var entry = ActivityLog.Shared.Entries.FirstOrDefault(e => e.Title == title && !e.State.IsActive() && !Seen.Contains(e.Id));
            if (entry is null) continue;
            Seen.Add(entry.Id);
            return entry;
        }
        throw new TestFailure($"the job “{title}” didn’t finish");
    }

    public static void Register()
    {
        Test("Collage", "Layouts: row, column and grid with the last row centred", () =>
        {
            var style = new CollageSpec { Width = 1000, Spacing = 0, Layout = CollageLayout.Row };
            var row = CollageRenderer.Layout([1, 2], style);
            Check(Math.Abs(row.Cells[0].Height - row.Cells[1].Height) < 1e-9 && Math.Abs(row.Cells[1].Width / row.Cells[0].Width - 2) < 1e-9, $"row: {string.Join(" ", row.Cells)}");
            Check(Math.Abs(row.Cells[1].MaxX - 1000) < 1e-6, "the row fills the width");
            style = style with { Layout = CollageLayout.Column };
            var column = CollageRenderer.Layout([1, 2], style);
            Check(column.Cells.All(c => Math.Abs(c.Width - 1000) < 1e-9), "a column has one width");
            style = style with { Layout = CollageLayout.Grid, Spacing = 0.01 };
            var grid = CollageRenderer.Layout([1, 1, 1], style);
            Check(grid.Cells[2].MidX > grid.Cells[0].MaxX - 1 && Math.Abs(grid.Cells[2].MidX - 500) < 1, $"the last row should be centred: {grid.Cells[2]}");
            using var a = Px.Quadrants();
            using var b = Px.Quadrants(300, 300);
            using var c = Px.Quadrants(200, 400);
            using var rendered = CollageRenderer.Render([a, b, c], style);
            Check(rendered.Width == 1000 && rendered.Height == (int)grid.Canvas.Height, $"canvas {rendered.Width}×{rendered.Height}");
        });

        Test("Collage", "The numbers of each layout: gaps, the median cell shape, nothing for no pictures", () =>
        {
            var style = new CollageSpec { Width = 1000, Spacing = 0.02 };
            var empty = CollageRenderer.Layout([], style);
            Check(empty.Cells.Length == 0 && empty.Canvas.Width == 0, "no pictures, no canvas");
            // Four cells in a 2×2 grid: gap 20, cell (1000 − 3·20) / 2.
            var four = CollageRenderer.Layout([1, 1, 1, 1], style);
            Check(Math.Abs(four.Cells[0].Width - 470) < 1e-9 && four.Cells[0].X == 20 && four.Cells[1].X == 510, $"grid cells: {four.Cells[0]} {four.Cells[1]}");
            Check(four.Canvas.Height == 2 * (470 + 20) + 20, $"grid height {four.Canvas.Height}");
            // Whole images in the grid take the median shape of the pictures.
            var whole = CollageRenderer.Layout([0.5, 1, 2], style with { Fill = false });
            Check(Math.Abs(whole.Cells[0].Height - whole.Cells[0].Width / 1) < 1e-9, "median aspect is 1");
            var row = CollageRenderer.Layout([1, 1], style with { Layout = CollageLayout.Row });
            Check(Math.Abs(row.Cells[0].X - 20) < 1e-9 && Math.Abs(row.Cells[0].Width - 470) < 1e-9 && row.Canvas.Height == 510, $"row: {row.Cells[0]} {row.Canvas}");
            var column = CollageRenderer.Layout([1, 2], style with { Layout = CollageLayout.Column });
            Check(Math.Abs(column.Cells[1].Y - (20 + 960 + 20)) < 1e-9 && column.Canvas.Height == 1500, $"column: {column.Cells[1]} {column.Canvas}");
        });

        Test("Collage", "What is drawn: backgrounds, corners, filling and fitting", () =>
        {
            using var wide = Px.Solid(400, 200, SKColors.Red);
            using var tall = Px.Solid(200, 400, SKColors.Blue);
            var style = new CollageSpec { Width = 600, Spacing = 0.05, Layout = CollageLayout.Row };
            using (var white = CollageRenderer.Render([wide, tall], style))
            {
                Check(Px.Close(Px.At(white, 2, 2), [255, 255, 255], 2), "white behind");
                var row = CollageRenderer.Layout([2, 0.5], style);
                var mid = row.Cells[0];
                Check(Px.Close(Px.At(white, (int)mid.MidX, (int)mid.MidY), Px.Red, 4), "the first picture is where the layout says");
            }
            using (var black = CollageRenderer.Render([wide, tall], style with { Background = CollageFill.Black })) Check(Px.Close(Px.At(black, 2, 2), [0, 0, 0], 2), "black behind");
            using (var clear = CollageRenderer.Render([wide, tall], style with { Background = CollageFill.Transparent })) Check(Px.At(clear, 2, 2)[3] == 0, "transparent behind");
            using (var first = CollageRenderer.Render([wide, tall], style with { Background = CollageFill.Gradient, Gradient = 0 }))
            using (var second = CollageRenderer.Render([wide, tall], style with { Background = CollageFill.Gradient, Gradient = 3 }))
                Check(Px.Difference(first, second) > 5 && !Px.Close(Px.At(first, 2, 2), [255, 255, 255], 20), "each gradient is its own");
            // Rounded corners cut the corner of a cell away.
            using (var round = CollageRenderer.Render([wide, tall], style with { Corner = 0.25 }))
            {
                var cell = CollageRenderer.Layout([2, 0.5], style).Cells[0];
                Check(Px.Close(Px.At(round, (int)cell.X + 1, (int)cell.Y + 1), [255, 255, 255], 8), "the corner of the first cell is cut");
                Check(Px.Close(Px.At(round, (int)cell.MidX, (int)cell.MidY), Px.Red, 4), "and its middle is not");
            }
            // A grid that fills crops the picture to the cell; one that doesn't shows all of it, with the background around.
            var grid = new CollageSpec { Width = 400, Spacing = 0, Layout = CollageLayout.Grid };
            using (var filled = CollageRenderer.Render([wide, wide, wide, wide], grid)) Check(Px.Close(Px.At(filled, 5, 5), Px.Red, 4) && Px.Close(Px.At(filled, 5, 195), Px.Red, 4), "filled cells have no bars");
            using (var whole = CollageRenderer.Render([wide, wide, wide, wide], grid with { Fill = false })) Check(Px.Close(Px.At(whole, 5, 5), Px.Red, 4), "whole pictures in cells of their own shape");
            using (var mixed = CollageRenderer.Render([wide, tall, wide, tall], grid with { Fill = false }))
                Check(Px.Close(Px.At(mixed, 205, 50), [255, 255, 255], 4) && Px.Close(Px.At(mixed, 300, 50), Px.Blue, 4), "a tall picture in a median cell leaves bars");
        });

        Test("Collage", "The model: order, removing, the preview follows, unreadable pictures are told", () =>
        {
            var model = new CollageModel([Fixtures.Url("quadrants.jpg"), Fixtures.Url("quadrants.png"), Fixtures.Url("fake.png")]);
            model.Pending.Wait();
            Check(model.Thumbnails.Count == 2 && model.Unreadable.Select(Path.GetFileName).SequenceEqual(["fake.png"]), $"fake.png is not a picture: {string.Join(",", model.Unreadable)}");
            Require(model.Rendered is not null, "a preview");
            Check(model.Rendered!.Width == 1200, $"the preview is 1200 wide: {model.Rendered.Width}");
            var before = model.Rendered;
            model.Move(0, 2);
            Check(model.Paths.Select(Path.GetFileName).SequenceEqual(["quadrants.png", "fake.png", "quadrants.jpg"]), "quadrants.jpg moved to the end");
            model.Pending.Wait();
            Check(!ReferenceEquals(before, model.Rendered), "the preview is drawn again");
            model.Remove(Fixtures.Url("quadrants.png"));
            model.Pending.Wait();
            Check(model.Paths.Count == 2, "removed");
            model.Style = model.Style with { Layout = CollageLayout.Column };
            model.Pending.Wait();
            Check(model.Rendered!.Height > model.Rendered.Width, "a column is taller than wide");
            ExpectError("Nothing to arrange", ["none of the images"], () =>
            {
                var none = new CollageModel([Fixtures.Url("fake.png")]);
                if (none.Thumbnails.Count == 0) throw new Problem(L("None of the images could be read."), L("Check that they open in Photos."));
            });
        });

        Test("Collage", "The preview is the saved picture in small: same layout, same corners", () =>
        {
            var model = new CollageModel([Fixtures.Url("quadrants.png"), Fixtures.Url("quadrants.jpg"), Fixtures.Url("quadrants.bmp")]);
            model.Style = model.Style with { Corner = 0.1, Spacing = 0.03 };
            model.Pending.Wait();
            var preview = model.Rendered!;
            using var a = EditorImages.Load(Fixtures.Url("quadrants.png"), 1200);
            using var b = EditorImages.Load(Fixtures.Url("quadrants.jpg"), 1200);
            using var c = EditorImages.Load(Fixtures.Url("quadrants.bmp"), 1200);
            using var full = CollageRenderer.Render([a, b, c], model.Style with { Width = 1200 });
            Check(full.Width == preview.Width && full.Height == preview.Height, $"{full.Width}×{full.Height} against {preview.Width}×{preview.Height}");
            Check(Px.Difference(full, preview) < 6, $"the two differ by {Px.Difference(full, preview):0.0} (only the reduction of the pictures may show)");
        });

        Test("Collage", "Saving: next to the first picture as Collage.jpg, again as Collage 2, transparent as PNG; originals stay", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_collage-save");
            Directory.CreateDirectory(folder);
            var one = Path.Combine(folder, "one.png");
            var two = Path.Combine(folder, "two.jpg");
            var three = Path.Combine(folder, "three.bmp");
            File.Copy(Fixtures.Url("quadrants.png"), one, overwrite: true);
            File.Copy(Fixtures.Url("quadrants.jpg"), two, overwrite: true);
            File.Copy(Fixtures.Url("quadrants.bmp"), three, overwrite: true);
            var before = new[] { one, two, three }.Select(Files.Sha).ToList();

            var model = new CollageModel([one, two, three]) { Style = new CollageSpec { Width = 1080 } };
            model.Save();
            var first = Finished("Creating the collage");
            Check(first.State == ActivityState.Succeeded && first.Outputs.Count == 1, $"{first.State} {first.Summary}");
            var saved = Path.Combine(folder, "Collage.jpg");
            Require(File.Exists(saved), "Collage.jpg is next to the first picture");
            using (var picture = EditorImages.Load(saved)) Check(picture.Width == 1080, $"the width of the style: {picture.Width}");

            model.Save();
            Finished("Creating the collage");
            Check(File.Exists(Path.Combine(folder, "Collage 2.jpg")), "a second collage never replaces the first");

            model.Style = model.Style with { Background = CollageFill.Transparent, Spacing = 0.05 };
            model.Save();
            Finished("Creating the collage");
            Require(File.Exists(Path.Combine(folder, "Collage.png")), "a transparent collage is a PNG");
            using (var clear = EditorImages.Load(Path.Combine(folder, "Collage.png"))) Check(Px.At(clear, 2, 2)[3] == 0, "and stays transparent behind");

            Check(new[] { one, two, three }.Select(Files.Sha).SequenceEqual(before), "the originals are never changed");
            Check(!Directory.EnumerateFileSystemEntries(folder, ".split-*").Any(), "no temporary files stay behind");

            // Undo takes the collage away and leaves the pictures.
            var bin = Path.Combine(folder, "bin");
            Directory.CreateDirectory(bin);
            var previous = ActivityLog.MoveToRecycleBin;
            ActivityLog.MoveToRecycleBin = path => File.Move(path, Path.Combine(bin, Path.GetFileName(path)));
            try { Check(ActivityLog.Shared.Undo(first.Id).Count == 0 && !File.Exists(saved) && File.Exists(one), "undo takes the collage back"); }
            finally { ActivityLog.MoveToRecycleBin = previous; }
        });

        Test("Collage", "A picture that can't be read stops the collage with an explanation", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_collage-broken");
            Directory.CreateDirectory(folder);
            var good = Path.Combine(folder, "good.png");
            var bad = Path.Combine(folder, "bad.png");
            File.Copy(Fixtures.Url("quadrants.png"), good, overwrite: true);
            File.Copy(Fixtures.Url("fake.png"), bad, overwrite: true);
            new CollageModel([good, bad]).Save();
            var failed = Finished("Creating the collage");
            Check(failed.State == ActivityState.Failed && failed.Failures.Count == 1, $"{failed.State}");
            Check(!File.Exists(Path.Combine(folder, "Collage.jpg")) && !Directory.EnumerateFileSystemEntries(folder, ".split-*").Any(), "nothing half-written");
        });
    }
}
