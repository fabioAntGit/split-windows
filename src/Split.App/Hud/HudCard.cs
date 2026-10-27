using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ellipse = System.Windows.Shapes.Ellipse;
using ShapePath = System.Windows.Shapes.Path;

namespace Split.App;

public enum HudState { Progress, Success, Warning, Failure, Info }

public sealed class HudModel
{
    public HudState State { get; set; } = HudState.Info;
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public double? Fraction { get; set; }
    public IReadOnlyList<string> Outputs { get; set; } = [];
    public IReadOnlyList<JobFailure> Failures { get; set; } = [];
    public bool Hovering { get; set; }
    /// <summary>The job behind the progress card, so it can be cancelled from here.</summary>
    public Guid? JobId { get; set; }
    /// <summary>The finished job whose results "Undo" moves to the Recycle Bin; null when there is nothing to undo.</summary>
    public Guid? UndoJob { get; set; }
    /// <summary>Raised after the model changed, so the card is drawn again.</summary>
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();
}

public sealed record HudActions(Action Reveal, Action Open, Action Details, Action Activity, Action Cancel, Action Undo, Action Close);

/// <summary>
/// The notification card: progress, results and explained errors. Closing it never loses anything: every
/// job stays listed in Settings › Activity.
/// </summary>
public sealed class HudCard : Border
{
    /// <summary>Room around the card for the close button, which sits on its corner, and for its shadow.</summary>
    public const double Inset = 8;

    public const double CardWidth = 344;

    private readonly HudModel model;
    private readonly HudActions actions;
    private readonly Border card = new();
    private readonly Grid content = new();
    private readonly Button close;

