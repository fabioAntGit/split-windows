namespace Split;

public static class ByteCount
{
    /// <summary>"640 KB", "12.5 MB", "1.25 GB": decimal units, more digits for bigger units, in the current language's number style.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        int[] digits = [0, 1, 2, 2];
        var value = bytes / 1000.0;
        var unit = 0;
        // 999.7 KB reads as 1 MB, not 1000 KB.
        while (Math.Round(value, digits[unit]) >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return value.ToString(digits[unit] == 0 ? "0" : "0." + new string('#', digits[unit]), Localizer.Shared.Culture) + " " + units[unit];
    }
}
