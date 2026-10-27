using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Split.App;

/// <summary>
/// The pieces the settings, help and about windows are built from, drawn in the app's own colours: grouped
/// rows, switches, checkboxes, segmented and drop-down pickers, a stepped slider and a text field.
/// </summary>
public static class Kit
{
    public static TextBlock Text(string text, double size = 13, bool secondary = false, FontWeight? weight = null, bool wrap = true)
    {
        var block = Fonts.Text(text, size, weight, secondary ? Palette.Secondary : Palette.Primary);
        if (wrap) block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    /// <summary>A hairline.</summary>
    public static Border Line(double alpha = 0.1) => new() { Height = 1, Background = Palette.Brush(Palette.With(Palette.Primary, alpha)) };

    /// <summary>A rounded panel with a faint fill.</summary>
    public static Border Panel(UIElement? child, double radius = 10, double alpha = 0.05, Thickness? padding = null) => new()
    {
        Child = child,
        CornerRadius = new CornerRadius(radius),
        Background = Palette.Brush(Palette.With(Palette.Primary, alpha)),
        Padding = padding ?? new Thickness(0),
    };

    /// <summary>One line of a group: the label on the left, the control on the right.</summary>
    public static Grid Row(UIElement left, UIElement? right = null, double height = 40)
    {
        var grid = new Grid { MinHeight = height, Margin = new Thickness(12, 4, 12, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (left is FrameworkElement l) l.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(left);
        if (right is not null)
        {
            if (right is FrameworkElement r) { r.VerticalAlignment = VerticalAlignment.Center; r.Margin = new Thickness(12, 0, 0, 0); }
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        return grid;
    }

    /// <summary>Rows in one rounded box, a hairline between them (a grouped form).</summary>
    public static Border Group(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(Line(0.08));
            stack.Children.Add(rows[i]);
        }
        return Panel(stack);
    }

    /// <summary>A heading above a group and a note below it.</summary>
    public static StackPanel Section(string? header, UIElement group, string? footer = null, UIElement? headerRight = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        if (header is not null)
        {
            var top = new Grid { Margin = new Thickness(4, 0, 4, 6) };
            top.Children.Add(Text(header, TextSize.Callout, secondary: true, weight: FontWeights.SemiBold));
            if (headerRight is FrameworkElement extra) { extra.HorizontalAlignment = HorizontalAlignment.Right; top.Children.Add(extra); }
            stack.Children.Add(top);
        }
        stack.Children.Add(group);
        if (footer is not null) { var note = Text(footer, TextSize.Subheadline, secondary: true); note.Margin = new Thickness(4, 6, 4, 0); stack.Children.Add(note); }
        return stack;
    }

    /// <summary>A label that acts as a button, without a frame.</summary>
    public static Border Link(string text, Action click, string? symbol = null, double size = TextSize.Subheadline)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (symbol is not null) { var icon = Icons.Block(symbol, size + 2); icon.Margin = new Thickness(0, 0, 4, 0); row.Children.Add(icon); }
        row.Children.Add(Text(text, size, weight: FontWeights.Medium, wrap: false));
        var link = new Border { Child = row, Cursor = Cursors.Hand, Background = Brushes.Transparent, Padding = new Thickness(4, 2, 4, 2), Focusable = true };
        link.MouseLeftButtonUp += (_, _) => click();
        link.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) click(); };
        System.Windows.Automation.AutomationProperties.SetName(link, text);
        return link;
    }

    public static FrameworkElement Symbol(string name, double size, bool secondary = false) =>
        Icons.Block(name, size, secondary ? Palette.Secondary : Palette.Primary);
}

/// <summary>A switch or a checkbox: it flips when clicked, or when Space is pressed on it.</summary>
public sealed class Toggle : Border
{
    private readonly bool check;
    private readonly Border knob = new();
    private bool on;

    public event Action<bool>? Changed;

    public Toggle(bool on, string name, bool check = false)
    {
        this.on = on;
        this.check = check;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Arrow;
        System.Windows.Automation.AutomationProperties.SetName(this, name);
        if (check) { Width = 16; Height = 16; CornerRadius = new CornerRadius(4); }
        else
        {
            Width = 36; Height = 20; CornerRadius = new CornerRadius(10);
            knob.Width = 16; knob.Height = 16; knob.CornerRadius = new CornerRadius(8); knob.Margin = new Thickness(2);
            knob.Background = Brushes.White;
            Child = knob;
        }
        MouseLeftButtonUp += (_, _) => Flip();
        KeyDown += (_, e) => { if (e.Key == Key.Space) { Flip(); e.Handled = true; } };
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
        Paint();
    }

    public bool IsOn
    {
        get => on;
        set { on = value; Paint(); }
    }

    /// <summary>Flips it, as a click does.</summary>
    public void Click() => Flip();

    private void Flip()
    {
        if (!IsEnabled) return;
        IsOn = !on;
        Changed?.Invoke(on);
    }

