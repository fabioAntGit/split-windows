using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ImageMagick;

namespace Split.Tests;

/// <summary>Reading back what an audio or video conversion wrote, with ffprobe.</summary>
public static class Probe
{
    public static JsonElement Json(string path)
    {
        var ffprobe = Engines.Path("ffprobe");
        if (ffprobe is null) return default;
        var result = ProcessRunner.Run(ffprobe, ["-v", "error", "-show_streams", "-show_format", "-show_chapters", "-of", "json", path]);
        try { return JsonDocument.Parse(result.StdoutData).RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    public static List<JsonElement> Streams(string path) =>
        Json(path) is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("streams", out var streams) ? streams.EnumerateArray().ToList() : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;

    public static bool IsCover(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) && Text(disposition, "attached_pic") == "1";

    public static List<string> Codecs(string path, string type) =>
        Streams(path).Where(s => Text(s, "codec_type") == type && !IsCover(s)).Select(s => Text(s, "codec_name") ?? "").ToList();

    public static double Duration(string path) =>
        Json(path) is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("format", out var format)
        && double.TryParse(Text(format, "duration"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;

    public static (int Width, int Height) VideoSize(string path) =>
        Streams(path).FirstOrDefault(s => Text(s, "codec_type") == "video") is { ValueKind: JsonValueKind.Object } stream
            ? (int.Parse(Text(stream, "width") ?? "0"), int.Parse(Text(stream, "height") ?? "0")) : (0, 0);

    public static int Channels(string path) =>
        Streams(path).FirstOrDefault(s => Text(s, "codec_type") == "audio") is { ValueKind: JsonValueKind.Object } stream ? int.Parse(Text(stream, "channels") ?? "0") : 0;

    public static int Chapters(string path) =>
        Json(path) is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("chapters", out var chapters) ? chapters.GetArrayLength() : 0;

    public static Dictionary<string, string> Tags(string path)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Json(path) is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("format", out var format) && format.TryGetProperty("tags", out var found))
            foreach (var tag in found.EnumerateObject()) tags[tag.Name] = tag.Value.ToString();
        return tags;
    }
}

public sealed record ImageInfo(int Width, int Height, int Frames, bool HasAlpha, int Orientation, bool HasGps, string? UserComment, bool HasProfile);

/// <summary>Reading back the pictures a conversion wrote.</summary>
public static class Pixels
{
    public static ImageInfo? Info(string path)
    {
        try
        {
            using var frames = ImageConverter.Open(path, Formats.Detect(path) ?? Format.Png);
            var first = frames.Images[0];
            var exif = first.GetExifProfile();
            var comment = exif?.GetValue(ExifTag.UserComment)?.Value;
            return new ImageInfo((int)first.Width, (int)first.Height, frames.Images.Count, first.HasAlpha,
                                 first.Orientation == Orientation.Undefined ? 1 : (int)first.Orientation,
                                 exif?.GetValue(ExifTag.GPSLatitude) is not null,
                                 comment is null ? null : Encoding.ASCII.GetString(comment).TrimStart('A', 'S', 'C', 'I', '\0'),
                                 first.GetColorProfile() is not null);
        }
        catch (Problem) { return null; }
    }

    /// <summary>The first picture of a file, upright.</summary>
    public static IMagickImage<ushort>? Load(string path)
    {
        try
        {
            using var frames = ImageConverter.Open(path, Formats.Detect(path) ?? Format.Png);
            var image = frames.Images[0].Clone();
            image.AutoOrient();
            return image;
        }
        catch (Problem) { return null; }
    }

    /// <summary>RGBA (0…255) at a top-left pixel coordinate.</summary>
    public static int[] At(IMagickImage<ushort> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        var color = pixels.GetPixel(x, y).ToColor()!;
        return [color.R / 257, color.G / 257, color.B / 257, color.A / 257];
    }

    public static bool Close(int[] a, int[] b, int tolerance = 40) => a.Take(3).Zip(b.Take(3)).All(p => Math.Abs(p.First - p.Second) <= tolerance);

    public static IMagickImage<ushort> Solid(int width, int height, MagickColor color) => new MagickImage(color, (uint)width, (uint)height);
}

/// <summary>A local TCP listener. If any conversion tries to load a remote resource pointing here, the connection is counted; a correct, local-only app never connects.</summary>
public sealed class Tripwire : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly List<string> requests = [];
    private int count;

    public Tripwire()
    {
        listener.Start();
        Task.Run(Accept);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public string Base => $"http://127.0.0.1:{Port}";

    private async Task Accept()
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var buffer = new byte[2048];
                string line = "";
                try
                {
                    client.ReceiveTimeout = 200;
                    var read = await client.GetStream().ReadAsync(buffer);
                    line = Encoding.UTF8.GetString(buffer, 0, read).Split("\r\n")[0];
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.0 404 Not Found\r\nContent-Length: 0\r\n\r\n"));
                }
                catch (IOException) { }
                lock (requests) { count++; requests.Add(line); }
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException) { }
    }

