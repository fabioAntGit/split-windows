namespace Split;

public enum WheelKind { Convert, Tools, Actions }

public static class WheelKinds
{
    public static readonly IReadOnlyList<WheelKind> All = Enum.GetValues<WheelKind>();

    private static readonly string[] Raws = ["convert", "tools", "actions"];

    public static string Raw(this WheelKind kind) => Raws[(int)kind];

    public static string Title(this WheelKind kind) => kind switch
    {
        WheelKind.Convert => L("Conversion"),
        WheelKind.Tools => L("Tools"),
        _ => L("Actions"),
    };

    public static string Summary(this WheelKind kind) => kind switch
    {
        WheelKind.Convert => L("to another format"),
        WheelKind.Tools => L("edit, compress, PDF, audio…"),
        _ => L("Shelf, copy, move, Terminal"),
    };

    /// <summary>The message of the file chooser opened from the tray menu.</summary>
    public static string ChoosePrompt(this WheelKind kind) => kind switch
    {
        WheelKind.Convert => L("Choose the files to convert."),
        WheelKind.Tools => L("Choose the files for the tools."),
        _ => L("Choose the files for the actions."),
    };

    // Defaults: Shift converts, Ctrl opens the tools and Alt the actions. All three can be changed.
    public static ModifierChoice DefaultModifier(this WheelKind kind) => kind switch
    {
        WheelKind.Convert => ModifierChoice.Shift,
        WheelKind.Tools => ModifierChoice.Ctrl,
        _ => ModifierChoice.Alt,
    };
}
