using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Split.App;

public enum SettingsTab { General, Activity, Shortcuts, Wheels, Formats, Engines }

/// <summary>Which tab is showing, so the tray menu and the notification card can open Settings straight on Activity.</summary>
public static class SettingsNavigation
{
    public static SettingsTab Tab { get; set; } = SettingsTab.General;
}

/// <summary>One tab of Settings. It builds its contents from its own state, so a new colour or language redraws it without losing the place.</summary>
public abstract class TabPage : Border
{
    public void Rebuild() => Child = Build();

    protected abstract UIElement Build();

    /// <summary>A form that scrolls when it is taller than the window.</summary>
    protected static ScrollViewer Form(params UIElement[] sections)
    {
        var stack = new StackPanel { Margin = new Thickness(20, 12, 20, 8) };
        foreach (var section in sections) stack.Children.Add(section);
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }
}

/// <summary>Split Settings: 760 × 660 below the title bar, not resizable, with six tabs.</summary>
public sealed class SettingsWindow : AppWindow
{
    public static readonly Size ContentSize = new(760, 660);

    private readonly Dictionary<SettingsTab, TabPage> pages;
    private readonly StackPanel strip = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4) };
    private readonly Border host = new();
    private readonly DispatcherTimer clock;
    private bool changedQueued;
    private readonly Action logChanged;

    public SettingsWindow(SettingsTab tab) : base(L("Split Settings"), ContentSize, canMinimize: true)
    {
        pages = new()
        {
            [SettingsTab.General] = new GeneralTab(),
            [SettingsTab.Activity] = new ActivityTab(),
            [SettingsTab.Shortcuts] = new ShortcutsTab(),
            [SettingsTab.Wheels] = new WheelsTab(),
            [SettingsTab.Formats] = new FormatsTab(),
            [SettingsTab.Engines] = new EnginesTab(),
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(strip);
        Grid.SetRow(host, 1);
        layout.Children.Add(host);
        Body = layout;

        // Jobs report from their own threads: many changes in a row become one redraw.
        logChanged = () =>
        {
            if (changedQueued) return;
            changedQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { changedQueued = false; ActivityChanged(); });
        };
        ActivityLog.Shared.Changed += logChanged;
        clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => (pages[SettingsTab.Activity] as ActivityTab)?.Tick(), Dispatcher);
        // Background priority: WPF reads the contrast theme from the same broadcast first.
        Microsoft.Win32.UserPreferenceChangedEventHandler windows = (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, WindowsLookChanged);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += windows;
        Closed += (_, _) => { ActivityLog.Shared.Changed -= logChanged; clock.Stop(); Microsoft.Win32.SystemEvents.UserPreferenceChanged -= windows; };
        Select(tab);
    }

    private bool solidWheel = Motion.ReduceTransparency || Motion.IncreaseContrast;

    /// <summary>Windows' settings changed: the Wheels tab is redrawn when the glass can or can no longer show.</summary>
    public void WindowsLookChanged()
    {
        var solid = Motion.ReduceTransparency || Motion.IncreaseContrast;
        if (solid == solidWheel) return;
        solidWheel = solid;
        if (Current == SettingsTab.Wheels) pages[Current].Rebuild();
    }

    public SettingsTab Current { get; private set; }

    public TabPage Page(SettingsTab tab) => pages[tab];

    /// <summary>The labels of the tab strip, for the tests.</summary>
    public IReadOnlyList<string> TabTitles => [.. strip.Children.OfType<FrameworkElement>().Select(e => System.Windows.Automation.AutomationProperties.GetName(e))];

    public static SettingsWindow Open(SettingsTab? tab = null)
    {
        var window = SingleWindows.Single("settings", () => new SettingsWindow(tab ?? SettingsNavigation.Tab));
        if (tab is { } chosen) window.Select(chosen);
        return window;
    }

    public void Select(SettingsTab tab)
    {
        Current = tab;
        SettingsNavigation.Tab = tab;
        BuildStrip();
        pages[tab].Rebuild();
        host.Child = pages[tab];
        if (tab == SettingsTab.Activity) clock.Start(); else clock.Stop();
    }

    protected override void Rebuild()
    {
        Title = L("Split Settings");
        BuildStrip();
        pages[Current].Rebuild();
    }

    private void ActivityChanged()
    {
        BuildStrip();
        if (Current == SettingsTab.Activity) ((ActivityTab)pages[SettingsTab.Activity]).Update();
    }

    private string ActivityTitle => ActivityLog.Shared.Active.Count is var n and > 0 ? L("Activity (%@)", n.ToString()) : L("Activity");

    private void BuildStrip()
    {
        strip.Children.Clear();
        (SettingsTab Tab, string Title, string Symbol)[] tabs =
        [
            (SettingsTab.General, L("General"), "gearshape"), (SettingsTab.Activity, ActivityTitle, "list.bullet.rectangle"),
            (SettingsTab.Shortcuts, L("Shortcuts"), "keyboard"), (SettingsTab.Wheels, L("Wheels"), "chart.pie"),
            (SettingsTab.Formats, L("Formats"), "arrow.left.arrow.right"), (SettingsTab.Engines, L("Engines"), "cpu"),
        ];
        foreach (var (tab, title, symbol) in tabs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Kit.Symbol(symbol, 15);
            icon.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(icon);
            var label = Kit.Text(title, TextSize.Callout, weight: FontWeights.Medium, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            var button = new Border
            {
                Child = row, Padding = new Thickness(12, 0, 12, 0), Height = 28, CornerRadius = new CornerRadius(7), Margin = new Thickness(2, 0, 2, 0),
                Background = tab == Current ? Palette.Brush(Palette.With(Palette.Primary, 0.14)) : System.Windows.Media.Brushes.Transparent,
                Focusable = true, Cursor = Cursors.Arrow,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            var chosen = tab;
            button.MouseLeftButtonUp += (_, _) => { if (chosen != Current) Select(chosen); };
            button.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) Select(chosen); };
            strip.Children.Add(button);
        }
    }
}
