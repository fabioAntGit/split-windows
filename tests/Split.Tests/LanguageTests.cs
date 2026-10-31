using System.IO;
using System.Text.RegularExpressions;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>
/// English is the source language: every text in the code is English and each table translates
/// all of it, with the same placeholders. Texts that differ on Windows live in Windows.strings.
/// </summary>
public static class LanguageTests
{

    public static void Register()
    {
        Language[] languages = [Language.Pt, Language.Fr, Language.Es];

        Test("Languages", "Every text in the code is translated, with the same placeholders", () =>
        {
            var keys = SourceKeys.All();
            Check(keys.Count > 100, $"only {keys.Count} texts found: did the search stop working?");
            foreach (var language in languages)
            {
                var shared = Localizer.LoadTable(language, "Localizable");
                var windows = Localizer.LoadTable(language, "Windows");
                Require(shared.Count > 0, $"the {language.Raw()} table didn’t load");
                Require(windows.Count > 0, $"the {language.Raw()} Windows table didn’t load");
                var table = Localizer.LoadTable(language);
                var missing = keys.Where(key => !table.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
                Check(missing.Count == 0, $"{language.Raw()}: {missing.Count} untranslated: {string.Join(" | ", missing.Take(8))}");
                var unused = shared.Keys.Concat(windows.Keys).Where(key => !keys.Contains(key) && !SharedOnlyTexts.All.Contains(key)).Distinct().Order(StringComparer.Ordinal).ToList();
                Check(unused.Count == 0, $"{language.Raw()}: {unused.Count} translations nothing uses: {string.Join(" | ", unused.Take(8))}");
                foreach (var (key, value) in shared.Concat(windows))
                    Check(SourceKeys.Placeholders(key) == SourceKeys.Placeholders(value), $"{language.Raw()}: table entry “{key}” has {SourceKeys.Placeholders(key)} placeholders, “{value}” has {SourceKeys.Placeholders(value)}");
                foreach (var key in keys.Where(table.ContainsKey))
                {
                    var value = table[key];
                    Check(value.Trim().Length > 0, $"{language.Raw()}: empty translation for “{key}”");
                    Check(SourceKeys.Placeholders(key) == SourceKeys.Placeholders(value),
                          $"{language.Raw()}: “{key}” has {SourceKeys.Placeholders(key)} placeholders, “{value}” has {SourceKeys.Placeholders(value)}");
                    Check(key.EndsWith('\n') == value.EndsWith('\n'), $"{language.Raw()}: “{key}” and its translation end differently");
                }
            }
        });

        Test("Languages", "Switching language changes the texts, numbers and dates at once", () =>
        {
            try
            {
                var expected = new Dictionary<Language, (string Cancel, string Files)>
                {
                    [Language.En] = ("Cancel", "3 files converted"), [Language.Pt] = ("Cancelar", "3 ficheiros convertidos"),
                    [Language.Fr] = ("Annuler", "3 fichiers convertis"), [Language.Es] = ("Cancelar", "3 archivos convertidos"),
                };
                foreach (var (language, texts) in expected)
                {
                    Localizer.Shared.Language = language;
                    var plural = JobRunner.Phrase(3, (L("1 file converted"), L("%@ files converted")));
                    Check(L("Cancel") == texts.Cancel, $"{language.Raw()}: “Cancel” → {L("Cancel")}");
                    Check(plural == texts.Files, $"{language.Raw()}: plural → {plural}");
                    Check(Localizer.Shared.Culture.Name.StartsWith(language.Raw(), StringComparison.Ordinal), $"{language.Raw()}: culture {Localizer.Shared.Culture.Name}");
                    var message = Explain.NoTargets([Fixtures.Url("A folder")], tools: false).Message;
                    Check(language == Language.En || message != "Folders can’t be converted.", $"{language.Raw()}: error messages follow the language: {message}");
                }
                Check(Loc.Fill("%2$@ before %1$@", ["one", "two"]) == "two before one", "translations can reorder their placeholders");
                Check(Loc.Fill("100%% of %@", [1.5]) == "100% of 1.5", "numbers are written the invariant way");
                Check(Language.System.Resolved() != Language.System, "“Windows language” always resolves to a real language");
            }
            finally { Localizer.Shared.Language = Language.En; }
        });

        Test("Languages", "The code is written in English", () =>
        {
            var portuguese = new Regex(@"\b(ficheiros?|não|está|também|pasta|imagem|vídeo|definições|prateleira|roda|conversão|guardar)\b", RegexOptions.IgnoreCase);
            foreach (var file in Hostile.Sources())
            {
                var lines = File.ReadAllLines(file);
                for (var number = 0; number < lines.Length; number++)
                {
                    if (!portuguese.IsMatch(lines[number])) continue;
                    // Each language's own name is allowed.
                    if (lines[number].Contains("\"Português\"")) continue;
                    Check(false, $"{Path.GetFileName(file)}:{number + 1} is not in English: {lines[number].Trim()[..Math.Min(100, lines[number].Trim().Length)]}");
                }
            }
        });
    }
}

/// <summary>The texts the code looks up, read from the sources the way the compiler sees them.</summary>
public static class SourceKeys
{
    public static HashSet<string> All()
    {
        var keys = new HashSet<string>();
        var multiline = new Regex("(?<![A-Za-z0-9_])L\\(\"\"\"\\r?\\n(.*?)\\r?\\n([ \\t]*)\"\"\"", RegexOptions.Singleline);
        var single = new Regex("(?<![A-Za-z0-9_])L\\(\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"");
        foreach (var file in Hostile.Sources())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in multiline.Matches(text))
            {
                var indent = match.Groups[2].Value;
                var lines = match.Groups[1].Value.Replace("\r\n", "\n").Split('\n');
                keys.Add(string.Join("\n", lines.Select(line => line.StartsWith(indent, StringComparison.Ordinal) ? line[indent.Length..] : line)));
            }
            foreach (Match match in single.Matches(text))
            {
                var literal = match.Groups[1].Value;
                if (literal.Length > 0) keys.Add(Unescape(literal));
            }
        }
        return keys;
    }

    public static string Unescape(string literal)
    {
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] != '\\' || i + 1 >= literal.Length) { result.Append(literal[i]); continue; }
            var next = literal[++i];
            if (next == 'u' && i + 4 < literal.Length)
            {
                result.Append((char)Convert.ToInt32(literal.Substring(i + 1, 4), 16));
                i += 4;
            }
            else
            {
                result.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
            }
        }
        return result.ToString();
    }

    public static int Placeholders(string text) =>
        text.Split('%').Skip(1).Count(part => part.StartsWith('@') || (part.Length > 0 && char.IsDigit(part[0]) && part.Contains("$@")));
}
