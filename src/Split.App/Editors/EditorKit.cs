using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Split.App;

/// <summary>
/// Every editor's window: the work area on the left, a grouped inspector on the right, the buttons at the
/// bottom right (the primary action first, then Cancel). Closing from the title bar with edits asks first; Cancel and
/// Esc throw them away without asking. Ctrl+Z and Ctrl+Shift+Z undo and redo where the editor keeps a history.
/// </summary>
public abstract class EditorWindow : AppWindow
{
    public static readonly Size Size = new(1120, 740);
    public static readonly Size Smallest = new(780, 500);

    /// <summary>Asks whether to throw the edits away; the tests replace it so no dialog opens.</summary>
    public static Func<Window, bool> ConfirmDiscard { get; set; } = owner => DiscardDialog.Ask(owner);

    private bool discarding;

    protected EditorWindow(string title) : base(title, Size, resizable: true)
    {
        MinWidth = Smallest.Width;
        MinHeight = Smallest.Height + TitleBarHeight;
        Closing += OnClosing;
        PreviewKeyDown += OnKey;
    }

    protected override bool ClosesOnEscape => true;

    protected override void Dismiss() => CloseNow();

    /// <summary>True when closing would lose work.</summary>
    protected abstract bool HasChanges { get; }

    protected virtual void Undo() { }

    protected virtual void Redo() { }

    /// <summary>What Enter does outside a text box: the primary button.</summary>
    protected virtual void Accept() { }

    /// <summary>Closes without asking, as Cancel does after the work was handed to a job or abandoned on purpose.</summary>
    public void CloseNow()
    {
        discarding = true;
        Close();
    }

    /// <summary>Closing the window by any road asks first when there are edits, for the tests to drive.</summary>
    public bool IsDiscarding => discarding;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (discarding || !HasChanges) return;
        if (ConfirmDiscard(this)) discarding = true; else e.Cancel = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox;
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (control && e.Key == Key.Z && !typing)
        {
            if (shift) Redo(); else Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !typing && Keyboard.FocusedElement is not ButtonBase && Keyboard.Modifiers == ModifierKeys.None)
        {
            Accept();
            e.Handled = true;
        }
    }
}

/// <summary>"Discard the changes?" with the destructive choice first, as the Windows order puts the main action first.</summary>
internal sealed class DiscardDialog : AppWindow
{
    private bool discard;

    private DiscardDialog(Window owner) : base(L("Discard the changes?"), new Size(380, 0), canMinimize: false, sizeToHeight: true)
    {
        Owner = owner;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(Kit.Text(L("Discard the changes?"), TextSize.Title3, weight: FontWeights.SemiBold));
        var detail = Kit.Text(L("The edits haven’t been saved and will be lost."), TextSize.Callout, secondary: true);
        detail.Margin = new Thickness(0, 6, 0, 18);
        stack.Children.Add(detail);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var yes = new SplitButton(L("Discard"), ButtonKind.Primary);
        yes.Click += (_, _) => { discard = true; Close(); };
        var no = new SplitButton(L("Cancel"));
        no.Margin = new Thickness(8, 0, 0, 0);
        no.Click += (_, _) => Close();
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        stack.Children.Add(buttons);
        Body = stack;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { discard = true; Close(); e.Handled = true; } };
    }

    protected override bool ClosesOnEscape => true;

    public static bool Ask(Window owner)
    {
        var dialog = new DiscardDialog(owner);
        dialog.ShowDialog();
        return dialog.discard;
    }
}

/// <summary>The editor's frame: work area, inspector and buttons.</summary>
public sealed class EditorLayout : Grid
{
    public const double InspectorWidth = 290;

    public SplitButton Save { get; }
    public SplitButton? Reset { get; }
    public SplitButton Cancel { get; }

