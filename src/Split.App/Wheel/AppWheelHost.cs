namespace Split.App;

/// <summary>What the wheel's slices ask of the app. The shelf is here; the rest waits for the pieces that do it.</summary>
public sealed class AppWheelHost : IWheelHost
{
    public void OpenEditor(EditorTool tool, IReadOnlyList<string> paths) => Hud.Current.ShowNotice(Pending.Error());

    public void Perform(FileAction action, IReadOnlyList<string> paths)
    {
        if (action == FileAction.Shelf) Shelf.Shared.Show(paths);
        else Hud.Current.ShowNotice(Pending.Error());
    }

    public void ChooseTargetSize(IReadOnlyList<string> paths, Action<long> chosen) => Hud.Current.ShowNotice(Pending.Error());
}
