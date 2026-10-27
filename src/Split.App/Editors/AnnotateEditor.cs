using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;

namespace Split.App;

/// <summary>The picture with its marks, the mark being drawn, and the dashed box around the selected one.</summary>
public sealed class AnnotateCanvas : EditorCanvas
{
    private Mark? draft;
    private (Guid Id, Mark Original)? moving;

    public AnnotateCanvas(AnnotateModel model)
    {
        Model = model;
    }

    public AnnotateModel Model { get; }

    /// <summary>Called when a click with the Text tool puts a text mark down, to start typing in it.</summary>
    public Action<Mark>? TextStarted { get; set; }

    /// <summary>Called before anything else starts, so a text being typed is kept first.</summary>
    public Action? Finishing { get; set; }

    public NBox Frame => Fitting.Fit(Model.Size, Area, 28);

    /// <summary>The marks plus the one being drawn.</summary>
    private SKBitmap Shown => draft is null ? Model.Rendered : MarkupRenderer.Render(Model.Rendered, [draft], Model.Blurred, Model.Pixelated);

    protected override void Paint(SKCanvas canvas, NSz size)
    {
        var frame = Frame;
        var image = Shown;
        DrawPicture(canvas, image, frame, Model.HasTransparency);
        if (Model.Selection is { } id && Model.Marks.FirstOrDefault(m => m.Id == id) is { } mark)
        {
            var box = mark.Bounds;
            if (mark.Tool is MarkupTool.Text or MarkupTool.Counter)
            {
                var pad = mark.Tool == MarkupTool.Counter ? 0.03 : 0;
                box = new NBox(box.MinX - pad, box.MinY - pad, Math.Max(box.Width, 0.12) + pad * 2, Math.Max(box.Height, 0.05) + pad * 2);
            }
            var rect = new SKRect((float)(frame.MinX + box.MinX * frame.Width - 6), (float)(frame.MinY + box.MinY * frame.Height - 6),
                                  (float)(frame.MinX + box.MaxX * frame.Width + 6), (float)(frame.MinY + box.MaxY * frame.Height + 6));
            using var dashes = new SKPaint
            {
                Color = Sk(Palette.Accent), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash([5, 3], 0),
            };
            canvas.DrawRoundRect(rect, 4, 4, dashes);
        }
        if (draft is not null) image.Dispose();
    }

    private NPt Normalised(NPt point, NBox frame) =>
        new(Math.Min(Math.Max((point.X - frame.MinX) / frame.Width, 0), 1), Math.Min(Math.Max((point.Y - frame.MinY) / frame.Height, 0), 1));

