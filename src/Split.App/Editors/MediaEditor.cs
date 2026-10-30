using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SkiaSharp;

namespace Split.App;

/// <summary>Bars for the level of a sound, the part inside the trim lit and the rest dim.</summary>
public static class WaveformPainter
{
    public static void Draw(SKCanvas canvas, SKRect area, float[] peaks, double start, double end, double duration, double current)
    {
        if (peaks.Length == 0) return;
        var step = area.Width / peaks.Length;
        var mid = area.MidY;
        using var lit = new SKPaint { Color = EditorCanvas.Sk(Palette.Accent), IsAntialias = true };
        using var dim = new SKPaint { Color = EditorCanvas.Sk(Palette.With(Palette.Primary, 0.25)), IsAntialias = true };
        for (var index = 0; index < peaks.Length; index++)
        {
            var time = (double)index / peaks.Length * duration;
            var height = Math.Max(1, peaks[index] * area.Height * 0.9f);
            var bar = new SKRect(area.Left + index * step, mid - height / 2, area.Left + index * step + Math.Max(1, step * 0.7f), mid + height / 2);
            var radius = Math.Min(1.5f, step / 2);
            canvas.DrawRoundRect(bar, radius, radius, time >= start && time <= end ? lit : dim);
        }
        if (current < 0 || duration <= 0) return;
        using var line = new SKPaint { Color = EditorCanvas.Sk(Palette.Primary) };
        var x = area.Left + (float)(current / duration) * area.Width;
        canvas.DrawRect(x - 1, area.Top, 2, area.Height, line);
    }
}

/// <summary>The waveform that fills the stage for a sound.</summary>
public sealed class WaveformStage : EditorCanvas
{
    private readonly MediaEditorModel model;

    public WaveformStage(MediaEditorModel model)
    {
        this.model = model;
    }

    protected override void Paint(SKCanvas canvas, NSz size)
    {
        var edit = model.Edit;
        WaveformPainter.Draw(canvas, new SKRect(30, 30, (float)size.Width - 30, (float)size.Height - 30), model.Waveform, edit.Start, edit.End, edit.Duration, model.Current);
    }
}

/// <summary>Over the picture: the covered areas, the one being drawn, and the crop frame.</summary>
public sealed class MediaStage : EditorCanvas
{
    private readonly MediaEditorModel model;
    private CropHandle? active;
    private NBox startRect;
    private NPt startPoint;
    private NBox? draft;

    public MediaStage(MediaEditorModel model, Action edited)
    {
        this.model = model;
        Edited = edited;
        Focusable = false;
    }

    /// <summary>Called after the pointer changed the edit.</summary>
    public Action Edited { get; }

    /// <summary>Where the picture is, as shown (after the turns).</summary>
    public NBox Frame
    {
        get
        {
            var (width, height) = model.DisplaySize;
            return Fitting.Fit(new NSz(width, height), Area, 20);
        }
    }

    public bool ShowsGrid => active is not null;
    public NBox? Draft => draft;

    private NBox ViewRect(NBox rect, NBox frame) => new(frame.MinX + rect.MinX * frame.Width, frame.MinY + rect.MinY * frame.Height, rect.Width * frame.Width, rect.Height * frame.Height);

    private NPt Normalised(NPt point, NBox frame) =>
        new(Math.Min(Math.Max((point.X - frame.MinX) / frame.Width, 0), 1), Math.Min(Math.Max((point.Y - frame.MinY) / frame.Height, 0), 1));

