namespace Split;

/// <summary>
/// How jobs tell the user what happens. The app's notification card implements it; until one is
/// set, the headless default does nothing (command line, tests).
/// </summary>
public interface IHud
{
    void ShowProgress(string title, string item, int index, int count, Guid? jobId = null);

    void UpdateProgress(double fraction, string? detail = null);

    void ShowDone(IReadOnlyList<string> outputs, string message, IReadOnlyList<JobFailure>? failures = null, int total = 1,
                  IReadOnlyList<string>? notes = null, Guid? jobId = null);

    void ShowMessage(string message, string detail = "", bool isError = false);

    /// <summary>Something isn't possible (not an engine failure): a warning, without the error sound.</summary>
    void ShowNotice(Problem problem);
}

/// <summary>Updates nothing and shows nothing.</summary>
public sealed class HeadlessHud : IHud
{
    public void ShowProgress(string title, string item, int index, int count, Guid? jobId = null) { }
    public void UpdateProgress(double fraction, string? detail = null) { }
    public void ShowDone(IReadOnlyList<string> outputs, string message, IReadOnlyList<JobFailure>? failures = null, int total = 1,
                         IReadOnlyList<string>? notes = null, Guid? jobId = null) { }
    public void ShowMessage(string message, string detail = "", bool isError = false) { }
    public void ShowNotice(Problem problem) { }
}

public static class Hud
{
    public static IHud Current { get; set; } = new HeadlessHud();
}
