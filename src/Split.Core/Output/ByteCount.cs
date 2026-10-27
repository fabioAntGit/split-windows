namespace Split;

public static class ByteCount
{
    /// <summary>"12.5 MB": decimal units, as Windows shows file sizes, in the current language's number style.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1000.0;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return value.ToString(value < 100 ? "0.#" : "0", Localizer.Shared.Culture) + " " + units[unit];
    }
}