    protected override void Paint(SKCanvas canvas, NSz size)
    {
        var frame = Frame;
        if (frame.Width <= 0) return;
        var edit = model.Edit;
        foreach (var mask in edit.Masks)
        {
            var rect = Rect(ViewRect(mask.Rect, frame));
            var selected = model.SelectedRedaction == mask.Id;
            using var fill = new SKPaint { Color = mask.Kind == MaskKind.Black ? SKColors.Black : new SKColor(255, 255, 255, 64), IsAntialias = true };
            using var edge = new SKPaint
            {
                Color = selected ? Sk(Palette.Accent) : new SKColor(255, 255, 255, 153), Style = SKPaintStyle.Stroke, StrokeWidth = selected ? 2 : 1, IsAntialias = true,
            };
            canvas.DrawRoundRect(rect, 2, 2, fill);
            canvas.DrawRoundRect(rect, 2, 2, edge);
        }
        if (draft is { } drawing)
        {
            using var dashed = new SKPaint
            {
                Color = Sk(Palette.Accent), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true, PathEffect = SKPathEffect.CreateDash([5, 3], 0),
            };
            canvas.DrawRoundRect(Rect(ViewRect(drawing, frame)), 2, 2, dashed);
        }
        if (model.Mode == MediaEditorModel.EditMode.Crop || edit.Crop is not null) PaintCrop(canvas, frame, edit.Crop ?? NBox.Full, model.Mode == MediaEditorModel.EditMode.Crop ? 1 : 0.7f);
    }

