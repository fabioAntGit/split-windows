using System.Globalization;

namespace Split;

/// <summary>
/// Languages the interface is written in. English is the source language: every text in the
/// code is English and is looked up, as written, in the other languages' tables
/// (<c>Resources/Localization/&lt;code&gt;.lproj</c>).
/// </summary>
public enum Language { System, En, Pt, Fr, Es }

public static class Languages
{
    public static readonly IReadOnlyList<Language> All = Enum.GetValues<Language>();

    public static string Raw(this Language language) => language.ToString().ToLowerInvariant();

    public static Language? FromRaw(string? raw) => All.Cast<Language?>().FirstOrDefault(l => l!.Value.Raw() == raw);

    /// <summary>Each language is named in itself, so it can be found by someone who can't read the current one.</summary>
    public static string NativeName(this Language language) => language switch
    {
        Language.System => L("Windows language"),
        Language.Pt => "Português",
        Language.En => "English",
        Language.Fr => "Français",
        _ => "Español",
    };

    /// <summary>The language actually shown: the chosen one, or the first supported language of Windows (English when it uses none of them).</summary>
    public static Language Resolved(this Language language)
    {
        if (language != Language.System) return language;
        var found = FromRaw(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        return found is { } known and not Language.System ? known : Language.En;
    }

    public static CultureInfo Culture(this Language language) => CultureInfo.GetCultureInfo(language.Resolved() switch
    {
        Language.Pt => "pt-PT",
        Language.Fr => "fr-FR",
        Language.Es => "es-ES",
        _ => "en-US",
    });
}
