using Microsoft.Win32;

namespace Split.Windows;

/// <summary>Starting with Windows: a value under the user's Run key, so no administrator is needed.</summary>
public static class LaunchAtLogin
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The Run key is the truth, not a copy in the settings: the user can change it in Windows too.</summary>
    public static bool IsEnabled(string name = "Split")
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(name) is string;
    }

    public static void Set(bool enabled, string name = "Split", string? program = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue(name, $"\"{program ?? Environment.ProcessPath}\"");
        else key.DeleteValue(name, throwOnMissingValue: false);
    }
}
