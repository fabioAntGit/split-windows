using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Split.Windows;

namespace Split.App;

/// <summary>The card's window: clear, always on top, and never taking the focus from what the user is doing.</summary>
public sealed class HudWindow : Window
{
    public HudWindow(HudCard card)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = HudCard.CardWidth + 2 * HudCard.Inset;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = card;
        SourceInitialized += (_, _) => MakeInert(new WindowInteropHelper(this).Handle);
    }

    public const int NoActivate = 0x08000000, ToolWindow = 0x80;

    private static void MakeInert(IntPtr window)
    {
        var style = GetWindowLong(window, -20);
        SetWindowLong(window, -20, style | NoActivate | ToolWindow);
    }

    /// <summary>The extended style of the real window, for the tests.</summary>
    public int ExtendedStyle => GetWindowLong(new WindowInteropHelper(this).EnsureHandle(), -20);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}

/// <summary>
/// The status card in the corner of the screen beside the tray: progress, results (click to show them) and
/// explained errors. It implements what the jobs report to; jobs run on other threads, so every change is
/// handed to the interface thread. Without one (tests, command line) it only keeps the model.
/// </summary>
public sealed class NotificationHud : IHud
{
    public static NotificationHud Shared { get; } = new();

    public HudModel Model { get; } = new();

    /// <summary>Updates the model without putting a window on screen (used by the test runner).</summary>
    public bool IsHeadless { get; set; }

    private Dispatcher? ui;
    private HudWindow? window;
    private HudCard? card;
    private DispatcherTimer? hideTimer;
    private TimeSpan? autoHide;
    private int exiting;

    /// <summary>Starts putting the card on screen, from the interface thread.</summary>
    public void Attach(Dispatcher dispatcher)
    {
        ui = dispatcher;
        Hud.Current = this;
    }

    private void OnUi(Action work)
    {
        if (ui is null || ui.CheckAccess()) work();
        else ui.BeginInvoke(work);
    }

    public bool IsVisible => window?.IsVisible ?? false;

    /// <summary>The window the card lives in, built (not shown) so the tests can look at it.</summary>
    public HudWindow MakeWindow()
    {
        if (window is not null) return window;
        var actions = new HudActions(RevealResults, OpenOutputs, ShowDetails, ShowActivity, CancelJob, Undo, Hide);
        card = new HudCard(Model, actions);
        // Never hide a message while it's being read.
        card.HoverChanged += hovering => { if (hovering) hideTimer?.Stop(); else ScheduleHide(); };
        window = new HudWindow(card);
        window.SizeChanged += (_, _) => { if (window.IsVisible) Reposition(animatedLeft: false); };
        Look.Changed += () => window!.Dispatcher.BeginInvoke(Model.Notify);
        return window;
    }

    // MARK: Placement

    private Box Frame(double height)
    {
        var scale = Desktop.PrimaryScale();
        var work = Desktop.PrimaryWorkArea();
        var area = new Box(work.X / scale, work.Y / scale, work.Width / scale, work.Height / scale);
        return HudPlacement.Frame(area, Desktop.TaskbarEdge(), HudCard.CardWidth + 2 * HudCard.Inset, height, HudCard.Inset);
    }

    private void Reposition(bool animatedLeft)
    {
        if (window is null) return;
        var target = Frame(window.ActualHeight);
        window.Top = target.Y;
        if (!animatedLeft) window.Left = target.X;
    }

