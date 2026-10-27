using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Split.Windows;

namespace Split.App;

/// <summary>The explained list of failures, with the technical details and a "Copy Report" button.</summary>
public static class FailureReport
{
    public static void Show(IReadOnlyList<JobFailure> failures)
    {
        if (failures.Count == 0) return;
        var named = failures.Where(f => f.File.Length > 0).ToList();
        var heading = named.Count > 1 ? L("%@ files weren’t processed", named.Count.ToString())
                    : named.Count == 1 ? L("Couldn’t process “%@”", named[0].File) : failures[0].Error.Message;
        var explanation = failures.Count == 1 ? failures[0].Error.FullText : L("Each file has the explanation and what you can do.");
        var report = Text(failures);

        var window = new Window
        {
            Title = "Split",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Palette.Brush(Palette.WheelSurface),
            Topmost = true,
        };
        window.SourceInitialized += (_, _) => Desktop.ColorTitleBar(new WindowInteropHelper(window).Handle, !Palette.IsWhite,
                                                                  Palette.WheelSurface.R << 16 | Palette.WheelSurface.G << 8 | Palette.WheelSurface.B);
        var stack = new StackPanel { Margin = new Thickness(20) };
        var title = Fonts.Text(heading, 13, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        var info = Fonts.Text(explanation, 12, color: Palette.Secondary);
        info.TextWrapping = TextWrapping.Wrap;
        info.Margin = new Thickness(0, 6, 0, 12);
        var box = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            Height = 200,
            Padding = new Thickness(6),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Palette.Brush(Palette.With(Palette.Primary, 0.06)),
            Foreground = Palette.Brush(Palette.Primary),
            BorderThickness = new Thickness(0),
        };
        var ok = new SplitButton(L("OK"), ButtonKind.Primary);
        ok.Click += (_, _) => window.Close();
        var copy = new SplitButton(L("Copy Report")) { Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => { Platform.Current.SetClipboardText(report); window.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(copy);
        stack.Children.Add(title);
        stack.Children.Add(info);
        stack.Children.Add(box);
        stack.Children.Add(buttons);
        window.Content = stack;
        window.ShowDialog();
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
