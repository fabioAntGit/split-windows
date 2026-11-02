using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using Split.Windows;

namespace Split.App;

/// <summary>The files on the shelf: held for a moment, so they can be collected from several folders and dragged out together.</summary>
public sealed class ShelfModel
{
    public List<string> Items { get; } = [];

    public event Action? Changed;

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!Items.Contains(path, StringComparer.OrdinalIgnoreCase)) Items.Add(path);
        }
        Changed?.Invoke();
    }

    public void Remove(string path)
    {
        Items.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        Changed?.Invoke();
    }

    public void Clear()
    {
        Items.Clear();
        Changed?.Invoke();
    }

    /// <summary>Files moved or deleted elsewhere disappear from the shelf.</summary>
    public void Prune()
    {
        Items.RemoveAll(item => !File.Exists(item) && !Directory.Exists(item));
        Changed?.Invoke();
    }
}

/// <summary>The shelf as a small floating window, always on top and never taking the focus.</summary>
public sealed class Shelf
{
    public static Shelf Shared { get; } = new();

    public ShelfModel Model { get; } = new();

    /// <summary>Set when the program quits, so closing the window then really closes it.</summary>
    public bool Quitting { get; set; }

    private Window? panel;

    public bool IsVisible => panel?.IsVisible ?? false;

    public void Show(IEnumerable<string>? adding = null)
    {
        if (adding is not null) Model.Add(adding);
        Model.Prune();
        var window = panel ??= MakeWindow();
        if (window.IsVisible) return;
        window.Opacity = 0;
        window.Show();
        Place(window);
        window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(Motion.ReduceMotion ? 0.12 : 0.2)) { FillBehavior = FillBehavior.Stop });
        window.Opacity = 1;
    }

    /// <summary>Against the right edge of the screen under the pointer, where the files were just dropped, in that screen's pixels.</summary>
    public static void Place(Window window)
    {
        var (work, scale) = Desktop.MonitorAtCursor();
        var (width, height) = (window.Width * scale, window.Height * scale);
        var handle = new WindowInteropHelper(window).Handle;
        // Twice: landing on a screen with another scale makes Windows resize the window for that screen first.
        for (var pass = 0; pass < 2; pass++)
            SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(work.Right - 20 * scale - width), (int)Math.Round(work.Y + (work.Height - height) / 2),
                         (int)Math.Round(width), (int)Math.Round(height), 0x14 /* SWP_NOZORDER | SWP_NOACTIVATE */);
    }

    public void Toggle()
    {
        if (IsVisible) panel?.Close(); else Show();
    }

    /// <summary>The window, built but not shown, so the tests can look at it.</summary>
    public Window MakeWindow()
    {
        var view = new ShelfView(Model);
        var window = new Window
        {
            Title = L("Shelf"),
            Width = 280,
            Height = 380,
            MinWidth = 240,
            MinHeight = 220,
            WindowStyle = WindowStyle.ToolWindow,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = view,
        };
        // Moved by its title bar only: dragging files out of the shelf must never drag the window too.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var style = GetWindowLong(handle, -20);
            SetWindowLong(handle, -20, style | HudWindow.NoActivate);
            Paint();
        };
        void Paint()
        {
            window.Background = Palette.Brush(Palette.WheelSurface);
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                var color = Palette.WheelSurface;
                Desktop.ColorTitleBar(handle, !Palette.IsWhite, color.R << 16 | color.G << 8 | color.B);
            }
        }
        Look.Changed += () => window.Dispatcher.BeginInvoke(Paint);
        Paint();
        window.Closing += (_, e) =>
        {
            if (Quitting) return;
            e.Cancel = true;
            window.Hide();
        };
        return window;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

/// <summary>The contents of the shelf window: the list of files, and the bar that drags them all.</summary>
public sealed class ShelfView : Border
{
    private readonly ShelfModel model;
    private bool targeted;
    private Rectangle? dashes;

    public ShelfView(ShelfModel model)
    {
        this.model = model;
        AllowDrop = true;
        Background = Brushes.Transparent;
        DragEnter += (_, e) => Target(e, true);
        DragOver += (_, e) => Target(e, true);
        DragLeave += (_, _) => Target(null, false);
        Drop += (_, e) =>
        {
            Target(null, false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) model.Add(paths);
            e.Handled = true;
        };
        model.Changed += Rebuild;
        Look.Changed += () => Dispatcher.BeginInvoke(Rebuild);
        Rebuild();
    }

