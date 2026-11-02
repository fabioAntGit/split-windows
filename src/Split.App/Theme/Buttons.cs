using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Split.App;

public enum ButtonKind { Primary, Secondary, Tool }

/// <summary>
/// The three buttons of the interface. Primary is the selection's colours, semibold; Secondary a light
/// fill with a hairline edge, so it reads on every surface; Tool a square icon button for palettes, the
/// chosen one turning to the selection's colours. Each answers a press at once, shrinking a little in a
/// tenth of a second and letting go the same way (with Reduce Motion it dims instead).
/// </summary>
public sealed class SplitButton : Button
{
    private readonly ButtonKind kind;
    private readonly bool compact;
    private readonly bool selected;
    private readonly Border root;
    private readonly ScaleTransform scale = new(1, 1);

    public SplitButton(object content, ButtonKind kind = ButtonKind.Secondary, bool compact = false, bool selected = false)
    {
        this.kind = kind;
        this.compact = compact;
        this.selected = selected;
        Content = content;
        FontFamily = Fonts.Inter;
        Focusable = false;
        Cursor = System.Windows.Input.Cursors.Arrow;
        var contrast = Motion.IncreaseContrast;

        (FontSize, FontWeight, Foreground, Padding, MinHeight) = kind switch
        {
            ButtonKind.Primary => (13.0, FontWeights.SemiBold, Palette.Brush(Palette.OnSelection), new Thickness(14, 0, 14, 0), 28.0),
            ButtonKind.Secondary => (compact ? 12.0 : 13.0, FontWeights.Medium, Palette.Brush(Palette.Primary),
                                     new Thickness(compact ? 10 : 12, 0, compact ? 10 : 12, 0), compact ? 24.0 : 28.0),
            _ => (14.0, FontWeights.Medium, Palette.Brush(selected ? Palette.OnSelection : Palette.Primary), new Thickness(0), 28.0),
        };
        if (kind == ButtonKind.Tool) Width = 30;

        var border = new FrameworkElementFactory(typeof(Border), "root");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.PaddingProperty, Padding);
        border.SetValue(MinHeightProperty, MinHeight);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(kind == ButtonKind.Secondary && !contrast ? 0.5 : 1));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        ApplyTemplate();
        root = (Border)Template.FindName("root", this);
        root.RenderTransform = scale;
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        Paint(false);
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
    }

    /// <summary>A template with no look of its own (no fill on hover or press), for a button that is only its content.</summary>
    public static ControlTemplate Plain() => new(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };

    /// <summary>The fill and edge for the state; also what the tests read.</summary>
    private void Paint(bool pressed)
    {
        var contrast = Motion.IncreaseContrast;
        Color fill, edge = Colors.Transparent;
        switch (kind)
        {
            case ButtonKind.Primary:
                fill = Palette.Selection;
                if (contrast) edge = Palette.With(Palette.Primary, 0.6);
                break;
            case ButtonKind.Secondary:
                fill = Palette.With(Palette.Primary, pressed ? 0.18 : 0.1);
                edge = Palette.With(Palette.Primary, contrast ? 0.5 : 0.12);
                break;
            default:
                fill = selected ? Palette.Selection : Palette.With(Palette.Primary, pressed ? 0.14 : 0);
                break;
        }
        root.Background = Palette.Brush(fill);
        root.BorderBrush = Palette.Brush(edge);
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsPressedChanged(e);
        Paint(IsPressed);
        var pressed = IsPressed;
        if (Motion.ReduceMotion)
        {
            root.Opacity = pressed ? 0.92 : 1;
            return;
        }
        var target = pressed ? (kind == ButtonKind.Tool ? 0.94 : 0.97) : 1;
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(100)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>The border the button draws, for the tests and for captures.</summary>
    public Border Face => root;
}
