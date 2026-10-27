using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Split.Windows;

/// <summary>Showing files to the user in File Explorer and in their own apps.</summary>
public static class Explorer
{
    /// <summary>Opens each folder with its files selected.</summary>
    public static void Reveal(IEnumerable<string> paths)
    {
        foreach (var folder in paths.Select(Path.GetFullPath).GroupBy(p => Path.GetDirectoryName(p) ?? p, StringComparer.OrdinalIgnoreCase))
        {
            var folderItem = ItemId(folder.Key);
            var absolute = folder.Select(ItemId).Where(id => id != IntPtr.Zero).ToArray();
            try
            {
                if (folderItem == IntPtr.Zero) continue;
                // The folder gets the last part of each item's identifier, which points into the full one.
                SHOpenFolderAndSelectItems(folderItem, (uint)absolute.Length, absolute.Select(ILFindLastID).ToArray(), 0);
            }
            finally
            {
                foreach (var id in absolute.Append(folderItem)) if (id != IntPtr.Zero) Marshal.FreeCoTaskMem(id);
            }
        }
    }

    /// <summary>Opens a file with the app Windows has chosen for it.</summary>
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException) { }
    }

    private static IntPtr ItemId(string path) => SHParseDisplayName(path, IntPtr.Zero, out var id, 0, out _) == 0 ? id : IntPtr.Zero;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr id, uint attributes, out uint flags);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr id);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] items, uint flags);
}
