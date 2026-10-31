using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Split.App;

/// <summary>One line of a <see cref="PopupMenu"/>; a line without a title is a separator.</summary>
public sealed record PopupItem(string? Title, Action? Run = null, bool Selected = false, bool Enabled = true);

/// <summary>A menu in the app's colours (the system's menus are always light), for the drop-downs and the right-click menus.</summary>
public static class PopupMenu
{
    public static Border Build(IEnumerable<PopupItem> items, Action close)
    {
        var list = new StackPanel { Margin = new Thickness(4) };
        var rows = new List<MenuRow>();
        var at = -1;
        IInputElement? opener = null;
        void Light(int index)
        {
            at = index;
            for (var i = 0; i < rows.Count; i++) rows[i].Background = i == index ? Palette.Brush(Palette.With(Palette.Primary, 0.12)) : Brushes.Transparent;
        }
        void Pick(MenuRow row) { close(); row.Run?.Invoke(); }
        foreach (var entry in items)
        {
            if (entry.Title is null)
            {
                var line = Kit.Line(0.12);
                line.Margin = new Thickness(6, 4, 6, 4);
                list.Children.Add(line);
                continue;
            }
            var item = new MenuRow
            {
                Run = entry.Run,
                Child = Kit.Text(entry.Title, TextSize.Callout, weight: entry.Selected ? FontWeights.SemiBold : FontWeights.Normal, wrap: false),
                Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.Arrow,
                IsEnabled = entry.Enabled, Opacity = entry.Enabled ? 1 : 0.4,
            };
            System.Windows.Automation.AutomationProperties.SetName(item, entry.Title);
            item.Activate = () => Pick(item);
            if (entry.Enabled)
            {
                rows.Add(item);
                item.MouseEnter += (_, _) => Light(rows.IndexOf(item));
                item.MouseLeave += (_, _) => Light(-1);
            }
            item.MouseLeftButtonUp += (_, e) => { e.Handled = true; Pick(item); };
            list.Children.Add(item);
        }
        var menu = new MenuPanel
        {
            Focusable = true, FocusVisualStyle = null,
            Child = list, MinWidth = 120, CornerRadius = new CornerRadius(8), Margin = new Thickness(8),
            Background = Palette.Brush(Palette.Over(Palette.With(Palette.Primary, 0.06), Palette.WheelSurface)),
            BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.18)), BorderThickness = new Thickness(0.5),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3 },
        };
        // Opened from the keyboard, the first line is lit; Esc, a choice or a click away hands the focus back to what opened it.
        menu.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                opener = Keyboard.FocusedElement;
                Light(InputManager.Current.MostRecentInputDevice is null or KeyboardDevice && rows.Count > 0 ? 0 : -1);
                menu.Focus();
            }
            else if (opener is not null) Keyboard.Focus(opener);
        };
        menu.KeyDown += (_, e) =>
        {
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Down when rows.Count > 0: Light((at + 1) % rows.Count); break;
                case Key.Up when rows.Count > 0: Light(at <= 0 ? rows.Count - 1 : at - 1); break;
                case Key.Home when rows.Count > 0: Light(0); break;
                case Key.End when rows.Count > 0: Light(rows.Count - 1); break;
                case Key.Enter or Key.Space when at >= 0: Pick(rows[at]); break;
                case Key.Escape: close(); break;
                default: e.Handled = false; break;
            }
        };
        return menu;
    }

    private sealed class MenuPanel : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(MenuPanel owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Menu;
            protected override string GetClassNameCore() => "Menu";
        }
    }

    /// <summary>A line of the menu, which a screen reader reads as a menu item and can invoke.</summary>
    private sealed class MenuRow : Border
    {
        public Action? Run;
        public Action? Activate;

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(MenuRow owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.MenuItem;
            protected override string GetClassNameCore() => "MenuItem";
            public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
            public void Invoke() => owner.Activate?.Invoke();
        }
    }

    /// <summary>Opens a menu at the pointer (a right-click) next to <paramref name="anchor"/>.</summary>
    public static Popup Show(UIElement anchor, IEnumerable<PopupItem> items)
    {
        var popup = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.MousePoint, PlacementTarget = anchor, PopupAnimation = PopupAnimation.None };
        popup.Child = Build(items, () => popup.IsOpen = false);
        popup.IsOpen = true;
        return popup;
    }
}