    private NPt start;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Press(Position(e));
        CaptureMouse();
    }

    /// <summary>The pointer went down at <paramref name="point"/> (view coordinates).</summary>
    public void Press(NPt point)
    {
        var frame = Frame;
        if (frame.Width <= 0) return;
        Finishing?.Invoke();
        start = Normalised(point, frame);
        pressed = point;
        switch (Model.Tool)
        {
            case MarkupTool.Select:
                if (Model.MarkAt(start) is { } hit)
                {
                    Model.Selection = hit.Id;
                    moving = (hit.Id, hit);
                }
                else Model.Selection = null;
                break;
            case MarkupTool.Text or MarkupTool.Counter:
                break;
            case MarkupTool.Pen or MarkupTool.Highlighter:
                draft = Mark.Create(Model.Tool, [start], Model.Color, Model.Width);
                break;
            default:
                draft = Mark.Create(Model.Tool, [start, start], Model.Color, Model.Width);
                break;
        }
        InvalidateVisual();
    }

    private NPt pressed;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Drag(Position(e));
    }

    /// <summary>The pointer moved to <paramref name="point"/> with the button down.</summary>
    public void Drag(NPt point)
    {
        var frame = Frame;
        if (frame.Width <= 0) return;
        var current = Normalised(point, frame);
        switch (Model.Tool)
        {
            case MarkupTool.Select:
                if (moving is { } held && Model.Marks.Select((m, i) => (m, i)).FirstOrDefault(p => p.m.Id == held.Id) is { m: not null } found)
                {
                    var changed = Model.Marks.ToList();
                    changed[found.i] = held.Original.Translated(new NSz(current.X - start.X, current.Y - start.Y));
                    Model.Marks = changed;
                }
                break;
            case MarkupTool.Pen or MarkupTool.Highlighter:
                if (draft is not null && draft.Points[^1] is var last && Math.Sqrt(Math.Pow(current.X - last.X, 2) + Math.Pow(current.Y - last.Y, 2)) > 0.0015)
                    draft = draft with { Points = [.. draft.Points, current] };
                break;
            case MarkupTool.Text or MarkupTool.Counter:
                break;
            default:
                if (draft is not null) draft = draft with { Points = [draft.Points[0], current] };
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        Release(Position(e));
    }

    /// <summary>The button went up at <paramref name="point"/>: the mark is finished and recorded.</summary>
    public void Release(NPt point)
    {
        var frame = Frame;
        if (frame.Width <= 0) return;
        var here = Normalised(point, frame);
        var tapped = Math.Sqrt(Math.Pow(point.X - pressed.X, 2) + Math.Pow(point.Y - pressed.Y, 2)) < 3;
        switch (Model.Tool)
        {
            case MarkupTool.Select:
                if (moving is { } held && Model.Marks.FirstOrDefault(m => m.Id == held.Id) is { } now && now != held.Original)
                {
                    // The move is one undoable step: the list goes back to before it, then forward with it recorded.
                    var after = Model.Marks;
                    Model.Marks = after.Select(m => m.Id == held.Id ? held.Original : m).ToList();
                    Model.Commit(after);
                }
                moving = null;
                break;
            case MarkupTool.Text:
            {
                var mark = Mark.Create(MarkupTool.Text, [here], Model.Color, Model.Width) with { Text = L("Text") };
                Model.Commit([.. Model.Marks, mark]);
                TextStarted?.Invoke(mark);
                break;
            }
            case MarkupTool.Counter:
            {
                var number = (Model.Marks.Where(m => m.Tool == MarkupTool.Counter).Select(m => m.Number).DefaultIfEmpty(0).Max()) + 1;
                Model.Commit([.. Model.Marks, Mark.Create(MarkupTool.Counter, [here], Model.Color, Model.Width) with { Number = number }]);
                break;
            }
            default:
            {
                if (draft is not { } finished) break;
                draft = null;
                var box = finished.Bounds;
                // A click without dragging doesn't leave an invisible mark behind.
                if (tapped || (finished.Tool.IsArea() && (box.Width < 0.005 || box.Height < 0.005))) break;
                Model.Commit([.. Model.Marks, finished]);
                break;
            }
        }
        draft = null;
        InvalidateVisual();
    }
}

/// <summary>Annotate and Cover: arrows, shapes, text and numbers, and areas blurred, pixelated or covered.</summary>
public sealed class AnnotateEditor : EditorWindow
{
    private static readonly (SKColor Color, string Name)[] Colors =
    [
        (new SKColor(255, 59, 48), L("Red")), (new SKColor(255, 149, 0), L("Orange")), (new SKColor(255, 204, 0), L("Yellow")), (new SKColor(52, 199, 89), L("Green")),
        (new SKColor(0, 122, 255), L("Blue")), (new SKColor(175, 82, 222), L("Purple")), (new SKColor(0, 0, 0), L("Black")), (new SKColor(255, 255, 255), L("White")),
    ];

    private readonly AnnotateModel model;
    private AnnotateCanvas canvas;
    private EditorLayout layout;
    private Grid surface = new();
    private Canvas overlay = new();
    private TextBox? typing;
    private TextBlock? note;
    private SplitButton? faces;

