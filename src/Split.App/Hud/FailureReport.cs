using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Split.App;

/// <summary>The explained list of failures, with the technical details and a "Copy Report" button.</summary>
public static class FailureReport
{
    public static void Show(IReadOnlyList<JobFailure> failures)
    {
        if (Window(failures) is { } window) window.ShowDialog();
    }

    /// <summary>The report's window, built but not shown (null without failures).</summary>
    public static AppWindow? Window(IReadOnlyList<JobFailure> failures)
    {
        if (failures.Count == 0) return null;
        var named = failures.Where(f => f.File.Length > 0).ToList();
        var heading = named.Count > 1 ? L("%@ files weren’t processed", named.Count.ToString())
                    : named.Count == 1 ? L("Couldn’t process “%@”", named[0].File) : failures[0].Error.Message;
        var explanation = failures.Count == 1 ? failures[0].Error.FullText : L("Each file has the explanation and what you can do.");
        var report = Text(failures);

        var window = new AppWindow("Split", new Size(520, 0), canMinimize: false, sizeToHeight: true) { Topmost = true };
        var icon = Kit.Symbol("exclamationmark.triangle", 30);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 0, 14, 0);
        var info = new StackPanel();
        info.Children.Add(Kit.Text(heading, 13, weight: FontWeights.SemiBold));
        var detail = Kit.Text(explanation, 12, secondary: true);
        detail.Margin = new Thickness(0, 6, 0, 12);
        info.Children.Add(detail);
        var box = new TextBox
        {
            Text = report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 200, Padding = new Thickness(6),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 11, BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Palette.Brush(Palette.With(Palette.Primary, 0.06)), Foreground = Palette.Brush(Palette.Primary),
            SelectionBrush = Palette.Brush(Palette.ControlTint),
        };
        info.Children.Add(box);
        var top = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(info);

        var ok = new SplitButton(L("OK"), ButtonKind.Primary);
        ok.Click += (_, _) => window.Close();
        var copy = new SplitButton(L("Copy Report")) { Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => { Platform.Current.SetClipboardText(report); window.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(copy);
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(top);
        stack.Children.Add(buttons);
        window.Body = stack;
        // Return answers OK and Esc closes, like the system's own alerts.
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter && !box.IsKeyboardFocused) { window.Close(); e.Handled = true; }
        };
        window.Loaded += (_, _) => window.Activate();
        return window;
    }

    public static string Text(IReadOnlyList<JobFailure> failures)
    {
        var report = new System.Text.StringBuilder();
        foreach (var failure in failures)
        {
            if (failure.File.Length > 0) report.Append($"• {failure.File}\n");
            report.Append($"  {failure.Error.Message}\n");
            if (failure.Error.Suggestion is { } suggestion) report.Append("  " + L("What to do: %@", suggestion) + "\n");
            if (failure.Error.Technical is { } technical)
            {
                report.Append("  " + L("Technical details:") + "\n");
                report.Append(string.Join("\n", technical.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => "    " + line)) + "\n");
            }
            report.Append('\n');
        }
        return report.ToString();
    }
}
