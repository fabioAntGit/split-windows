using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Split.App;

/// <summary>
/// The tray and shelf menus in the app's colours and rounded, like <see cref="PopupMenu"/>, in place of the
/// system's light menu. The colours are taken when the menu is filled, so it is filled each time it opens.
/// </summary>
public static class MenuLook
{
    public static void Fill(ContextMenu menu, IEnumerable<MenuEntry> entries)
    {
        menu.Template = Frame();
        menu.HasDropShadow = false; // The frame draws its own.
        menu.Items.Clear();
        var row = Row();
        var line = Line();
        foreach (var entry in entries)
        {
            if (entry.IsSeparator) { menu.Items.Add(new Separator { Template = line, Focusable = false }); continue; }
            var item = new MenuItem
            {
                Header = entry.Title, IsEnabled = entry.Enabled, Template = row, FocusVisualStyle = null,
                Foreground = Palette.Brush(Palette.Primary), FontFamily = Fonts.Inter, FontSize = TextSize.Callout,
            };
            if (entry.Symbol is { } symbol) item.Icon = Icons.Block(symbol, 15, Palette.With(Palette.Primary, 0.75));
            if (entry.Run is { } run) item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
    }

    /// <summary>A rounded surface with a soft shadow, the items on it; the outer margin leaves room for the shadow.</summary>
    private static ControlTemplate Frame()
    {
        var shadow = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.3, Color = Colors.Black };
        shadow.Freeze();
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        surface.SetValue(Border.BackgroundProperty, Palette.Brush(Palette.Floating));
        surface.SetValue(Border.BorderBrushProperty, Palette.Brush(Palette.With(Palette.Primary, Motion.IncreaseContrast ? 0.4 : 0.14)));
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(Motion.IncreaseContrast ? 1 : 0.5));
        // On the surface alone, so the text casts no shadow through it.
        surface.SetValue(UIElement.EffectProperty, shadow);
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        items.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(12));
        root.SetValue(FrameworkElement.MinWidthProperty, 200.0);
        root.SetValue(FrameworkElement.UseLayoutRoundingProperty, true);
        root.AppendChild(surface);
        root.AppendChild(items);
        return new ControlTemplate(typeof(ContextMenu)) { VisualTree = root };
    }

    /// <summary>An icon column without the system's gutter, the title, and a rounded highlight under the pointer.</summary>
    private static ControlTemplate Row()
    {
        var icon = new FrameworkElementFactory(typeof(ContentPresenter));
        icon.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
        icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var title = new FrameworkElementFactory(typeof(ContentPresenter));
        title.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        title.SetValue(Grid.ColumnProperty, 1);
        title.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var iconColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
        iconColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(26));
        var titleColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.AppendChild(iconColumn);
        grid.AppendChild(titleColumn);
        grid.AppendChild(icon);
        grid.AppendChild(title);
        var row = new FrameworkElementFactory(typeof(Border), "Row");
        row.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        row.SetValue(Border.PaddingProperty, new Thickness(8, 6, 14, 6));
        row.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        row.AppendChild(grid);

        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = row };
        var highlighted = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        highlighted.Setters.Add(new Setter(Border.BackgroundProperty, Palette.Brush(Palette.With(Palette.Primary, 0.12)), "Row"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
        template.Triggers.Add(highlighted);
        template.Triggers.Add(disabled);
        return template;
    }

    private static ControlTemplate Line()
    {
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.HeightProperty, 1.0);
        line.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4));
        line.SetValue(Border.BackgroundProperty, Palette.Brush(Palette.With(Palette.Primary, 0.12)));
        return new ControlTemplate(typeof(Separator)) { VisualTree = line };
    }
}
