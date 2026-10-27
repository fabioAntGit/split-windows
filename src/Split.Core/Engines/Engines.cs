namespace Split;

/// <summary>The programs outside the app that some conversions wait for.</summary>
public static class Engines
{
    private static readonly Dictionary<string, string> Cache = [];

    /// <summary>Only programs that were found are remembered, so one installed while Split runs is picked up on the next try.</summary>
    public static string? Path(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            var found = Platform.Current.FindProgram(name);
            if (found is not null) Cache[name] = found;
            return found;
        }
    }

    /// <summary>What each engine is for, as the subject of "… needs the program".</summary>
    private static string Purpose(string name) => name switch
    {
        "ffmpeg" => L("Converting and editing audio and video"),
        "ffprobe" => L("Reading audio and video files"),
        "exiftool" => L("Removing image metadata"),
        _ => L("This operation"),
    };

    public static string Require(string name, string? package = null)
    {
        if (Path(name) is { } path) return path;
        var id = package ?? name;
        var install = Path("winget") is null
            ? L("Install App Installer from the Microsoft Store, then run: winget install -e --id %@", id)
            : L("Install it in a terminal with: winget install -e --id %@. There’s no need to reopen Split.", id);
        throw new Problem(L("%@ needs the program “%@”, which isn’t installed.", Purpose(name), name), install);
    }

    /// <summary>The programs winget provides, with the package each comes in.</summary>
    public static readonly (string Program, string Package)[] WingetPackages =
    [
        ("ffmpeg", "Gyan.FFmpeg"), ("exiftool", "OliverBetz.ExifTool"),
    ];

    /// <summary>One command that installs every missing program, or null when nothing is missing.</summary>
    public static string? MissingPackagesCommand
    {
        get
        {
            var missing = WingetPackages.Where(p => Path(p.Program) is null).Select(p => "winget install -e --id " + p.Package).ToList();
            return missing.Count == 0 ? null : string.Join("; ", missing);
        }
    }

    public static bool Word => Platform.Current.IsOfficeInstalled(OfficeApp.Word);
    public static bool Excel => Platform.Current.IsOfficeInstalled(OfficeApp.Excel);
    public static bool PowerPoint => Platform.Current.IsOfficeInstalled(OfficeApp.PowerPoint);
    public static string? Soffice => Path("soffice");
}
