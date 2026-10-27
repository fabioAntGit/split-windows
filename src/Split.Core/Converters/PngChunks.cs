using System.Buffers.Binary;
using System.IO.Compression;
using ImageMagick;

namespace Split;

/// <summary>
/// PNG at the level of its chunks: animated PNG (acTL, fcTL, fdAT) is assembled and taken apart here,
/// since the picture library only reads the first image and writes animations through another program,
/// and a colour profile is put back when the encoder replaced it by the plain sRGB marker.
/// </summary>
public static class PngChunks
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public sealed record Chunk(string Type, byte[] Data);

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(string type, byte[] data)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in System.Text.Encoding.ASCII.GetBytes(type)) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    public static List<Chunk>? Parse(byte[] bytes)
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        var chunks = new List<Chunk>();
        var at = 8;
        while (at + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
            if (length > bytes.Length - at - 12) return null;
            chunks.Add(new Chunk(System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4), bytes.AsSpan(at + 8, (int)length).ToArray()));
            at += 12 + (int)length;
        }
        return chunks;
    }

    public static byte[] Build(IEnumerable<Chunk> chunks)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> number = stackalloc byte[4];
        foreach (var chunk in chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(number, (uint)chunk.Data.Length);
            output.Write(number);
            output.Write(System.Text.Encoding.ASCII.GetBytes(chunk.Type));
            output.Write(chunk.Data);
            BinaryPrimitives.WriteUInt32BigEndian(number, Crc(chunk.Type, chunk.Data));
            output.Write(number);
        }
        return output.ToArray();
    }

    // MARK: Colour profile

    public static bool HasProfile(byte[] png) => Parse(png)?.Any(c => c.Type == "iCCP") == true;

    /// <summary>The PNG with <paramref name="profile"/> embedded as its iCCP chunk (the sRGB marker, which says the same less precisely, goes).</summary>
    public static byte[] WithProfile(byte[] png, byte[] profile)
    {
        if (Parse(png) is not { } chunks) return png;
        using var packed = new MemoryStream();
        packed.Write([(byte)'I', (byte)'C', (byte)'C', 0, 0]);
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(profile);
        chunks.RemoveAll(c => c.Type is "iCCP" or "sRGB");
        chunks.Insert(1, new Chunk("iCCP", packed.ToArray()));
        return Build(chunks);
    }

    // MARK: Animation

    /// <summary>Puts same-size PNG frames (RGBA, 8 bits each) in one animated PNG. <paramref name="loops"/> 0 plays forever.</summary>
    public static byte[] Assemble(IReadOnlyList<(byte[] Png, double Delay)> frames, int loops)
    {
        var first = Parse(frames[0].Png) ?? throw new InvalidDataException("The first frame is not a PNG.");
        var header = first.First(c => c.Type == "IHDR");
        var output = new List<Chunk> { header };
        var control = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(0, 4), (uint)frames.Count);
        BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4, 4), (uint)loops);
        output.Add(new Chunk("acTL", control));
        output.AddRange(first.Where(c => c.Type is not ("IHDR" or "IDAT" or "IEND" or "acTL" or "fcTL" or "fdAT" or "tIME")));
        var sequence = 0u;
        for (var index = 0; index < frames.Count; index++)
        {
            var chunks = Parse(frames[index].Png) ?? throw new InvalidDataException("A frame is not a PNG.");
            var info = chunks.First(c => c.Type == "IHDR").Data;
            var frame = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), sequence++);
            info.AsSpan(0, 8).CopyTo(frame.AsSpan(4, 8));
            // x and y stay 0; the delay is in thousandths of a second; no disposal, source blending.
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20, 2), (ushort)Math.Clamp(Math.Round(frames[index].Delay * 1000), 0, 65535));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(22, 2), 1000);
            output.Add(new Chunk("fcTL", frame));
            foreach (var data in chunks.Where(c => c.Type == "IDAT"))
            {
                if (index == 0)
                {
                    output.Add(data);
                    continue;
                }
                var shifted = new byte[data.Data.Length + 4];
                BinaryPrimitives.WriteUInt32BigEndian(shifted.AsSpan(0, 4), sequence++);
                data.Data.CopyTo(shifted.AsSpan(4));
                output.Add(new Chunk("fdAT", shifted));
            }
        }
        output.Add(new Chunk("IEND", []));
        return Build(output);
    }

    public sealed record Animation(List<IMagickImage<ushort>> Frames, List<double> Delays, int Loops);

    /// <summary>The frames of an animated PNG, each already composed on the full canvas; null when the file isn't one.</summary>
    public static Animation? Disassemble(byte[] png)
    {
        if (Parse(png) is not { } chunks || !chunks.Any(c => c.Type == "acTL")) return null;
        var header = chunks.First(c => c.Type == "IHDR").Data;
        var width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        var loops = (int)BinaryPrimitives.ReadUInt32BigEndian(chunks.First(c => c.Type == "acTL").Data.AsSpan(4, 4));
        var shared = chunks.TakeWhile(c => c.Type is not ("IDAT" or "fcTL"))
            .Where(c => c.Type is not ("IHDR" or "acTL" or "IEND")).ToList();

        var pieces = new List<(byte[] Control, List<byte[]> Data)>();
        foreach (var chunk in chunks)
        {
            switch (chunk.Type)
            {
                case "fcTL": pieces.Add((chunk.Data, [])); break;
                // The first IDAT belongs to the animation only when a frame control came before it.
                case "IDAT" when pieces.Count > 0: pieces[^1].Data.Add(chunk.Data); break;
                case "fdAT" when pieces.Count > 0: pieces[^1].Data.Add(chunk.Data[4..]); break;
            }
        }
        if (pieces.Count == 0) return null;

        var frames = new List<IMagickImage<ushort>>();
        var delays = new List<double>();
        using var canvas = new MagickImage(MagickColors.Transparent, width, height);
        canvas.Alpha(AlphaAction.Set);
        IMagickImage<ushort>? saved = null;
        (int X, int Y, uint W, uint H, byte Dispose)? previous = null;
        try
        {
            foreach (var (control, data) in pieces)
            {
                JobProgress.CheckCancelled();
                var (w, h) = (BinaryPrimitives.ReadUInt32BigEndian(control.AsSpan(4, 4)), BinaryPrimitives.ReadUInt32BigEndian(control.AsSpan(8, 4)));
                var (x, y) = ((int)BinaryPrimitives.ReadUInt32BigEndian(control.AsSpan(12, 4)), (int)BinaryPrimitives.ReadUInt32BigEndian(control.AsSpan(16, 4)));
                var (num, den) = (BinaryPrimitives.ReadUInt16BigEndian(control.AsSpan(20, 2)), BinaryPrimitives.ReadUInt16BigEndian(control.AsSpan(22, 2)));
                byte dispose = control[24], blend = control[25];
                if (previous is { } last)
                {
                    if (last.Dispose == 1)
                    {
                        using var clear = new MagickImage(MagickColors.Transparent, last.W, last.H);
                        canvas.Composite(clear, last.X, last.Y, CompositeOperator.Copy);
                    }
                    else if (last.Dispose == 2 && saved is not null)
                    {
                        canvas.Composite(saved, 0, 0, CompositeOperator.Copy);
                    }
                }
                saved?.Dispose();
                saved = dispose == 2 ? canvas.Clone() : null;

                var piece = shared.Select(c => c).ToList();
                var headerCopy = (byte[])header.Clone();
                BinaryPrimitives.WriteUInt32BigEndian(headerCopy.AsSpan(0, 4), w);
                BinaryPrimitives.WriteUInt32BigEndian(headerCopy.AsSpan(4, 4), h);
                var single = new List<Chunk> { new("IHDR", headerCopy) };
                single.AddRange(piece);
                single.AddRange(data.Select(d => new Chunk("IDAT", d)));
                single.Add(new Chunk("IEND", []));
                using var image = new MagickImage(Build(single), new MagickReadSettings { Format = MagickFormat.Png });
                image.Alpha(AlphaAction.Set);
                canvas.Composite(image, x, y, blend == 0 ? CompositeOperator.Copy : CompositeOperator.Over);
                frames.Add(canvas.Clone());
                delays.Add(den == 0 ? num / 100.0 : (double)num / den);
                previous = (x, y, w, h, dispose);
            }
        }
        catch
        {
            foreach (var frame in frames) frame.Dispose();
            throw;
        }
        finally { saved?.Dispose(); }
        return new Animation(frames, delays, loops);
    }
}
