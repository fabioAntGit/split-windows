using System.ComponentModel;
using System.Diagnostics;

namespace Split.Windows;

/// <summary>Opening a terminal in a folder: Windows Terminal when it is installed, otherwise PowerShell.</summary>
public static class Terminal
{
    /// <summary>The folder is handed over as an argument or a working directory, never inside a command line a shell would read.</summary>
    public static void Open(string folder)
    {
        try { Process.Start(Start(folder, FindOnPath("wt.exe")))?.Dispose(); }
        catch (Win32Exception e)
        {
            throw new Problem(L("The terminal couldn’t be opened."), L("Install Windows Terminal from the Microsoft Store, or check that PowerShell is available."), e.Message);
        }
    }

    // Windows Terminal reads ";" as the start of another command, even inside a quoted argument, unless it is written "\;".
    public static ProcessStartInfo Start(string folder, string? windowsTerminal) => windowsTerminal is null
        ? new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = false }
        : new ProcessStartInfo(windowsTerminal) { ArgumentList = { "-d", folder.Replace(";", "\\;") }, UseShellExecute = false };

    /// <summary>A command the user chose to run, in a PowerShell window they can read and answer; it waits for a key before closing.</summary>
    public static ProcessStartInfo Run(string command) =>
        new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            ArgumentList = { "-NoProfile", "-Command", command + "; cmd /c pause" }, UseShellExecute = false,
        };

    private static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => { try { return Path.Combine(directory.Trim('"'), program); } catch (ArgumentException) { return ""; } })
            .FirstOrDefault(File.Exists);
}