    private void PaintCrop(SKCanvas canvas, NBox frame, NBox crop, float opacity)
    {
        var r = ViewRect(crop, frame);
        byte A(double alpha) => (byte)Math.Round(alpha * opacity * 255);
        using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, A(0.5)) })
        {
            canvas.Save();
            canvas.ClipRect(Rect(r), SKClipOperation.Difference);
            canvas.DrawRect(Rect(frame), dim);
            canvas.Restore();
        }
        using var line = new SKPaint { Color = new SKColor(255, 255, 255, A(0.9)), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRect(Rect(r), line);
        if (ShowsGrid)
        {
            using var grid = new SKPaint { Color = new SKColor(255, 255, 255, A(0.55)), Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, IsAntialias = true };
            for (var i = 1; i <= 2; i++)
            {
                var x = (float)(r.MinX + r.Width * i / 3);
                var y = (float)(r.MinY + r.Height * i / 3);
                canvas.DrawLine(x, (float)r.MinY, x, (float)r.MaxY, grid);
                canvas.DrawLine((float)r.MinX, y, (float)r.MaxX, y, grid);
            }
        }
        using var marks = new SKPaint { Color = new SKColor(255, 255, 255, A(1)), Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
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
        Press(Position(e));
        if (model.Mode != MediaEditorModel.EditMode.View) CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Drag(Position(e));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        Release(Position(e));
    }

    /// <summary>The start of a drag at <paramref name="point"/> (view coordinates), for the pointer and for the tests.</summary>
    public void Press(NPt point)
    {
        startPoint = point;
        active = null;
        if (model.Mode != MediaEditorModel.EditMode.Crop) return;
        var crop = model.Edit.Crop ?? NBox.Full;
        active = CropRenderer.HandleAt(point, crop, Frame);
        startRect = crop;
    }

    public void Drag(NPt point)
    {
        var frame = Frame;
        if (frame.Width <= 0) return;
        switch (model.Mode)
        {
            case MediaEditorModel.EditMode.Crop:
                if (active is not { } handle) return;
                var delta = new NSz((point.X - startPoint.X) / frame.Width, (point.Y - startPoint.Y) / frame.Height);
                model.Edit.Crop = CropRenderer.Drag(handle, startRect, delta, null);
                break;
            case MediaEditorModel.EditMode.Redact:
                var a = Normalised(startPoint, frame);
                var b = Normalised(point, frame);
                draft = new NBox(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
                break;
            default:
                return;
        }
        InvalidateVisual();
        Edited();
    }

    public void Release(NPt point)
    {
        active = null;
        var frame = Frame;
        if (model.Mode == MediaEditorModel.EditMode.Crop && model.Edit.Crop == NBox.Full) model.Edit.Crop = null;
        if (model.Mode == MediaEditorModel.EditMode.Redact && frame.Width > 0)
        {
            if (draft is { Width: > 0.01, Height: > 0.01 } rect)
            {
                var mask = new MaskRegion(rect, model.MaskKind);
                model.Edit.Masks.Add(mask);
                model.SelectedRedaction = mask.Id;
            }
            else
            {
                var at = Normalised(point, frame);
                model.SelectedRedaction = model.Edit.Masks.LastOrDefault(m => m.Rect.Contains(at))?.Id;
            }
            draft = null;
        }
        InvalidateVisual();
        Edited();
    }
}

/// <summary>The filmstrip (or the waveform) with the trim handles and the playhead.</summary>
public sealed class TimelineCanvas : EditorCanvas
{
    public const double Pad = 4;

    private readonly MediaEditorModel model;
    private readonly Action<double> seek;
    private readonly Action edited;
    private int? dragging;   // 0 = start, 1 = end, 2 = playhead

    public TimelineCanvas(MediaEditorModel model, Action<double> seek, Action edited)
    {
        this.model = model;
        this.seek = seek;
        this.edited = edited;
        Height = 64 + Pad * 2;
        Cursor = Cursors.Arrow;
    }

    private double Duration => Math.Max(model.Edit.Duration, 0.001);

    public double StartX => model.Edit.Start / Duration * ActualWidth;
    public double EndX => model.Edit.End / Duration * ActualWidth;
    public double PlayX => model.Current / Duration * ActualWidth;

    protected override void Paint(SKCanvas canvas, NSz size)
    {
        var width = (float)size.Width;
        var height = (float)(size.Height - Pad * 2);
        if (width <= 0 || height <= 0) return;
        canvas.Translate(0, (float)Pad);
        var edit = model.Edit;
        var (startX, endX, playX) = ((float)StartX, (float)EndX, (float)PlayX);
        var strip = new SKRect(0, 0, width, height);

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(strip, 6, 6), antialias: true);
        var frames = model.Thumbnails;
        if (frames.Count > 0)
        {
            var cell = width / frames.Count;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var scale = Math.Max(cell / frame.Width, height / frame.Height);
                var (w, h) = (frame.Width * scale, frame.Height * scale);
                canvas.Save();
                canvas.ClipRect(new SKRect(i * cell, 0, (i + 1) * cell, height));
                using var picture = SKImage.FromBitmap(frame);
                canvas.DrawImage(picture, new SKRect((float)(i * cell + (cell - w) / 2), (float)((height - h) / 2), (float)(i * cell + (cell + w) / 2), (float)((height + h) / 2)),
                                 new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                canvas.Restore();
            }
        }
        else
        {
            using var back = new SKPaint { Color = Sk(Palette.With(Palette.Primary, 0.05)) };
            canvas.DrawRect(strip, back);
            WaveformPainter.Draw(canvas, strip, model.Waveform, 0, Duration, Duration, -1);
        }
        // Outside the selection is dimmed.
        using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 140) };
        canvas.DrawRect(0, 0, Math.Max(0, startX), height, shade);
        canvas.DrawRect(endX, 0, Math.Max(0, width - endX), height, shade);
        canvas.Restore();

        using var border = new SKPaint { Color = Sk(Palette.Accent), Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(startX + 1.5f, 1.5f, startX + Math.Max(8, endX - startX) - 1.5f, height - 1.5f), 5, 5, border);
        using var handle = new SKPaint { Color = Sk(Palette.Selection), IsAntialias = true };
        using var grip = new SKPaint { Color = Sk(Palette.With(Palette.OnSelection, 0.8)), IsAntialias = true };
        foreach (var x in new[] { startX - 5, endX - 5 })
        {
            canvas.DrawRoundRect(new SKRect(x, 0, x + 10, height), 3, 3, handle);
            canvas.DrawRoundRect(new SKRect(x + 4, height / 2 - 8, x + 6, height / 2 + 8), 1, 1, grip);
        }
        using var head = new SKPaint { Color = SKColors.White, ImageFilter = SKImageFilter.CreateDropShadow(0, 0, 1, 1, new SKColor(0, 0, 0, 102)) };
        canvas.DrawRect(playX - 1, -4, 2, height + 8, head);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        var x = Position(e).X;
        Press(x);
        Drag(x);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Drag(Position(e).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        Release();
    }

    /// <summary>Picks what a drag from <paramref name="x"/> moves: a trim handle if one is within 12 points, else the playhead.</summary>
    public void Press(double x)
    {
        dragging = Math.Abs(x - StartX) < 12 ? 0 : Math.Abs(x - EndX) < 12 ? 1 : 2;
    }

    public void Drag(double x)
    {
        if (ActualWidth <= 0 || dragging is null) return;
        var time = Math.Min(Math.Max(x / ActualWidth, 0), 1) * Duration;
        var edit = model.Edit;
        switch (dragging)
        {
            case 0:
                edit.Start = Math.Min(time, edit.End - 0.1);
                seek(edit.Start);
                break;
            case 1:
                edit.End = Math.Max(time, edit.Start + 0.1);
                seek(edit.End);
                break;
            default:
                seek(time);
                break;
        }
        edited();
    }

    public void Release() => dragging = null;
}