    public EditorLayout(UIElement work, IEnumerable<UIElement> sections, string saveTitle, Action? reset, Action cancel, Action save)
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InspectorWidth) });

        var area = new Border { Child = work, Background = Palette.Brush(Color.FromArgb(56, 0, 0, 0)), ClipToBounds = true };
        Children.Add(area);
        var divider = new Border { Background = Palette.Brush(Palette.With(Palette.Primary, 0.1)) };
        SetColumn(divider, 1);
        Children.Add(divider);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetColumn(right, 2);
        var stack = new StackPanel { Margin = new Thickness(16, 16, 16, 0) };
        foreach (var section in sections) stack.Children.Add(section);
        right.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        var buttons = new DockPanel { Margin = new Thickness(12) };
        var rule = Kit.Line();
        var footer = new StackPanel();
        footer.Children.Add(rule);
        footer.Children.Add(buttons);
        SetRow(footer, 1);
        right.Children.Add(footer);
        if (reset is not null)
        {
            Reset = new SplitButton(L("Reset"));
            Reset.Click += (_, _) => reset();
            DockPanel.SetDock(Reset, Dock.Left);
            buttons.Children.Add(Reset);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Save = new SplitButton(saveTitle, ButtonKind.Primary);
        Save.Click += (_, _) => save();
        Cancel = new SplitButton(L("Cancel")) { Margin = new Thickness(8, 0, 0, 0) };
        Cancel.Click += (_, _) => cancel();
        actions.Children.Add(Save);
        actions.Children.Add(Cancel);
        buttons.Children.Add(actions);
        Children.Add(right);
    }
}

/// <summary>An editor's work area: a Skia surface drawn in device-independent units, with the picture cached as an image.</summary>
public abstract class EditorCanvas : SKElement
{
    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private SKBitmap? shownBitmap;
    private SKImage? shownImage;

    protected EditorCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    public NSz Area => new(ActualWidth, ActualHeight);

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var scale = ActualWidth > 0 ? e.Info.Width / ActualWidth : 1;
        canvas.ResetMatrix();
        canvas.Scale((float)scale);
        Paint(canvas, Area);
    }

    protected abstract void Paint(SKCanvas canvas, NSz size);

    protected void DrawPicture(SKCanvas canvas, SKBitmap bitmap, NBox frame, bool checkerboard = false)
    {
        if (!ReferenceEquals(bitmap, shownBitmap))
        {
            shownImage?.Dispose();
            shownImage = SKImage.FromBitmap(bitmap);
            shownBitmap = bitmap;
        }
        if (checkerboard) DrawChecker(canvas, frame);
        canvas.DrawImage(shownImage, Rect(frame), Smooth);
    }

    /// <summary>The pale squares that show through the transparent parts of a picture.</summary>
    public static void DrawChecker(SKCanvas canvas, NBox frame)
    {
        const float tile = 8;
        using var light = new SKPaint { Color = new SKColor(235, 235, 235) };
        using var dark = new SKPaint { Color = new SKColor(204, 204, 204) };
        canvas.Save();
        canvas.ClipRect(Rect(frame));
        canvas.DrawRect(Rect(frame), light);
        var row = 0;
        for (var y = (float)frame.MinY; y < frame.MaxY; y += tile, row++)
        {
            for (var x = (float)frame.MinX + (row % 2 == 0 ? 0 : tile); x < frame.MaxX; x += tile * 2) canvas.DrawRect(x, y, tile, tile, dark);
        }
        canvas.Restore();
    }

    public static SKRect Rect(NBox box) => new((float)box.MinX, (float)box.MinY, (float)box.MaxX, (float)box.MaxY);

    public static SKColor Sk(Color color) => new(color.R, color.G, color.B, color.A);

    protected NPt Position(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        return new NPt(point.X, point.Y);
    }
}

/// <summary>A slider that moves smoothly between two values, in the app's colours.</summary>
public sealed class SliderBar : FrameworkElement
{
    private readonly double minimum, maximum;
    private double value;

    public event Action<double>? Changed;

    public SliderBar(double value, double minimum, double maximum, string name)
    {
        (this.value, this.minimum, this.maximum) = (Math.Clamp(value, minimum, maximum), minimum, maximum);
        Height = 22;
        Focusable = true;
        Cursor = Cursors.Arrow;
        System.Windows.Automation.AutomationProperties.SetName(this, name);
    }

    public double Value
    {
        get => value;
        set
        {
            this.value = Math.Clamp(value, minimum, maximum);
            InvalidateVisual();
        }
    }

    private double X(double v) => 9 + (ActualWidth - 18) * (v - minimum) / (maximum - minimum);

    public void Move(double next)
    {
        next = Math.Clamp(next, minimum, maximum);
        if (next == value) return;
        value = next;
        InvalidateVisual();
        Changed?.Invoke(next);
    }

