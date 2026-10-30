using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using SkiaSharp.Views.WPF;

namespace Split.App;

/// <summary>One page in the grid: its picture, turned by its extra rotation, with its number under it.</summary>
public sealed class PageCell : Border
{
    private const double PageWidth = 130, PageHeight = 160;

    private readonly Border frame = new() { Width = PageWidth, Height = PageHeight, Margin = new Thickness(6), CornerRadius = new CornerRadius(10), ClipToBounds = false };
    private readonly Image picture = new() { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid paper = new() { Width = PageWidth, Height = PageHeight };
    private readonly Border white = new() { Background = Brushes.White };
    private readonly RotateTransform turn = new();
    private readonly TextBlock number;
    private readonly Border box = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Margin = new Thickness(-2) };
    private int rotation = int.MinValue;

    public PageCell(PageItem item, string? documentName)
    {
        Item = item;
        Background = Brushes.Transparent;
        Width = 142;
        Margin = new Thickness(0, 0, 18, 22);
        Cursor = Cursors.Hand;
        var stack = new StackPanel();
        paper.Children.Add(white);
        picture.RenderTransform = turn;
        paper.Children.Add(picture);
        paper.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 270, Opacity = 0.18, Color = Colors.Black };
        frame.Child = paper;
        var holder = new Grid();
        holder.Children.Add(box);
        holder.Children.Add(frame);
        stack.Children.Add(holder);
        number = Kit.Text("", 11, wrap: false);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(number);
        if (documentName is not null)
        {
            var name = Kit.Text(documentName, 10, secondary: true, wrap: false);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Width = PageWidth;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextAlignment = TextAlignment.Center;
            name.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(name);
        }
        Child = stack;
        System.Windows.Automation.AutomationProperties.SetName(this, documentName ?? "");
    }

    public PageItem Item { get; private set; }
    public bool IsSelected { get; private set; }
    public int Number { get; private set; }

    public void SetThumbnail(ImageSource source)
    {
        picture.Source = source;
        white.Visibility = Visibility.Collapsed;
        Fit();
    }

    public bool HasThumbnail => picture.Source is not null;

    public void Update(PageItem item, int pageNumber, bool selected)
    {
        Item = item;
        Number = pageNumber;
        IsSelected = selected;
        number.Text = pageNumber.ToString();
        number.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        number.Foreground = Palette.Brush(selected ? Palette.Primary : Palette.Secondary);
        box.Background = selected ? Palette.Brush(Palette.With(Palette.Accent, 0.18)) : Brushes.Transparent;
        box.BorderBrush = selected ? Palette.Brush(Palette.Accent) : Brushes.Transparent;
        System.Windows.Automation.AutomationProperties.SetName(this, $"{pageNumber}");
        if (rotation != item.Rotation)
        {
            var first = rotation == int.MinValue;
            var from = first ? item.Rotation : turn.Angle;
            rotation = item.Rotation;
            Fit();
            if (first || Motion.ReduceMotion) turn.Angle = rotation;
            else turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(from, TargetAngle(from, rotation), TimeSpan.FromSeconds(0.32)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>The rotation closest to where the picture is now, so that 270° → 0° turns a quarter, not three.</summary>
    private static double TargetAngle(double from, int to)
    {
        var angle = to + Math.Round((from - to) / 360) * 360;
        return angle;
    }

    /// <summary>A turned picture is fitted by its turned shape, so it stays inside the page frame.</summary>
    private void Fit()
    {
        var sideways = rotation / 90 % 2 != 0;
        picture.Width = sideways ? PageHeight : PageWidth;
        picture.Height = sideways ? PageWidth : PageHeight;
    }

    public void Dim(bool dim) => Opacity = dim ? 0.4 : 1;
}

/// <summary>Organize PDF: the pages as thumbnails to reorder, turn, duplicate, delete, combine with other PDFs and pictures, or save apart.</summary>
public sealed class PdfOrganizerEditor : EditorWindow
{
    private readonly PdfOrganizerModel model;
    private readonly Dictionary<Guid, PageCell> cells = [];
    private readonly Dictionary<(int, int), ImageSource> pictures = [];
    private readonly WrapPanel grid = new() { Margin = new Thickness(24, 24, 6, 2) };
    private EditorLayout layout;
    private TextBlock total = new(), selected = new(), note = new();
    private SplitButton duplicate = null!, remove = null!, extract = null!;
    private Guid? dragged;
    private Point pressedAt;
    private PageCell? pressed;

    public PdfOrganizerEditor(PdfOrganizerModel model, string title) : base(title)
    {
        this.model = model;
        model.Changed += () => Dispatcher.BeginInvoke(Sync);
        model.ThumbnailsChanged += () => Dispatcher.BeginInvoke(ShowThumbnails);
        Closed += (_, _) => model.Dispose();
        PreviewKeyDown += OnKeys;
        layout = Compose();
        Body = layout;
        Sync();
    }

    public PdfOrganizerModel Model => model;
    public EditorLayout Layout => layout;

    /// <summary>The page cells in the order they are shown, for the tests.</summary>
    public IReadOnlyList<PageCell> Cells => grid.Children.Cast<PageCell>().ToList();

    protected override bool HasChanges => model.HasChanges;

    protected override void Rebuild()
    {
        layout = Compose();
        Body = layout;
        Sync();
    }

    protected override void Accept() { if (layout.Save.IsEnabled) Save(false); }

    private void Save(bool onlySelection)
    {
        model.Save(onlySelection);
        CloseNow();
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) model.SelectAll();
        else if (e.Key is Key.Delete or Key.Back && model.Selection.Count > 0) model.RemoveSelected();
        else return;
        e.Handled = true;
    }

    private EditorLayout Compose()
    {
        grid.Children.Clear();
        cells.Clear();
        var scroll = new ScrollViewer
        {
            Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Background = Brushes.Transparent, AllowDrop = true,
        };
        scroll.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        scroll.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) model.AddFiles(files);
            e.Handled = true;
        };
        grid.PreviewMouseMove += OnDrag;
        grid.PreviewMouseLeftButtonUp += (_, _) => EndPress();

        total = Kit.Text("", TextSize.Body, secondary: true, wrap: false);
        selected = Kit.Text("", TextSize.Body, secondary: true, wrap: false);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 4) };
        void Add(SplitButton button) { button.Margin = new Thickness(0, 0, 6, 0); tools.Children.Add(button); }
        Add(EditorParts.Tool("rotate.left", L("Rotate Left"), () => model.Rotate(-90)));
        Add(EditorParts.Tool("rotate.right", L("Rotate Right"), () => model.Rotate(90)));
        duplicate = EditorParts.Tool("plus.square.on.square", L("Duplicate the Selected Pages"), model.DuplicateSelection);
        Add(duplicate);
        remove = EditorParts.Tool("trash", L("Delete the Selected Pages (⌫)"), model.RemoveSelected);
        Add(remove);
        var selectAll = new SplitButton(L("Select All Pages")) { Margin = new Thickness(12, 4, 12, 12), HorizontalAlignment = HorizontalAlignment.Left };
        selectAll.Click += (_, _) => model.SelectAll();

        var pages = new StackPanel();
        pages.Children.Add(Kit.Row(Kit.Text(L("Total"), TextSize.Body, wrap: false), total));
        pages.Children.Add(Kit.Line(0.08));
        pages.Children.Add(Kit.Row(Kit.Text(L("Selected"), TextSize.Body, wrap: false), selected));
        pages.Children.Add(Kit.Line(0.08));
        pages.Children.Add(tools);
        pages.Children.Add(selectAll);

        var add = new SplitButton(L("Add PDFs or Images…")) { Margin = new Thickness(12, 12, 12, 8), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => ChooseFiles();
        note = EditorParts.Note("");
        note.Margin = new Thickness(12, 0, 12, 12);
        var combine = new StackPanel();
        combine.Children.Add(add);
        var hint = EditorParts.Note(L("You can also drag files from File Explorer onto the grid. Drag pages to change their order."));
        hint.Margin = new Thickness(12, 0, 12, 12);
        combine.Children.Add(hint);
        combine.Children.Add(note);

        extract = new SplitButton(L("Save Only the Selected Pages…")) { Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Left };
        extract.Click += (_, _) => Save(true);

        var built = new EditorLayout(scroll,
        [
            Kit.Section(L("Pages"), Kit.Panel(pages)),
            Kit.Section(L("Combine"), Kit.Panel(combine)),
            Kit.Section(L("Extract"), Kit.Panel(extract)),
        ], L("Save PDF"), null, CloseNow, () => Save(false));
        return built;
    }

    /// <summary>Brings the grid and the inspector in line with the model.</summary>
    private void Sync()
    {
        var items = model.Items;
        var live = items.Select(i => i.Id).ToHashSet();
        foreach (var gone in cells.Keys.Where(id => !live.Contains(id)).ToList()) cells.Remove(gone);
        var many = model.Sources.Count > 1;
        grid.Children.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!cells.TryGetValue(item.Id, out var cell))
            {
                cell = new PageCell(item, many ? model.Sources[item.Source].Name : null);
                cell.PreviewMouseLeftButtonDown += OnPress;
                cells[item.Id] = cell;
            }
            cell.Update(item, i + 1, model.Selection.Contains(item.Id));
            cell.Dim(dragged == item.Id);
            grid.Children.Add(cell);
        }
        ShowThumbnails();
        total.Text = items.Count.ToString();
        selected.Text = model.Selection.Count == 0 ? L("none (applies to all)") : model.Selection.Count.ToString();
        note.Text = model.Note ?? "";
        note.Visibility = model.Note is null ? Visibility.Collapsed : Visibility.Visible;
        var any = model.Selection.Count > 0;
        duplicate.IsEnabled = remove.IsEnabled = extract.IsEnabled = any;
        layout.Save.IsEnabled = items.Count > 0;
    }

    private void ShowThumbnails()
    {
        foreach (var cell in cells.Values.Where(c => !c.HasThumbnail))
        {
            var key = (cell.Item.Source, cell.Item.Page);
            if (!pictures.TryGetValue(key, out var source))
            {
                if (model.Thumbnail(cell.Item) is not { } bitmap) continue;
                source = bitmap.ToWriteableBitmap();
                source.Freeze();
                pictures[key] = source;
            }
            cell.SetThumbnail(source);
        }
    }

    // MARK: Pointer

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        Press((PageCell)sender, e.GetPosition(grid));
        grid.CaptureMouse();
        e.Handled = true;
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) Drag(e.GetPosition(grid));
    }

    private void EndPress() => Release((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0);

    /// <summary>The pointer went down on a page (grid coordinates), for the pointer and for the tests.</summary>
    public void Press(PageCell cell, Point at)
    {
        pressed = cell;
        pressedAt = at;
    }

    /// <summary>The pointer moved while down: past a few points it carries the page, and over another page it takes that page's place.</summary>
    public void Drag(Point point)
    {
        if (pressed is null) return;
        if (dragged is null)
        {
            if (Math.Abs(point.X - pressedAt.X) < 4 && Math.Abs(point.Y - pressedAt.Y) < 4) return;
            dragged = pressed.Item.Id;
            pressed.Dim(true);
        }
        if (cells.Values.FirstOrDefault(c => c.Item.Id != dragged && new Rect(c.TranslatePoint(new Point(), grid), new Size(c.ActualWidth, c.ActualHeight)).Contains(point)) is { } over)
            model.Move(dragged.Value, over.Item.Id);
    }

    /// <summary>The pointer came up: a drag ends, and a press without one is a click on the page.</summary>
    public void Release(bool extend)
    {
        var cell = pressed;
        var drag = dragged;
        (pressed, dragged) = (null, null);
        grid.ReleaseMouseCapture();
        if (drag is { } id && cells.TryGetValue(id, out var moved)) moved.Dim(false);
        else if (cell is not null) Click(cell.Item.Id, extend);
    }

    /// <summary>A click on a page: it alone is selected (or none if it was), and with Ctrl or Shift it joins or leaves the selection.</summary>
    public void Click(Guid id, bool extend) => model.Toggle(id, extend);

    private void ChooseFiles()
    {
        var patterns = string.Join(";", Enum.GetValues<Format>().Where(f => f == Format.Pdf || f.Group() == FormatGroup.Image).Select(f => "*." + f.Ext()));
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = $"{patterns}|{patterns}" };
        if (dialog.ShowDialog(this) == true) model.AddFiles(dialog.FileNames);
    }
}