    public AnnotateEditor(string path, string title, SKBitmap preview) : base(title)
    {
        model = new AnnotateModel(path, preview);
        model.Changed += () => Dispatcher.BeginInvoke(Sync);
        (canvas, layout) = Compose();
        Body = layout;
    }

    public AnnotateModel Model => model;
    public AnnotateCanvas Canvas => canvas;
    public EditorLayout Layout => layout;
    public TextBox? Typing => typing;

    protected override bool HasChanges => model.Marks.Count > 0;

    /// <summary>Escape cancels the editor, except while a text is being typed.</summary>
    protected override bool ClosesOnEscape => typing is null;

    protected override void Rebuild()
    {
        FinishText();
        (canvas, layout) = Compose();
        Body = layout;
    }

    protected override void Undo() { FinishText(); model.Undo(); }

    protected override void Redo() { FinishText(); model.Redo(); }

    protected override void Accept() { if (layout.Save.IsEnabled) Save(); }

    public void Save()
    {
        FinishText();
        model.Save();
        CloseNow();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if ((e.Key is Key.Delete or Key.Back) && model.Selection is not null && typing is null && Keyboard.FocusedElement is not TextBox)
        {
            model.RemoveSelected();
            e.Handled = true;
        }
    }

    private void Sync()
    {
        canvas.InvalidateVisual();
        layout.Save.IsEnabled = model.Marks.Count > 0;
        if (layout.Reset is not null) layout.Reset.Visibility = model.Marks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (note is not null) note.Text = model.Note ?? "";
        if (note is not null) note.Visibility = model.Note is null ? Visibility.Collapsed : Visibility.Visible;
        if (faces is not null) faces.IsEnabled = true;
    }

