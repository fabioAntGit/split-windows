using System.Text.Json;
using System.Text.Json.Nodes;

namespace Split;

public enum Quality { Compact, Balanced, High }

public static class Qualities
{
    public static readonly IReadOnlyList<Quality> All = Enum.GetValues<Quality>();

    public static string Raw(this Quality quality) => quality.ToString().ToLowerInvariant();

    public static string Label(this Quality quality) => quality switch
    {
        Quality.Compact => L("Compact"),
        Quality.Balanced => L("Balanced"),
        _ => L("High"),
    };

    public static double Lossy(this Quality quality) => quality switch { Quality.Compact => 0.6, Quality.Balanced => 0.8, _ => 0.92 };

    public static int Webp(this Quality quality) => (int)(quality.Lossy() * 100);

    public static string AudioKbps(this Quality quality) => quality switch { Quality.Compact => "128k", Quality.Balanced => "192k", _ => "256k" };

    public static string X264Crf(this Quality quality) => quality switch { Quality.Compact => "28", Quality.Balanced => "23", _ => "19" };

    public static string Vp9Crf(this Quality quality) => quality switch { Quality.Compact => "38", Quality.Balanced => "33", _ => "28" };
}

/// <summary>How big the wheels are drawn (Settings › Wheels).</summary>
public enum WheelSize { ExtraSmall, Small, Medium, Large, ExtraLarge }

public static class WheelSizes
{
    public static readonly IReadOnlyList<WheelSize> All = Enum.GetValues<WheelSize>();

    public static string Label(this WheelSize size) => size switch
    {
        WheelSize.ExtraSmall => L("Extra Small"),
        WheelSize.Small => L("Small"),
        WheelSize.Medium => L("Medium"),
        WheelSize.Large => L("Large"),
        _ => L("Extra Large"),
    };
}

/// <summary>The colour of the wheel, the editors and every other window: a soft black, or white.</summary>
public enum LookColor { Black, White }

public static class LookColors
{
    public static readonly IReadOnlyList<LookColor> All = Enum.GetValues<LookColor>();

    public static string Raw(this LookColor color) => color.ToString().ToLowerInvariant();

    public static string Label(this LookColor color) => color == LookColor.Black ? L("Black") : L("White");
}

/// <summary>The key that opens a wheel while files are dragged. Win stays out: it opens the Start menu.</summary>
public enum ModifierChoice { Shift, Ctrl, Alt, CtrlAlt, Off }

public static class ModifierChoices
{
    public static readonly IReadOnlyList<ModifierChoice> All = Enum.GetValues<ModifierChoice>();

    private static readonly string[] Names = ["shift", "ctrl", "alt", "ctrl+alt", "off"];

    public static string Raw(this ModifierChoice choice) => Names[(int)choice];

    public static string Label(this ModifierChoice choice) => choice switch
    {
        ModifierChoice.Shift => "Shift",
        ModifierChoice.Ctrl => "Ctrl",
        ModifierChoice.Alt => "Alt",
        ModifierChoice.CtrlAlt => "Ctrl + Alt",
        _ => L("Off"),
    };

    public static ModifierChoice? FromRaw(string? raw) => Array.IndexOf(Names, raw) is var index and >= 0 ? (ModifierChoice)index : null;

    /// <summary>Each modifier opens one wheel: choosing a taken one swaps the two.</summary>
    public static void Set(ModifierChoice choice, WheelKind wheel)
    {
        if (choice != ModifierChoice.Off)
        {
            foreach (var other in WheelKinds.All.Where(other => other != wheel && Settings.Modifier(other) == choice))
                Settings.SetModifier(Settings.Modifier(wheel), other);
        }
        Settings.SetModifier(choice, wheel);
    }
}

/// <summary>
/// The user's choices, kept as JSON in <c>%APPDATA%\Split\settings.json</c>. Every write replaces the
/// file as a whole, so a crash never leaves half of one.
/// </summary>
public static class Settings
{
    private static readonly object Gate = new();
    private static string storePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Split", "settings.json");
    private static JsonObject? values;

    /// <summary>Where the settings live; the tests point it at their own folder before anything reads it.</summary>
    public static string StorePath
    {
        get { lock (Gate) return storePath; }
        set { lock (Gate) { storePath = value; values = null; } }
    }

    public static Quality Quality
    {
        get => Qualities.All.Cast<Quality?>().FirstOrDefault(q => q!.Value.Raw() == Read<string>("quality")) ?? Quality.Balanced;
        set => Write("quality", value.Raw());
    }

    public static bool KeepsMetadata
    {
        get => Read<bool?>("keepsMetadata") ?? true;
        set => Write("keepsMetadata", value);
    }

