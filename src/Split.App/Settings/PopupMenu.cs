using System.Windows;
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
        foreach (var entry in items)
        {
            if (entry.Title is null)
            {
                var line = Kit.Line(0.12);
                line.Margin = new Thickness(6, 4, 6, 4);
                list.Children.Add(line);
                continue;
            }
            var item = new Border
            {
                Child = Kit.Text(entry.Title, TextSize.Callout, weight: entry.Selected ? FontWeights.SemiBold : FontWeights.Normal, wrap: false),
                Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.Arrow,
                IsEnabled = entry.Enabled, Opacity = entry.Enabled ? 1 : 0.4,
            };
            System.Windows.Automation.AutomationProperties.SetName(item, entry.Title);
            item.MouseEnter += (_, _) => item.Background = Palette.Brush(Palette.With(Palette.Primary, 0.12));
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, e) => { close(); e.Handled = true; entry.Run?.Invoke(); };
            list.Children.Add(item);
        }
        return new Border
        {
            Child = list, MinWidth = 120, CornerRadius = new CornerRadius(8), Margin = new Thickness(8),
            Background = Palette.Brush(Palette.Over(Palette.With(Palette.Primary, 0.06), Palette.WheelSurface)),
            BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.18)), BorderThickness = new Thickness(0.5),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3 },
        };
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
