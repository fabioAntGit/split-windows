namespace Split;

/// <summary>
/// What a slice does once chosen and only the app can do: open a window, ask a question, use
/// the clipboard or the shelf. The app sets <see cref="WheelHost.Current"/>; the rest of a
/// slice's work (conversions, tools) runs in the Core.
/// </summary>
public interface IWheelHost
{
    void OpenEditor(EditorTool tool, IReadOnlyList<string> paths);

    void Perform(FileAction action, IReadOnlyList<string> paths);

    /// <summary>Asks for the size "Compress To…" aims at, once the wheel has closed, and calls <paramref name="chosen"/> with it.</summary>
    void ChooseTargetSize(IReadOnlyList<string> paths, Action<long> chosen);
}

public static class WheelHost
{
    public static IWheelHost Current { get; set; } = new PendingHost();

    private sealed class PendingHost : IWheelHost
    {
        public void OpenEditor(EditorTool tool, IReadOnlyList<string> paths) => Hud.Current.ShowNotice(Pending.Error());
        public void Perform(FileAction action, IReadOnlyList<string> paths) => Hud.Current.ShowNotice(Pending.Error());
        public void ChooseTargetSize(IReadOnlyList<string> paths, Action<long> chosen) => Hud.Current.ShowNotice(Pending.Error());
    }
}
