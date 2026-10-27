using System.Text;

namespace Split;

/// <summary>
/// Text from older apps is often not UTF-8: Windows and old Apple systems each had their own
/// single-byte encoding, and some Windows tools write UTF-16 without a byte-order mark.
/// Everything is read into a string, so every result is written back out as UTF-8.
/// </summary>
public static class TextDecoding
{
    public sealed record Decoded(string Text, Encoding Encoding);

    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16Little = new UnicodeEncoding(false, false, true);
    private static readonly Encoding Utf16Big = new UnicodeEncoding(true, false, true);
    private static readonly Encoding Utf32Little = new UTF32Encoding(false, false, true);
    private static readonly Encoding Utf32Big = new UTF32Encoding(true, false, true);

    public static Decoded Decode(byte[] data)
    {
        if (ByteOrderMark(data) is { } marked) return marked;
        // Real text never holds NUL bytes, while UTF-16 of plain Latin text is half zeros
        // (and would otherwise pass as UTF-8).
        if (data.Contains((byte)0) && UnmarkedUtf16(data) is { } wide) return wide;
        if (TryDecode(Utf8Strict, data) is { } text) return new Decoded(text, Encoding.UTF8);
        return SingleByte(data);
    }

    /// <summary>The text as UTF-8 bytes, for readers that only take UTF-8 (JSON, ffmpeg's subtitles).</summary>
    public static (byte[] Data, bool WasUtf8) Utf8(byte[] data)
    {
        var decoded = Decode(data);
        return decoded.Encoding.CodePage == Encoding.UTF8.CodePage ? (data, true) : (Encoding.UTF8.GetBytes(decoded.Text), false);
    }

    private static string? TryDecode(Encoding encoding, ReadOnlySpan<byte> data)
    {
        try { return encoding.GetString(data); }
        catch (DecoderFallbackException) { return null; }
    }

    private static Decoded? ByteOrderMark(byte[] data)
    {
        (byte[] Mark, Encoding Encoding, Encoding Reported)[] marks =
        [
            ([0xEF, 0xBB, 0xBF], Utf8Strict, Encoding.UTF8),
            ([0xFF, 0xFE, 0x00, 0x00], Utf32Little, Utf32Little), ([0x00, 0x00, 0xFE, 0xFF], Utf32Big, Utf32Big),
            ([0xFF, 0xFE], Utf16Little, Utf16Little), ([0xFE, 0xFF], Utf16Big, Utf16Big),
        ];
        foreach (var (mark, encoding, reported) in marks)
        {
            if (!data.AsSpan().StartsWith(mark)) continue;
            return TryDecode(encoding, data.AsSpan(mark.Length)) is { } text ? new Decoded(text, reported) : null;
        }
        return null;
    }

    private static Decoded? UnmarkedUtf16(byte[] data)
    {
        if (data.Length < 2 || data.Length % 2 != 0) return null;
        int zeroEven = 0, zeroOdd = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != 0) continue;
            if (i % 2 == 0) zeroEven++; else zeroOdd++;
        }
        var half = data.Length / 2;
        Encoding encoding;
        if (zeroOdd * 10 >= half * 3 && zeroEven * 10 < half) encoding = Utf16Little;
        else if (zeroEven * 10 >= half * 3 && zeroOdd * 10 < half) encoding = Utf16Big;
        else return null;
        return TryDecode(encoding, data) is { } text ? new Decoded(text, encoding) : null;
    }

    /// <summary>
    /// Windows-1252 or Apple's old single-byte encoding (code page 10000). Both decode almost any
    /// bytes, so the one that turns more of the non-ASCII bytes into lowercase letters (é, ç, ã…)
    /// wins; ties go to Windows, by far the more common of the two today.
    /// </summary>
    private static Decoded SingleByte(byte[] data)
    {
        var windows = Encoding.GetEncoding(1252);
        var apple = Encoding.GetEncoding(10000);
        var windowsText = windows.GetString(data);
        var appleText = apple.GetString(data);
        return Score(appleText) > Score(windowsText) ? new Decoded(appleText, apple) : new Decoded(windowsText, windows);
    }

    private static int Score(string text) => text.EnumerateRunes().Count(rune => !rune.IsAscii && Rune.IsLower(rune));
}
