using System.IO;
using Split.App;

// Usage: dotnet run --project tools/MakeIcon -c Release -- <Split.ico> [<folder for a PNG of each size>]
if (args.Length == 0) { Console.Error.WriteLine("usage: MakeIcon <Split.ico> [png folder]"); return 2; }

// WPF draws on an STA thread.
var thread = new Thread(() =>
{
    File.WriteAllBytes(args[0], Logo.AppIcon(Logo.AppIconSizes));
    if (args.Length < 2) return;
    Directory.CreateDirectory(args[1]);
    foreach (var pixels in Logo.AppIconSizes)
        File.WriteAllBytes(Path.Combine(args[1], $"Split-{pixels}.png"), Logo.Png(Logo.AppIconVisual(pixels), pixels));
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return 0;
