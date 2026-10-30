using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using Path = System.Windows.Shapes.Path;

namespace Split.App;

/// <summary>
/// A window of the app (Settings, How to Use, About, the failure report): a solid background in the app's
/// colour and a title bar of the same colour, drawn by the app with the window buttons on the right. It
/// shows in the taskbar while open, repaints when the colour changes and rebuilds when the language does.
/// </summary>
public class AppWindow : Window
{
    public const double TitleBarHeight = 32;

    private readonly Grid root = new();
    private readonly Border bar = new() { Height = TitleBarHeight };
    private readonly TextBlock caption = new();
    private readonly Border body = new();
    private readonly List<Border> buttons = [];
    private readonly Action look, language;

    public AppWindow(string title, Size client, bool canMinimize = true, bool sizeToHeight = false, bool resizable = false)
    {
        Title = title;
        Width = client.Width;
        if (sizeToHeight) SizeToContent = SizeToContent.Height; else Height = client.Height + TitleBarHeight;
        ResizeMode = resizable ? ResizeMode.CanResize : canMinimize ? ResizeMode.CanMinimize : ResizeMode.NoResize;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TitleBarHeight, ResizeBorderThickness = new Thickness(resizable ? 6 : 0), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });

        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
        root.RowDefinitions.Add(new RowDefinition());
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = new Viewbox { Width = 16, Height = 16, Margin = new Thickness(10, 0, 8, 0), Child = LogoTile.Make(64), IsHitTestVisible = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        left.Children.Add(mark);
        caption.FontFamily = Fonts.Inter;
        caption.FontSize = 12;
        caption.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(caption);
        titleRow.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        if (canMinimize) right.Children.Add(CaptionButton("M 0 0.5 L 10 0.5", () => WindowState = WindowState.Minimized, L("Minimize")));
        right.Children.Add(CaptionButton("M 0 0 L 10 10 M 10 0 L 0 10", Close, L("Close")));
        Grid.SetColumn(right, 1);
        titleRow.Children.Add(right);
        bar.Child = titleRow;
        // A maximised window with its own title bar spills over the screen edges by the width of the resize border.
        if (resizable) StateChanged += (_, _) => root.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        root.Children.Add(bar);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        base.Content = root;

        look = () => Dispatcher.BeginInvoke(Refresh);
        language = () => Dispatcher.BeginInvoke(Refresh);
        Look.Changed += look;
        Localizer.Shared.Changed += language;
        Closed += (_, _) => { Look.Changed -= look; Localizer.Shared.Changed -= language; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && ClosesOnEscape) { Dismiss(); e.Handled = true; } };
        Paint();
    }

    /// <summary>What the window shows below its title bar.</summary>
    public UIElement? Body
    {
        get => body.Child;
        set => body.Child = value;
    }

    protected virtual bool ClosesOnEscape => false;

    /// <summary>What Esc does when the window closes on it.</summary>
    protected virtual void Dismiss() => Close();

    /// <summary>Called after the colour or the language changed: builds the contents again.</summary>
    protected virtual void Rebuild() { }

    /// <summary>Repaints the window and builds its contents again (once, however many changes arrive together).</summary>
    public void Refresh()
    {
        Paint();
        Rebuild();
    }

    private void Paint()
    {
        Background = Palette.Brush(Palette.WheelSurface);
        bar.Background = Palette.Brush(Palette.Over(Palette.With(Palette.Primary, 0.05), Palette.WheelSurface));
        bar.BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.08));
        bar.BorderThickness = new Thickness(0, 0, 0, 1);
        caption.Foreground = Palette.Brush(Palette.Secondary);
        caption.Text = Title;
        foreach (var button in buttons) ((Path)button.Child).Stroke = Palette.Brush(Palette.Primary);
        Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = ScrollBarStyle();
    }

    private Border CaptionButton(string geometry, Action click, string name)
    {
        var button = new Border { Width = 46, Background = Brushes.Transparent, Focusable = false };
        button.Child = new Path { Data = Geometry.Parse(geometry), StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.None };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.MouseEnter += (_, _) => button.Background = Palette.Brush(Palette.With(Palette.Primary, 0.1));
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, _) => click();
        buttons.Add(button);
        return button;
    }

    /// <summary>The window buttons, for the tests.</summary>
    public IReadOnlyList<Border> CaptionButtons => buttons;

    /// <summary>The title bar, for the tests.</summary>
    public Border TitleBar => bar;

    /// <summary>A slim scroll bar in the app's colour: the system's would be a light-grey strip in a black window.</summary>
    private static Style ScrollBarStyle()
    {
        var thumb = Palette.With(Palette.Primary, 0.35);
        var xaml = $"""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
              <Setter Property="Width" Value="10"/><Setter Property="MinWidth" Value="10"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
                <Track Name="PART_Track" IsDirectionReversed="True">
                  <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0"/></Track.DecreaseRepeatButton>
                  <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0"/></Track.IncreaseRepeatButton>
                  <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Margin="2,0,2,0" CornerRadius="3" Background="#{thumb.A:X2}{thumb.R:X2}{thumb.G:X2}{thumb.B:X2}"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                </Track>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """;
        return (Style)XamlReader.Parse(xaml);
    }
}

/// <summary>Each window of the app opens once: asking again brings the open one to the front.</summary>
public static class SingleWindows
{
    private static readonly Dictionary<string, Window> Open = [];

    /// <summary>The window with this id if it is open (restored and in front), else a new one from <paramref name="make"/>.</summary>
    public static T Single<T>(string id, Func<T> make) where T : Window
    {
        if (Open.TryGetValue(id, out var existing) && existing.IsVisible)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            // Windows may refuse to move a window to the front for an app that isn't in front; the toggle gets past that.
            existing.Topmost = true;
            existing.Topmost = false;
            return (T)existing;
        }
        var window = make();
        Open[id] = window;
        window.Closed += (_, _) => { if (Open.TryGetValue(id, out var current) && current == window) Open.Remove(id); };
        window.Show();
        window.Activate();
        return window;
    }

    public static Window? Find(string id) => Open.GetValueOrDefault(id);
}
