using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Split.App;

/// <summary>
/// The wheel of drop targets: one element that draws the rim, separators, highlighted slice, icons, names and
/// centre itself. With nothing inside it, a drag moving over the wheel never reports leaving and entering its
/// parts. The window around it feeds it the pointer and keys; it keeps the state (open groups, the slice under
/// the pointer) and the motion.
/// </summary>
public sealed class WheelView : FrameworkElement
{
    /// <summary>How long a drag rests on a group before it opens (or on the centre before going back).</summary>
    public const double SpringDelay = 0.5;

    /// <summary>Room round the wheel for the shadow it casts, which a clear window doesn't get from the system.</summary>
    public const double ShadowMargin = 24;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static readonly ITextMeasurer Text = new WpfTextMeasurer();

    private readonly record struct Level(List<WheelItem> Items, string Title, string Subtitle, int Group);

    private sealed class Fade(double duration, double from, double to, bool easeIn = false)
    {
        private readonly double start = Clock.Elapsed.TotalSeconds;

        public bool Running => Clock.Elapsed.TotalSeconds < start + duration;

        public double Value
        {
            get
            {
                var p = Math.Clamp((Clock.Elapsed.TotalSeconds - start) / duration, 0, 1);
                return from + (to - from) * (easeIn ? p * p : p);
            }
        }
    }

    private readonly bool forWindow;
    private readonly double margin;
    private readonly Stack<Level> trail = new();
    private SpringValue[] pops;
    private DispatcherTimer? animation;
    private DispatcherTimer? spring;
    private double lastTick = Clock.Elapsed.TotalSeconds;
    private bool pressedOutside, pressedHub;
    private int? highlighted;
    private bool hubHighlighted;
    private Fade? entryFade, levelFade, exitFade;
    private readonly SpringValue entryScale = new() { Response = 0.3, Value = 1, Target = 1 };
    private readonly SpringValue levelScale = new() { Response = 0.28, Value = 1, Target = 1 };
    private Point? entryAnchor;
    private Action? exitDone;
    private readonly Lazy<ImageSource?> fileIcon;

    public WheelStyle Appearance { get; }
    public List<WheelItem> Items { get; private set; }
    public WheelLayout Layout { get; private set; }
    public string Title { get; private set; }
    public string Subtitle { get; private set; }
    public IReadOnlyList<string> Paths { get; private set; }
    public bool ClickMode { get; private set; }

    /// <summary>On screen: groups open by themselves while a drag rests on them, and changes of level move.</summary>
    public bool IsLive { get; set; }

    /// <summary>Called with the chosen slice; groups never reach it, they open in place.</summary>
    public Action<WheelItem, IReadOnlyList<string>>? OnPerform { get; set; }

    public Action? OnCancel { get; set; }

    /// <summary>Files were dropped on a group: the wheel stays open for a click on one of its slices.</summary>
    public Action? OnNeedsClicks { get; set; }

    /// <summary>How often a change asked for the wheel to be drawn again; tests use it to see that every change reaches the screen.</summary>
    public int RedrawCount { get; private set; }

    public WheelView(IEnumerable<WheelItem> items, string title, string subtitle, IReadOnlyList<string> paths, bool clickMode,
                     WheelStyle? style = null, bool forWindow = false)
    {
        Appearance = style ?? WheelStyle.Current;
        Items = [.. items];
        Layout = Build(Items);
        Title = title;
        Subtitle = subtitle;
        Paths = paths;
        ClickMode = clickMode;
        this.forWindow = forWindow;
        margin = forWindow ? ShadowMargin : 0;
        pops = NewPops();
        fileIcon = new(() => Paths.Count > 0 ? FileIcons.Of(Paths[0]) : null);
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = false;
    }

    private WheelLayout Build(List<WheelItem> items) =>
        WheelLayout.Make(items.Select(i => i.Title).ToList(), items.Select(i => i.Symbol).ToList(), Appearance, Text);

    private SpringValue[] NewPops() => [.. Items.Select(_ => new SpringValue())];