    private void Paint()
    {
        if (check)
        {
            Background = Palette.Brush(on ? Palette.ControlTint : Palette.With(Palette.Primary, 0.08));
            BorderBrush = Palette.Brush(Palette.With(Palette.Primary, on ? 0 : 0.35));
            BorderThickness = new Thickness(1);
            Child = on ? new Path
            {
                Data = Geometry.Parse("M 3.5 8.5 L 6.5 11.5 L 12.5 4.5"), Stroke = Palette.Brush(Palette.OnSelection), StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            } : null;
            return;
        }
        Background = Palette.Brush(on ? Palette.ControlTint : Palette.With(Palette.Primary, 0.22));
        knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}

/// <summary>A row of choices of which one is chosen.</summary>
public sealed class Segmented : Border
{
    public Segmented(IReadOnlyList<string> labels, int selected, Action<int> choose, string name, double? width = null)
    {
        CornerRadius = new CornerRadius(7);
        Background = Palette.Brush(Palette.With(Palette.Primary, 0.08));
        Padding = new Thickness(2);
        if (width is { } w) Width = w;
        System.Windows.Automation.AutomationProperties.SetName(this, name);
        var grid = new Grid();
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var label = Kit.Text(labels[i], TextSize.Callout, weight: FontWeights.Medium, wrap: false);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Equal shares while they fit; a longer label (a translation) takes the room it needs before any is cut.
            grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = Math.Ceiling(label.DesiredSize.Width) + 8 });
            var segment = new Border
            {
                Child = label, Height = 24, CornerRadius = new CornerRadius(5), Padding = new Thickness(4, 0, 4, 0), Cursor = Cursors.Arrow, Focusable = true,
                Background = i == selected ? Palette.Brush(Palette.With(Palette.Primary, 0.2)) : Brushes.Transparent,
            };
            Grid.SetColumn(segment, i);
            System.Windows.Automation.AutomationProperties.SetName(segment, labels[i]);
            segment.MouseLeftButtonUp += (_, _) => { if (index != selected) choose(index); };
            segment.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) choose(index); };
            grid.Children.Add(segment);
        }
        Child = grid;
    }
}

/// <summary>A button showing the chosen value; it opens the list of choices below itself.</summary>
public sealed class DropDown : Border
{
    private readonly Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.None };

    public DropDown(IReadOnlyList<string> labels, int selected, Action<int> choose, string name, double? width = null)
    {
        Height = 28;
        MinWidth = 120;
        if (width is { } w) Width = w;
        CornerRadius = new CornerRadius(7);
        Background = Palette.Brush(Palette.With(Palette.Primary, 0.1));
        BorderBrush = Palette.Brush(Palette.With(Palette.Primary, Motion.IncreaseContrast ? 0.5 : 0.12));
        BorderThickness = new Thickness(Motion.IncreaseContrast ? 1 : 0.5);
        Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(this, name);
        var grid = new Grid { Margin = new Thickness(10, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Kit.Text(selected >= 0 && selected < labels.Count ? labels[selected] : "", TextSize.Callout, weight: FontWeights.Medium, wrap: false);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var chevron = Icons.Block("chevron.down", 11, Palette.Secondary);
        chevron.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(text);
        grid.Children.Add(chevron);
        Child = grid;

        popup.Child = PopupMenu.Build(labels.Select((label, i) => new PopupItem(label, () => { if (i != selected) choose(i); }, i == selected)), () => popup.IsOpen = false);
        popup.PlacementTarget = this;
        MouseLeftButtonUp += (_, _) => popup.IsOpen = !popup.IsOpen;
        KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter or Key.Down) { popup.IsOpen = true; e.Handled = true; } };
    }

    /// <summary>The list of choices, for the tests.</summary>
    public Border Menu => (Border)popup.Child;

    /// <summary>Open, for the tests.</summary>
    public bool IsOpen => popup.IsOpen;
}

/// <summary>A slider whose handle rests on a few steps.</summary>
public sealed class StepSlider : FrameworkElement
{
    private readonly int steps;
    private int value;
    private readonly Action<int> changed;

    public StepSlider(int steps, int value, Action<int> changed, string name)
    {
        this.steps = steps;
        this.value = value;
        this.changed = changed;
        Height = 22;
        Focusable = true;
        Cursor = Cursors.Arrow;
        System.Windows.Automation.AutomationProperties.SetName(this, name);
    }

    public int Value => value;

    private double X(int step) => 9 + (ActualWidth - 18) * step / Math.Max(1, steps - 1);

    private void Set(int next)
    {
        next = Math.Clamp(next, 0, steps - 1);
        if (next == value) return;
        value = next;
        InvalidateVisual();
        changed(next);
    }

