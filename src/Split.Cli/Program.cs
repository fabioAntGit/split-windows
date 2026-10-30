using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using Split;
using Split.Windows;

// The same commands the app runs, so every engine can be driven (and tested) without a window.
// Without a command it starts Split.exe, which lives in the same folder.
Console.OutputEncoding = Encoding.UTF8;
Platform.Current = new WindowsPlatform();

string[] commands = ["convert", "targets", "tool", "doctor", "help", "--help", "-h"];
if (args.Length == 0 || !commands.Contains(args[0])) return StartApp(args);
return Run(args[0], args[1..]);

static int Run(string command, string[] rest)
{
    switch (command)
    {
        case "convert":
        {
            if (rest.Length < 2 || Formats.Parse(rest[0]) is not { } target) return Usage();
            var status = Each(Files(rest[1..]), path => Conversions.Convert(path, target));
            Platform.Current.QuitLaunchedOfficeApps();
            return status;
        }
        case "targets":
        {
            foreach (var path in Files(rest))
            {
                var targets = Formats.Detect(path) is { } format ? Conversions.Targets(format) : [];
                Console.WriteLine($"{Path.GetFileName(path)}: {(targets.Count == 0 ? "—" : string.Join(" ", targets.Select(f => f.Ext())))}");
            }
            return 0;
        }
        case "tool":
        {
            if (rest.Length < 2 || QuickTools.FromRaw(rest[0]) is not { } tool) return Usage();
            // `tool compress-to 2MB file…`
            long? targetBytes = null;
            var paths = rest[1..];
            if (tool == QuickTool.CompressToSize)
            {
                if (TargetSize.Parse(paths[0]) is not { } size) return Usage();
                targetBytes = size;
                paths = paths[1..];
            }
            var files = Files(paths);
            if (!tool.Applies(files))
            {
                Console.Error.Write(L("✗ The tool “%@” doesn’t apply to these files. %@\n", tool.Title(), Explain.NoTargets(files, tools: true).Suggestion ?? ""));
                return 1;
            }
            try
            {
                var (result, notes) = Fidelity.Collect(() => tool.Run(files, targetBytes));
                foreach (var output in result.Outputs) Console.WriteLine($"✓ {output}");
                foreach (var note in notes) Console.WriteLine($"  ! {note}");
                if (result.Message is { } message) Console.WriteLine(message);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"✗ {Explain.Error(error, files.Count == 1 ? files[0] : null).FullText}");
                return 1;
            }
        }
        case "doctor":
            Console.WriteLine(Doctor.Report());
            return 0;
        default:
            return Usage();
    }
}

static List<string> Files(IEnumerable<string> paths) => Intake.Resolve(paths.SelectMany(Expand));

// No Windows shell expands "*.heic" before a program sees it, so the command does, in the file name only.
static IEnumerable<string> Expand(string path)
{
    var pattern = Path.GetFileName(path);
    if (pattern.IndexOfAny(['*', '?']) < 0) return [path];
    var folder = Path.GetFullPath(Path.GetDirectoryName(path) is { Length: > 0 } directory ? directory : ".");
    if (!Directory.Exists(folder)) return [path];
    // Matched by hand: the folder search alone lets "*.htm" find ".html" files too.
    var found = Directory.EnumerateFiles(folder).Where(file => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(file)))
        .Order(StringComparer.OrdinalIgnoreCase).ToList();
    return found.Count > 0 ? found : [path];
}

static int Each(List<string> paths, Func<string, IReadOnlyList<string>> work)
{
    var failures = 0;
    foreach (var path in paths)
    {
        try
        {
            var (outputs, notes) = Fidelity.Collect(() => work(path));
            Console.WriteLine($"✓ {Path.GetFileName(path)} → {string.Join(", ", outputs.Select(Path.GetFileName))}");
            foreach (var note in notes) Console.WriteLine($"  ! {note}");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"✗ {Path.GetFileName(path)}: {Explain.Error(error, path).FullText}");
            failures++;
        }
    }
    return failures == 0 ? 0 : 1;
}

static int Usage()
{
    Console.WriteLine(L("""
        Split — a local file converter

        Usage:
          splitapp convert <format> <files…>     converts (e.g. convert webp *.heic)
          splitapp targets <files…>              lists the possible formats
          splitapp tool <tool> <files…>          %@
          splitapp doctor                        checks the installed engines

        Without arguments it opens the app in the notification area.
        """.ReplaceLineEndings("\n"), string.Join(" | ", QuickTools.All.Select(t => t.Raw()))));
    return 2;
}

static int StartApp(string[] arguments)
{
    var app = Path.Combine(AppContext.BaseDirectory, "Split.exe");
    if (!File.Exists(app))
    {
        Console.Error.WriteLine(L("Split.exe wasn’t found in the folder of splitapp."));
        return 1;
    }
    var info = new ProcessStartInfo(app) { UseShellExecute = false };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    Process.Start(info);
    return 0;
}