    /// <summary>The side of the element: the window a wheel of this size needs, and the room for its shadow.</summary>
    public double Side => Layout.Size + 2 * margin;

    public Point Center => new(Side / 2, Side / 2);

    public bool IsInGroup => trail.Count > 0;

    /// <summary>The wheel the open group belongs to.</summary>
    public string? ParentTitle => trail.Count > 0 ? trail.Peek().Title : null;

    protected override Size MeasureOverride(Size availableSize) => new(Side, Side);

    public int? Highlighted
    {
        get => highlighted;
        private set
        {
            if (highlighted == value) return;
            highlighted = value;
            for (var i = 0; i < pops.Length; i++) pops[i].Target = i == highlighted ? 1 : 0;
            StartAnimating();
            ArmSpring();
            Redraw();
        }
    }

    /// <summary>The pointer is on the centre of an open group, which goes back.</summary>
    public bool HubHighlighted
    {
        get => hubHighlighted;
        private set
        {
            if (hubHighlighted == value) return;
            hubHighlighted = value;
            ArmSpring();
            Redraw();
        }
    }

    public void Redraw()
    {
        RedrawCount++;
        InvalidateVisual();
    }

    public double Pop(int index) => index >= 0 && index < pops.Length ? pops[index].Value : 0;

    /// <summary>A point of the element as an offset from the wheel's centre, y up.</summary>
    private Pt Offset(Point point) => new(point.X - Center.X, Center.Y - point.Y);

    /// <summary>The slice under a point of the element. A generous margin outside the ring still counts.</summary>
    public int? IndexAt(Point point) => Layout.SliceAt(Offset(point));

    public bool IsOnHub(Point point) => Layout.IsOnHub(Offset(point));

    /// <summary>
    /// The wheel as a picture, drawn by the wheel itself, with <paramref name="highlighted"/> already popped out
    /// (Settings previews arrangements with it). It is clear round the disc.
    /// </summary>
    public static BitmapSource Picture(IEnumerable<WheelItem> items, string title, string subtitle, int? highlighted = null,
                                       WheelStyle? style = null, string? back = null, double scale = 2)
    {
        var view = new WheelView(items, title, subtitle, [], false, style);
        if (back is not null) view.trail.Push(new Level([], back, "", 0));
        if (highlighted is { } index && index >= 0 && index < view.pops.Length)
        {
            view.Highlighted = index;
            view.StopAnimating();
            view.pops[index].Value = 1;
        }
        view.Measure(new Size(view.Side, view.Side));
        view.Arrange(new Rect(0, 0, view.Side, view.Side));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.Side * scale), (int)Math.Ceiling(view.Side * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        view.StopAnimating();
        return bitmap;
    }

    // Choosing and groups

    /// <summary>Performs the slice, or opens it when it is a group.</summary>
    public void Choose(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        if (Items[index].IsGroup) OpenGroup(index);
        else OnPerform?.Invoke(Items[index], Paths);
    }

    public void OpenGroup(int index)
    {
        if (index < 0 || index >= Items.Count || !Items[index].IsGroup) return;
        var group = Items[index];
        trail.Push(new Level(Items, Title, Subtitle, index));
        Show(group.Children, group.Title, Subtitle, null);
    }

    /// <summary>Back to the wheel the open group belongs to; <paramref name="highlightGroup"/> keeps the keyboard on the group.</summary>
    public bool GoBack(bool highlightGroup = false)
    {
        if (!trail.TryPop(out var level)) return false;
        Show(level.Items, level.Title, level.Subtitle, highlightGroup ? level.Group : null);
        return true;
    }

    /// <summary>Starts over with other slices (a tap of another key while the files are still being dragged).</summary>
    public void Reset(IEnumerable<WheelItem> items, string title, string subtitle, IReadOnlyList<string> paths)
    {
        trail.Clear();
        Paths = paths;
        Show([.. items], title, subtitle, null);
    }

