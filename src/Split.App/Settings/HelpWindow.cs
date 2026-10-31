using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Split.App;

/// <summary>How to Use Split: three steps, with the keys that are set in Settings drawn as keys.</summary>
public sealed class HelpWindow : AppWindow
{
    public HelpWindow() : base(L("How to Use"), new Size(540, 0), sizeToHeight: true)
    {
        Rebuild();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Close(); e.Handled = true; } };
    }

    protected override bool ClosesOnEscape => true;

    public static HelpWindow Open() => SingleWindows.Single("help", () => new HelpWindow());

    protected override void Rebuild()
    {
        Title = L("How to Use");
        Body = Compose();
    }

    private UIElement Compose()
    {
        var stack = new StackPanel { Margin = new Thickness(28) };

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        title.Children.Add(Kit.Text(L("How to Use Split"), 20, weight: FontWeights.Bold));
        title.Children.Add(Kit.Text(L("Convert and edit files without leaving what you are doing."), 13, secondary: true));
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(LogoTile.Make(56));
        header.Children.Add(title);
        stack.Children.Add(header);

        stack.Children.Add(Step(1, L("Start dragging files"), L("In File Explorer, on the desktop or in another app. Don’t let go."), 22));
        stack.Children.Add(Step(2, L("Tap a key once"), L("A wheel appears around the pointer:"), 22));
        var keys = new StackPanel { Margin = new Thickness(40, 10, 0, 0) };
        Grid.SetIsSharedSizeScope(keys, true);
        foreach (var wheel in WheelKinds.All)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "keys" });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var caps = KeyCap.For(Settings.Modifier(wheel));
            caps.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(caps);
            var name = Kit.Text(wheel.Title(), 13, weight: FontWeights.Medium, wrap: false);
            name.Margin = new Thickness(14, 0, 10, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            var summary = Kit.Text(wheel.Summary(), 12, secondary: true, wrap: false);
            summary.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            Grid.SetColumn(summary, 2);
            row.Children.Add(name);
            row.Children.Add(summary);
            keys.Children.Add(row);
        }
        stack.Children.Add(keys);
        stack.Children.Add(Step(3, L("Drop on the slice you want"), L("The result is saved next to the original. The original is never changed."), 14));

        if (Engines.MissingPackagesCommand is not null)
            stack.Children.Add(Note("shippingbox", L("Audio, video and metadata removal need free programs. Settings › Engines has the winget command that installs them."), 22));
        stack.Children.Add(Note("exclamationmark.triangle", L("Apps running as administrator can’t drag files to Split. Drag from File Explorer, or from an app that isn’t running as administrator."), 14));

        var footer = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        var start = new SplitButton(L("Get Started"), ButtonKind.Primary);
        start.Click += (_, _) => Close();
        DockPanel.SetDock(start, Dock.Right);
        footer.Children.Add(start);
        var privacy = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var lockIcon = Kit.Symbol("lock.shield", 13, secondary: true);
        lockIcon.Margin = new Thickness(0, 0, 6, 0);
        privacy.Children.Add(lockIcon);
        privacy.Children.Add(Kit.Text(L("Everything happens on this PC, without the Internet."), 12, secondary: true, wrap: false));
        footer.Children.Add(privacy);
        stack.Children.Add(footer);
        return stack;
    }

    private static UIElement Step(int number, string title, string detail, double top)
    {
        var circle = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Palette.Brush(Palette.Selection), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number.ToString(), FontFamily = Fonts.Inter, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Palette.Brush(Palette.OnSelection),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(Kit.Text(title, 14, weight: FontWeights.SemiBold));
        text.Children.Add(Kit.Text(detail, 12, secondary: true));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0) };
        row.Children.Add(circle);
        row.Children.Add(text);
        return row;
    }

    private static UIElement Note(string symbol, string text, double top)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0) };
        var icon = Kit.Symbol(symbol, 14, secondary: true);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 8, 0);
        row.Children.Add(icon);
        var label = Kit.Text(text, 12, secondary: true);
        label.MaxWidth = 450;
        row.Children.Add(label);
        return row;
    }
}

/// <summary>About Split: the mark, the name, the version and a line on what the app is. Nothing to open on the Internet.</summary>
public sealed class AboutWindow : AppWindow
{
    public AboutWindow() : base(L("About Split"), new Size(340, 0), sizeToHeight: true) => Rebuild();

    protected override bool ClosesOnEscape => true;

    public static AboutWindow Open() => SingleWindows.Single("about", () => new AboutWindow());

    /// <summary>The version of the app, without the build identifier some tools append.</summary>
    public static string Version =>
        (typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0").Split('+')[0];

    protected override void Rebuild()
    {
        Title = L("About Split");
        var stack = new StackPanel { Margin = new Thickness(28, 28, 28, 24), HorizontalAlignment = HorizontalAlignment.Center };
        var logo = LogoTile.Make(84);
        logo.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(logo);
        var name = Kit.Text("Split", 20, weight: FontWeights.Bold, wrap: false);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Margin = new Thickness(0, 14, 0, 2);
        stack.Children.Add(name);
        var version = Kit.Text(L("Version %@", Version), 11, secondary: true, wrap: false);
        version.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(version);
        var credits = Kit.Text(L("A local file converter and editor. Everything happens on this PC, without the Internet."), 11, secondary: true);
        credits.TextAlignment = TextAlignment.Center;
        credits.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(credits);
        Body = stack;
    }
}
