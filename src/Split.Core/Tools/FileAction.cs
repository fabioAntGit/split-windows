namespace Split;

public enum FileAction { Shelf, Copy, CopyPaths, Move, Terminal }

public static class FileActions
{
    public static readonly IReadOnlyList<FileAction> All = Enum.GetValues<FileAction>();

    private static readonly string[] Raws = ["shelf", "copy", "copyPaths", "move", "terminal"];

    public static string Raw(this FileAction action) => Raws[(int)action];

    public static string Title(this FileAction action) => action switch
    {
        FileAction.Shelf => L("Shelf"),
        FileAction.Copy => L("Copy"),
        FileAction.CopyPaths => L("Copy Paths"),
        FileAction.Move => L("Move To…"),
        _ => L("Open Terminal"),
    };

    public static string Symbol(this FileAction action) => action switch
    {
        FileAction.Shelf => "tray.and.arrow.down",
        FileAction.Copy => "doc.on.clipboard",
        FileAction.CopyPaths => "link",
        FileAction.Move => "folder",
        _ => "terminal",
    };

    public static string Detail(this FileAction action) => action switch
    {
        FileAction.Shelf => L("Hold for a moment"),
        FileAction.Copy => L("To paste somewhere else"),
        FileAction.CopyPaths => L("Full path as text"),
        FileAction.Move => L("Choose another folder"),
        _ => L("Terminal in this folder"),
    };

    /// <summary>What it does, for Settings. Every action takes any file.</summary>
    public static string Explanation(this FileAction action) => action switch
    {
        FileAction.Shelf => L("Keeps the files in a small floating window, to drag them somewhere later."),
        FileAction.Copy => L("Puts the files on the clipboard, to paste them in File Explorer or in another app."),
        FileAction.CopyPaths => L("Copies where the files are, as text, for Terminal or a message."),
        FileAction.Move => L("Moves the files to a folder chosen in a panel."),
        _ => L("Opens Terminal in the folder the files are in."),
    };
}