    private void Show(List<WheelItem> items, string title, string subtitle, int? highlight)
    {
        spring?.Stop();
        spring = null;
        Highlighted = null;
        HubHighlighted = false;
        Items = items;
        Title = title;
        Subtitle = subtitle;
        Layout = Build(items);
        pops = NewPops();
        Highlighted = highlight;
        Redraw();
        AnimateLevelChange();
        if (UIElementAutomationPeer.FromElement(this) is { } peer) peer.InvalidatePeer();
    }

    /// <summary>While files are dragged, resting on a group opens it and resting on the centre goes back.</summary>
    private void ArmSpring()
    {
        spring?.Stop();
        spring = null;
        if (ClickMode || !IsLive) return;
        Action? open = null;
        if (highlighted is { } index && Items[index].IsGroup) open = () => OpenGroup(index);
        else if (hubHighlighted) open = () => GoBack();
        if (open is null) return;
        spring = new DispatcherTimer(TimeSpan.FromSeconds(SpringDelay), DispatcherPriority.Normal, (timer, _) =>
        {
            ((DispatcherTimer)timer!).Stop();
            spring = null;
            open();
        }, Dispatcher);
    }

    /// <summary>Files dropped on a group: open it, and wait for a click on one of its slices.</summary>
    private void BecomeClickable()
    {
        if (ClickMode) return;
        ClickMode = true;
        OnNeedsClicks?.Invoke();
    }

    // Motion

    private void StartAnimating()
    {
        if (animation is not null) return;
        lastTick = Clock.Elapsed.TotalSeconds;
        animation = new DispatcherTimer(TimeSpan.FromSeconds(1.0 / 120), DispatcherPriority.Render, (_, _) => Tick(), Dispatcher);
    }

    public void StopAnimating()
    {
        animation?.Stop();
        animation = null;
    }

