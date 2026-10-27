using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Split.Windows;

/// <summary>What the Core asks of Windows (see <see cref="IPlatform"/>).</summary>
public sealed class WindowsPlatform : IPlatform
{
    private static readonly string[] OfficeProgIds = ["Word.Application", "Excel.Application", "PowerPoint.Application"];

    public string? FindProgram(string name) => EngineLocator.Find(name);

    public bool IsOfficeInstalled(OfficeApp app) => Type.GetTypeFromProgID(OfficeProgIds[(int)app]) is not null;

    // The Office bridge starts and closes the apps itself; until it exists nothing was launched.
    public void QuitLaunchedOfficeApps() { }

    /// <summary>The model that cuts out the subject ships next to the program.</summary>
    public bool CanRemoveBackground => File.Exists(Path.Combine(AppContext.BaseDirectory, "models", "isnet-general-use.onnx"));

    public IReadOnlyList<string> OcrLanguages
    {
        get
        {
            try { return global::Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(language => language.DisplayName).ToList(); }
            catch (Exception) { return []; }
        }
    }

    public string? ResolveShortcut(string path)
    {
        try
        {
            var link = new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return target.Length == 0 ? null : target.ToString();
        }
        catch (Exception e) when (e is COMException or IOException) { return null; }
    }

    public void MoveToRecycleBin(string path)
    {
        // IFileOperation wants a single-threaded apartment of its own.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Recycle(Path.GetFullPath(path)); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new Problem(L("“%@” can’t be moved to the Recycle Bin.", Path.GetFileName(path)),
                              L("Split never deletes anything for good. Delete it yourself if you don’t want it."));
        }
    }

    private static void Recycle(string path)
    {
        var operation = (IFileOperation)new FileOperation();
        // Recycle, or fail: never delete for good when the disk has no Recycle Bin.
        const uint allowUndo = 0x40, noConfirmation = 0x10, noErrorUi = 0x400, silent = 0x4, recycleOnDelete = 0x80000;
        operation.SetOperationFlags(allowUndo | noConfirmation | noErrorUi | silent | recycleOnDelete);
        var iid = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        operation.DeleteItem(item, IntPtr.Zero);
        operation.PerformOperations();
        Marshal.ReleaseComObject(item);
        Marshal.ReleaseComObject(operation);
    }

    public void SetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 10 && !OpenClipboard(IntPtr.Zero); attempt++) Thread.Sleep(20);
        try
        {
            var bytes = (text.Length + 1) * 2;
            var memory = GlobalAlloc(0x2, (UIntPtr)bytes);
            var locked = GlobalLock(memory);
            Marshal.Copy((text + "\0").ToCharArray(), 0, locked, text.Length + 1);
            GlobalUnlock(memory);
            EmptyClipboard();
            if (SetClipboardData(13, memory) == IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid riid, out IShellItem item);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr data, uint flags);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    }

    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")] private class FileOperation { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    private interface IShellItem { }

    // Only the methods up to the ones used are named; the others hold their places in the table.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    private interface IFileOperation
    {
        void Advise();
        void Unadvise();
        void SetOperationFlags(uint flags);
        void SetProgressMessage();
        void SetProgressDialog();
        void SetProperties();
        void SetOwnerWindow();
        void ApplyPropertiesToItem();
        void ApplyPropertiesToItems();
        void RenameItem();
        void RenameItems();
        void MoveItem();
        void MoveItems();
        void CopyItem();
        void CopyItems();
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems();
        void NewItem();
        void PerformOperations();
    }

    public IReadOnlyList<PixelBox> FindFaces(byte[] gray, int width, int height) => FaceFinder.Find(gray, width, height);
}

/// <summary>Finds programs where installers put them, also right after an install (before the PATH of this process knows).</summary>
internal static class EngineLocator
{
    public static string? Find(string name)
    {
        foreach (var directory in SearchDirectories())
        {
            var candidate = Path.Combine(directory, name + ".exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        // The PATH of this process may predate an install; the registry holds the current one.
        var path = new List<string>(Environment.ExpandEnvironmentVariables(Environment.GetEnvironmentVariable("Path") ?? "").Split(';'));
        path.AddRange(RegistryPath(Registry.CurrentUser, "Environment"));
        path.AddRange(RegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var winget = Path.Combine(local, "Microsoft", "WinGet");
        path.Add(Path.Combine(winget, "Links"));
        path.AddRange(PackageFolders(Path.Combine(winget, "Packages")));
        path.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims"));
        path.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin"));
        path.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program"));
        return path.Select(entry => entry.Trim().Trim('"')).Where(entry => entry.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> RegistryPath(RegistryKey root, string subkey)
    {
        try
        {
            using var key = root.OpenSubKey(subkey);
            return Environment.ExpandEnvironmentVariables(key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "").Split(';');
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    /// <summary>winget unpacks portable programs into one folder per package, with the program in it or in a "bin" folder one level down.</summary>
    private static IEnumerable<string> PackageFolders(string packages)
    {
        if (!Directory.Exists(packages)) yield break;
        foreach (var package in Directory.EnumerateDirectories(packages))
        {
            yield return package;
            yield return Path.Combine(package, "bin");
            foreach (var inner in Directory.EnumerateDirectories(package))
            {
                yield return inner;
                yield return Path.Combine(inner, "bin");
            }
        }
    }
}
