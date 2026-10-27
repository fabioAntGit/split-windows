using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;

namespace Split.App;

/// <summary>The picture with the adjustments applied; holding the button on "compare" shows the original.</summary>
public sealed class AdjustCanvas : EditorCanvas
{
    public AdjustCanvas(AdjustModel model)
    {
        Model = model;
    }

    public AdjustModel Model { get; }

    protected override void Paint(SKCanvas canvas, NSz size)
    {
        var image = Model.ShowOriginal ? Model.Preview : Model.Rendered;
        DrawPicture(canvas, image, Fitting.Fit(new NSz(image.Width, image.Height), size, 28), Model.HasTransparency);
    }
}

/// <summary>Adjust: light, colour and detail, or a filter. For images.</summary>
public sealed class AdjustEditor : EditorWindow
{
    private readonly AdjustModel model;
    private AdjustCanvas canvas;
    private EditorLayout layout;
    private Border compare = new();
    private TextBlock compareText = new();

    public AdjustEditor(IReadOnlyList<string> paths, string title, SKBitmap preview) : base(title)
    {
        model = new AdjustModel(paths, preview);
        model.Changed += () => Dispatcher.BeginInvoke(Sync);
        (canvas, layout) = Compose();
        Body = layout;
    }

    public AdjustModel Model => model;
    public AdjustCanvas Canvas => canvas;
    public EditorLayout Layout => layout;
    public Border CompareButton => compare;

    protected override bool HasChanges => !model.Adjustments.IsNeutral;

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

    private void Sync()
    {
        canvas.InvalidateVisual();
        layout.Save.IsEnabled = !model.Adjustments.IsNeutral;
        compare.Visibility = model.Adjustments.IsNeutral ? Visibility.Hidden : Visibility.Visible;
    }

    private void Set(Func<Adjustments, Adjustments> change)
    {
        model.Adjustments = change(model.Adjustments);
        Sync();
    }

    private (AdjustCanvas, EditorLayout) Compose()
    {
        var area = new AdjustCanvas(model);
        var a = model.Adjustments;
        var many = model.Paths.Count > 1;

        // Press and hold to see the original: the answer comes on the press, not on the release.
        compareText = Kit.Text(model.ShowOriginal ? L("Original") : L("Press and hold to compare"), TextSize.Subheadline, weight: FontWeights.Medium, wrap: false);
        compare = new Border
        {
            Child = compareText, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.Hand,
            Background = Palette.Brush(Palette.Over(Palette.With(Palette.Primary, 0.08), Palette.WheelSurface)),
            BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.1)), BorderThickness = new Thickness(0.5),
            Visibility = a.IsNeutral ? Visibility.Hidden : Visibility.Visible,
        };
        compare.MouseLeftButtonDown += (_, _) => Compare(true);
        compare.MouseLeftButtonUp += (_, _) => Compare(false);
        compare.LostMouseCapture += (_, _) => Compare(false);
        var work = new Grid();
        work.Children.Add(area);
        work.Children.Add(compare);

        var auto = new Toggle(a.Auto, L("Auto Enhance"));
        auto.Changed += on => Set(x => x with { Auto = on });
        var filter = new DropDown(PhotoFilters.All.Select(f => f.Title()).ToList(), PhotoFilters.All.ToList().IndexOf(a.Filter),
                                  i => { Set(x => x with { Filter = PhotoFilters.All[i] }); Refresh(); }, L("Filter"), 140);

        ValueSlider Slider(string title, double value, double min, double max, Func<double, string>? format, Func<Adjustments, double, Adjustments> change) =>
            new(title, value, min, max, format, v => Set(x => change(x, v)));

        var signed = ValueSlider.Signed("", 100);
        var sections = new List<UIElement>
        {
            EditorParts.Group(null, null, Kit.Row(Kit.Text(L("Auto Enhance"), TextSize.Body, wrap: false), auto), Kit.Row(Kit.Text(L("Filter"), TextSize.Body, wrap: false), filter)),
            EditorParts.Group(L("Light"), null,
                Slider(L("Exposure"), a.Exposure, -2, 2, ValueSlider.Signed(" EV", 1, 1), (x, v) => x with { Exposure = v }),
                Slider(L("Brightness"), a.Brightness, -0.5, 0.5, ValueSlider.Signed("", 200), (x, v) => x with { Brightness = v }),
                Slider(L("Contrast"), a.Contrast, 0.5, 1.5, v => ValueSlider.Signed("", 200)(v - 1), (x, v) => x with { Contrast = v }),
                Slider(L("Highlights"), a.Highlights, 0, 1, null, (x, v) => x with { Highlights = v }),
                Slider(L("Shadows"), a.Shadows, -1, 1, signed, (x, v) => x with { Shadows = v })),
            EditorParts.Group(L("Color"), null,
                Slider(L("Saturation"), a.Saturation, 0, 2, v => signed(v - 1), (x, v) => x with { Saturation = v }),
                Slider(L("Vibrance"), a.Vibrance, -1, 1, signed, (x, v) => x with { Vibrance = v }),
                Slider(L("Temperature"), a.Warmth, -1, 1, signed, (x, v) => x with { Warmth = v }),
                Slider(L("Tint"), a.Tint, -1, 1, signed, (x, v) => x with { Tint = v })),
            EditorParts.Group(L("Detail"), null,
                Slider(L("Sharpness"), a.Sharpness, 0, 1, null, (x, v) => x with { Sharpness = v }),
                Slider(L("Noise Reduction"), a.Noise, 0, 1, null, (x, v) => x with { Noise = v }),
                Slider(L("Vignette"), a.Vignette, 0, 1, null, (x, v) => x with { Vignette = v })),
        };
        if (many) sections.Add(EditorParts.Note(L("The same adjustments are applied to the %@ images (“(%@)”).", model.Paths.Count, L("adjusted"))));

        var built = new EditorLayout(work, sections, many ? L("Apply to %@", model.Paths.Count) : L("Save"),
                                     () => { model.Adjustments = new Adjustments(); Refresh(); }, Close, Save);
        built.Save.IsEnabled = !a.IsNeutral;
        return (area, built);
    }

    private void Compare(bool original)
    {
        if (model.ShowOriginal == original) return;
        model.ShowOriginal = original;
        compareText.Text = original ? L("Original") : L("Press and hold to compare");
        canvas.InvalidateVisual();
        if (original) compare.CaptureMouse(); else compare.ReleaseMouseCapture();
    }
}