    private void Tick()
    {
        var now = Clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - lastTick, 1.0 / 30);
        lastTick = now;
        var reduce = Motion.ReduceMotion;
        var settled = true;
        foreach (var pop in pops)
        {
            if (reduce) (pop.Value, pop.Velocity) = (pop.Target, 0);
            else pop.Step(dt);
            settled &= pop.IsSettled;
        }
        foreach (var scale in new[] { entryScale, levelScale })
        {
            if (reduce) (scale.Value, scale.Velocity) = (scale.Target, 0);
            else scale.Step(dt);
            settled &= scale.IsSettled;
        }
        entryFade = entryFade is { Running: true } ? entryFade : null;
        levelFade = levelFade is { Running: true } ? levelFade : null;
        var leaving = exitFade is not null;
        if (leaving && !exitFade!.Running)
        {
            exitFade = null;
            var done = exitDone;
            exitDone = null;
            StopAnimating();
            Redraw();
            done?.Invoke();
            return;
        }
        Redraw();
        if (settled && entryFade is null && levelFade is null && !leaving) StopAnimating();
    }

    private static double FadeTime => Motion.ReduceMotion ? 0.15 : 0.18;

    /// <summary>The wheel grows out of the cursor, so it's clear where it came from.</summary>
    public void AnimateIn(Point anchor)
    {
        entryAnchor = anchor;
        entryFade = new Fade(FadeTime, 0, 1);
        entryScale.Value = Motion.ReduceMotion ? 1 : 0.82;
        entryScale.Velocity = 0;
        exitFade = null;
        StartAnimating();
        Redraw();
    }

    /// <summary>A group opening (or closing) settles into place from slightly smaller, like a new menu level.</summary>
    private void AnimateLevelChange()
    {
        if (!IsLive) return;
        levelFade = new Fade(Motion.ReduceMotion ? 0.15 : 0.16, 0, 1);
        levelScale.Value = Motion.ReduceMotion ? 1 : 0.92;
        levelScale.Velocity = 0;
        StartAnimating();
    }

    public void AnimateOut(Action completion)
    {
        if (!IsLive)
        {
            completion();
            return;
        }
        exitFade = new Fade(Motion.ReduceMotion ? 0.15 : 0.16, 1, 0, easeIn: true);
        exitDone = completion;
        StartAnimating();
    }

    // Drag destination

    /// <summary>Files dragged over a point of the element. Whether the slice there takes them.</summary>
    public bool DragMoved(Point point)
    {
        Highlighted = IndexAt(point);
        HubHighlighted = IsInGroup && IsOnHub(point);
        return Highlighted is not null;
    }

    /// <summary>The pointer really left the window.</summary>
    public void DragExited()
    {
        Highlighted = null;
        HubHighlighted = false;
    }

    /// <summary>Files dropped where the pointer is. False when no slice is there.</summary>
    public bool Dropped(IReadOnlyList<string>? dropped)
    {
        if (Highlighted is not { } index || index >= Items.Count) return false;
        var item = Items[index];
        var chosen = dropped is { Count: > 0 } ? Intake.Resolve(dropped) : Paths;
        Dispatcher.BeginInvoke(() =>
        {
            if (!item.IsGroup)
            {
                OnPerform?.Invoke(item, chosen);
                return;
            }
            Paths = chosen;
            BecomeClickable();
            OpenGroup(index);
        });
        return true;
    }

    // Click mode (highlight on press, commit on release)

    public void Hover(Point point)
    {
        Highlighted = IndexAt(point);
        HubHighlighted = IsInGroup && IsOnHub(point);
    }

    public void Press(Point point)
    {
        var index = IndexAt(point);
        pressedHub = IsInGroup && IsOnHub(point);
        pressedOutside = index is null && !pressedHub;
        Highlighted = index;
    }

    /// <summary>The pointer moves with the button down: the highlight follows it.</summary>
    public void Dragged(Point point) => Highlighted = IndexAt(point);

    public void Release(Point point)
    {
        if (IndexAt(point) is { } index) Choose(index);
        else if (pressedHub && IsOnHub(point)) GoBack();
        else if (pressedOutside) OnCancel?.Invoke();
    }

    public void Leave()
    {
        Highlighted = null;
        HubHighlighted = false;
    }

    public bool PressKey(Key key)
    {
        switch (key)
        {
            case Key.Escape: // out of a group first, then away
                if (!GoBack(highlightGroup: true)) OnCancel?.Invoke();
                return true;
            case Key.Back or Key.Delete:
                GoBack(highlightGroup: true);
                return true;
            case Key.Return or Key.Space:
                if (Highlighted is { } index) Choose(index);
                return true;
            case Key.Left or Key.Down: // counter-clockwise
                if (Items.Count > 0) Highlighted = ((Highlighted ?? 0) - 1 + Items.Count) % Items.Count;
                return true;
            case Key.Right or Key.Up: // clockwise
                if (Items.Count > 0) Highlighted = Highlighted is { } at ? (at + 1) % Items.Count : 0;
                return true;
        }
        return false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!ClickMode) return;
        if (IsMouseCaptured) Dragged(e.GetPosition(this));
        else Hover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (ClickMode && !IsMouseCaptured) Leave();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        Press(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        Release(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e) => e.Handled = PressKey(e.Key);

    // Accessibility

    protected override AutomationPeer OnCreateAutomationPeer() => new WheelAutomationPeer(this);

    /// <summary>Where slice <paramref name="index"/>'s icon and name sit on screen.</summary>
    public Rect ScreenFrame(int index)
    {
        if (index < 0 || index >= Items.Count) return Rect.Empty;
        var frame = Layout.Frame(index);
        return OnScreen(new Rect(new Point(Center.X + frame.MinX, Center.Y - frame.MaxY), new Point(Center.X + frame.MaxX, Center.Y - frame.MinY)));
    }

    public Rect HubScreenFrame
    {
        get
        {
            var radius = Layout.Inner - 8;
            return OnScreen(new Rect(Center.X - radius, Center.Y - radius, radius * 2, radius * 2));
        }
    }

    private Rect OnScreen(Rect local) =>
        PresentationSource.FromVisual(this) is null ? Rect.Empty : new Rect(PointToScreen(local.TopLeft), PointToScreen(local.BottomRight));

    // Drawing

    private static Color Ink(double alpha) => Palette.With(Palette.WheelInk, alpha);

    private Point At(double angle, double radius) => new(Center.X + radius * Math.Sin(angle), Center.Y - radius * Math.Cos(angle));

    private static Point At(Point centre, double angle, double radius) => new(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));

    protected override void OnRender(DrawingContext dc)
    {
        var center = Center;
        var reduceMotion = Motion.ReduceMotion;
        var contrast = Motion.IncreaseContrast;
        var layout = Layout;
        var (inner, outer, disc) = (layout.Inner, layout.Outer, layout.Disc);
        var step = layout.Step;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // A clear window passes the pointer through unless something is drawn: a near-invisible fill
        // keeps all of it a target for the files being dragged.
        if (forWindow) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, Side, Side));
        var opacity = (entryFade?.Value ?? 1) * (exitFade?.Value ?? 1);
        dc.PushOpacity(opacity);
        dc.PushTransform(AroundPoint(entryScale.Value, entryAnchor ?? center));
        dc.PushTransform(AroundPoint(exitFade is { } leaving && !reduceMotion ? 1 - 0.06 * (1 - leaving.Value) : 1, center));

        if (forWindow) DrawShadow(dc, center, disc);
        dc.DrawEllipse(Palette.Brush(forWindow ? Palette.Floating : Palette.WheelSurface), null, center, disc, disc);

        dc.PushOpacity(levelFade?.Value ?? 1);
        dc.PushTransform(AroundPoint(levelScale.Value, center));

        // Edge of the disc: a hairline.
        dc.DrawEllipse(null, new Pen(Palette.Brush(Ink(contrast ? 0.5 : 0.14)), contrast ? 1.5 : 1), center, disc - 0.5, disc - 0.5);

        // Separators between segments.
        if (Items.Count > 1)
        {
            var pen = new Pen(Palette.Brush(Ink(contrast ? 0.4 : 0.14)), 1);
            for (var index = 0; index < Items.Count; index++)
                dc.DrawLine(pen, At(index * step, inner + 10), At(index * step, outer - 10));
        }

        // Highlight: a platter inside its own slice, 5 pt from the dividers and the hub and short of the rim,
        // where a group's dots sit, with a hairline edge, like a menu selection. It is drawn `lift` closer
        // to the hub, so once it has risen with its slice it sits evenly.
        var lift = reduceMotion ? 0 : WheelLayout.Pop;
        for (var index = 0; index < Items.Count; index++)
        {
            var pop = Pop(index);
            var strength = index == Highlighted ? 1 : pop;
            if (strength <= 0.01) continue;
            var mid = layout.AngleOf(index);
            var veil = Palette.WheelSelection;
            var path = Segment(center, index * step, (index + 1) * step, inner - 8 - lift, outer + 7.5 - lift, 5, 9, Items.Count == 1);
            dc.PushTransform(new TranslateTransform(lift * pop * Math.Sin(mid), -lift * pop * Math.Cos(mid)));
            dc.DrawGeometry(Palette.Brush(Palette.With(veil, veil.A / 255.0 * strength)), new Pen(Palette.Brush(Ink((contrast ? 0.5 : 0.14) * strength)), 1), path);
            dc.Pop();
        }

        for (var index = 0; index < Items.Count; index++)
        {
            var pop = reduceMotion ? 0 : Pop(index);
            var angle = layout.AngleOf(index);
            var origin = new Point(center.X + WheelLayout.Pop * pop * Math.Sin(angle), center.Y - WheelLayout.Pop * pop * Math.Cos(angle));
            DrawSlice(dc, Items[index], layout.Slices[index], layout, origin, pop, dpi);
            if (Items[index].IsGroup) DrawGroupMark(dc, angle, outer + WheelLayout.RimMargin / 2, contrast);
        }

        DrawHub(dc, center, inner - 8, dpi);
        for (var i = 0; i < 5; i++) dc.Pop();
    }

    private static Transform AroundPoint(double factor, Point point) => new MatrixTransform(new Matrix(factor, 0, 0, factor, point.X * (1 - factor), point.Y * (1 - factor)));

    private static void DrawShadow(DrawingContext dc, Point center, double disc)
    {
        var reach = disc + ShadowMargin - 4;
        var edge = disc / reach;
        var brush = new RadialGradientBrush { GradientOrigin = new Point(0.5, 0.5), Center = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
        foreach (var (at, alpha) in new[] { (0.0, 0.34), (edge, 0.34), (edge + 0.25 * (1 - edge), 0.17), (edge + 0.5 * (1 - edge), 0.07), (edge + 0.75 * (1 - edge), 0.02), (1.0, 0.0) })
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0), at));
        // Only around the disc: under it the shadow would show through the clear surface and darken it.
        var around = new RectangleGeometry(new Rect(center.X - reach, center.Y + 4 - reach, 2 * reach, 2 * reach));
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, around, new EllipseGeometry(center, disc, disc)));
        dc.DrawEllipse(brush, null, new Point(center.X, center.Y + 4), reach, reach);
        dc.Pop();
    }

    /// <summary>
    /// Annular segment whose edges all sit <paramref name="inset"/> points inside the slice, with rounded corners of
    /// radius <paramref name="corner"/>, or less where the slice is too narrow for it.
    /// </summary>
    private static Geometry Segment(Point center, double start, double end, double inner, double outer, double inset, double corner, bool single)
    {
        var ro = outer - inset;
        var ri = inner + inset;
        if (single)
        {
            return new GeometryGroup
            {
                FillRule = FillRule.EvenOdd,
                Children = { new EllipseGeometry(center, ro, ro), new EllipseGeometry(center, ri, ri) },
            };
        }
        var narrow = Math.Sin((end - start) / 2);
        var r = Math.Max(1, Math.Min(corner, (narrow * ri - inset) / (1 - narrow)));
        // Each corner is a circle of radius r that touches its straight edge and its arc.
        var outerCentre = ro - r;
        var innerCentre = ri + r;
        var outerTurn = Math.Asin(Math.Min(1, (inset + r) / outerCentre));
        var innerTurn = Math.Asin(Math.Min(1, (inset + r) / innerCentre));
        const double quarter = Math.PI / 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = true;
            void Arc(Point about, double radius, double from, double to)
            {
                var begin = At(about, from, radius);
                if (first) context.BeginFigure(begin, true, true);
                else context.LineTo(begin, true, true);
                first = false;
                context.ArcTo(At(about, to, radius), new Size(radius, radius), 0, Math.Abs(to - from) > Math.PI,
                              to > from ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
            }
            Arc(At(center, start + innerTurn, innerCentre), r, start + innerTurn + Math.PI, start + 3 * quarter);
            Arc(At(center, start + outerTurn, outerCentre), r, start - quarter, start + outerTurn);
            Arc(center, ro, start + outerTurn, end - outerTurn);
            Arc(At(center, end - outerTurn, outerCentre), r, end - outerTurn, end + quarter);
            Arc(At(center, end - innerTurn, innerCentre), r, end + quarter, end - innerTurn + Math.PI);
            Arc(center, ri, end - innerTurn, start + innerTurn);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The icon on the ring every icon shares, and the name under it. The icon swells a little with <paramref name="grow"/>.</summary>
    private static void DrawSlice(DrawingContext dc, WheelItem item, WheelLayout.Slice slice, WheelLayout layout, Point origin, double grow, double dpi)
    {
        var ink = Palette.WheelInk;
        if (slice.Icon is { } icon && item.Symbol is { } symbol && WpfTextMeasurer.Symbol(symbol, layout.SymbolSize) is { } outline)
        {
            var bounds = outline.Bounds;
            var scale = 1 + 0.06 * grow;
            var matrix = Matrix.Identity;
            matrix.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
            matrix.Scale(scale, scale);
            matrix.Translate(origin.X + icon.X, origin.Y - icon.Y);
            dc.PushTransform(new MatrixTransform(matrix));
            dc.DrawGeometry(Palette.Brush(ink), null, outline);
            dc.Pop();
        }
        if (slice.Text is not { } box) return;
        var bold = slice.Icon is null;
        var size = bold ? layout.LabelFontSize : layout.FontSize;
        var weight = WpfTextMeasurer.Weight(bold);
        var lines = WpfTextMeasurer.Lines(box.Height, size, weight);
        var text = WpfTextMeasurer.Format(item.Title, size, weight, ink, box.Width, lines, dpi);
        dc.DrawText(text, new Point(origin.X + box.MinX, origin.Y - box.MaxY));
    }

    /// <summary>Three dots on the rim: this slice opens a wheel of its own.</summary>
    private void DrawGroupMark(DrawingContext dc, double angle, double radius, bool contrast)
    {
        var brush = Palette.Brush(Ink(contrast ? 0.8 : 0.5));
        var gap = 5.5 / radius;
        for (var step = -1; step <= 1; step++)
            dc.DrawEllipse(brush, null, At(angle + step * gap, radius), 1.6, 1.6);
    }

    private void DrawHub(DrawingContext dc, Point center, double radius, double dpi)
    {
        var scale = Layout.TextScale;
        dc.DrawEllipse(Palette.Brush(Ink(HubHighlighted ? 0.2 : 0.08)), null, center, radius, radius);

        var width = radius * 2 - 18 * scale;
        var title = Title;
        var detail = Subtitle;
        ImageSource? image = fileIcon.Value;
        Geometry? chevron = null;
        if (Highlighted is { } index)
        {
            // Just the name, so there is room for all of it; VoiceOver still reads the detail.
            title = Items[index].Title;
            detail = "";
            image = null;
        }
        else if (ParentTitle is { } parent)
        {
            detail = L("Back to %@", parent);
            image = null;
            chevron = WpfTextMeasurer.Symbol("chevron.backward", 15 * scale);
        }
        else if (Items.Count == 0)
        {
            title = L("Nothing to do");
        }
        var titleSize = Math.Max(10.5, 12.5 * scale);
        var detailSize = Math.Max(9.5, 10.5 * scale);
        var titleWeight = FontWeights.SemiBold;
        // A name on its own may take a third line.
        var titleHeight = Math.Min((detail.Length == 0 ? 48 : 32) * scale, Math.Ceiling(WpfTextMeasurer.Format(title, titleSize, titleWeight, Colors.Black, width).Height));
        var detailHeight = detail.Length == 0 ? 0 : Math.Min(27 * scale, Math.Ceiling(WpfTextMeasurer.Format(detail, detailSize, FontWeights.Normal, Colors.Black, width).Height));
        var iconSize = image is not null ? new Size(30 * scale, 30 * scale) : chevron is { } c ? c.Bounds.Size : default;
        var total = iconSize.Height + (iconSize.Height > 0 ? 4 : 0) + titleHeight + (detail.Length == 0 ? 0 : 1 + detailHeight);
        var y = center.Y - total / 2;
        if (iconSize.Height > 0)
        {
            var area = new Rect(center.X - iconSize.Width / 2, y, iconSize.Width, iconSize.Height);
            if (image is not null) dc.DrawImage(image, area);
            else if (chevron is not null)
            {
                dc.PushTransform(new TranslateTransform(area.X - chevron.Bounds.X, area.Y - chevron.Bounds.Y));
                dc.DrawGeometry(Palette.Brush(Palette.WheelInk), null, chevron);
                dc.Pop();
            }
            y += iconSize.Height + 4;
        }
        dc.DrawText(WpfTextMeasurer.Format(title, titleSize, titleWeight, Palette.WheelInk, width, WpfTextMeasurer.Lines(titleHeight, titleSize, titleWeight), dpi),
                    new Point(center.X - width / 2, y));
        if (detail.Length == 0) return;
        y += titleHeight + 1;
        dc.DrawText(WpfTextMeasurer.Format(detail, detailSize, FontWeights.Normal, Ink(0.62), width, WpfTextMeasurer.Lines(detailHeight, detailSize, FontWeights.Normal), dpi),
                    new Point(center.X - width / 2, y));
    }
}