    public static Language Language
    {
        get => Languages.FromRaw(Read<string>("language")) ?? Language.System;
        set => Write("language", value.Raw());
    }

    /// <summary>Black until the user picks white.</summary>
    public static LookColor LookColor
    {
        get => LookColors.All.Cast<LookColor?>().FirstOrDefault(c => c!.Value.Raw() == Read<string>("lookColor")) ?? LookColor.Black;
        set => Write("lookColor", value.Raw());
    }

    public static WheelSize WheelSize
    {
        get => Read<int?>("wheelSize") is { } raw && Enum.IsDefined((WheelSize)raw) ? (WheelSize)raw : WheelSize.Medium;
        set => Write("wheelSize", (int)value);
    }

    /// <summary>Names under the icons of the slices; without them the wheel shows icons only.</summary>
    public static bool WheelNames
    {
        get => Read<bool?>("wheelNames") ?? true;
        set => Write("wheelNames", value);
    }

    /// <summary>What was typed last time in "Compress To…".</summary>
    public static string LastTargetSize
    {
        get => Read<string>("lastTargetSize") ?? "2 MB";
        set => Write("lastTargetSize", value);
    }

    public static bool PlaySounds
    {
        get => Read<bool?>("playSounds") ?? true;
        set => Write("playSounds", value);
    }

    /// <summary>Word, Excel and PowerPoint keep Office layouts intact when exporting to PDF.</summary>
    public static bool UseOfficeApps
    {
        get => Read<bool?>("useOfficeApps") ?? true;
        set => Write("useOfficeApps", value);
    }

    /// <summary>The welcome window opens once, the first time Split runs.</summary>
    public static bool DidShowWelcome
    {
        get => Read<bool?>("didShowWelcome") ?? false;
        set => Write("didShowWelcome", value);
    }

    /// <summary>The order, hidden slices and groups of one wheel; <paramref name="key"/> names the wheel (see <see cref="WheelScope"/>).</summary>
    public static WheelArrangement WheelArrangement(string key)
    {
        List<WheelGroup>? groups = null;
        try { groups = Node($"wheel.{key}.groups")?.Deserialize<List<WheelGroup>>(); }
        catch (JsonException) { }
        return new WheelArrangement
        {
            Order = Read<string[]>($"wheel.{key}.order") ?? [],
            Hidden = [.. Read<string[]>($"wheel.{key}.hidden") ?? []],
            Groups = groups,
        };
    }

    public static void SetWheelArrangement(WheelArrangement arrangement, string key)
    {
        lock (Gate)
        {
            var root = Values();
            Put(root, $"wheel.{key}.order", arrangement.Order.Length == 0 ? null : JsonSerializer.SerializeToNode(arrangement.Order));
            Put(root, $"wheel.{key}.hidden", arrangement.Hidden.Count == 0 ? null : JsonSerializer.SerializeToNode(arrangement.Hidden.Order(StringComparer.Ordinal)));
            Put(root, $"wheel.{key}.groups", arrangement.Groups is null ? null : JsonSerializer.SerializeToNode(arrangement.Groups));
            Save(root);
        }
    }

    public static ModifierChoice Modifier(WheelKind wheel) =>
        ModifierChoices.FromRaw(Read<string>($"modifier.{wheel.Raw()}")) ?? wheel.DefaultModifier();

    public static void SetModifier(ModifierChoice choice, WheelKind wheel) => Write($"modifier.{wheel.Raw()}", choice.Raw());

    // MARK: Storage

    private static JsonNode? Node(string key)
    {
        lock (Gate) return Values()[key]?.DeepClone();
    }

    /// <summary>A value of the wrong type counts as not set, as if the key were missing.</summary>
    private static T? Read<T>(string key)
    {
        try { return Node(key) is { } node ? node.Deserialize<T>() : default; }
        catch (JsonException) { return default; }
    }

    private static void Write<T>(string key, T value)
    {
        lock (Gate)
        {
            var root = Values();
            Put(root, key, JsonSerializer.SerializeToNode(value));
            Save(root);
        }
    }

    private static void Put(JsonObject root, string key, JsonNode? value)
    {
        if (value is null) root.Remove(key); else root[key] = value;
    }

    private static JsonObject Values()
    {
        if (values is not null) return values;
        try { values = JsonNode.Parse(File.ReadAllText(storePath)) as JsonObject; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return values ??= [];
    }

    /// <summary>Temp file plus replace; a settings file that can't be written must not stop the app, so the choice only lasts this run.</summary>
    private static void Save(JsonObject root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            var temp = storePath + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(storePath)) File.Replace(temp, storePath, null); else File.Move(temp, storePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
