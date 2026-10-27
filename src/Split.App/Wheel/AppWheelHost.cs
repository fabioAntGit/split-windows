using System.Windows;
using Microsoft.Win32;
using Split.Windows;

namespace Split.App;

/// <summary>What the wheel's slices ask of the app: the shelf, the clipboard, folders and terminals, and the size prompt.</summary>
public sealed class AppWheelHost : IWheelHost
{
    public void OpenEditor(EditorTool tool, IReadOnlyList<string> paths) => Hud.Current.ShowNotice(Pending.Error());

    public void Perform(FileAction action, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            switch (action)
            {
                case FileAction.Shelf:
                    Shelf.Shared.Show(paths);
                    break;
                case FileAction.Copy:
                    Copy(paths);
                    Hud.Current.ShowMessage(paths.Count == 1 ? L("File copied") : L("%@ files copied", paths.Count));
                    break;
                case FileAction.CopyPaths:
                    // One path per line, with the line ends Windows text boxes expect.
                    Platform.Current.SetClipboardText(string.Join("\r\n", paths));
                    Hud.Current.ShowMessage(paths.Count == 1 ? L("Path copied") : L("%@ paths copied", paths.Count));
                    break;
                case FileAction.Move:
                    Move(paths);
                    break;
                default:
                    Terminal.Open(Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(Path.GetFullPath(paths[0]))!);
                    break;
            }
        }
        catch (Exception e) when (e is Problem or IOException or System.Runtime.InteropServices.COMException)
        {
            Hud.Current.ShowNotice(e as Problem ?? Explain.Error(e));
        }
    }

    /// <summary>The files on the clipboard as File Explorer puts them, marked to be copied (not cut) when pasted.</summary>
    public static void Copy(IReadOnlyList<string> paths)
    {
        var data = new DataObject();
        data.SetFileDropList([.. paths]);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        // Another app may hold the clipboard for a moment.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 10) { Thread.Sleep(30); }
        }
    }

    private static void Move(IReadOnlyList<string> paths)
    {
        var dialog = new OpenFolderDialog
        {
            Title = paths.Count == 1 ? L("Choose where to move “%@”.", Path.GetFileName(paths[0])) : L("Choose where to move %@ items.", paths.Count),
        };
        if (dialog.ShowDialog() != true) return;
        var (moved, failures) = FileMover.Move(paths, dialog.FolderName);
        var message = failures.Count == 0 ? (moved.Count == 1 ? L("Moved") : L("%@ items moved", moved.Count))
            : moved.Count == 0 ? L("Nothing was moved") : L("%@ of %@ moved", moved.Count, paths.Count);
        Hud.Current.ShowDone(moved, message, failures, paths.Count);
    }

    public void ChooseTargetSize(IReadOnlyList<string> paths, Action<long> chosen) => TargetSizeWindow.Open(paths, chosen);
}
