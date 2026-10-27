using System.IO;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace Split.Tests;

/// <summary>The samples of the conversion suites: pictures with metadata, every audio and video flavour, subtitles, workbooks and a book.</summary>
public static partial class Fixtures
{
    /// <summary>600×400: red top-left, blue top-right, green bottom-left, yellow bottom-right, alpha in the centre when asked.</summary>
    public static MagickImage Quads(int width = 600, int height = 400, bool transparentCentre = false)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = (y < height / 2, x < width / 2) switch
                {
                    (true, true) => (255, 0, 0),
                    (true, false) => (0, 0, 255),
                    (false, true) => (0, 204, 0),
                    _ => (255, 230, 0),
                };
                var clear = transparentCentre && x >= width * 0.4 && x < width * 0.6 && y >= height * 0.4 && y < height * 0.6;
                var at = (y * width + x) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = clear ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)r, (byte)g, (byte)b, (byte)255);
            }
        }
        return new MagickImage(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
    }

    /// <summary>A busy photographic-like image (gradient plus noise) that compresses meaningfully.</summary>
    public static MagickImage Noise(int width, int height)
    {
        var random = new Random(7);
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = (x / (double)width + y / (double)height) / 2;
                var noise = random.Next(-24, 25);
                var at = (y * width + x) * 3;
                pixels[at] = (byte)Math.Clamp(0.9 * 255 * (1 - t) + 0.1 * 255 * t + noise, 0, 255);
                pixels[at + 1] = (byte)Math.Clamp(0.3 * 255 + noise, 0, 255);
                pixels[at + 2] = (byte)Math.Clamp(0.1 * 255 * (1 - t) + 0.8 * 255 * t + noise, 0, 255);
            }
        }
        return new MagickImage(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
    }

    public static void Save(IMagickImage<ushort> image, string name, MagickFormat format, uint? quality = null)
    {
        if (quality is { } q) image.Quality = q;
        using var file = File.Create(Url(name));
        image.Write(file, format);
        Stubs.Remove(name);
    }

    private static void WriteZip(string name, Dictionary<string, string> entries)
    {
        File.Delete(Url(name));
        using var archive = ZipFile.Open(Url(name), ZipArchiveMode.Create);
        foreach (var (path, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    private static void Converters()
    {
        Imaging.Initialize();

        // MARK: Pictures
        using (var transparent = Quads(transparentCentre: true)) Save(transparent, "quadrants.png", MagickFormat.Png);
        using (var quads = Quads())
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(38, 1, false), new Rational(43, 1, false), new Rational(12, 1, false) });
            exif.SetValue(ExifTag.GPSLongitudeRef, "W");
            exif.SetValue(ExifTag.GPSLongitude, new[] { new Rational(9, 1, false), new Rational(8, 1, false), new Rational(24, 1, false) });
            exif.SetValue(ExifTag.UserComment, Encoding.ASCII.GetBytes("ASCII\0\0\0test"));
            quads.SetProfile(exif);
            Save(quads, "quadrants.jpg", MagickFormat.Jpeg, 95);
        }
        using (var rotated = Quads(300, 200))
        {
            // Stored landscape, displayed portrait (EXIF orientation 6 = rotate 90° clockwise).
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            rotated.SetProfile(exif);
            rotated.Orientation = Orientation.RightTop;
            Save(rotated, "rotated.jpg", MagickFormat.Jpeg, 95);
        }
        using (var profiled = Quads())
        {
            profiled.SetProfile(ColorProfiles.AppleRGB);
            Save(profiled, "profile.jpg", MagickFormat.Jpeg, 95);
        }
        using (var quads = Quads())
        {
            Save(quads, "quadrants.tiff", MagickFormat.Tiff);
            Save(quads, "quadrants.bmp", MagickFormat.Bmp);
            Save(quads, "quadrants.webp", MagickFormat.WebP, 90);
            Save(quads, "quadrants.avif", MagickFormat.Avif, 80);
        }
        using (var icon = Quads(64, 64)) Save(icon, "icon.ico", MagickFormat.Ico);
        using (var photo = Noise(2400, 1600)) Save(photo, "photo.jpg", MagickFormat.Jpeg, 100);
        using (var animation = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Lime, MagickColors.Blue })
                animation.Add(new MagickImage(color, 120, 80) { AnimationDelay = 10, AnimationIterations = 0 });
            using var file = File.Create(Url("animated.gif"));
            animation.Write(file, MagickFormat.Gif);
        }
        // The HEIC sample can't be made here: it stays a stand-in unless tests/Fixtures has one.

        // MARK: Audio and video
        Ffmpeg("-i", Url("tone.wav"), "-c:a", "aac", "-b:a", "128k", Url("tone.m4a"));
        Ffmpeg("-i", Url("tone.wav"), "-c:a", "libopus", Url("tone.opus"));
        Ffmpeg("-i", Url("tone.wav"), "-c:a", "wmav2", Url("tone.wma"));
        string[] video = ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2", "-f", "lavfi", "-i", "sine=frequency=660:duration=2"];
        Ffmpeg([.. video, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", Url("clip.m4v")]);
        Ffmpeg("-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", Url("silent.mp4"));
        // Longer clip so progress reports have time to arrive.
        Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=640x480:rate=30:duration=12", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", Url("long.mp4"));
        // Recorded on a phone: landscape pixels with a 90° display rotation.
        Ffmpeg("-display_rotation", "90", "-i", Url("silent.mp4"), "-c", "copy", Url("portrait.mp4"));
        File.WriteAllText(Url("subs.srt"), "1\n00:00:00,500 --> 00:00:02,000\nHello Split\n\n2\n00:00:02,500 --> 00:00:04,000\nSecond subtitle\n\n", new UTF8Encoding(false));
        File.WriteAllText(Url("subs.vtt"), "WEBVTT\n\n00:00:00.500 --> 00:00:02.000\nHello Split\n\n", new UTF8Encoding(false));

        // MARK: Workbooks and a book
        Xlsx.Write([new Sheet("Sales", [["Month", "Value"], ["March", "42"], ["April", "17.5"]]), new Sheet("Notes", [["Hello Split"]])], Url("sales.xlsx"));
        StyledWorkbook("dates.xlsx", date1904: false);
        StyledWorkbook("dates1904.xlsx", date1904: true);
        EpubWriter.Write(Html, "Book", null, Url("book.epub"));
    }

    /// <summary>A workbook as Excel writes it: shared strings, date and percent formats, a formula with a cached value.</summary>
    private static void StyledWorkbook(string name, bool date1904)
    {
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string header = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
        WriteZip(name, new()
        {
            ["[Content_Types].xml"] = header + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/></Types>",
            ["_rels/.rels"] = header + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = header + $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{rel}\"><workbookPr{(date1904 ? " date1904=\"1\"" : "")}/><sheets><sheet name=\"Dates\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = header + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{rel}/styles\" Target=\"styles.xml\"/><Relationship Id=\"rId3\" Type=\"{rel}/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>",
            ["xl/styles.xml"] = header + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"1\"><numFmt numFmtId=\"170\" formatCode=\"dd/mm/yyyy\\ hh:mm\"/></numFmts><fonts count=\"1\"><font/></fonts><fills count=\"1\"><fill/></fills><borders count=\"1\"><border/></borders><cellXfs count=\"4\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\" applyNumberFormat=\"1\"/><xf numFmtId=\"170\" applyNumberFormat=\"1\"/><xf numFmtId=\"10\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>",
            ["xl/sharedStrings.xml"] = header + "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"3\" uniqueCount=\"3\"><si><t>Date</t></si><si><t>When</t></si><si><r><t>Rate</t></r><rPh><t>X</t></rPh></si></sst>",
            ["xl/worksheets/sheet1.xml"] = header + $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c><c r=\"D1\" t=\"inlineStr\"><is><t>Sum</t></is></c></row><row r=\"2\"><c r=\"A2\" s=\"1\"><v>{(date1904 ? "43555" : "45017")}</v></c><c r=\"B2\" s=\"2\"><v>{(date1904 ? "43555.5" : "45017.5")}</v></c><c r=\"C2\" s=\"3\"><v>0.125</v></c><c r=\"D2\"><f>1+1</f><v>2</v></c><c r=\"E2\" t=\"b\"><v>1</v></c></row></sheetData></worksheet>",
        });
    }
}
