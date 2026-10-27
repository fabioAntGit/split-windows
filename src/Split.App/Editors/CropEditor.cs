using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkiaSharp;

namespace Split.App;

/// <summary>The crop frame over the picture: dimmed outside, corner marks, and (while dragging) the rule-of-thirds grid.</summary>
public sealed class CropCanvas : EditorCanvas
{
    private CropHandle? active;
    private NBox startRect;
    private NPt startPoint;

    public CropCanvas(CropModel model)
    {
        Model = model;
    }

    public CropModel Model { get; }

    /// <summary>The grid shows while a handle is held.</summary>
    public bool ShowsGrid => active is not null;

    public NBox Frame => Fitting.Fit(Model.DisplaySize, Area, 36);

    protected override void Paint(SKCanvas canvas, NSz size)
    {
        var frame = Frame;
        DrawPicture(canvas, Model.Display, frame, Model.HasTransparency);
        var crop = Model.Settings.Rect;
        var r = new NBox(frame.MinX + crop.MinX * frame.Width, frame.MinY + crop.MinY * frame.Height, crop.Width * frame.Width, crop.Height * frame.Height);

        using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, 128) })
        {
            canvas.Save();
            canvas.ClipRect(Rect(r), SKClipOperation.Difference);
            canvas.DrawRect(Rect(frame), dim);
            canvas.Restore();
        }
        using var line = new SKPaint { Color = new SKColor(255, 255, 255, 230), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRect(Rect(r), line);
        if (ShowsGrid)
        {
            using var grid = new SKPaint { Color = new SKColor(255, 255, 255, 140), Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, IsAntialias = true };
            for (var i = 1; i <= 2; i++)
            {
                var x = (float)(r.MinX + r.Width * i / 3);
                var y = (float)(r.MinY + r.Height * i / 3);
                canvas.DrawLine(x, (float)r.MinY, x, (float)r.MaxY, grid);
                canvas.DrawLine((float)r.MinX, y, (float)r.MaxX, y, grid);
            }
        }
        using var marks = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        var length = (float)Math.Min(22, Math.Min(r.Width / 3, r.Height / 3));
        foreach (var (x, y, sx, sy) in new[] { (r.MinX, r.MinY, 1f, 1f), (r.MaxX, r.MinY, -1f, 1f), (r.MinX, r.MaxY, 1f, -1f), (r.MaxX, r.MaxY, -1f, -1f) })
        {
            canvas.DrawLine((float)x, (float)y + sy * length, (float)x, (float)y, marks);
            canvas.DrawLine((float)x, (float)y, (float)x + sx * length, (float)y, marks);
        }
        var bar = (float)Math.Min(18, Math.Min(r.Width / 4, r.Height / 4));
        canvas.DrawLine((float)r.MidX - bar / 2, (float)r.MinY, (float)r.MidX + bar / 2, (float)r.MinY, marks);
        canvas.DrawLine((float)r.MidX - bar / 2, (float)r.MaxY, (float)r.MidX + bar / 2, (float)r.MaxY, marks);
        canvas.DrawLine((float)r.MinX, (float)r.MidY - bar / 2, (float)r.MinX, (float)r.MidY + bar / 2, marks);
        canvas.DrawLine((float)r.MaxX, (float)r.MidY - bar / 2, (float)r.MaxX, (float)r.MidY + bar / 2, marks);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = Position(e);
        Press(point);
        if (active is not null) CaptureMouse();
    }

    /// <summary>The start of a drag at <paramref name="point"/> (view coordinates), for the pointer and for the tests.</summary>
    public void Press(NPt point)
    {
        active = CropRenderer.HandleAt(point, Model.Settings.Rect, Frame);
        startRect = Model.Settings.Rect;
        startPoint = point;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = Position(e);
        if (active is null || !IsMouseCaptured)
        {
            Cursor = CursorFor(CropRenderer.HandleAt(point, Model.Settings.Rect, Frame));
            return;
        }
        Drag(point);
    }

    /// <summary>The pointer moved to <paramref name="point"/> while a handle is held.</summary>
    public void Drag(NPt point)
    {
        var frame = Frame;
        if (active is not { } handle || frame.Width <= 0) return;
        var delta = new NSz((point.X - startPoint.X) / frame.Width, (point.Y - startPoint.Y) / frame.Height);
        Model.Settings = Model.Settings with { Rect = CropRenderer.Drag(handle, startRect, delta, Model.NormalisedRatio) };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => Release();

    public void Release()
    {
        active = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    private static Cursor CursorFor(CropHandle? handle) => handle switch
    {
        CropHandle.TopLeft or CropHandle.BottomRight => Cursors.SizeNWSE,
        CropHandle.TopRight or CropHandle.BottomLeft => Cursors.SizeNESW,
        CropHandle.Top or CropHandle.Bottom => Cursors.SizeNS,
        CropHandle.Left or CropHandle.Right => Cursors.SizeWE,
        CropHandle.Move => Cursors.SizeAll,
        _ => Cursors.Arrow,
    };
}

/// <summary>Crop, straighten, rotate or flip pictures, several at once.</summary>
public sealed class CropEditor : EditorWindow
{
    private readonly CropModel model;
    private CropCanvas canvas;
    private EditorLayout layout;
    private TextBlock? result;

    public CropEditor(IReadOnlyList<string> paths, string title, SKBitmap preview) : base(title)
    {
        model = new CropModel(paths, preview);
        model.Changed += () => Dispatcher.BeginInvoke(Sync);
        (canvas, layout) = Compose();
        Body = layout;
    }

    public CropModel Model => model;
    public CropCanvas Canvas => canvas;
    public EditorLayout Layout => layout;

    protected override bool HasChanges => !model.Settings.IsIdentity;

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
        if (result is not null) result.Text = EditorParts.Dimensions(model.ResultSize.Width, model.ResultSize.Height);
        layout.Save.IsEnabled = !model.Settings.IsIdentity;
    }

    private (CropCanvas, EditorLayout) Compose()
    {
        var area = new CropCanvas(model);
        var many = model.Paths.Count > 1;

        var items = new List<PopupItem>();
        items.AddRange(CropAspects.General.Select(a => new PopupItem(a.Title(), () => Choose(a), a == model.Aspect)));
        items.Add(new PopupItem(null));
        items.Add(new PopupItem(L("Social Media"), Enabled: false));
        items.AddRange(CropAspects.Social.Select(a => new PopupItem(a.Title(), () => Choose(a), a == model.Aspect)));
        var format = Kit.Row(Kit.Text(L("Format"), TextSize.Body, wrap: false), new Picker(model.Aspect.Title(), items, L("Format"), 170));

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        void Add(SplitButton button) { button.Margin = new Thickness(0, 0, 6, 0); tools.Children.Add(button); }
        Add(EditorParts.Tool("rotate.left", L("Rotate 90° Left"), () => model.Rotate(clockwise: false)));
        Add(EditorParts.Tool("rotate.right", L("Rotate 90° Right"), () => model.Rotate(clockwise: true)));
        Add(EditorParts.Tool("arrow.left.and.right.righttriangle.left.righttriangle.right", L("Flip Horizontally"), () => { model.Flip(horizontal: true); Refresh(); }, model.Settings.FlipH));
        Add(EditorParts.Tool("arrow.up.and.down.righttriangle.up.righttriangle.down", L("Flip Vertically"), () => { model.Flip(horizontal: false); Refresh(); }, model.Settings.FlipV));

        var note = many
            ? L("The same crop is applied to the %@ images, in the same shape. Each is saved next to its original with “(%@)”.", model.Paths.Count, L("cropped"))
            : L("Drag the corners or sides to crop and the middle to move. The copy is saved next to the original with “(%@)”.", L("cropped"));
        var size = EditorParts.Group(L("Size (preview)"), note,
            EditorParts.Pair(L("Original"), EditorParts.Dimensions(model.Preview.Width, model.Preview.Height)),
            EditorParts.Pair(L("Result"), EditorParts.Dimensions(model.ResultSize.Width, model.ResultSize.Height), out var shown));
        result = shown;

        var built = new EditorLayout(area,
        [
            EditorParts.Group(L("Aspect Ratio"), null, format),
            EditorParts.Group(L("Rotate and Flip"), null, tools),
            size,
        ], many ? L("Crop %@", model.Paths.Count) : L("Crop"), () => { model.Reset(); Refresh(); }, Close, Save);
        built.Save.IsEnabled = !model.Settings.IsIdentity;
        return (area, built);
    }

    private void Choose(CropAspect aspect)
    {
        model.Aspect = aspect;
        Refresh();
    }
}