    private static void Animate(Window w, DependencyProperty property, double from, double to, double seconds, KeySpline spline, Action? done = null)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), spline));
        animation.Completed += (_, _) => { w.SetValue(property, to); done?.Invoke(); };
        w.SetValue(property, from);
        w.BeginAnimation(property, animation);
    }

    private void Layout(bool entering)
    {
        var w = MakeWindow();
        w.Measure(new Size(w.Width, double.PositiveInfinity));
        var target = Frame(w.DesiredSize.Height);
        var side = HudPlacement.SlideDirection(Desktop.TaskbarEdge());
        exiting++; // an exit still running must not hide the card that comes back
        exitingNow = false;
        if (entering && !w.IsVisible)
        {
            w.Top = target.Y;
            w.Left = target.X;
            w.Opacity = 0;
            w.Show();
            var reduce = Motion.ReduceMotion;
            var seconds = reduce ? 0.15 : 0.32;
            var spline = new KeySpline(0.2, 0.9, 0.25, 1);
            Animate(w, Window.LeftProperty, reduce ? target.X : target.X + side * HudPlacement.Slide, target.X, seconds, spline);
            Animate(w, UIElement.OpacityProperty, 0, 1, seconds, spline);
        }
        else
        {
            w.BeginAnimation(Window.LeftProperty, null);
            w.BeginAnimation(UIElement.OpacityProperty, null);
            w.Left = target.X;
            w.Top = target.Y;
            if (!w.IsVisible) { w.Opacity = 1; w.Show(); } else w.Opacity = 1;
        }
    }

    private void Present(TimeSpan? hideAfter)
    {
        if (IsHeadless) return;
        OnUi(() =>
        {
            Layout(entering: !IsVisible || exitingNow);
            autoHide = hideAfter;
            ScheduleHide();
        });
    }

    private bool exitingNow;

    private void ScheduleHide()
    {
        hideTimer?.Stop();
        if (autoHide is not { } delay || Model.Hovering) return;
        hideTimer = new DispatcherTimer(delay, DispatcherPriority.Normal, (_, _) => Hide(), ui ?? Dispatcher.CurrentDispatcher);
    }

    public void Hide()
    {
        OnUi(() =>
        {
            hideTimer?.Stop();
            if (window is not { IsVisible: true } w || exitingNow) return;
            exitingNow = true;
            var ticket = ++exiting;
            var reduce = Motion.ReduceMotion;
            var seconds = reduce ? 0.15 : 0.22;
            var spline = new KeySpline(0.4, 0, 1, 1);
            var side = HudPlacement.SlideDirection(Desktop.TaskbarEdge());
            Animate(w, Window.LeftProperty, w.Left, reduce ? w.Left : w.Left + side * HudPlacement.Slide, seconds, spline);
            Animate(w, UIElement.OpacityProperty, 1, 0, seconds, spline, () =>
            {
                exitingNow = false;
                if (ticket == exiting) w.Hide();
            });
        });
    }

    // MARK: States

    public void ShowProgress(string title, string item, int index, int count, Guid? jobId = null)
    {
        OnUi(() =>
        {
            Model.JobId = jobId;
            Model.UndoJob = null;
            Model.State = HudState.Progress;
            Model.Title = title;
            Model.Detail = count > 1 ? L("%@ of %@ · %@", index + 1, count, item) : item;
            Model.Fraction = (double)index / Math.Max(count, 1);
            Model.Outputs = [];
            Model.Failures = [];
            Model.Notify();
            Present(null);
        });
    }

    public void UpdateProgress(double fraction, string? detail = null)
    {
        OnUi(() =>
        {
            if (Model.State != HudState.Progress) return;
            Model.Fraction = fraction;
            if (detail is not null) Model.Detail = detail;
            Model.Notify();
        });
    }

    public void ShowDone(IReadOnlyList<string> outputs, string message, IReadOnlyList<JobFailure>? failures = null, int total = 1,
                         IReadOnlyList<string>? notes = null, Guid? jobId = null)
    {
        failures ??= [];
        notes ??= [];
        OnUi(() =>
        {
            Model.JobId = null;
            Model.UndoJob = outputs.Count == 0 ? null : jobId;
            Model.Outputs = outputs;
            Model.Failures = failures;
            Model.Fraction = null;
            Model.Title = message;
            if (failures.Count == 0)
            {
                Model.State = HudState.Success;
                Model.Detail = outputs.Count == 1 ? L("“%@” is next to the original.", Path.GetFileName(outputs[0]))
                             : outputs.Count == 0 ? "" : L("The results are next to the originals.");
                // What the new format couldn't keep is said plainly, never dropped in silence.
                if (notes.Count > 0)
                {
                    Model.State = HudState.Warning;
                    Model.Detail = string.Join("\n", new[] { Model.Detail }.Concat(notes).Where(line => line.Length > 0));
                }
                Model.Notify();
                Present(TimeSpan.FromSeconds(notes.Count == 0 ? 5 : 12));
                Sounds.Success();
            }
            else
            {
                Model.State = failures.Count >= total ? HudState.Failure : HudState.Warning;
                var first = failures[0];
                var detail = total > 1 ? $"{first.File}: {first.Error.Message}" : first.Error.Message;
                if (first.Error.Suggestion is { } suggestion && total == 1) detail += "\n" + suggestion;
                if (failures.Count > 1) detail += L("\n…and %@ more with problems.", failures.Count - 1);
                Model.Detail = detail;
                Model.Notify();
                Present(TimeSpan.FromSeconds(14));
                Sounds.Failure();
            }
        });
    }

    private void Reset(HudState state, string title, string detail)
    {
        Model.UndoJob = null;
        Model.State = state;
        Model.Title = title;
        Model.Detail = detail;
        Model.Fraction = null;
        Model.Outputs = [];
        Model.Failures = [];
    }

    public void ShowMessage(string message, string detail = "", bool isError = false)
    {
        OnUi(() =>
        {
            Model.JobId = null;
            Reset(isError ? HudState.Failure : HudState.Info, message, detail);
            Model.Notify();
            Present(TimeSpan.FromSeconds(isError ? 12 : detail.Length == 0 ? 2.5 : 6));
            if (isError) Sounds.Failure();
        });
    }

    /// <summary>An explained error: the reason as the title, what to do underneath.</summary>
    public void ShowError(Problem error, string? title = null)
    {
        OnUi(() =>
        {
            Reset(HudState.Failure, title ?? error.Message, title is null ? error.Suggestion ?? "" : error.FullText);
            Model.Failures = error.Technical is null ? [] : [new JobFailure("", error)];
            Model.Notify();
            Present(TimeSpan.FromSeconds(12));
            Sounds.Failure();
        });
    }

    /// <summary>Something isn't possible (not an engine failure): a warning, without the error sound.</summary>
    public void ShowNotice(Problem problem)
    {
        OnUi(() =>
        {
            Reset(HudState.Warning, problem.Message, problem.Suggestion ?? "");
            Model.Notify();
            Present(TimeSpan.FromSeconds(8));
        });
    }

    // MARK: Actions

    private void RevealResults()
    {
        if (Model.Outputs.Count > 0) Explorer.Reveal(Model.Outputs);
        Hide();
    }

    private void ShowActivity()
    {
        Hide();
        AppWindows.ShowActivity();
    }

    private void CancelJob()
    {
        if (Model.JobId is { } id) ActivityLog.Shared.Cancel(id);
    }

    private void Undo()
    {
        if (Model.UndoJob is not { } id) return;
        var failures = ActivityLog.Shared.Undo(id);
        if (failures.Count == 0)
        {
            ShowMessage(L("Moved to the Recycle Bin"), L("File Explorer’s “Restore” brings the files back if you need them again."));
        }
        else
        {
            Hide();
            FailureReport.Show(failures);
        }
    }

    private void OpenOutputs()
    {
        foreach (var output in Model.Outputs.Take(5)) Explorer.Open(output);
        Hide();
    }

    private void ShowDetails()
    {
        var failures = Model.Failures;
        if (failures.Count == 0) return;
        Hide();
        FailureReport.Show(failures);
    }
}
