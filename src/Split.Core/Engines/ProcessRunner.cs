using System.Diagnostics;
using System.Text;

namespace Split;

public sealed record ProcessResult(int Status, byte[] StdoutData, string Stderr)
{
    public string Stdout => Encoding.UTF8.GetString(StdoutData);
}

/// <summary>Runs the external engines: no shell, arguments always as a list, cancellable and with a time limit.</summary>
public static class ProcessRunner
{
    public static ProcessResult Run(string executable, IEnumerable<string> arguments, string? cwd = null, double timeoutSeconds = 3600, Action? monitor = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (cwd is not null) info.WorkingDirectory = cwd;

        // Files instead of memory: a tool that writes a lot of text can't fill it up or stall on a full pipe.
        var temp = System.IO.Path.GetTempPath();
        var outPath = System.IO.Path.Combine(temp, $"split-{Guid.NewGuid()}.out");
        var errPath = System.IO.Path.Combine(temp, $"split-{Guid.NewGuid()}.err");
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"{executable} did not start");
            process.StandardInput.Close();
            Task Copy(Stream from, string to) => Task.Run(() => { using var file = File.Create(to); from.CopyTo(file); });
            var copies = new[] { Copy(process.StandardOutput.BaseStream, outPath), Copy(process.StandardError.BaseStream, errPath) };

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            var lastMonitor = DateTime.UtcNow;
            while (!process.WaitForExit(20))
            {
                if (JobProgress.IsCancelled)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    throw Problem.Cancelled;
                }
                if (DateTime.UtcNow > deadline)
                {
                    process.Kill(entireProcessTree: true);
                    throw new Problem(L("%@ took more than %@ minutes and was stopped.", System.IO.Path.GetFileNameWithoutExtension(executable), (int)(timeoutSeconds / 60)),
                                      L("Very long files take time. Choose the “Compact” quality or trim the file first."));
                }
                if (monitor is not null && (DateTime.UtcNow - lastMonitor).TotalSeconds > 0.25)
                {
                    lastMonitor = DateTime.UtcNow;
                    monitor();
                }
            }
            Task.WaitAll(copies);
            return new ProcessResult(process.ExitCode, File.ReadAllBytes(outPath), Encoding.UTF8.GetString(File.ReadAllBytes(errPath)));
        }
        finally
        {
            File.Delete(outPath);
            File.Delete(errPath);
        }
    }

    public static ProcessResult RunChecked(string executable, IEnumerable<string> arguments, string what, string? cwd = null, string app = "", Action? monitor = null)
    {
        var result = Run(executable, arguments, cwd, monitor: monitor);
        if (result.Status == 0) return result;
        var stdout = result.Stdout;
        throw Explain.ToolFailure(what, System.IO.Path.GetFileNameWithoutExtension(executable),
                                  result.Stderr + "\n" + stdout[Math.Max(0, stdout.Length - 400)..], result.Status, app);
    }
}