    private void Follow(MouseEventArgs e) => Move(minimum + (e.GetPosition(this).X - 9) / Math.Max(1, ActualWidth - 18) * (maximum - minimum));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); Follow(e); }

    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Follow(e); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = (maximum - minimum) / 50;
        if (e.Key is Key.Right or Key.Up) Move(value + step);
        else if (e.Key is Key.Left or Key.Down) Move(value - step);
        else return;
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var y = ActualHeight / 2;
        dc.DrawRoundedRectangle(Palette.Brush(Palette.With(Palette.Primary, 0.2)), null, new Rect(9, y - 2, Math.Max(0, ActualWidth - 18), 4), 2, 2);
        dc.DrawRoundedRectangle(Palette.Brush(Palette.ControlTint), null, new Rect(9, y - 2, Math.Max(0, X(value) - 9), 4), 2, 2);
        dc.DrawEllipse(Brushes.White, new Pen(Palette.Brush(Color.FromArgb(70, 0, 0, 0)), 0.5), new Point(X(value), y), 8, 8);
    }
}

/// <summary>An inspector row with a slider and its value, the label on the left.</summary>
public sealed class ValueSlider : StackPanel
{
    private readonly TextBlock shown;
    private readonly Func<double, string> format;

    public SliderBar Bar { get; }

    public ValueSlider(string title, double value, double minimum, double maximum, Func<double, string>? format, Action<double> changed)
    {
        this.format = format ?? Percent;
        Margin = new Thickness(12, 8, 12, 8);
        var top = new Grid();
        top.Children.Add(Kit.Text(title, TextSize.Body, wrap: false));
        shown = Kit.Text(this.format(value), TextSize.Subheadline, secondary: true, wrap: false);
        shown.FontFamily = new FontFamily("Consolas");
        shown.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(shown);
        Children.Add(top);
        Bar = new SliderBar(value, minimum, maximum, title) { Margin = new Thickness(0, 4, 0, 0) };
        Bar.Changed += v => { shown.Text = this.format(v); changed(v); };
        Children.Add(Bar);
    }

    public string Shown => shown.Text;

    public static string Percent(double v) => $"{(int)Math.Round(v * 100)}%";

    /// <summary>A value with a sign, scaled: "+25", "−1.0 EV".</summary>
    public static Func<double, string> Signed(string unit, double factor, int decimals = 0) => v =>
    {
        var scaled = v * factor;
        var text = decimals > 0 ? scaled.ToString("F" + decimals, CultureInfo.InvariantCulture) : Math.Round(scaled, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        return (scaled > 0.0001 ? "+" : "") + text + unit;
    };
}

/// <summary>A square swatch for colours and gradients; the chosen one gets a ring.</summary>
public sealed class Swatch : Border
{
    public Swatch(Brush fill, bool selected, string help, Action click, double size = 30)
    {
        Width = size + 6;
        Height = size + 6;
        CornerRadius = new CornerRadius(9);
        BorderThickness = new Thickness(2);
        BorderBrush = selected ? Palette.Brush(Palette.Accent) : Brushes.Transparent;
        Padding = new Thickness(1);
        ToolTip = help;
        Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(this, help);
        Child = new Border
        {
            CornerRadius = new CornerRadius(7), Background = fill,
            BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.12)), BorderThickness = new Thickness(0.5),
        };
        MouseLeftButtonUp += (_, _) => click();
        KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) click(); };
    }

    public bool IsSelected => BorderBrush is SolidColorBrush { Color.A: > 0 };
}

/// <summary>A button that shows the chosen value and opens a menu of choices; unlike a plain drop-down it can hold separators and headings.</summary>
public sealed class Picker : Border
{
    private readonly Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.None };

    public Picker(string current, IEnumerable<PopupItem> items, string name, double? width = null)
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
        var text = Kit.Text(current, TextSize.Callout, weight: FontWeights.Medium, wrap: false);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var chevron = Icons.Block("chevron.down", 11, Palette.Secondary);
        chevron.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(text);
        grid.Children.Add(chevron);
        Child = grid;
        Shown = current;
        // A long list scrolls instead of running off the screen.
        var menu = PopupMenu.Build(items, () => popup.IsOpen = false);
        popup.Child = new ScrollViewer { Content = menu, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        popup.PlacementTarget = this;
        MouseLeftButtonUp += (_, _) => popup.IsOpen = !popup.IsOpen;
        KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter or Key.Down) { popup.IsOpen = true; e.Handled = true; } };
    }

    public string Shown { get; }
}

