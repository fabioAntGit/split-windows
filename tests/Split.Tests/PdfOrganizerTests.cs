using System.IO;
using PdfSharp.Pdf.IO;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>Organize PDF: the page list, the document written from it, and saving it through a job.</summary>
public static class PdfOrganizerTests
{
    /// <summary>The text of each page of a PDF, as PDFium reads it.</summary>
    private static List<string> Pages(string path)
    {
        using var document = PdfTools.Open(path);
        return Enumerable.Range(0, document.PageCount).Select(i => new string(document.Characters(i).Select(c => c.Character).ToArray())).ToList();
    }

    private static int Rotation(string path, int page)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.Pages[page].Rotate;
    }

    public static void Register()
    {
        Test("Organize PDF", "Order, rotation, duplicates and an empty document", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_pdf-build");
            Directory.CreateDirectory(folder);
            using var source = PdfOrganizer.Load(Fixtures.Url("report.pdf"));
            var output = Path.Combine(folder, "built.pdf");
            PdfOrganizer.Write([new PageItem(0, 2) { Rotation = 90 }, new PageItem(0, 0)], [source], output);
            var pages = Pages(output);
            Check(pages.Count == 2, $"{pages.Count} pages");
            Check(pages[0].Contains("Page 3") && pages[1].Contains("Page 1"), $"the order follows the list: {string.Join(" | ", pages)}");
            Check(Rotation(output, 0) == 90 && Rotation(output, 1) == 0, "a 90° rotation on the first only");

            ExpectError("PDF without pages", ["no pages"], () => PdfOrganizer.Write([], [source], Path.Combine(folder, "empty.pdf")));
            ExpectError("A page that is gone", ["no longer available"], () => PdfOrganizer.Write([new PageItem(0, 9)], [source], Path.Combine(folder, "gone.pdf")));
            Check(!File.Exists(Path.Combine(folder, "empty.pdf")) && !File.Exists(Path.Combine(folder, "gone.pdf")) && !Directory.EnumerateFileSystemEntries(folder, ".split-*").Any(),
                  "nothing is left after a refusal");
        });

        Test("Organize PDF", "A page used twice turns on its own, and rotations add up and wrap", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_pdf-twice");
            Directory.CreateDirectory(folder);
            using var source = PdfOrganizer.Load(Fixtures.Url("report.pdf"));
            var output = Path.Combine(folder, "twice.pdf");
            PdfOrganizer.Write([new PageItem(0, 0), new PageItem(0, 0) { Rotation = 180 }, new PageItem(0, 0) { Rotation = 270 }, new PageItem(0, 1) { Rotation = 360 }], [source], output);
            Check(Enumerable.Range(0, 4).Select(i => Rotation(output, i)).SequenceEqual([0, 180, 270, 0]), "each copy has its own turn");
            Check(Pages(output).Take(3).All(p => p.Contains("Page 1")), "the copies are the same page");
        });

        Test("Organize PDF", "The model: pictures become pages, duplicating, deleting, moving and selecting", () =>
        {
            using var model = new PdfOrganizerModel([Fixtures.Url("report.pdf"), Fixtures.Url("quadrants.png")]);
            Check(model.Items.Count == 4 && model.Sources.Count == 2, "3 pages + 1 image");
            Check(model.Names.SequenceEqual(["report.pdf", "quadrants.png"]), "each source keeps its name");
            Check(!model.HasChanges, "nothing changed yet");
            model.Pending.Wait();
            Check(model.Items.All(i => model.Thumbnail(i) is not null), "every page has a thumbnail");
            var thumbnail = model.Thumbnail(model.Items[0])!;
            Check(thumbnail.Width <= 240 && thumbnail.Height <= 300 && (thumbnail.Width == 240 || thumbnail.Height == 300), $"thumbnails fit 240×300: {thumbnail.Width}×{thumbnail.Height}");

            model.Selection = [model.Items[0].Id];
            model.DuplicateSelection();
            Check(model.Items.Count == 5 && model.Items[1].Page == 0 && model.Items[1].Id != model.Items[0].Id, "duplicating inserts the copy right after");
            Check(model.HasChanges, "duplicating counts as a change");

            model.Selection = [];
            model.Rotate(90);
            Check(model.Items.All(i => i.Rotation == 90), "with no selection, rotating turns every page");
            model.Rotate(-90);
            model.Rotate(-90);
            Check(model.Items.All(i => i.Rotation == 270), "and wraps below zero");
            model.Rotate(90);

            var (first, last) = (model.Items[0].Id, model.Items[^1].Id);
            model.Move(last, first);
            Check(model.Items[0].Id == last && model.Items[1].Id == first, "a page dropped on another takes its place");
            model.Move(first, first);
            Check(model.Items[1].Id == first, "dropped on itself, nothing moves");

            model.Toggle(first, extend: false);
            Check(model.Selection.SetEquals([first]), "a click selects");
            model.Toggle(first, extend: false);
            Check(model.Selection.Count == 0, "a second click on the only selected page lets go");
            model.Toggle(first, extend: false);
            model.Toggle(last, extend: true);
            Check(model.Selection.Count == 2, "Ctrl or Shift adds");
            model.Toggle(first, extend: true);
            Check(model.Selection.SetEquals([last]), "and takes away");

            model.SelectAll();
            model.RemoveSelected();
            Check(model.Items.Count == 5 && model.Note is not null, "deleting everything is refused with an explanation");
            model.Selection = [last];
            model.RemoveSelected();
            Check(model.Items.Count == 4 && model.Selection.Count == 0, "deleting some pages");
        });

        Test("Organize PDF", "Adding files: pictures and PDFs join, others are told about", () =>
        {
            using var model = new PdfOrganizerModel([Fixtures.Url("report.pdf")]);
            model.AddFiles([Fixtures.Url("quadrants.jpg"), Fixtures.Url("report.pdf"), CollageTests.Garbage("fake.png")]);
            Check(model.Items.Count == 3 + 1 + 3 && model.Sources.Count == 3, $"{model.Items.Count} pages");
            Check(model.Note is { } note && note.Contains("fake.png") && note.StartsWith("Couldn’t add"), $"the one that failed is named: {model.Note}");
            model.AddFiles([Fixtures.Url("quadrants.png")]);
            Check(model.Note is null, "the note goes away when everything is added");
            var bad = Path.Combine(Fixtures.Root, "_pdf-bad", "bad.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
            File.WriteAllText(bad, "this is not a PDF");
            ExpectError("A file that isn’t a PDF", ["valid PDF"], () => PdfOrganizer.Load(bad));
        });

        Test("Organize PDF", "Saving: organized or extracted pages next to the first file; originals and temporaries untouched", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_pdf-save");
            Directory.CreateDirectory(folder);
            var report = Path.Combine(folder, "report.pdf");
            var picture = Path.Combine(folder, "scan.png");
            File.Copy(Fixtures.Url("report.pdf"), report, overwrite: true);
            File.Copy(Fixtures.Url("quadrants.png"), picture, overwrite: true);
            var before = (Files.Sha(report), Files.Sha(picture));

            var model = new PdfOrganizerModel([report, picture]);
            model.Selection = [model.Items[2].Id];
            model.Rotate(90);
            model.Selection = [model.Items[1].Id, model.Items[2].Id];
            model.Save(onlySelection: true);
            var extracted = CollageTests.Finished("Extracting pages");
            Check(extracted.State == ActivityState.Succeeded, $"{extracted.State} {extracted.Summary}");
            var extractedFile = Path.Combine(folder, "report (extracted pages).pdf");
            Require(File.Exists(extractedFile), "“(extracted pages)” next to the first file");
            var pages = Pages(extractedFile);
            Check(pages.Count == 2 && pages[0].Contains("Page 2") && pages[1].Contains("Page 3") && Rotation(extractedFile, 1) == 90, $"the chosen pages, in order: {string.Join(" | ", pages)}");

            model.Selection = [];
            model.Items.Reverse();
            model.Save(onlySelection: false);
            CollageTests.Finished("Saving the PDF");
            var organizedFile = Path.Combine(folder, "report (organized).pdf");
            Require(File.Exists(organizedFile), "“(organized)”");
            pages = Pages(organizedFile);
            Check(pages.Count == 4 && pages[0].Length < 5 && pages[3].Contains("Page 1"), $"all the pages, reversed, the picture first: {string.Join(" | ", pages)}");
            model.Dispose();

            // A picture first: the result is named after it.
            using var other = new PdfOrganizerModel([picture, report]);
            other.Save(onlySelection: false);
            CollageTests.Finished("Saving the PDF");
            Check(File.Exists(Path.Combine(folder, "scan (organized).pdf")), "named after the first file, whatever it is");

            Check((Files.Sha(report), Files.Sha(picture)) == before, "the originals are never changed");
            Check(!Directory.EnumerateFileSystemEntries(folder, ".split-*").Any(), "no temporary files stay behind");

            var bin = Path.Combine(folder, "bin");
            Directory.CreateDirectory(bin);
            var previous = ActivityLog.MoveToRecycleBin;
            ActivityLog.MoveToRecycleBin = path => File.Move(path, Path.Combine(bin, Path.GetFileName(path)));
            try { Check(ActivityLog.Shared.Undo(extracted.Id).Count == 0 && !File.Exists(extractedFile) && File.Exists(report), "undo takes the extract back"); }
            finally { ActivityLog.MoveToRecycleBin = previous; }
        });

        Test("Organize PDF", "A page picked up after the window closed still saves: the job reads the sources itself", () =>
        {
            var folder = Path.Combine(Fixtures.Root, "_pdf-closed");
            Directory.CreateDirectory(folder);
            var report = Path.Combine(folder, "report.pdf");
            File.Copy(Fixtures.Url("report.pdf"), report, overwrite: true);
            var model = new PdfOrganizerModel([report]);
            model.Save(onlySelection: false);
            model.Dispose();
            var entry = CollageTests.Finished("Saving the PDF");
            Check(entry.State == ActivityState.Succeeded && Pages(Path.Combine(folder, "report (organized).pdf")).Count == 3, $"{entry.State} {entry.Summary}");
        });
    }
}
