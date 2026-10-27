using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.WPF;

namespace Split.App;

/// <summary>The collage as it will be saved, on a checkerboard where it is transparent.</summary>
public sealed class CollageCanvas : EditorCanvas
{
    public CollageCanvas(CollageModel model)
    {
        Model = model;
    }

    public CollageModel Model { get; }

    protected override void Paint(SKCanvas canvas, NSz size)
    {
        if (Model.Rendered is not { } rendered) return;
        var frame = Fitting.Fit(new NSz(rendered.Width, rendered.Height), size, 28);
        var transparent = Model.Style.Background == CollageFill.Transparent;
        if (!transparent)
        {
            using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 31), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4) };
            canvas.DrawRect(Rect(frame with { Y = frame.Y + 3 }), shadow);
        }
        DrawPicture(canvas, rendered, frame, transparent);
    }
}

/// <summary>Collage: several pictures arranged into one.</summary>
public sealed class CollageEditor : EditorWindow
{
    private const double RowHeight = 36;

    private readonly CollageModel model;
    private CollageCanvas canvas = null!;
    private EditorLayout layout;
    private StackPanel order = new();

    public CollageEditor(CollageModel model, string title) : base(title)
    {
        this.model = model;
        model.Changed += Sync;
        (canvas, layout) = Compose();
        Body = layout;
    }

    public CollageModel Model => model;
    public CollageCanvas Canvas => canvas;
    public EditorLayout Layout => layout;

    /// <summary>The rows of the order list, top to bottom, for the tests.</summary>
    public IReadOnlyList<UIElement> OrderRows => order.Children.Cast<UIElement>().ToList();

    /// <summary>Closing never asks: a collage is a quick arrangement, not work to keep.</summary>
    protected override bool HasChanges => false;

    private void Sync() => Dispatcher.BeginInvoke(() => canvas?.InvalidateVisual());

    protected override void Rebuild()
    {
        (canvas, layout) = Compose();
        Body = layout;
    }

    protected override void Accept() { if (layout.Save.IsEnabled) Save(); }

    private void Save()
    {
        model.Save();
        CloseNow();
    }

    private void Set(Func<CollageSpec, CollageSpec> change) => model.Style = change(model.Style);

    private (CollageCanvas, EditorLayout) Compose()
    {
        var area = new CollageCanvas(model);
        var style = model.Style;

        var layouts = new Segmented(CollageLayouts.All.Select(l => l.Title()).ToList(), CollageLayouts.All.ToList().IndexOf(style.Layout),
                                    i => { Set(s => s with { Layout = CollageLayouts.All[i] }); Refresh(); }, L("Layout"), symbols: CollageLayouts.All.Select(l => l.Symbol()).ToList());
        layouts.Margin = new Thickness(12, 10, 12, 8);
        var shape = new List<UIElement> { layouts };
        if (style.Layout == CollageLayout.Grid)
        {
            var fill = new Toggle(style.Fill, L("Fill the cells (crops)"));
            fill.Changed += on => Set(s => s with { Fill = on });
            shape.Add(Kit.Row(Kit.Text(L("Fill the cells (crops)"), TextSize.Body, wrap: false), fill));
        }
        shape.Add(new ValueSlider(L("Spacing"), style.Spacing, 0, 0.08, null, v => Set(s => s with { Spacing = v })));
        shape.Add(new ValueSlider(L("Corners"), style.Corner, 0, 0.25, null, v => Set(s => s with { Corner = v })));

        var background = new List<UIElement>
        {
            Kit.Row(Kit.Text(L("Background"), TextSize.Body, wrap: false),
                    new DropDown(CollageFills.All.Select(f => f.Title()).ToList(), CollageFills.All.ToList().IndexOf(style.Background),
                                 i => { Set(s => s with { Background = CollageFills.All[i] }); Refresh(); }, L("Background"), 130)),
        };
        if (style.Background == CollageFill.Gradient)
        {
            var grid = new UniformGrid { Columns = 5, Margin = new Thickness(8, 8, 8, 2), HorizontalAlignment = HorizontalAlignment.Left };
            for (var i = 0; i < GradientPreset.All.Count; i++)
            {
                var index = i;
                var preset = GradientPreset.All[i];
                var brush = new LinearGradientBrush(Color.FromRgb(preset.Colors[0].Red, preset.Colors[0].Green, preset.Colors[0].Blue),
                                                    Color.FromRgb(preset.Colors[1].Red, preset.Colors[1].Green, preset.Colors[1].Blue), new Point(0, 0), new Point(1, 1));
                var swatch = new Swatch(brush, style.Gradient == i, preset.Name, () => { Set(s => s with { Gradient = index }); Refresh(); }, 30) { Margin = new Thickness(0, 0, 6, 6) };
                grid.Children.Add(swatch);
            }
            background.Add(grid);
        }
        double[] widths = [1080, 2400, 4000];
        background.Add(Kit.Row(Kit.Text(L("Width"), TextSize.Body, wrap: false),
                               new DropDown(widths.Select(w => $"{w:0} px").ToList(), Array.IndexOf(widths, style.Width),
                                            i => Set(s => s with { Width = widths[i] }), L("Width"), 130)));

        order = new StackPanel { MinHeight = 140 };
        FillOrder();
        var orderNote = model.Unreadable.Count == 0
            ? L("Drag to change the order. The collage is saved in the first image’s folder.")
            : L("Couldn’t read: %@. These images are left out.", string.Join(", ", model.Unreadable.Select(Path.GetFileName)));

        var built = new EditorLayout(area,
        [
            EditorParts.Group(L("Layout"), null, [.. shape]),
            EditorParts.Group(L("Background"), null, [.. background]),
            Kit.Section(L("Order"), Kit.Panel(order), orderNote),
        ], L("Create Collage"), () => { model.Style = new CollageSpec(); Refresh(); }, Close, Save);
        built.Save.IsEnabled = model.Paths.Count >= 2;
        return (area, built);
    }