/// <summary>A row "label … colour": the colour opens a small mixer of red, green and blue, and a hex code.</summary>
public sealed class ColorRow : Grid
{
    private readonly Border chip = new() { Width = 28, Height = 20, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0.5) };
    private readonly Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.None };
    private SKColor color;
    private readonly SliderBar[] bars = new SliderBar[3];
    private readonly TextBox hex;
    private bool updating;

    public ColorRow(string title, SKColor color, Action<SKColor> changed)
    {
        this.color = color;
        Margin = new Thickness(12, 4, 12, 4);
        MinHeight = 36;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Kit.Text(title, TextSize.Body, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(label);
        chip.BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.3));
        chip.VerticalAlignment = VerticalAlignment.Center;
        chip.Cursor = Cursors.Hand;
        SetColumn(chip, 1);
        Children.Add(chip);
        System.Windows.Automation.AutomationProperties.SetName(chip, title);

        var mixer = new StackPanel { Width = 220, Margin = new Thickness(10) };
        string[] names = [L("Red"), L("Green"), L("Blue")];
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            bars[i] = new SliderBar(Channel(i), 0, 255, names[i]);
            bars[i].Changed += v =>
            {
                if (updating) return;
                byte[] c = [color.Red, color.Green, color.Blue];
                c[index] = (byte)Math.Round(v);
                Set(new SKColor(c[0], c[1], c[2]), changed);
            };
            mixer.Children.Add(Kit.Text(names[i], TextSize.Subheadline, secondary: true, wrap: false));
            mixer.Children.Add(bars[i]);
        }
        hex = new TextBox
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(6, 3, 6, 3),
            Background = Palette.Brush(Palette.With(Palette.Primary, 0.06)), Foreground = Palette.Brush(Palette.Primary), CaretBrush = Palette.Brush(Palette.Primary),
            BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.2)), BorderThickness = new Thickness(0.5),
        };
        System.Windows.Automation.AutomationProperties.SetName(hex, "Hex");
        hex.TextChanged += (_, _) =>
        {
            if (updating) return;
            var text = hex.Text.Trim().TrimStart('#');
            if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                Set(new SKColor((byte)(value >> 16), (byte)(value >> 8), (byte)value), changed);
        };
        mixer.Children.Add(hex);
        popup.Child = new Border
        {
            Child = mixer, CornerRadius = new CornerRadius(8), Margin = new Thickness(8),
            Background = Palette.Brush(Palette.Over(Palette.With(Palette.Primary, 0.06), Palette.WheelSurface)),
            BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.18)), BorderThickness = new Thickness(0.5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3 },
        };
        popup.PlacementTarget = chip;
        chip.MouseLeftButtonUp += (_, _) => popup.IsOpen = !popup.IsOpen;
        Paint();
    }

    public SKColor Color => color;

    private byte Channel(int i) => i == 0 ? color.Red : i == 1 ? color.Green : color.Blue;

    private void Set(SKColor next, Action<SKColor> changed)
    {
        color = next;
        Paint();
        changed(next);
    }

    private void Paint()
    {
        updating = true;
        chip.Background = Palette.Brush(System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue));
        for (var i = 0; i < 3; i++) bars[i].Value = Channel(i);
        if (!hex.IsKeyboardFocusWithin) hex.Text = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        updating = false;
    }
}

public static class EditorParts
{
    /// <summary>A square icon button of a palette; the chosen one turns to the selection's colours.</summary>
    public static SplitButton Tool(string symbol, string help, Action click, bool selected = false)
    {
        var button = new SplitButton(Icons.Block(symbol, 15, selected ? Palette.OnSelection : Palette.Primary), ButtonKind.Tool, selected: selected) { ToolTip = help };
        System.Windows.Automation.AutomationProperties.SetName(button, help);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A line of the inspector's note under its controls: explicit, never a silent no-op.</summary>
    public static TextBlock Note(string text) => Kit.Text(text, TextSize.Subheadline, secondary: true);

    /// <summary>"Original … 1200 × 800": a label and a value in one row of a group.</summary>
    public static Grid Pair(string label, string value, out TextBlock shown)
    {
        shown = Kit.Text(value, TextSize.Body, secondary: true, wrap: false);
        return Kit.Row(Kit.Text(label, TextSize.Body, wrap: false), shown);
    }

    public static Grid Pair(string label, string value) => Pair(label, value, out _);

    /// <summary>A group of rows under a heading, as the inspector shows them.</summary>
    public static StackPanel Group(string? header, string? footer, params UIElement[] rows) => Kit.Section(header, Kit.Group(rows), footer);

    public static string Dimensions(double width, double height) => $"{(int)width} × {(int)height}";
}