/// <summary>Edit Media: trim, change the speed and volume, fade, crop or cover parts of a video, or trim and change a sound.</summary>
public sealed class MediaEditor : EditorWindow
{
    private static readonly double[] Speeds = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 4];

    private readonly MediaEditorModel model;
    private readonly MediaElement player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly Grid work = new();
    private readonly Grid stageHost = new() { ClipToBounds = true };
    private readonly Canvas videoHost = new() { IsHitTestVisible = false };
    private readonly StackPanel message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1.0 / 30) };
    private readonly MediaStage stage;
    private readonly WaveformStage wave;
    private readonly TimelineCanvas timeline;
    private readonly SplitButton play;
    private readonly TextBlock clock;
    private readonly SplitButton startHere, endHere;
    private EditorLayout layout;
    private string? playing;
    private bool composedForFile;

    // Parts of the inspector that follow the edit.
    private TextBlock startText = new(), endText = new(), lengthText = new();
    private StackPanel quick = new(), quickNote = new(), masks = new(), coverOptions = new();
    private Toggle? fastTrim;
    private SplitButton noCrop = null!;
    private ValueSlider? volume;
    private Toggle? fadeIn, fadeOut;

    public MediaEditor(string path, string title) : base(title)
    {
        model = new MediaEditorModel(path);
        stage = new MediaStage(model, () => Dispatcher.BeginInvoke(Sync));
        wave = new WaveformStage(model);
        timeline = new TimelineCanvas(model, Seek, Sync) { Margin = new Thickness(16, 0, 16, 10) };
        play = EditorParts.Tool("play.fill", L("Play (space)"), TogglePlay);
        clock = Kit.Text("", TextSize.Subheadline, secondary: true, wrap: false);
        clock.FontFamily = new FontFamily("Consolas");
        startHere = new SplitButton(L("Start Here"), compact: true) { ToolTip = L("Sets the start of the trim at the current position (I)") };
        startHere.Click += (_, _) => { model.SetStart(); Sync(); };
        endHere = new SplitButton(L("End Here"), compact: true) { ToolTip = L("Sets the end of the trim at the current position (O)") };
        endHere.Click += (_, _) => { model.SetEnd(); Sync(); };

        player.MediaOpened += (_, _) => { player.Pause(); Seek(model.Edit.Start); };
        player.MediaFailed += (_, _) => model.PlayerFailed();
        player.MediaEnded += (_, _) =>
        {
            Seek(model.Edit.Start);
            if (model.IsPlaying) player.Play();
        };
        Width = 1160;
        Height = 760 + TitleBarHeight;
        timer.Tick += (_, _) => Tick();
        timer.Start();
        model.Changed += () => Dispatcher.BeginInvoke(Sync);
        Closed += (_, _) =>
        {
            timer.Stop();
            player.Close();
            model.Dispose();
        };
        PreviewKeyDown += OnKeys;

        BuildWork();
        layout = Compose();
        Body = layout;
        Sync();
    }

    public MediaEditorModel Model => model;
    public EditorLayout Layout => layout;
    public MediaElement Player => player;
    public MediaStage Stage => stage;
    public TimelineCanvas Timeline => timeline;
    public SplitButton PlayButton => play;
    public string Clock => clock.Text;

    protected override bool HasChanges => !model.Edit.IsIdentity;

    protected override void Rebuild()
    {
        // The player and the canvases live on while the inspector is built again.
        if (VisualTreeHelper.GetParent(work) is Border holder) holder.Child = null;
        layout = Compose();
        Body = layout;
        Sync();
    }

    protected override void Accept() { if (layout.Save.IsEnabled) Save(); }

    private void Save()
    {
        player.Pause();
        model.Save();
        CloseNow();
    }

    // MARK: Work area

    private void BuildWork()
    {
        work.RowDefinitions.Add(new RowDefinition());
        work.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        work.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        work.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        videoHost.Children.Add(player);
        stageHost.Children.Add(wave);
        stageHost.Children.Add(videoHost);
        stageHost.Children.Add(stage);
        stageHost.Children.Add(message);
        stageHost.SizeChanged += (_, _) => PositionPlayer();
        work.Children.Add(stageHost);

        var rule = Kit.Line();
        Grid.SetRow(rule, 1);
        work.Children.Add(rule);

        var transport = new Grid { Margin = new Thickness(16, 8, 16, 8) };
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transport.ColumnDefinitions.Add(new ColumnDefinition());
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        clock.Margin = new Thickness(10, 0, 0, 0);
        clock.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(clock, 1);
        endHere.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(startHere, 3);
        Grid.SetColumn(endHere, 4);
        transport.Children.Add(play);
        transport.Children.Add(clock);
        transport.Children.Add(startHere);
        transport.Children.Add(endHere);
        Grid.SetRow(transport, 2);
        work.Children.Add(transport);

        Grid.SetRow(timeline, 3);
        work.Children.Add(timeline);
    }

    /// <summary>Puts the player where the picture is, turned and flipped as the edit says.</summary>
    private void PositionPlayer()
    {
        var frame = stage.Frame;
        if (frame.Width <= 0) return;
        var turned = model.Edit.Turns % 2 != 0;
        var (w, h) = turned ? (frame.Height, frame.Width) : (frame.Width, frame.Height);
        Canvas.SetLeft(player, frame.MidX - w / 2);
        Canvas.SetTop(player, frame.MidY - h / 2);
        player.Width = w;
        player.Height = h;
        player.RenderTransformOrigin = new Point(0.5, 0.5);
        var group = new TransformGroup();
        group.Children.Add(new RotateTransform(model.Edit.Turns * 90));
        group.Children.Add(new ScaleTransform(model.Edit.FlipH ? -1 : 1, 1));
        player.RenderTransform = group;
    }

    // MARK: Playback

    private void Tick()
    {
        if (!model.Ready) return;
        if (model.IsPlaying)
        {
            model.Current = player.Position.TotalSeconds;
            // Play only the selected range, looping.
            if (model.Current >= model.Edit.End - 0.02) Seek(model.Edit.Start);
        }
        clock.Text = ClockText();
        timeline.InvalidateVisual();
        wave.InvalidateVisual();
    }

    public void TogglePlay()
    {
        if (!model.Ready) return;
        if (model.IsPlaying)
        {
            player.Pause();
            model.IsPlaying = false;
        }
        else
        {
            if (model.Current < model.Edit.Start || model.Current >= model.Edit.End - 0.05) Seek(model.Edit.Start);
            player.SpeedRatio = model.Edit.Speed;
            player.Play();
            model.IsPlaying = true;
        }
        Sync();
    }

    public void Seek(double time)
    {
        var clamped = Math.Min(Math.Max(time, 0), Math.Max(model.Edit.Duration, 0));
        model.Current = clamped;
        if (model.Ready) player.Position = TimeSpan.FromSeconds(clamped);
        clock.Text = ClockText();
        timeline.InvalidateVisual();
        wave.InvalidateVisual();
    }

    private void ApplyPlayback()
    {
        player.Volume = Math.Min(1, model.Edit.Volume);
        player.IsMuted = model.Edit.Mute;
        player.SpeedRatio = model.Edit.Speed;
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ButtonBase or Toggle or Border || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.I: model.SetStart(); Sync(); break;
            case Key.O: model.SetEnd(); Sync(); break;
            default: return;
        }
        e.Handled = true;
    }

    // MARK: Following the model

    public static string TimeCode(double seconds)
    {
        if (!double.IsFinite(seconds)) return "0:00.0";
        var total = Math.Max(0, seconds);
        var minutes = (int)total / 60;
        return $"{minutes}:{(total - minutes * 60).ToString("00.0", CultureInfo.InvariantCulture)}";
    }

    private string ClockText() => $"{TimeCode(model.Current)} / {TimeCode(model.Edit.Duration)}";

    /// <summary>Brings the stage, the transport and the inspector in line with the model.</summary>
    private void Sync()
    {
        if (model.Probed != composedForFile)
        {
            // What the file holds decides which controls exist (a sound has no picture to crop).
            composedForFile = model.Probed;
            Rebuild();
            return;
        }
        var edit = model.Edit;
        message.Children.Clear();
        if (model.Failure is { } failure)
        {
            var icon = Icons.Block("exclamationmark.triangle", 28, Palette.Secondary);
            icon.Margin = new Thickness(0, 0, 0, 8);
            message.Children.Add(icon);
            var text = Kit.Text(failure.Message, TextSize.Headline, weight: FontWeights.SemiBold);
            text.TextAlignment = TextAlignment.Center;
            message.Children.Add(text);
            if (failure.Suggestion is { } suggestion)
            {
                var detail = Kit.Text(suggestion, TextSize.Body, secondary: true);
                detail.TextAlignment = TextAlignment.Center;
                detail.Margin = new Thickness(0, 6, 0, 0);
                message.Children.Add(detail);
            }
        }
        else if (model.Loading is { } loading)
        {
            if (model.LoadingFraction is { } fraction)
            {
                var bar = new Bar(fraction) { Width = 200, Margin = new Thickness(0, 0, 0, 10) };
                message.Children.Add(bar);
            }
            var text = Kit.Text(loading, TextSize.Body, secondary: true);
            text.TextAlignment = TextAlignment.Center;
            message.Children.Add(text);
        }
        message.Visibility = message.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var showsPicture = model.Ready && model.IsVideo;
        videoHost.Visibility = showsPicture ? Visibility.Visible : Visibility.Collapsed;
        stage.Visibility = showsPicture ? Visibility.Visible : Visibility.Collapsed;
        wave.Visibility = model.Ready && !model.IsVideo ? Visibility.Visible : Visibility.Collapsed;
        if (model.PlaybackSource is { } source && source != playing)
        {
            playing = source;
            player.Source = new Uri(source);
            ApplyPlayback();
            player.Pause();
        }
        PositionPlayer();

        play.Content = Icons.Block(model.IsPlaying ? "pause.fill" : "play.fill", 15, Palette.Primary);
        play.ToolTip = model.IsPlaying ? L("Pause (space)") : L("Play (space)");
        System.Windows.Automation.AutomationProperties.SetName(play, (string)play.ToolTip);
        play.IsEnabled = startHere.IsEnabled = endHere.IsEnabled = model.Ready;
        clock.Text = ClockText();

        startText.Text = TimeCode(edit.Start);
        endText.Text = TimeCode(edit.End);
        lengthText.Text = TimeCode(edit.ResultDuration);
        quick.Visibility = edit.CanCopy ? Visibility.Visible : Visibility.Collapsed;
        quickNote.Visibility = edit.FastTrim ? Visibility.Visible : Visibility.Collapsed;
        noCrop.Visibility = edit.Crop is not null ? Visibility.Visible : Visibility.Collapsed;
        coverOptions.Visibility = model.Mode == MediaEditorModel.EditMode.Redact ? Visibility.Visible : Visibility.Collapsed;
        if (volume is not null) { volume.IsEnabled = !edit.Mute; volume.Opacity = edit.Mute ? 0.4 : 1; }
        if (fadeIn is not null) fadeIn.IsEnabled = !edit.Mute;
        if (fadeOut is not null) fadeOut.IsEnabled = !edit.Mute;
        ShowMasks();
        layout.Save.IsEnabled = model.Ready && !edit.IsIdentity;

        stage.InvalidateVisual();
        timeline.InvalidateVisual();
        wave.InvalidateVisual();
    }

    private void ShowMasks()
    {
        var current = model.Edit.Masks;
        if (masks.Children.Count == current.Count && masks.Children.Cast<FrameworkElement>().Select(c => c.Tag).SequenceEqual(current.Select(m => (object)m.Id))) return;
        masks.Children.Clear();
        foreach (var mask in current)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Tag = mask.Id };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = Icons.Block(mask.Kind == MaskKind.Blur ? "drop" : "rectangle.fill", 14, Palette.Secondary);
            icon.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(icon);
            var name = Kit.Text(mask.Kind == MaskKind.Blur ? L("Blurred Area") : L("Black Area"), TextSize.Body, wrap: false);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var remove = new Border { Child = Icons.Block("minus.circle", 15, Palette.Secondary), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L("Remove") };
            System.Windows.Automation.AutomationProperties.SetName(remove, L("Remove"));
            var id = mask.Id;
            remove.MouseLeftButtonUp += (_, _) => { model.Edit.Masks.RemoveAll(m => m.Id == id); Sync(); };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            masks.Children.Add(row);
        }
    }

    // MARK: Inspector

    private EditorLayout Compose()
    {
        var edit = model.Edit;
        var sections = new List<UIElement>();

        // Trim
        quickNote = new StackPanel { Margin = new Thickness(12, 0, 12, 8) };
        quickNote.Children.Add(EditorParts.Note(L("Instant and lossless, but the start snaps to the nearest keyframe (it may move by a few tenths of a second).")));
        fastTrim = new Toggle(edit.FastTrim, L("Quick trim (no re-encoding)"));
        fastTrim.Changed += on => { model.Edit.FastTrim = on; Sync(); };
        quick = new StackPanel();
        quick.Children.Add(Kit.Line(0.08));
        quick.Children.Add(Kit.Row(Kit.Text(L("Quick trim (no re-encoding)"), TextSize.Body), fastTrim));
        quick.Children.Add(quickNote);
        sections.Add(EditorParts.Group(L("Trim"), null,
            EditorParts.Pair(L("Start"), TimeCode(edit.Start), out startText),
            EditorParts.Pair(L("End"), TimeCode(edit.End), out endText),
            EditorParts.Pair(L("Final Length"), TimeCode(edit.ResultDuration), out lengthText),
            quick));

        // Speed and sound
        var speed = new DropDown(Speeds.Select(SpeedLabel).ToList(), Array.IndexOf(Speeds, edit.Speed),
                                 i => { model.Edit.Speed = Speeds[i]; ApplyPlayback(); Refresh(); }, L("Speed"), 130);
        var sound = new List<UIElement> { Kit.Row(Kit.Text(L("Speed"), TextSize.Body, wrap: false), speed) };
        (volume, fadeIn, fadeOut) = (null, null, null);
        if (model.Probe.ContainsAudio)
        {
            volume = new ValueSlider(L("Volume"), edit.Volume, 0, 2, null, v => { model.Edit.Volume = v; ApplyPlayback(); Sync(); });
            sound.Add(volume);
            if (model.IsVideo)
            {
                var mute = new Toggle(edit.Mute, L("Mute"));
                mute.Changed += on => { model.Edit.Mute = on; ApplyPlayback(); Sync(); };
                sound.Add(Kit.Row(Kit.Text(L("Mute"), TextSize.Body, wrap: false), mute));
            }
            fadeIn = new Toggle(edit.FadeIn, L("Fade In"));
            fadeIn.Changed += on => { model.Edit.FadeIn = on; Sync(); };
            fadeOut = new Toggle(edit.FadeOut, L("Fade Out"));
            fadeOut.Changed += on => { model.Edit.FadeOut = on; Sync(); };
            sound.Add(Kit.Row(Kit.Text(L("Fade In"), TextSize.Body, wrap: false), fadeIn));
            sound.Add(Kit.Row(Kit.Text(L("Fade Out"), TextSize.Body, wrap: false), fadeOut));
        }
        sections.Add(EditorParts.Group(L("Speed and Sound"), null, [.. sound]));

        // Picture
        noCrop = new SplitButton(L("No Crop"), compact: true);
        noCrop.Click += (_, _) => { model.Edit.Crop = null; Sync(); };
        masks = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        coverOptions = new StackPanel();
        if (model.IsVideo)
        {
            var modes = new[] { MediaEditorModel.EditMode.View, MediaEditorModel.EditMode.Crop, MediaEditorModel.EditMode.Redact };
            var tool = new Segmented([L("View"), L("Crop"), L("Cover")], Array.IndexOf(modes, model.Mode), i => { model.Mode = modes[i]; Refresh(); }, L("Tool"));
            tool.Margin = new Thickness(12, 10, 12, 8);

            var tools = new Grid { Margin = new Thickness(12, 0, 12, 8) };
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tools.ColumnDefinitions.Add(new ColumnDefinition());
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            void Add(SplitButton button) { button.Margin = new Thickness(0, 0, 6, 0); buttons.Children.Add(button); }
            Add(EditorParts.Tool("rotate.left", L("Rotate 90° Left"), () => Turn(3)));
            Add(EditorParts.Tool("rotate.right", L("Rotate 90° Right"), () => Turn(1)));
            Add(EditorParts.Tool("arrow.left.and.right.righttriangle.left.righttriangle.right", L("Flip"), () => { model.Edit.FlipH = !model.Edit.FlipH; Refresh(); }, edit.FlipH));
            tools.Children.Add(buttons);
            noCrop.HorizontalAlignment = HorizontalAlignment.Right;
            noCrop.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(noCrop, 1);
            tools.Children.Add(noCrop);

            var kinds = new[] { MaskKind.Blur, MaskKind.Black };
            var cover = Kit.Row(Kit.Text(L("Cover With"), TextSize.Body, wrap: false),
                                new DropDown([L("Blurred"), L("Black")], Array.IndexOf(kinds, model.MaskKind), i => { model.MaskKind = kinds[i]; Refresh(); }, L("Cover With"), 130));
            var coverNote = EditorParts.Note(L("Drag over the video to cover an area for the whole clip."));
            coverNote.Margin = new Thickness(12, 0, 12, 8);
            coverOptions.Children.Add(cover);
            coverOptions.Children.Add(coverNote);

            var save = new SplitButton(L("Save Frame (PNG)")) { Margin = new Thickness(12, 4, 12, 12), HorizontalAlignment = HorizontalAlignment.Left };
            save.Click += (_, _) => model.SaveFrame();

            var picture = new StackPanel();
            picture.Children.Add(tool);
            picture.Children.Add(tools);
            picture.Children.Add(coverOptions);
            picture.Children.Add(masks);
            picture.Children.Add(save);
            sections.Add(Kit.Section(L("Picture"), Kit.Panel(picture)));
        }

        // Format
        var outputs = new List<(string Label, MediaOutput Output)> { (L("Same as the Original"), MediaOutput.Same) };
        if (model.IsVideo)
        {
            outputs.Add(("MP4", MediaOutput.Mp4));
            outputs.Add((L("Animated GIF"), MediaOutput.Gif));
        }
        outputs.Add((L("Audio Only (M4A)"), MediaOutput.M4a));
        outputs.Add((L("Audio Only (MP3)"), MediaOutput.Mp3));
        if (!model.IsVideo) outputs.Add(("WAV", MediaOutput.Wav));
        var saveAs = new DropDown(outputs.Select(o => o.Label).ToList(), Math.Max(0, outputs.FindIndex(o => o.Output == edit.Output)),
                                  i => { model.Edit.Output = outputs[i].Output; Sync(); }, L("Save As"), 170);
        sections.Add(EditorParts.Group(L("Format"), L("The result is saved next to the original with “(%@)”.", L("edited")),
                                       Kit.Row(Kit.Text(L("Save As"), TextSize.Body, wrap: false), saveAs)));

        return new EditorLayout(work, sections, L("Export"), () => { model.Reset(); ApplyPlayback(); Refresh(); }, CloseNow, Save);
    }

    private void Turn(int quarter)
    {
        // Areas are kept in the turned picture's coordinates, so a turn starts them over.
        model.Edit.Turns = (model.Edit.Turns + quarter) % 4;
        model.Edit.Crop = null;
        model.Edit.Masks.Clear();
        Sync();
    }

    private static string SpeedLabel(double value) =>
        value == 1 ? L("Normal") : value.ToString(value < 1 ? "G2" : "G3", CultureInfo.InvariantCulture) + "×";
}