    public HudCard(HudModel model, HudActions actions)
    {
        this.model = model;
        this.actions = actions;
        Padding = new Thickness(Inset);
        Child = card;
        card.Width = CardWidth;
        var root = new Grid();
        card.Child = root;
        card.CornerRadius = new CornerRadius(16);
        card.Background = Brushes.Transparent;
        card.MouseEnter += (_, _) => Hover(true);
        card.MouseLeave += (_, _) => Hover(false);
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (model.State == HudState.Success || (model.State == HudState.Warning && model.Failures.Count == 0)) actions.Reveal();
        };
        close = CloseButton();
        root.Children.Add(content);
        root.Children.Add(close);
        // Raised from any thread that reports on a job; the card is only touched from its own.
        model.Changed += () => { if (Dispatcher.CheckAccess()) Rebuild(); else Dispatcher.BeginInvoke(Rebuild); };
        Rebuild();
    }

    public event Action<bool>? HoverChanged;

    public Button CloseButtonElement => close;

    private void Hover(bool hovering)
    {
        model.Hovering = hovering;
        close.Visibility = hovering && model.State != HudState.Progress ? Visibility.Visible : Visibility.Collapsed;
        HoverChanged?.Invoke(hovering);
    }

    private Button CloseButton()
    {
        var glyph = Icons.Block("xmark", 8);
        glyph.FontWeight = FontWeights.Bold;
        var button = new Button
        {
            Width = 18,
            Height = 18,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-6),
            Visibility = Visibility.Collapsed,
            ToolTip = L("Close"),
            Cursor = System.Windows.Input.Cursors.Arrow,
            Template = Plain(),
            Content = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = Palette.Brush(Palette.WheelSurface),
                BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.15)),
                BorderThickness = new Thickness(0.5),
                Child = glyph,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L("Close"));
        button.Click += (_, _) => actions.Close();
        return button;
    }

    private static ControlTemplate Plain()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        return new ControlTemplate(typeof(Button)) { VisualTree = presenter };
    }

    private void Rebuild()
    {
        var contrast = Motion.IncreaseContrast;
        card.Background = Palette.Brush(Palette.Floating);
        card.BorderBrush = Palette.Brush(Palette.With(Palette.Primary, contrast ? 0.4 : 0.1));
        card.BorderThickness = new Thickness(contrast ? 1 : 0.5);
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 7, ShadowDepth = 1.5, Direction = 270, Opacity = 0.3, Color = Colors.Black };

        content.Children.Clear();
        content.ColumnDefinitions.Clear();
        content.Margin = new Thickness(14);
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = StatusGlyph(model.State, model.Fraction);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        glyph.Margin = new Thickness(0, 1, 12, 0);
        content.Children.Add(glyph);

        var text = new StackPanel { Orientation = Orientation.Vertical };
        Grid.SetColumn(text, 1);
        var title = Fonts.Text(model.Title, 13, FontWeights.SemiBold);
        Lines(title, 13, 16, 2);
        text.Children.Add(title);
        if (model.Detail.Length > 0)
        {
            var detail = Fonts.Text(model.Detail, 12, color: Palette.Secondary);
            Lines(detail, 12, 15, 5);
            detail.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(detail);
        }
        if (model.State == HudState.Progress)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            if (model.JobId != null) row.Children.Add(Button(L("Cancel"), actions.Cancel, first: true));
            row.Children.Add(Button(L("Show Activity"), actions.Activity, first: model.JobId == null));
            text.Children.Add(row);
        }
        else if (HasActions)
        {
            // Narrower languages get every button; otherwise "Open" gives way first.
            var row = ResultButtons(includeOpen: true);
            row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (row.DesiredSize.Width > CardWidth - 28 - 30 - 12) row = ResultButtons(includeOpen: false);
            row.Margin = new Thickness(0, 6, 0, 0);
            text.Children.Add(row);
        }
        content.Children.Add(text);
        close.Visibility = model.Hovering && model.State != HudState.Progress ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A text of at most <paramref name="lines"/> lines, ending in "…" when it is longer.</summary>
    private static void Lines(TextBlock block, double size, double lineHeight, int lines)
    {
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.LineHeight = lineHeight;
        block.MaxHeight = lineHeight * lines;
    }

    private bool HasActions => model.State != HudState.Progress && (model.Outputs.Count > 0 || model.Failures.Count > 0);

    private StackPanel ResultButtons(bool includeOpen)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var first = true;
        void Add(string label, Action click, string? tip = null)
        {
            var button = Button(label, click, first);
            if (tip != null) button.ToolTip = tip;
            row.Children.Add(button);
            first = false;
        }
        if (model.Failures.Count > 0) Add(L("Details…"), actions.Details);
        if (model.Outputs.Count > 0)
        {
            Add(L("Show in File Explorer"), actions.Reveal);
            if (includeOpen && model.Outputs.Count == 1 && model.Failures.Count == 0) Add(L("Open"), actions.Open);
        }
        if (model.UndoJob != null) Add(L("Undo"), actions.Undo, L("Move the new files to the Recycle Bin"));
        return row;
    }

    private static SplitButton Button(string label, Action click, bool first)
    {
        var button = new SplitButton(label) { Margin = new Thickness(first ? 0 : 8, 0, 0, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A progress ring while working; a symbol for the outcome.</summary>
    private static FrameworkElement StatusGlyph(HudState state, double? fraction)
    {
        var grid = new Grid { Width = 30, Height = 30 };
        switch (state)
        {
            case HudState.Progress when fraction is { } value:
                grid.Children.Add(new Ellipse { Stroke = Palette.Brush(Palette.With(Palette.Primary, 0.12)), StrokeThickness = 3, Margin = new Thickness(1.5) });
                grid.Children.Add(Arc(Math.Max(0.02, Math.Min(1, value))));
                var percent = Fonts.Text($"{(int)(value * 100)}", 9, FontWeights.SemiBold, Palette.Secondary);
                percent.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
                percent.HorizontalAlignment = HorizontalAlignment.Center;
                percent.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(percent);
                break;
            case HudState.Progress:
                grid.Children.Add(Spinner());
                break;
            case HudState.Success: grid.Children.Add(Symbol("checkmark.circle.fill", Palette.Accent)); break;
            case HudState.Warning: grid.Children.Add(Symbol("exclamationmark.triangle.fill", Palette.Primary)); break;
            case HudState.Failure: grid.Children.Add(Symbol("xmark.octagon.fill", Palette.Primary)); break;
            default: grid.Children.Add(Symbol("info.circle.fill", Palette.Accent)); break;
        }
        return grid;
    }

    private static UIElement Symbol(string name, Color color)
    {
        var symbol = Icons.Block(name, 24, color);
        if (!Motion.ReduceMotion)
        {
            // The outcome arrives small and settles.
            var scale = new ScaleTransform(0.6, 0.6);
            symbol.RenderTransform = scale;
            symbol.RenderTransformOrigin = new Point(0.5, 0.5);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
            symbol.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        }
        return symbol;
    }

    /// <summary>The finished share of the ring, from twelve o'clock clockwise, with round ends.</summary>
    private static ShapePath Arc(double fraction)
    {
        const double radius = 13.5, centre = 15;
        var end = fraction >= 0.9999 ? 0.9999 : fraction;
        var angle = end * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(centre, centre - radius), false, false);
            context.ArcTo(new Point(centre + radius * Math.Sin(angle), centre - radius * Math.Cos(angle)), new Size(radius, radius), 0, angle > Math.PI,
                          SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return new ShapePath
        {
            Data = geometry,
            Stroke = Palette.Brush(Palette.Accent),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    /// <summary>Work of unknown length: a quarter of the ring turning.</summary>
    private static UIElement Spinner()
    {
        var arc = Arc(0.25);
        arc.Margin = new Thickness(0);
        var turn = new RotateTransform(0, 15, 15);
        arc.RenderTransform = turn;
        if (!Motion.ReduceMotion) turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        return arc;
    }
}
