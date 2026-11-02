using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Split.Tests;

/// <summary>
/// Every sample file the tests use, generated fresh for each run (nothing is downloaded).
/// Samples Windows can't make (older Word files, HEIC pictures…) are copied from
/// <c>tests/Fixtures</c> when they are there; otherwise a small file with the right signature
/// stands in, is listed in <see cref="Stubs"/>, and the tests that need the real thing skip.
/// </summary>
public static partial class Fixtures
{
    public static string Root { get; private set; } = "";
    public const string Phrase = "Hello Split — café, crème brûlée, São Paulo";

    /// <summary>Samples that only hold the signature of their format.</summary>
    public static readonly HashSet<string> Stubs = [];

    public static string Url(string name) => Path.Combine(Root, name);

    /// <summary>A clean copy of one fixture in its own folder, so outputs of one test don't collide with another's.</summary>
    public static string Isolated(string name, string folder)
    {
        var directory = Path.Combine(Root, "_" + folder);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, name);
        if (Directory.Exists(Url(name)))
        {
            // A folder sample (an RTFD) is copied with its files.
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(Url(name))) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        else if (!File.Exists(target)) File.Copy(Url(name), target);
        return target;
    }

    public static void Ffmpeg(params string[] arguments) =>
        ProcessRunner.RunChecked(Engines.Require("ffmpeg"), ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. arguments], "Creating a sample");

    /// <summary>Fills an existing, empty folder with every sample.</summary>
    public static void Create(string folder, string shared)
    {
        Root = folder;
        if (Directory.Exists(shared))
        {
            foreach (var file in Directory.EnumerateFiles(shared)) File.Copy(file, Url(Path.GetFileName(file)), overwrite: true);
        }
        Images();
        Media();
        Documents();
        Archives();
        Data();
        Converters();
        DocumentSamples();
        Misc();
    }

    // MARK: Images

    /// <summary>600×400: red top-left, blue top-right, green bottom-left, yellow bottom-right, alpha in the centre (PNG only).</summary>
    public static BitmapSource Quadrants(int width = 600, int height = 400, bool transparentCentre = false)
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
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = clear ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)b, (byte)g, (byte)r, (byte)255);
            }
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    /// <summary>A busy photographic-like image (gradient + noise) that compresses meaningfully.</summary>
    public static BitmapSource Photo(int width, int height)
    {
        var random = new Random(7);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = (x / (double)width + y / (double)height) / 2;
                var noise = random.Next(-24, 25);
                var at = (y * width + x) * 4;
                pixels[at] = (byte)Math.Clamp(0.8 * 255 * t + 0.1 * 255 * (1 - t) + noise, 0, 255);
                pixels[at + 1] = (byte)Math.Clamp(0.3 * 255 + noise, 0, 255);
                pixels[at + 2] = (byte)Math.Clamp(0.1 * 255 * t + 0.9 * 255 * (1 - t) + noise, 0, 255);
                pixels[at + 3] = 255;
            }
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static byte[] Encode(BitmapEncoder encoder, params BitmapSource[] frames)
    {
        foreach (var frame in frames) encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Write(string name, byte[] bytes) => File.WriteAllBytes(Url(name), bytes);

    /// <summary>A sample copied from tests/Fixtures wins; otherwise the stand-in is written and listed.</summary>
    private static void Stub(string name, byte[] signature)
    {
        if (File.Exists(Url(name))) return;
        Write(name, signature);
        Stubs.Add(name);
    }

    private static void Images()
    {
        var quads = Quadrants();
        Write("quadrants.png", Encode(new PngBitmapEncoder(), Quadrants(transparentCentre: true)));
        Write("quadrants.jpg", Encode(new JpegBitmapEncoder { QualityLevel = 95 }, new FormatConvertedBitmap(quads, PixelFormats.Bgr32, null, 0)));
        Write("quadrants.tiff", Encode(new TiffBitmapEncoder(), quads));
        Write("quadrants.bmp", Encode(new BmpBitmapEncoder(), quads));
        Write("photo.jpg", Encode(new JpegBitmapEncoder { QualityLevel = 100 }, new FormatConvertedBitmap(Photo(2400, 1600), PixelFormats.Bgr32, null, 0)));

        // A Vista-style icon: the directory entry points at a PNG.
        var png = Encode(new PngBitmapEncoder(), Quadrants(64, 64));
        using (var icon = new MemoryStream())
        using (var writer = new BinaryWriter(icon))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)64); writer.Write((byte)64); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(png.Length); writer.Write(22);
            writer.Write(png);
            Write("icon.ico", icon.ToArray());
        }

        // Three solid frames.
        BitmapSource Solid(byte r, byte g, byte b)
        {
            var pixels = new byte[120 * 80 * 4];
            for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
            return BitmapSource.Create(120, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 120 * 4);
        }
        Write("animated.gif", Encode(new GifBitmapEncoder(), Solid(255, 0, 0), Solid(0, 255, 0), Solid(0, 0, 255)));

        // A 1×1 lossless WebP: only its signature matters until the converters exist.
        Stub("quadrants.webp", Convert.FromBase64String("UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA=="));
        Stub("quadrants.heic", [0, 0, 0, 24, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1heic"u8]);
        File.WriteAllText(Url("drawing.svg"), """
            <svg xmlns="http://www.w3.org/2000/svg" width="300" height="200" viewBox="0 0 300 200">
              <rect width="300" height="200" fill="#ff8800"/><circle cx="150" cy="100" r="60" fill="#ffffff"/>
            </svg>
            """, new UTF8Encoding(false));
    }

    // MARK: Audio and video

    private static void Media()
    {
        // 2 s tone with 0.5 s of silence before and after (3 s total), stereo.
        Ffmpeg("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=2", "-af", "adelay=500|500,apad=pad_dur=0.5,volume=0.3",
               "-ac", "2", "-c:a", "pcm_s16le", Url("tone.wav"));
        Ffmpeg("-i", Url("tone.wav"), "-c:a", "libmp3lame", "-b:a", "128k", "-metadata", "title=Secret track", "-metadata", "artist=Split", Url("tone.mp3"));
        Ffmpeg("-i", Url("tone.wav"), "-c:a", "flac", Url("tone.flac"));
        Ffmpeg("-i", Url("tone.wav"), "-c:a", "vorbis", "-strict", "experimental", Url("tone.ogg"));
        Ffmpeg("-i", Url("tone.wav"), "-c:a", "pcm_s16be", Url("tone.aiff"));

        string[] video = ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2", "-f", "lavfi", "-i", "sine=frequency=660:duration=2"];
        Ffmpeg([.. video, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-metadata", "title=Test video", Url("clip.mp4")]);
        Ffmpeg([.. video, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", Url("clip.mov")]);
        Ffmpeg([.. video, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", Url("clip.mkv")]);
        Ffmpeg([.. video, "-c:v", "libvpx-vp9", "-b:v", "300k", "-c:a", "libopus", "-shortest", Url("clip.webm")]);
        Ffmpeg([.. video, "-c:v", "mpeg4", "-c:a", "libmp3lame", "-shortest", Url("clip.avi")]);
        Ffmpeg([.. video, "-c:v", "wmv2", "-c:a", "wmav2", "-shortest", Url("clip.wmv")]);
    }

    // MARK: Documents

    public const string Html = $"""
        <!DOCTYPE html><html><head><meta charset="utf-8"><title>Report</title></head><body>
        <h1>Report</h1><p>{Phrase}.</p><ul><li>First point</li><li>Second point</li></ul>
        <table><tr><th>Month</th><th>Value</th></tr><tr><td>March</td><td>42</td></tr></table></body></html>
        """;

    private static void Documents()
    {
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Url("notes.txt"), Phrase + "\nSecond line with accents: çãõéü.\n", utf8);
        File.WriteAllText(Url("readme.md"), $"# Report\n\n{Phrase}.\n\n- First point\n- Second **point**\n\n[Link](https://example.com)", utf8);
        File.WriteAllText(Url("page.html"), Html, utf8);
        File.WriteAllText(Url("letter.rtf"), @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\f0\fs24 Hello Split, caf\'e9 and cr\'e8me br\'fbl\'e9e.\par}", Encoding.ASCII);
        // An RTFD is a folder with the text as TXT.rtf beside its pictures.
        Directory.CreateDirectory(Url("notes.rtfd"));
        File.Copy(Url("letter.rtf"), Path.Combine(Url("notes.rtfd"), "TXT.rtf"));

        using (var zip = ZipFile.Open(Url("letter.docx"), ZipArchiveMode.Create))
        {
            void Entry(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), utf8);
                writer.Write(text);
            }
            Entry("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
            Entry("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
            Entry("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>{Phrase}</w:t></w:r></w:p></w:body></w:document>""");
        }
        // The older binary Word format can't be written here.
        Stub("letter.doc", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0, 0, 0, 0, 0]);

        File.WriteAllBytes(Url("report.pdf"), Pdf(3));
    }

    /// <summary>A PDF with "Page N" and the phrase's plain part on each of its pages.</summary>
    private static byte[] Pdf(int pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        objects.Add($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{4 + 2 * i} 0 R"))}] /Count {pages} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (var number = 1; number <= pages; number++)
        {
            var content = $"BT /F1 28 Tf 72 760 Td (Page {number}) Tj 0 -40 Td (Hello Split) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + 2 * (number - 1)} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    // MARK: Archives

    private static void Archives()
    {
        var parent = Path.Combine(Root, "_bundle");
        var folder = Path.Combine(parent, "Folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "one.txt"), "one");
        File.WriteAllText(Path.Combine(folder, "two.txt"), "two");
        ZipFile.CreateFromDirectory(folder, Url("folder.zip"), CompressionLevel.Optimal, includeBaseDirectory: true);
        TarFile.CreateFromDirectory(folder, Url("folder.tar"), includeBaseDirectory: true);
        using (var tar = File.OpenRead(Url("folder.tar")))
        using (var gzip = new GZipStream(File.Create(Url("folder.tar.gz")), CompressionLevel.Optimal))
            tar.CopyTo(gzip);
        // Windows' own tar writes 7z; nothing here writes RAR, so that one is laid out by hand.
        var bsdtar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (File.Exists(bsdtar)) ProcessRunner.RunChecked(bsdtar, ["--format", "7zip", "-cf", Url("folder.7z"), "-C", parent, "Folder"], "Creating a sample");
        else Stub("folder.7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4]);
        Write("folder.rar", Rar(new() { [@"Folder\one.txt"] = "one", [@"Folder\two.txt"] = "two" }));
        using (var zip = ZipFile.Open(Url("two.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(folder, "one.txt"), "one.txt");
            zip.CreateEntryFromFile(Path.Combine(folder, "two.txt"), "two.txt");
        }
        Directory.Delete(parent, recursive: true);
        var corrupt = new byte[404];
        new Random(3).NextBytes(corrupt);
        corrupt[0] = 0x50; corrupt[1] = 0x4B; corrupt[2] = 0x03; corrupt[3] = 0x04;
        Write("corrupt.zip", corrupt);
    }

    /// <summary>A RAR 4 archive storing its files as they are: the marker, a main header, a header before each file, the end.</summary>
    private static byte[] Rar(Dictionary<string, string> files)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("Rar!\u001A\u0007\u0000"u8);
        Block(0x73, 0, new byte[6]);
        foreach (var (name, text) in files)
        {
            var data = Encoding.ASCII.GetBytes(text);
            var path = Encoding.ASCII.GetBytes(name);
            using var header = new MemoryStream();
            using (var fields = new BinaryWriter(header))
            {
                // Packed and unpacked sizes, made on Windows, the data's CRC, 1 January 2024 as a DOS time,
                // version 2.0, stored, the name's length, a plain file's attributes, the name.
                fields.Write(data.Length); fields.Write(data.Length); fields.Write((byte)2); fields.Write(Crc32(data)); fields.Write(0x58210000);
                fields.Write((byte)20); fields.Write((byte)0x30); fields.Write((ushort)path.Length); fields.Write(0x20); fields.Write(path);
            }
            Block(0x74, 0x8000, header.ToArray());
            writer.Write(data);
        }
        Block(0x7B, 0x4000, []);
        return stream.ToArray();

        // Each block is the low half of its CRC, then its type, flags, size and the rest.
        void Block(byte type, ushort flags, byte[] rest)
        {
            var size = 7 + rest.Length;
            byte[] head = [type, (byte)flags, (byte)(flags >> 8), (byte)size, (byte)(size >> 8), .. rest];
            writer.Write((ushort)Crc32(head));
            writer.Write(head);
        }
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = ~0u;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    // MARK: Data

    private static void Data()
    {
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Url("prices.csv"), "Product;Price;Date;Discount;Total\nOrange;1,25;31/03/2024;10%;=B2*0.9\nLemon;0,80;2024-04-01;5%;=B3*0.95", utf8);
        File.WriteAllText(Url("people.tsv"), "Name\tAge\nAna\t31\nRui\t45\n", utf8);
        File.WriteAllText(Url("data.json"), """
            {"name": "Split", "version": 1.50, "tags": ["local", "café"], "nested": {"empty": null, "ok": true, "big": 12345678901234567890},
             "list": [{"a": 1}, {"a": 2, "b": "x"}]}
            """, utf8);
        File.WriteAllText(Url("data.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <catalog lang="en"><book id="1"><title>Café Split</title><price>9.50</price></book><book id="2"><title>Tea</title></book></catalog>
            """, utf8);
    }

    // MARK: Odd files for error handling

    private static void Misc()
    {
        File.WriteAllBytes(Url("empty.txt"), []);
        File.Copy(Url("quadrants.jpg"), Url("fake.png"));
        File.WriteAllText(Url("page.jpg"), "<!DOCTYPE html><html><body>Page not found</body></html>");
        File.WriteAllBytes(Url("broken.mp4"), File.ReadAllBytes(Url("clip.mp4"))[..3000]);
        Directory.CreateDirectory(Url("A folder"));
        File.WriteAllText(Url("mystery.xyz"), "?");
    }
}