    public int Connections { get { lock (requests) return count; } }

    /// <summary>Request lines seen ("GET /img.png HTTP/1.1"), to say which reference leaked.</summary>
    public List<string> RequestsSince(int index) { lock (requests) return requests.Skip(index).ToList(); }

    /// <summary>Proves the tripwire works (so "0 connections" really means nothing connected).</summary>
    public bool SelfTest()
    {
        var before = Connections;
        using (var client = new TcpClient()) client.Connect(IPAddress.Loopback, Port);
        for (var i = 0; i < 50 && Connections == before; i++) Thread.Sleep(20);
        return Connections == before + 1;
    }

    public void Dispose() => listener.Stop();
}

/// <summary>Builders for hostile inputs, kept out of the shared fixtures so nothing else reads them.</summary>
public static partial class Hostile
{
    public static string Folder(string name)
    {
        var directory = Path.Combine(Fixtures.Root, "_security", name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static string File(string name, string folder, string content)
    {
        var path = Path.Combine(Folder(folder), name);
        System.IO.File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A zip package (EPUB/DOCX/XLSX); <paramref name="stored"/> entries go first and uncompressed, as EPUB requires.</summary>
    public static string Package(string name, string folder, Dictionary<string, string> files, Dictionary<string, string>? stored = null)
    {
        var output = Path.Combine(Folder(folder), name);
        System.IO.File.Delete(output);
        using var archive = ZipFile.Open(output, ZipArchiveMode.Create);
        void Add(string path, string content, CompressionLevel level)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path, level).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        foreach (var (path, content) in stored ?? []) Add(path, content, CompressionLevel.NoCompression);
        foreach (var (path, content) in files) Add(path, content, CompressionLevel.Optimal);
        return output;
    }

    /// <summary>A minimal workbook whose only sheet is <paramref name="sheet"/>.</summary>
    public static string Spreadsheet(string name, string sheet)
    {
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        return Package(name, "xlsx", new()
        {
            ["[Content_Types].xml"] = """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""",
            ["_rels/.rels"] = $"""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{r}/officeDocument" Target="xl/workbook.xml"/></Relationships>""",
            ["xl/workbook.xml"] = $"""<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="{r}"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""",
            ["xl/_rels/workbook.xml.rels"] = $"""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{r}/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""",
            ["xl/worksheets/sheet1.xml"] = sheet,
        });
    }

    /// <summary>A local stylesheet that imports a remote one, and a second local level that does the same.</summary>
    public static void Stylesheets(string net, string nextToPage)
    {
        var directory = Path.GetDirectoryName(nextToPage)!;
        System.IO.File.WriteAllText(Path.Combine(directory, "style.css"), $"@import url({net}/css-import.css); @import \"level2.css\"; body {{ background: url({net}/css-bg.png); }}");
        System.IO.File.WriteAllText(Path.Combine(directory, "level2.css"), $"p {{ background-image: url('{net}/css-level2.png'); }} @import '{net}/css-level2.css';");
    }
}