    private void FillOrder()
    {
        order.Children.Clear();
        foreach (var path in model.Paths) order.Children.Add(Row(path));
    }

    private Border Row(string path)
    {
        var row = new Border { Height = RowHeight, Padding = new Thickness(8, 0, 8, 0), Background = Brushes.Transparent, Tag = path, Cursor = Cursors.SizeNS };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (model.Thumbnails.TryGetValue(path, out var thumbnail))
        {
            var picture = new Image { Source = thumbnail.ToWriteableBitmap(), Width = 28, Height = 28, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 28, 28), 4, 4) };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            grid.Children.Add(picture);
        }
        var name = Kit.Text(Path.GetFileName(path), TextSize.Body, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var remove = new Border { Child = Icons.Block("minus.circle", 15, Palette.Secondary), Width = 24, Height = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L("Remove from the collage") };
        System.Windows.Automation.AutomationProperties.SetName(remove, L("Remove from the collage"));
        remove.MouseLeftButtonDown += (_, e) => e.Handled = true;
        remove.MouseLeftButtonUp += (_, e) =>
        {
            Remove(path);
            e.Handled = true;
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        row.Child = grid;

        // Dragging a row moves it past its neighbours as the pointer crosses them.
        row.MouseLeftButtonDown += (_, e) => { row.CaptureMouse(); e.Handled = true; };
        row.MouseMove += (_, e) =>
        {
            if (!row.IsMouseCaptured) return;
            var from = order.Children.IndexOf(row);
            var to = Math.Clamp((int)(e.GetPosition(order).Y / RowHeight), 0, order.Children.Count - 1);
            if (to != from) MoveRow(from, to);
        };
        row.MouseLeftButtonUp += (_, _) => row.ReleaseMouseCapture();
        return row;
    }

    /// <summary>Moves a row of the list and the picture in the collage, keeping the element the pointer holds.</summary>
    public void MoveRow(int from, int to)
    {
        var row = order.Children[from];
        order.Children.RemoveAt(from);
        order.Children.Insert(to, row);
        model.Move(from, to);
    }

    /// <summary>Takes a picture out of the collage.</summary>
    public void Remove(string path)
    {
        model.Remove(path);
        FillOrder();
        layout.Save.IsEnabled = model.Paths.Count >= 2;
    }
}
