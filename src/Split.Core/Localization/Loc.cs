using System.Globalization;
using System.Text;

namespace Split;

/// <summary>
/// Holds the strings of the current language. Thread-safe: jobs build their messages off the
/// interface thread. Windows listen to <see cref="Changed"/> and rebuild when the language changes.
/// </summary>
public sealed class Localizer
{
    public static Localizer Shared { get; } = new();

    /// <summary>Where the <c>.lproj</c> folders are: next to the program, or set by the test runner.</summary>
    public static string TablesDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Localization");

    public event Action? Changed;

    /// <summary>False when SPLIT_LANGUAGE chose the language for this run only (tests, scripts): then changing it is never saved.</summary>
    private readonly bool persists;
    private readonly object gate = new();
    private Language language;
    private Dictionary<string, string> table = [];
    private CultureInfo culture = CultureInfo.GetCultureInfo("en-US");

    private Localizer()
    {
        if (Languages.FromRaw(Environment.GetEnvironmentVariable("SPLIT_LANGUAGE")) is { } forced)
        {
            language = forced;
            persists = false;
        }
        else
        {
            language = Settings.Language;
            persists = true;
        }
        Reload();
    }

    public Language Language
    {
        get { lock (gate) return language; }
        set
        {
            lock (gate) language = value;
            if (persists) Settings.Language = value;
            Reload();
            Changed?.Invoke();
        }
    }

    public void Reload()
    {
        var current = Language;
        var resolved = current.Resolved();
        var loaded = resolved == Language.En ? [] : LoadTable(resolved);
        lock (gate)
        {
            table = loaded;
            culture = current.Culture();
        }
    }

    /// <summary>The tables of a language: the shared texts, then those that differ on Windows, which win.</summary>
    public static Dictionary<string, string> LoadTable(Language language)
    {
        var merged = LoadTable(language, "Localizable");
        foreach (var (key, value) in LoadTable(language, "Windows")) merged[key] = value;
        return merged;
    }

    public static Dictionary<string, string> LoadTable(Language language, string file)
    {
        var path = Path.Combine(TablesDirectory, $"{language.Raw()}.lproj", file + ".strings");
        try { return StringsFile.Parse(File.ReadAllText(path, Encoding.UTF8)); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public string String(string key)
    {
        lock (gate) return table.GetValueOrDefault(key) ?? key;
    }

    public CultureInfo Culture
    {
        get { lock (gate) return culture; }
    }
}

public static class Loc
{
    /// <summary>
    /// The text for <paramref name="key"/> (written in English) in the current language, with each
    /// <c>%@</c> replaced by the next argument. A translation that needs another order uses <c>%1$@</c>, <c>%2$@</c>.
    /// </summary>
    public static string L(string key, params object?[] arguments) => Fill(Localizer.Shared.String(key), arguments);

    /// <summary>
    /// Fills in a format that is already translated, such as one of the pair passed to
    /// <see cref="JobRunner.Phrase"/> (<c>L("%@ files converted")</c> without arguments returns the format).
    /// Numbers are written the invariant way, as in the English source: "1.5", never "1,5".
    /// </summary>
    public static string Fill(string format, IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0) return format;
        var text = new StringBuilder();
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length) { text.Append(format[i]); continue; }
            var following = format[i + 1];
            if (following == '%') { text.Append('%'); i++; continue; }
            if (following == '@') { text.Append(Argument(arguments, next++)); i++; continue; }
            var end = i + 1;
            while (end < format.Length && char.IsAsciiDigit(format[end])) end++;
            if (end > i + 1 && end + 1 < format.Length && format[end] == '$' && format[end + 1] == '@')
            {
                text.Append(Argument(arguments, int.Parse(format.AsSpan(i + 1, end - i - 1)) - 1));
                i = end + 1;
                continue;
            }
            text.Append('%');
        }
        return text.ToString();
    }

    private static string Argument(IReadOnlyList<object?> arguments, int index) =>
        index >= 0 && index < arguments.Count ? Convert.ToString(arguments[index], CultureInfo.InvariantCulture) ?? "" : "";

    /// <summary>"A, B, and C" in the current language (the comma before "and" is English only).</summary>
    public static string Listed(IReadOnlyList<string> items)
    {
        if (items.Count < 2) return string.Join("", items);
        var and = Localizer.Shared.Language.Resolved() switch { Language.Pt => "e", Language.Fr => "et", Language.Es => "y", _ => "and" };
        return string.Join(", ", items.Take(items.Count - 1)) + (items.Count > 2 && and == "and" ? "," : "") + " " + and + " " + items[^1];
    }
}