    private void Target(DragEventArgs? e, bool on)
    {
        var files = e?.Data.GetDataPresent(DataFormats.FileDrop) ?? false;
        if (e is not null)
        {
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        var now = on && files;
        if (now == targeted) return;
        targeted = now;
        if (dashes is not null) dashes.Stroke = Palette.Brush(targeted ? Palette.Accent : Palette.With(Palette.Primary, 0.2));
    }

    /// <summary>The shelf's contents drawn from the model: the header, then the list or the empty state.</summary>
    private void Rebuild()
    {
        Background = Palette.Brush(Palette.WheelSurface);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(14, 10, 14, 8) };
        var count = model.Items.Count;
        header.Children.Add(Fonts.Text(count == 0 ? L("Shelf") : count == 1 ? L("1 item") : L("%@ items", count), 13, FontWeights.SemiBold));
        if (count > 0) header.Children.Add(ActionsButton());
        root.Children.Add(header);

        if (count == 0)
        {
            var empty = EmptyState();
            Grid.SetRow(empty, 1);
            root.Children.Add(empty);
        }
        else
        {
            var list = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
            foreach (var path in model.Items.ToList()) list.Children.Add(Row(path));
            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var divider = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = Palette.Brush(Palette.With(Palette.Primary, 0.12)) };
            var handle = new DragAllHandle(() => model.Items.ToList()) { Height = 50 };
            var bottom = new Grid();
            bottom.Children.Add(handle);
            bottom.Children.Add(divider);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);
        }
        Child = root;
    }

    private Button ActionsButton()
    {
        var button = new Button
        {
            Width = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            Focusable = false,
            ToolTip = L("Actions"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = Icons.Block("ellipsis.circle", 15),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L("Actions"));
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            MenuLook.Fill(menu,
            [
                new(L("Convert…"), "arrow.triangle.2.circlepath", () => WheelLauncher.Show(WheelKind.Convert, model.Items.ToList(), clickMode: true)),
                new(L("Tools…"), "wrench.and.screwdriver", () => WheelLauncher.Show(WheelKind.Tools, model.Items.ToList(), clickMode: true)),
                new(L("Show in File Explorer"), "folder", () => Explorer.Reveal(model.Items.ToList())),
                new(null),
                new(L("Empty the Shelf"), "trash", model.Clear),
            ]);
            menu.IsOpen = true;
        };
        return button;
    }

    private UIElement EmptyState()
    {
        dashes = new Rectangle
        {
            RadiusX = 12,
            RadiusY = 12,
            StrokeThickness = 1.5,
            StrokeDashArray = [6 / 1.5, 4 / 1.5],
            Stroke = Palette.Brush(targeted ? Palette.Accent : Palette.With(Palette.Primary, 0.2)),
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
        var icon = Icons.Block("tray.and.arrow.down", 28, targeted ? Palette.Accent : Palette.Secondary);
        stack.Children.Add(icon);
        var title = Fonts.Text(L("Drop files here to collect them"), 12, FontWeights.Medium);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.TextAlignment = TextAlignment.Center;
        title.TextWrapping = TextWrapping.Wrap;
        title.Margin = new Thickness(0, 8, 0, 0);
        var detail = Fonts.Text(L("Then drag them all at once to wherever you need them."), 11, color: Palette.Secondary);
        detail.TextAlignment = TextAlignment.Center;
        detail.TextWrapping = TextWrapping.Wrap;
        detail.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(title);
        stack.Children.Add(detail);
        var grid = new Grid { Margin = new Thickness(12) };
        grid.Children.Add(dashes);
        grid.Children.Add(stack);
        return grid;
    }

    private UIElement Row(string path)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Width = 26, Height = 26, Source = FileIcons.Of(path), Margin = new Thickness(0, 0, 8, 0) };
        grid.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Fonts.Text(Path.GetFileName(path.TrimEnd('\\')), 12);
        Fonts.TrimMiddle(name);
        var folder = Fonts.Text(Path.GetFileName(Path.GetDirectoryName(path)?.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : Path.GetDirectoryName(path) ?? "", 10.5, color: Palette.Secondary);
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        folder.Margin = new Thickness(0, 1, 0, 0);
        texts.Children.Add(name);
        texts.Children.Add(folder);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var remove = new Button
        {
            Visibility = Visibility.Collapsed,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Content = Icons.Block("xmark.circle.fill", 15, Palette.Secondary),
            ToolTip = L("Remove from the shelf (the file isn’t deleted)"),
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, L("Remove from the shelf (the file isn’t deleted)"));
        remove.Click += (_, _) => model.Remove(path);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent, Child = grid };
        row.MouseEnter += (_, _) =>
        {
            row.Background = Palette.Brush(Palette.With(Palette.Primary, 0.07));
            remove.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            remove.Visibility = Visibility.Collapsed;
        };
        DragStart.On(row, () => [path]);
        return row;
    }
}

/// <summary>Starts a drag of files once the pointer has moved far enough with the button held.</summary>
public static class DragStart
{
    public static void On(UIElement element, Func<IReadOnlyList<string>> paths)
    {
        Point? down = null;
        element.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(null);
        element.PreviewMouseLeftButtonUp += (_, _) => down = null;
        element.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(null);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            down = null;
            var list = paths();
            if (list.Count == 0) return;
            // The drop decides, as a drag from File Explorer does: a move on the same drive, a copy elsewhere, Shift or Ctrl to choose.
            DragDrop.DoDragDrop(element, new DataObject(DataFormats.FileDrop, list.ToArray()), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        };
    }
}

/// <summary>The whole bar is the handle, so any press on it starts the drag of every file.</summary>
public sealed class DragAllHandle : Border
{
    public DragAllHandle(Func<IReadOnlyList<string>> paths)
    {
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        var grid = new Grid { Margin = new Thickness(16, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Icons.Block("square.stack.3d.up.fill", 18, Palette.Accent);
        icon.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Fonts.Text(L("Drag All"), 12, FontWeights.Medium);
        var detail = Fonts.Text(L("Drop in another app or folder"), 10.5, color: Palette.Secondary);
        detail.Margin = new Thickness(0, 1, 0, 0);
        title.TextTrimming = detail.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(title);
        texts.Children.Add(detail);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Child = grid;
        System.Windows.Automation.AutomationProperties.SetName(this, L("Drag All"));
        DragStart.On(this, paths);
    }
}

/// <summary>The icon Explorer shows for a file or folder.</summary>
public static class FileIcons
{
    public static BitmapSource? Of(string path)
    {
        var info = new FileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<FileInfo>(), 0x100 | 0x0) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { DestroyIcon(info.Icon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