    private (AnnotateCanvas, EditorLayout) Compose()
    {
        var area = new AnnotateCanvas(model) { Finishing = FinishText, TextStarted = BeginText };

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 6) };
        foreach (var tool in MarkupTools.All)
        {
            var chosen = tool;
            var button = EditorParts.Tool(tool.Symbol(), tool.Title(), () =>
            {
                FinishText();
                model.Tool = chosen;
                if (chosen != MarkupTool.Select) model.Selection = null;
                Refresh();
            }, model.Tool == tool);
            button.Margin = new Thickness(0, 0, 2, 0);
            toolbar.Children.Add(button);
            if (tool is MarkupTool.Select or MarkupTool.Counter)
                toolbar.Children.Add(new Border { Width = 1, Height = 18, Margin = new Thickness(6, 0, 8, 0), Background = Palette.Brush(Palette.With(Palette.Primary, 0.15)) });
        }
        var bar = new Border { Child = toolbar, Background = Palette.Brush(Color.FromArgb(46, 0, 0, 0)), BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.1)), BorderThickness = new Thickness(0, 0, 0, 1) };

        surface = new Grid();
        surface.Children.Add(area);
        overlay = new Canvas();
        surface.Children.Add(overlay);
        var work = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        work.Children.Add(bar);
        work.Children.Add(surface);

        var swatches = new WrapPanel { Margin = new Thickness(8, 8, 8, 4) };
        foreach (var (color, name) in Colors)
        {
            var chosen = color;
            swatches.Children.Add(new Swatch(Palette.Brush(Color.FromRgb(color.Red, color.Green, color.Blue)), model.Color == color, name, () =>
            {
                model.Color = chosen;
                ApplyStyleToSelection();
                Refresh();
            }, 22));
        }
        var other = new ColorRow(L("Other Color"), model.Color, c => { model.Color = c; ApplyStyleToSelection(); });
        var width = new ValueSlider(L("Line Width"), model.Width, 1, 20, v => $"{(int)v} pt", v => { model.Width = v; ApplyStyleToSelection(); });

        note = EditorParts.Note(model.Note ?? "");
        note.Margin = new Thickness(12, 0, 12, 8);
        note.Visibility = model.Note is null ? Visibility.Collapsed : Visibility.Visible;
        faces = new SplitButton(L("Blur Faces Automatically")) { Margin = new Thickness(12, 10, 12, 8), HorizontalAlignment = HorizontalAlignment.Left };
        faces.Click += (_, _) => FindFaces();
        var cover = EditorParts.Note(L("“Cover Area” fills the area with black: it is the only option that leaves no trace of the original information."));
        cover.Margin = new Thickness(12, 0, 12, 10);
        var privacy = new StackPanel();
        privacy.Children.Add(faces);
        privacy.Children.Add(note);
        privacy.Children.Add(cover);

        var built = new EditorLayout(work,
        [
            EditorParts.Group(L("Color and Width"), null, swatches, other, width),
            Kit.Section(L("Privacy"), Kit.Panel(privacy)),
            EditorParts.Note(L("Drag to draw. With “Select” you can move or delete marks (Delete key). Ctrl+Z undoes. The copy gets “(%@)”.", L("annotated"))),
        ], L("Save"), () => { FinishText(); model.Commit([]); }, Close, Save);
        built.Save.IsEnabled = model.Marks.Count > 0;
        if (built.Reset is not null) built.Reset.Visibility = model.Marks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return (area, built);
    }

    private void FindFaces()
    {
        FinishText();
        if (faces is not null) faces.IsEnabled = false;
        // The detector can take a moment: it runs off the window's thread and the model announces the result.
        Task.Run(model.BlurFaces);
    }

    /// <summary>A new colour or width also applies to the selected mark.</summary>
    private void ApplyStyleToSelection()
    {
        if (model.Selection is not { } id || model.Marks.All(m => m.Id != id)) return;
        model.Commit(model.Marks.Select(m => m.Id == id ? m with { Color = model.Color, Width = model.Width } : m).ToList());
    }

    // MARK: Text

    private void BeginText(Mark mark)
    {
        model.EditingText = mark.Id;
        var frame = canvas.Frame;
        var scale = frame.Width / model.Size.Width * Math.Max(model.Size.Width, model.Size.Height) / 1000;
        var fontSize = (16 + mark.Width * 2.2) * scale;
        var origin = new Point(frame.MinX + mark.Points[0].X * frame.Width, frame.MinY + mark.Points[0].Y * frame.Height);
        var field = new TextBox
        {
            FontFamily = Fonts.Inter, FontSize = fontSize, FontWeight = FontWeights.SemiBold, Padding = new Thickness(2),
            Foreground = new SolidColorBrush(Color.FromRgb(mark.Color.Red, mark.Color.Green, mark.Color.Blue)), Background = Brushes.Transparent,
            CaretBrush = Palette.Brush(Palette.Accent), BorderBrush = Palette.Brush(Palette.Accent), BorderThickness = new Thickness(1),
            MinWidth = 160, Width = Math.Max(160, frame.MaxX - origin.X - 8), Text = "",
        };
        System.Windows.Automation.AutomationProperties.SetName(field, L("Text"));
        System.Windows.Controls.Canvas.SetLeft(field, origin.X);
        System.Windows.Controls.Canvas.SetTop(field, origin.Y);
        field.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { FinishText(); e.Handled = true; }
        };
        typing = field;
        overlay.Children.Add(field);
        field.Dispatcher.BeginInvoke(() => field.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Keeps the text that was typed (an empty one takes its mark away) and closes the text box.</summary>
    public void FinishText()
    {
        if (typing is null || model.EditingText is not { } id) return;
        var value = typing.Text.Trim();
        var marks = model.Marks.ToList();
        if (value.Length == 0) marks.RemoveAll(m => m.Id == id);
        else if (marks.FindIndex(m => m.Id == id) is var index and >= 0) marks[index] = marks[index] with { Text = value };
        overlay.Children.Remove(typing);
        typing = null;
        model.EditingText = null;
        model.Commit(marks);
    }
}
