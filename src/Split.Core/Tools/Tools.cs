namespace Split;

/// <summary>The work of the one-click tools.</summary>
public static class Tools
{
    /// <summary>Removing a background needs the model that ships with the app.</summary>
    public static bool CanRemoveBackground => Platform.Current.CanRemoveBackground;
}

/// <summary>"Compress To…": the largest size the user picks, for e-mail limits, upload forms…</summary>
public static class TargetSize
{
    /// <summary>"2MB", "1,5 MB", "800kb", "2 Mo", "300000" → bytes (decimal units; French writes octets: Ko, Mo, Go).</summary>
    public static long? Parse(string text)
    {
        var cleaned = string.Concat(text.ToLowerInvariant().Replace(',', '.').Where(c => !char.IsWhiteSpace(c)));
        (string Suffix, double Factor)[] units =
            [("gb", 1e9), ("mb", 1e6), ("kb", 1e3), ("go", 1e9), ("mo", 1e6), ("ko", 1e3), ("g", 1e9), ("m", 1e6), ("k", 1e3), ("b", 1), ("o", 1)];
        foreach (var (suffix, factor) in units)
        {
            if (!cleaned.EndsWith(suffix, StringComparison.Ordinal)) continue;
            return double.TryParse(cleaned[..^suffix.Length], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
                ? (long)(value * factor) : null;
        }
        return double.TryParse(cleaned, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var plain) && plain >= 1
            ? (long)plain : null;
    }
}
