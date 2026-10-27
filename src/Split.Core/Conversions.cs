namespace Split;

/// <summary>
/// A family of conversions (images, media, documents…). Each family says what it can produce
/// from a source format with the engines installed right now, and does the work. A new format
/// plugs in by adding a family to <see cref="Conversions.Families"/>; nothing else routes.
/// </summary>
public interface IConversionFamily
{
    IReadOnlyList<Format> Targets(Format source);

    IReadOnlyList<string> Convert(string path, Format source, Format target);
}

public static class Conversions
{
    /// <summary>The order is the order of the wheel, and the first family that offers a target converts to it.</summary>
    public static readonly IReadOnlyList<IConversionFamily> Families =
    [
        new ImageConverter(), new MediaConverter(), new DocumentPipeline(), new SpreadsheetConverter(),
        new PresentationConverter(), new ArchiveTools(), new JsonConverter(),
    ];

    public static List<Format> Targets(Format source)
    {
        var seen = new HashSet<Format> { source };
        return Families.SelectMany(family => family.Targets(source)).Where(seen.Add).ToList();
    }

    /// <summary>Targets valid for every dropped file, in the order of the first file's list.</summary>
    public static List<Format> Targets(IReadOnlyList<string> paths)
    {
        var formats = paths.Select(Formats.Detect).ToList();
        if (formats.Count == 0 || formats.Contains(null)) return [];
        var lists = formats.Select(f => Targets(f!.Value)).ToList();
        return lists.Skip(1).Aggregate(lists[0], (common, list) => common.Where(list.Contains).ToList());
    }

    /// <summary>
    /// The free program a conversion still waits for, when it isn't installed. These conversions
    /// are offered anyway, and choosing one says what to install.
    /// </summary>
    public static string? MissingProgram(Format source, Format target)
    {
        var needsFfmpeg = source.Group() is FormatGroup.Audio or FormatGroup.Video || (source == Format.Gif && target is Format.Mp4 or Format.Webm);
        return needsFfmpeg && Engines.Path("ffmpeg") is null ? "ffmpeg" : null;
    }

    public static IReadOnlyList<string> Convert(string path, Format target)
    {
        FileCheck.Preflight(path);
        if (Formats.Detect(path) is not { } source) throw Explain.NoTargets([path], tools: false);
        var family = target == source ? null : Families.FirstOrDefault(f => f.Targets(source).Contains(target));
        if (family is null)
        {
            var targets = Targets(source);
            if (targets.Count == 0) throw Explain.NoTargets([path], tools: false);
            throw new Problem(L("Split doesn’t convert %@ to %@.", source.Label(), target.Label()),
                              L("From %@ it can make: %@.", source.Label(), string.Join(", ", targets.Select(f => f.Label()))));
        }
        return family.Convert(path, source, target);
    }
}