    private void Follow(MouseEventArgs e) => Set((int)Math.Round((e.GetPosition(this).X - 9) / Math.Max(1, ActualWidth - 18) * (steps - 1)));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); Follow(e); }

    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Follow(e); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Right or Key.Up) Set(value + 1);
        else if (e.Key is Key.Left or Key.Down) Set(value - 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var y = ActualHeight / 2;
        dc.DrawRoundedRectangle(Palette.Brush(Palette.With(Palette.Primary, 0.2)), null, new Rect(9, y - 2, ActualWidth - 18, 4), 2, 2);
        dc.DrawRoundedRectangle(Palette.Brush(Palette.ControlTint), null, new Rect(9, y - 2, X(value) - 9, 4), 2, 2);
        for (var i = 0; i < steps; i++) dc.DrawEllipse(Palette.Brush(Palette.With(Palette.Primary, 0.35)), null, new Point(X(i), y), 1.5, 1.5);
        dc.DrawEllipse(Brushes.White, new Pen(Palette.Brush(Color.FromArgb(70, 0, 0, 0)), 0.5), new Point(X(value), y), 8, 8);
    }
}

/// <summary>A one-line text box with a prompt shown while it is empty.</summary>
public sealed class Field : Border
{
    public TextBox Box { get; }

    public Field(string text, string prompt, Action<string> changed, string name)
    {
        Height = 28;
        CornerRadius = new CornerRadius(7);
        Background = Palette.Brush(Palette.With(Palette.Primary, 0.06));
        BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.2));
        BorderThickness = new Thickness(0.5);
        Box = new TextBox
        {
            Text = text, FontFamily = Fonts.Inter, FontSize = 13, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Palette.Brush(Palette.Primary), CaretBrush = Palette.Brush(Palette.Primary), SelectionBrush = Palette.Brush(Palette.ControlTint),
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(Box, name);
        var hint = Kit.Text(prompt, 13, secondary: true, wrap: false);
        hint.Margin = new Thickness(9, 0, 8, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        hint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Box.TextChanged += (_, _) => { hint.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; changed(Box.Text); };
        var grid = new Grid();
        grid.Children.Add(hint);
        grid.Children.Add(Box);
        Child = grid;
    }
}

/// <summary>A list of progress: a thin bar.</summary>
public sealed class Bar : Border
{
    private readonly Border fill = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };

    public Bar(double fraction)
    {
        Height = 4;
        CornerRadius = new CornerRadius(2);
        Background = Palette.Brush(Palette.With(Palette.Primary, 0.15));
        fill.Background = Palette.Brush(Palette.Accent);
        Child = fill;
        SizeChanged += (_, _) => Set(Fraction);
        Set(fraction);
    }

    public double Fraction { get; private set; }

    public void Set(double fraction)
    {
        Fraction = Math.Clamp(fraction, 0, 1);
        fill.Width = ActualWidth * Fraction;
    }
}

/// <summary>A key as drawn on a keyboard: used in the help for the keys that open the wheels.</summary>
public sealed class KeyCap : Border
{
    public string Label { get; }

    public KeyCap(string text)
    {
        Label = text;
        var label = Kit.Text(text, 13, weight: FontWeights.Medium, wrap: false);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        MinWidth = 30;
        MinHeight = 26;
        Padding = new Thickness(8, 0, 8, 0);
        CornerRadius = new CornerRadius(6);
        Background = Palette.Brush(Palette.Over(Palette.With(Palette.Primary, 0.1), Palette.WheelSurface));
        BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.2));
        BorderThickness = new Thickness(0.5);
        Effect = new DropShadowEffect { ShadowDepth = 1, Direction = 270, BlurRadius = 0, Opacity = 0.12, Color = Colors.Black };
        Child = label;
    }

    /// <summary>The key or keys of a choice, as caps: "Ctrl + Alt" is two caps, "Off" a dash.</summary>
    public static FrameworkElement For(ModifierChoice choice)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        string[] parts = choice switch
        {
            ModifierChoice.Off => ["—"],
            ModifierChoice.CtrlAlt => ["Ctrl", "Alt"],
            _ => [choice.Label()],
        };
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) { var plus = Kit.Text("+", 12, secondary: true, wrap: false); plus.Margin = new Thickness(4, 0, 4, 0); plus.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(plus); }
            row.Children.Add(new KeyCap(parts[i]));
        }
        return row;
    }
}

/// <summary>Split's mark on a dark tile: what the help and About windows show as the app's icon.</summary>
public static class LogoTile
{
    public static FrameworkElement Make(double side)
    {
        var canvas = new Canvas { Width = side, Height = side };
        canvas.Children.Add(new Rectangle { Width = side, Height = side, RadiusX = side * 0.23, RadiusY = side * 0.23, Fill = Palette.Brush(Color.FromRgb(28, 28, 30)) });
        for (var i = 0; i < LogoGeometry.Slices; i++)
        {
            var alpha = i == 0 ? 1 : 0.4;
            canvas.Children.Add(new Path
            {
                Data = Logo.Slice(i, side / 2, side / 2, side * 0.36),
                Fill = Palette.Brush(Color.FromArgb((byte)(alpha * 255), 255, 255, 255)),
            });
        }
        return canvas;
    }
}
