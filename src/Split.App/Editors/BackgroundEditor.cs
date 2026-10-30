using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SkiaSharp;

namespace Split.App;

/// <summary>The picture on its background, with a checkerboard where there is none.</summary>
public sealed class BackgroundCanvas : EditorCanvas
{
    public BackgroundCanvas(BackgroundModel model)
    {
        Model = model;
    }

    public BackgroundModel Model { get; }

    protected override void Paint(SKCanvas canvas, NSz size)
    {
        if (Model.Rendered is not { } rendered) return;
        DrawPicture(canvas, rendered, Fitting.Fit(new NSz(rendered.Width, rendered.Height), size, 32), Model.Style.Kind == BackdropKind.Transparent);
    }
}

/// <summary>Add Background: a picture on a coloured background or in a frame, with rounded corners and a shadow.</summary>
public sealed class BackgroundEditor : EditorWindow
{
    private readonly BackgroundModel model;
    private BackgroundCanvas canvas;
    private EditorLayout layout;

    public BackgroundEditor(IReadOnlyList<string> paths, string title, SKBitmap preview) : base(title)
    {
        model = new BackgroundModel(paths, preview);
        model.Changed += Sync;
        (canvas, layout) = Compose();
        Body = layout;
    }

    public BackgroundModel Model => model;
    public BackgroundCanvas Canvas => canvas;
    public EditorLayout Layout => layout;

    /// <summary>Closing never asks: nothing here is work worth keeping.</summary>
    protected override bool HasChanges => false;

    protected override void Rebuild()
    {
        (canvas, layout) = Compose();
        Body = layout;
    }

    protected override void Accept() => Save();

    private void Sync() => Dispatcher.BeginInvoke(canvas.InvalidateVisual);

    private void Save()
    {
        model.Save();
        CloseNow();
    }

    private void Set(Func<BackdropStyle, BackdropStyle> change) => model.Style = change(model.Style);

    private (BackgroundCanvas, EditorLayout) Compose()
    {
        var area = new BackgroundCanvas(model);
        var style = model.Style;
        var many = model.Paths.Count > 1;

        var kinds = new Segmented(BackdropKinds.All.Select(k => k.Title()).ToList(), BackdropKinds.All.ToList().IndexOf(style.Kind),
                                  i => { Set(s => s with { Kind = BackdropKinds.All[i] }); Refresh(); }, L("Type"));
        kinds.Margin = new Thickness(12, 10, 12, 8);
        var background = new StackPanel();
        background.Children.Add(kinds);
        switch (style.Kind)
        {
            case BackdropKind.Gradient:
            {
                var grid = new UniformGrid { Columns = 5, Margin = new Thickness(8, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Left };
                for (var i = 0; i < GradientPreset.All.Count; i++)
                {
                    var index = i;
                    var preset = GradientPreset.All[i];
                    var brush = new LinearGradientBrush(Color.FromRgb(preset.Colors[0].Red, preset.Colors[0].Green, preset.Colors[0].Blue),
                                                        Color.FromRgb(preset.Colors[1].Red, preset.Colors[1].Green, preset.Colors[1].Blue), new Point(0, 0), new Point(1, 1));
                    grid.Children.Add(new Swatch(brush, style.Gradient == i, preset.Name, () => { Set(s => s with { Gradient = index }); Refresh(); }, 30));
                }
                background.Children.Add(grid);
                break;
            }
            case BackdropKind.Solid:
                background.Children.Add(new ColorRow(L("Color"), style.Solid, c => Set(s => s with { Solid = c })));
                break;
            case BackdropKind.Blur:
                background.Children.Add(Padded(EditorParts.Note(L("Uses the image itself, enlarged and blurred, as the background."))));
                break;
            default:
                background.Children.Add(Padded(EditorParts.Note(L("No background: the result is a PNG with transparency, useful for rounded corners and a shadow."))));
                break;
        }

        ValueSlider Slider(string title, double value, double max, Func<BackdropStyle, double, BackdropStyle> change) =>
            new(title, value, 0, max, null, v => Set(s => change(s, v)));
        var ratio = new DropDown(BackdropRatios.All.Select(r => r.Title()).ToList(), BackdropRatios.All.ToList().IndexOf(style.Ratio),
                                 i => Set(s => s with { Ratio = BackdropRatios.All[i] }), L("Aspect Ratio"), 130);

        var sections = new List<UIElement>
        {
            Kit.Section(L("Background"), Kit.Panel(background)),
            EditorParts.Group(L("Frame"), null,
                Slider(L("Margin"), style.Padding, 0.3, (s, v) => s with { Padding = v }),
                Slider(L("Corners"), style.Corner, 0.2, (s, v) => s with { Corner = v }),
                Slider(L("Shadow"), style.Shadow, 1, (s, v) => s with { Shadow = v })),
            EditorParts.Group(L("Format"), many ? L("The same settings are applied to the %@ images.", model.Paths.Count) : null,
                Kit.Row(Kit.Text(L("Aspect Ratio"), TextSize.Body, wrap: false), ratio)),
        };
        var built = new EditorLayout(area, sections, many ? L("Apply to %@", model.Paths.Count) : L("Save"),
                                     () => { model.Style = new BackdropStyle(); Refresh(); }, CloseNow, Save);
        return (area, built);
    }

    private static UIElement Padded(UIElement note)
    {
        ((FrameworkElement)note).Margin = new Thickness(12, 0, 12, 10);
        return note;
    }
}
