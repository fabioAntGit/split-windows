using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Split.App;

/// <summary>
/// What each wheel shows, where each slice sits (dragged on a live preview of the wheel, or from the list onto
/// it), the groups that gather several slices behind one, what each slice does, and how the wheel looks.
/// </summary>
public sealed class WheelsTab : TabPage
{
    /// <summary>The preview's square, and the size of the wheel in it: always the same, since it is there to arrange the slices, whatever size the wheel opens at.</summary>
    public const double PreviewSide = 330;

    private static readonly WheelSize PreviewSize = WheelSize.ExtraLarge;
    private static double Scale => PreviewSide / WheelLayout.WindowSize(PreviewSize);

    /// <summary>A slice being dragged on the preview, and the place it would take; an unchecked slice or group dragged in from the list is shown while it is over the wheel.</summary>
    public sealed record PreviewDrag(string Id, int To, string? Revealed = null);

    /// <summary>What the preview shows: the wheel as it opens, or the open group, and the slice or group of the draft each of its slices stands for.</summary>
    private sealed record Shown(List<WheelItem> Items, string Title, string? Back, Func<string, string> Owner);

    private WheelKind wheel = WheelKind.Convert;
    private SourceKind source = SourceKind.Image;
    private WheelDraft draft = new();
    private readonly HashSet<string> selection = [];
    private string? group;
    private WheelSize size = Settings.WheelSize;
    private bool names = Settings.WheelNames;
    private PreviewDrag? drag;
    private string? listDrag;
    private List<WheelChoice> choices = [];
    private List<WheelGroup> defaultGroups = [];
    private bool loaded;
    private double listOffset;
    private Grid preview = new();
    private Image picture = new();
    private Point? pressedAt;
    private Point? rowPressed;

    // What the tests and the window read.
    public WheelKind Wheel => wheel;
    public SourceKind Source => source;
    public WheelDraft Draft => draft;
    public IReadOnlySet<string> Selection => selection;
    public string? OpenGroupId => group;
    public PreviewDrag? Drag => drag;
    public Grid PreviewElement => preview;

    private WheelScope Scope => wheel switch
    {
        WheelKind.Convert => WheelScope.Convert(source),
        WheelKind.Tools => WheelScope.Tools,
        _ => WheelScope.Actions,
    };

    private List<string> Ids => choices.Select(c => c.Id).ToList();
    private WheelArrangement Arrangement => draft.Arrangement(Ids, defaultGroups);
    private string WheelTitle => wheel == WheelKind.Convert ? source.Title() : wheel.Title();
    private WheelEntry? OpenGroupEntry => group is null ? null : draft.Entry(group) is { IsGroup: true } entry ? entry : null;
    private string? Focused => selection.Count == 1 ? selection.First() : group;

    private void Load()
    {
        var kinds = WheelCatalog.ArrangeableKinds;
        if (!kinds.Contains(source) && kinds.Count > 0) source = kinds[0];
        choices = WheelCatalog.Choices(Scope);
        defaultGroups = WheelCatalog.DefaultGroups(Scope);
        draft = new WheelDraft(Scope.Arrangement, Ids, defaultGroups);
        selection.Clear();
        group = null;
        drag = null;
        listDrag = null;
        listOffset = 0;
    }

    /// <summary>Whatever matches the app's defaults is saved as nothing, so it keeps following them.</summary>
    private void Save() => Scope.Arrangement = Arrangement;

    private void Redo() => Dispatcher.BeginInvoke(Rebuild);

    // MARK: Layout

    protected override UIElement Build()
    {
        if (!loaded) { Load(); loaded = true; }
        var kinds = WheelCatalog.ArrangeableKinds;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var kindsRow = new Segmented(WheelKinds.All.Select(k => k.Title()).ToList(), WheelKinds.All.ToList().IndexOf(wheel), i => SelectWheel(WheelKinds.All[i]), L("Wheel"), 290);
        kindsRow.HorizontalAlignment = HorizontalAlignment.Left;
        top.Children.Add(kindsRow);
        if (wheel == WheelKind.Convert)
        {
            var files = new StackPanel { Orientation = Orientation.Horizontal };
            var label = Kit.Text(L("Files"), 13, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 10, 0);
            files.Children.Add(label);
            files.Children.Add(new DropDown(kinds.Select(k => k.Title()).ToList(), kinds.IndexOf(source), i => SelectSource(kinds[i]), L("Files"), 190));
            Grid.SetColumn(files, 1);
            top.Children.Add(files);
        }

        var left = new Grid { Width = 300, VerticalAlignment = VerticalAlignment.Stretch };
        left.RowDefinitions.Add(new RowDefinition());
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var listArea = OpenGroupEntry is { } open ? GroupEditor(open) : WheelList();
        left.Children.Add(listArea);
        var line = Kit.Line();
        line.Margin = new Thickness(0, 10, 0, 10);
        Grid.SetRow(line, 1);
        left.Children.Add(line);
        var look = Appearance();
        Grid.SetRow(look, 2);
        left.Children.Add(look);

        var right = new StackPanel { Width = PreviewSide, Margin = new Thickness(18, 0, 0, 0) };
        right.Children.Add(MakePreview());
        var about = About();
        about.Margin = new Thickness(0, 12, 0, 0);
        right.Children.Add(about);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.Children.Add(left);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        columns.Margin = new Thickness(0, 14, 0, 0);

        var hint = Kit.Text(OpenGroupEntry is null
            ? L("Drag the slices on the wheel, or rows from the list onto it, to put them where you want them. Unchecked slices don’t appear.")
              + " " + L("To put several slices behind one that opens them, select them (Ctrl-click) and click Group.")
            : L("These slices open from the group’s slice: drag them on the wheel, or from the list onto it, to change their order. A group left with one slice shows that slice on the wheel instead."), 11, secondary: true);
        hint.Margin = new Thickness(0, 12, 0, 0);

        var page = new Grid { Margin = new Thickness(20) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.Children.Add(top);
        Grid.SetRow(columns, 1);
        page.Children.Add(columns);
        Grid.SetRow(hint, 2);
        page.Children.Add(hint);
        return page;
    }

    public void SelectWheel(WheelKind kind)
    {
        wheel = kind;
        Load();
        Redo();
    }

    public void SelectSource(SourceKind kind)
    {
        source = kind;
        Load();
        Redo();
    }

    // MARK: The list: what shows, and groups

    private UIElement WheelList()
    {
        var rows = new StackPanel();
        foreach (var entry in draft.Entries) rows.Children.Add(Row(entry));
        var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        var chosen = ChosenSlices;
        var make = new SplitButton(L("Group")) { IsEnabled = chosen.Count >= 2, HorizontalAlignment = HorizontalAlignment.Left };
        make.Click += (_, _) => MakeGroup(ChosenSlices);
        var restore = new SplitButton(L("Restore Defaults")) { IsEnabled = !Arrangement.IsDefault, HorizontalAlignment = HorizontalAlignment.Right };
        restore.Click += (_, _) => RestoreDefaults();
        buttons.Children.Add(make);
        buttons.Children.Add(restore);
        return ListWithButtons(rows, buttons);
    }

    private UIElement ListWithButtons(StackPanel rows, UIElement buttons, UIElement? above = null)
    {
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var offset = listOffset;
        var ready = false;
        scroll.ScrollChanged += (_, e) => { if (ready) listOffset = e.VerticalOffset; };
        scroll.Loaded += (_, _) => { scroll.UpdateLayout(); scroll.ScrollToVerticalOffset(offset); ready = true; };
        var box = Kit.Panel(scroll, 8, 0.05);
        box.BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.1));
        box.BorderThickness = new Thickness(0.5);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (above is not null) grid.Children.Add(above);
        Grid.SetRow(box, 1);
        grid.Children.Add(box);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        return grid;
    }

    private UIElement Row(WheelEntry entry)
    {
        if (entry.Members is not { } members)
        {
            var choice = ChoiceOf(entry.Id);
            return SliceRow(entry.Id, choice?.Title ?? entry.Id, choice?.Symbol, null, null, groupOf: null);
        }
        var titles = members.Select(m => ChoiceOf(m)?.Title).OfType<string>();
        return SliceRow(entry.Id, TitleOf(entry), SymbolOf(entry) ?? "square.stack", string.Join(", ", titles), () => OpenGroup(entry.Id), groupOf: null);
    }

    /// <summary>One slice or group in the list: whether it shows, and ways to move it without dragging. A group lists its slices and opens with the chevron.</summary>
    private Border SliceRow(string id, string title, string? symbol, string? detail, Action? open, string? groupOf)
    {
        var shown = !draft.Hidden.Contains(id);
        var grid = new Grid { Margin = new Thickness(8, 5, 8, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var check = new Toggle(shown, title, check: true) { VerticalAlignment = VerticalAlignment.Center };
        check.Changed += on => SetShown(id, on);
        // A click on the box only flips it; it must not also select the row.
        check.MouseLeftButtonDown += (_, e) => e.Handled = true;
        grid.Children.Add(check);
        var column = 1;
        if (symbol is not null)
        {
            var icon = Kit.Symbol(symbol, 15, secondary: !shown);
            icon.Width = 20;
            icon.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);
            column = 2;
        }
        var text = new StackPanel { Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Kit.Text(title, 13, secondary: !shown, wrap: false));
        if (detail is not null)
        {
            var sub = Kit.Text(detail, 10, secondary: true, wrap: false);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(sub);
        }
        Grid.SetColumn(text, column);
        Grid.SetColumnSpan(text, column == 1 ? 2 : 1);
        grid.Children.Add(text);
        if (open is not null)
        {
            var chevron = Kit.Link("", open, "chevron.right", 12);
            chevron.ToolTip = L("Open Group");
            System.Windows.Automation.AutomationProperties.SetName(chevron, L("Open Group"));
            chevron.MouseLeftButtonDown += (_, e) => e.Handled = true;
            Grid.SetColumn(chevron, 3);
            grid.Children.Add(chevron);
        }
        var row = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(5), Margin = new Thickness(4, 2, 4, 0), Tag = id, Cursor = Cursors.Arrow, Focusable = true,
            Background = selection.Contains(id) ? Palette.Brush(Palette.With(Palette.Primary, 0.16)) : Brushes.Transparent,
        };
        System.Windows.Automation.AutomationProperties.SetName(row, title);
        row.MouseLeftButtonDown += (_, e) => rowPressed = e.GetPosition(row);
        row.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || rowPressed is not { } from) return;
            var now = e.GetPosition(row);
            if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            rowPressed = null;
            DragFromList(row, id);
        };
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (rowPressed is null) return;
            rowPressed = null;
            ClickRow(id);
        };
        row.MouseRightButtonUp += (_, e) => { PopupMenu.Show(row, MenuFor(id, group is not null)); e.Handled = true; };
        return row;
    }

    /// <summary>A click selects the row; Ctrl-click adds it to, or takes it out of, the selection.</summary>
    public void ClickRow(string id)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { if (!selection.Remove(id)) selection.Add(id); }
        else { selection.Clear(); selection.Add(id); }
        Redo();
    }

    /// <summary>The same, for code that clicks with the Ctrl key held (the tests).</summary>
    public void ClickRow(string id, bool control)
    {
        if (control) { if (!selection.Remove(id)) selection.Add(id); }
        else { selection.Clear(); selection.Add(id); }
        Rebuild();
    }

    private List<PopupItem> MenuFor(string id, bool inGroup)
    {
        var items = new List<PopupItem>();
        var entry = draft.Entry(id);
        if (!inGroup && entry is { IsGroup: true })
        {
            items.Add(new(L("Open Group"), () => OpenGroup(id)));
            items.Add(new(L("Ungroup"), () => { draft.Ungroup(id); Save(); Redo(); }));
            items.Add(new(null));
        }
        else if (inGroup)
        {
            items.Add(new(L("Remove from Group"), () => TakeOut(id, group!)));
            items.Add(new(null));
        }
        else
        {
            var slices = selection.Contains(id) && ChosenSlices.Count > 1 ? ChosenSlices : [id];
            items.Add(new(L("New Group"), () => MakeGroup(slices)));
            foreach (var target in draft.Entries.Where(e => e.IsGroup))
            {
                items.Add(new(L("Add to Group") + " › " + TitleOf(target), () =>
                {
                    foreach (var slice in slices) draft.Add(slice, target.Id);
                    selection.Clear();
                    Save();
                    Redo();
                }));
            }
            items.Add(new(null));
        }
        items.Add(new(L("Move to Top"), () => MoveRow(id, DraftMove.Top)));
        items.Add(new(L("Move Up"), () => MoveRow(id, DraftMove.Up)));
        items.Add(new(L("Move Down"), () => MoveRow(id, DraftMove.Down)));
        items.Add(new(L("Move to Bottom"), () => MoveRow(id, DraftMove.Bottom)));
        return items;
    }

    // MARK: A group

    private UIElement GroupEditor(WheelEntry entry)
    {
        var members = entry.Members ?? [];
        var loose = draft.Entries.Where(e => !e.IsGroup).ToList();
        var back = Kit.Link(WheelTitle, CloseGroup, "chevron.backward", 13);
        System.Windows.Automation.AutomationProperties.SetName(back, L("Back to %@", WheelTitle));
        back.HorizontalAlignment = HorizontalAlignment.Left;
        var prompt = WheelCatalog.GroupTitle(entry.Id, null, members.FirstOrDefault() is { } first ? ChoiceOf(first)?.Title : null);
        var name = new Field(draft.Names.GetValueOrDefault(entry.Id) ?? "", prompt, text =>
        {
            draft.Names[entry.Id] = text;
            Save();
            RefreshPreview();
        }, L("Group name")) { Margin = new Thickness(0, 6, 0, 8) };
        var head = new StackPanel();
        head.Children.Add(back);
        head.Children.Add(name);

        var rows = new StackPanel();
        foreach (var id in members) rows.Children.Add(SliceRow(id, ChoiceOf(id)?.Title ?? id, ChoiceOf(id)?.Symbol, null, null, entry.Id));

        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var add = new SplitButton(L("Add Slice") + "  ▾") { IsEnabled = loose.Count > 0 };
        add.Click += (_, _) => PopupMenu.Show(add, loose.Select(slice => new PopupItem(ChoiceOf(slice.Id)?.Title ?? slice.Id, () =>
        {
            draft.Add(slice.Id, entry.Id);
            Save();
            Redo();
        })));
        var remove = new SplitButton(L("Remove from Group")) { IsEnabled = members.Any(selection.Contains), Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) =>
        {
            foreach (var id in members.Where(selection.Contains).ToList()) TakeOut(id, entry.Id);
        };
        var ungroup = new SplitButton(L("Ungroup"));
        ungroup.Click += (_, _) =>
        {
            draft.Ungroup(entry.Id);
            CloseGroup();
            Save();
        };
        DockPanel.SetDock(ungroup, Dock.Right);
        buttons.Children.Add(ungroup);
        buttons.Children.Add(add);
        buttons.Children.Add(remove);
        return ListWithButtons(rows, buttons, head);
    }

    // MARK: The wheel, live

    /// <summary><paramref name="revealed"/> is an unchecked slice or group shown anyway, while its row is dragged over the wheel.</summary>
    private Shown ShownWheel(string? revealed = null)
    {
        var shown = draft.Copy();
        if (revealed is not null) shown.Hidden.Remove(revealed);
        var offered = choices.Select(c => new WheelItem(c.Id, c.Title, c.Symbol)).ToList();
        if (OpenGroupEntry is { } open)
        {
            var members = (open.Members ?? []).Where(id => !shown.Hidden.Contains(id)).Select(id => offered.FirstOrDefault(o => o.Id == id)).OfType<WheelItem>().ToList();
            return new Shown(members, TitleOf(open), wheel.Title(), id => id);
        }
        var arrangement = shown.Arrangement(Ids, defaultGroups);
        var items = WheelCatalog.Arranged(offered, arrangement, defaultGroups);
        // A group left with one slice shows that slice in its place, so the slice moves the group.
        var saved = shown;
        string Owner(string id) => saved.Entry(id) is null ? saved.GroupOf(id) ?? id : id;
        // A wheel holding a single group shows that group's slices: they move inside it.
        var single = items.Count > 1 && items.Select(i => Owner(i.Id)).Distinct().Count() == 1;
        return new Shown(items, wheel.Title(), null, single ? id => id : Owner);
    }

    private Border MakePreview()
    {
        preview = new Grid { Width = PreviewSide, Height = PreviewSide, Background = Brushes.Transparent, AllowDrop = true, SnapsToDevicePixels = true };
        preview.Children.Add(DesktopPicture.Element(PreviewSide));
        picture = new Image { Width = PreviewSide, Height = PreviewSide, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        preview.Children.Add(picture);
        RefreshPreview();

        preview.MouseLeftButtonDown += (_, e) => { pressedAt = e.GetPosition(preview); preview.CaptureMouse(); };
        preview.MouseMove += (_, e) => PreviewMoved(e);
        preview.MouseLeftButtonUp += (_, e) =>
        {
            if (!preview.IsMouseCaptured) return;
            preview.ReleaseMouseCapture();
            var at = e.GetPosition(preview);
            pressedAt = null;
            if (drag is { } finished)
            {
                drag = null;
                DropOn(finished, ShownWheel());
            }
            else ClickPreview(at);
        };
        preview.DragEnter += (_, e) => ListOver(e);
        preview.DragOver += (_, e) => ListOver(e);
        preview.DragLeave += (_, _) => { if (listDrag is not null && drag is not null) { drag = null; RefreshPreview(); } };
        preview.Drop += (_, e) =>
        {
            e.Handled = true;
            var landing = listDrag is null ? null : Landing(listDrag, e.GetPosition(preview));
            drag = null;
            if (landing is null) { RefreshPreview(); return; }
            DropOn(landing, ShownWheel(landing.Revealed));
        };

        var frame = new Border
        {
            Child = preview, Width = PreviewSide, Height = PreviewSide, CornerRadius = new CornerRadius(12), ClipToBounds = true,
            BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.1)), BorderThickness = new Thickness(0.5),
        };
        frame.Clip = new RectangleGeometry(new Rect(0, 0, PreviewSide, PreviewSide), 12, 12);
        System.Windows.Automation.AutomationProperties.SetName(frame, L("Preview of the wheel"));
        System.Windows.Automation.AutomationProperties.SetItemStatus(frame, string.Join(", ", ShownWheel().Items.Select(i => i.Title)));
        return frame;
    }

    private void ListOver(DragEventArgs e)
    {
        e.Handled = true;
        if (listDrag is null || e.Data.GetData(DataFormats.UnicodeText) as string != listDrag) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        var next = Landing(listDrag, e.GetPosition(preview));
        if (next == drag) return;
        drag = next;
        RefreshPreview();
    }

    /// <summary>Draws the wheel again as the draft and any drag in progress have it: the slice dragged sits in the place it would take.</summary>
    public void RefreshPreview()
    {
        var shown = ShownWheel(drag?.Revealed);
        var items = shown.Items.ToList();
        int? highlighted = selection.Count == 1 && items.FindIndex(i => selection.Contains(i.Id)) is var at and >= 0 ? at : null;
        if (drag is { } active && items.FindIndex(i => i.Id == active.Id) is var from and >= 0)
        {
            var moving = items[from];
            items.RemoveAt(from);
            items.Insert(Math.Min(active.To, items.Count), moving);
            highlighted = Math.Min(active.To, items.Count - 1);
        }
        picture.Source = WheelView.Picture(items, shown.Title, wheel == WheelKind.Convert ? source.Title() : "", highlighted, new WheelStyle(PreviewSize, names), shown.Back);
    }

    private void PreviewMoved(MouseEventArgs e)
    {
        var at = e.GetPosition(preview);
        var shown = ShownWheel();
        if (drag is null && pressedAt is { } start && preview.IsMouseCaptured)
        {
            if ((at - start).Length > 4 && SliceAt(start, shown) is { } index)
            {
                drag = new PreviewDrag(shown.Items[index].Id, index);
                preview.Cursor = Cursors.SizeAll;
                RefreshPreview();
            }
            return;
        }
        if (drag is { } current)
        {
            if (SliceAt(at, shown) is { } to && to != current.To)
            {
                drag = current with { To = to };
                RefreshPreview();
            }
            return;
        }
        preview.Cursor = SliceAt(at, shown) is not null ? Cursors.Hand : Cursors.Arrow;
    }

    private static Pt Offset(Point location)
    {
        var middle = PreviewSide / 2;
        return new Pt((location.X - middle) / Scale, (middle - location.Y) / Scale);
    }

    private int? SliceAt(Point location, Shown shown) => WheelLayout.SliceAt(Offset(location), shown.Items.Count, PreviewSize);

    /// <summary>Where a row of the list would land on the wheel: the slice under the pointer.</summary>
    public PreviewDrag? Landing(string id, Point location)
    {
        var revealed = draft.Hidden.Contains(id) ? id : null;
        var shown = ShownWheel(revealed);
        var item = shown.Items.FirstOrDefault(i => shown.Owner(i.Id) == id);
        return item is not null && SliceAt(location, shown) is { } to ? new PreviewDrag(item.Id, to, revealed) : null;
    }

    /// <summary>Puts the dragged slice where it was dropped, in the draft and in the saved arrangement.</summary>
    private void DropOn(PreviewDrag landing, Shown shown)
    {
        var order = shown.Items.Select(i => i.Id).ToList();
        selection.Clear();
        selection.Add(landing.Id);
        var from = order.IndexOf(landing.Id);
        if (from < 0 || (from == landing.To && landing.Revealed is null)) { Redo(); return; }
        if (landing.Revealed is not null) draft.Hidden.Remove(landing.Revealed);
        order.RemoveAt(from);
        order.Insert(Math.Min(landing.To, order.Count), landing.Id);
        var next = landing.To + 1 < order.Count ? order[landing.To + 1] : null;
        draft.Place(shown.Owner(landing.Id), next is null ? null : shown.Owner(next));
        Save();
        Redo();
    }

    /// <summary>Moves the slice at one place of the wheel to another, as dragging it on the preview does.</summary>
    public void DropSlice(int from, int to)
    {
        var shown = ShownWheel();
        if (from < 0 || from >= shown.Items.Count) return;
        DropOn(new PreviewDrag(shown.Items[from].Id, to), shown);
    }

    /// <summary>Drops a row of the list at a point of the preview, as dragging it there does.</summary>
    public bool DropRow(string id, Point at)
    {
        if (Landing(id, at) is not { } landing) return false;
        DropOn(landing, ShownWheel(landing.Revealed));
        return true;
    }

    /// <summary>The slices of the wheel as the preview shows them, in order.</summary>
    public IReadOnlyList<string> ShownIds => ShownWheel().Items.Select(i => i.Id).ToList();

    /// <summary>A press that doesn't move chooses the slice or opens the group as the wheel would (the centre of an open group goes back).</summary>
    public void ClickPreview(Point at)
    {
        var shown = ShownWheel();
        if (SliceAt(at, shown) is { } index)
        {
            var item = shown.Items[index];
            if (item.IsGroup) { OpenGroup(item.Id); return; }
            selection.Clear();
            selection.Add(item.Id);
            Redo();
        }
        else if (group is not null && WheelLayout.IsOnHub(Offset(at), PreviewSize)) CloseGroup();
    }

    /// <summary>The point of the preview where slice <paramref name="index"/> of <paramref name="count"/> has its middle.</summary>
    public static Point SliceMiddle(int index, int count)
    {
        var angle = (index + 0.5) * 2 * Math.PI / count;
        var radius = (WheelLayout.InnerRadius(PreviewSize) + WheelLayout.OuterRadius(PreviewSize)) / 2;
        var middle = PreviewSide / 2;
        // Slices run clockwise from twelve o'clock.
        return new Point(middle + Math.Sin(angle) * radius * Scale, middle - Math.Cos(angle) * radius * Scale);
    }

    /// <summary>A row taken from the list, to be dropped on the wheel.</summary>
    private void DragFromList(UIElement row, string id)
    {
        listDrag = id;
        try { DragDrop.DoDragDrop(row, new DataObject(DataFormats.UnicodeText, id), DragDropEffects.Move); }
        finally
        {
            listDrag = null;
            if (drag is not null) { drag = null; RefreshPreview(); }
        }
    }

    // MARK: What the chosen slice does

    private FrameworkElement About()
    {
        if (Focused is { } id && draft.Entry(id) is { Members: { } members } entry)
        {
            return AboutCard(id, TitleOf(entry), SymbolOf(entry) ?? "square.stack",
                             L("Opens a wheel of its own with: %@.", Listed(members.Select(m => ChoiceOf(m)?.Title).OfType<string>().ToList())), group == id ? null : () => OpenGroup(id));
        }
        if (Focused is { } one && ChoiceOf(one) is { } choice) return AboutCard(one, choice.Title, choice.Symbol, choice.Explanation, null);
        var text = Kit.Text(L("Click a slice to see what it does. A file shows only the slices that apply to it."), 12, secondary: true);
        text.Margin = new Thickness(12);
        return Kit.Panel(text, 10, 0.05);
    }

    private FrameworkElement AboutCard(string id, string title, string? symbol, string text, Action? open)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        if (symbol is not null)
        {
            var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Palette.Brush(Palette.With(Palette.Primary, 0.08)), Child = Kit.Symbol(symbol, 17) };
            tile.Margin = new Thickness(0, 0, 10, 0);
            head.Children.Add(tile);
        }
        var name = Kit.Text(title, 14, weight: FontWeights.SemiBold, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(name);
        var body = Kit.Text(text, 12, secondary: true);
        body.Margin = new Thickness(0, 8, 0, 8);
        var show = new Toggle(!draft.Hidden.Contains(id), L("Show on the wheel"));
        show.Changed += on => SetShown(id, on);
        var label = Kit.Text(L("Show on the wheel"), 12, wrap: false);
        label.Margin = new Thickness(8, 0, 0, 0);
        var bottom = new DockPanel();
        if (open is not null)
        {
            var button = new SplitButton(L("Open Group"), compact: true);
            button.Click += (_, _) => open();
            DockPanel.SetDock(button, Dock.Right);
            bottom.Children.Add(button);
        }
        var toggle = new StackPanel { Orientation = Orientation.Horizontal };
        toggle.Children.Add(show);
        toggle.Children.Add(label);
        bottom.Children.Add(toggle);
        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(head);
        stack.Children.Add(body);
        stack.Children.Add(bottom);
        return Kit.Panel(stack, 10, 0.05);
    }

    // MARK: Look

    private UIElement Appearance()
    {
        var stack = new StackPanel();
        var heading = new DockPanel();
        var value = Kit.Text(size.Label(), 13, secondary: true, wrap: false);
        DockPanel.SetDock(value, Dock.Right);
        heading.Children.Add(value);
        heading.Children.Add(Kit.Text(L("Size"), 13, wrap: false));
        stack.Children.Add(heading);

        var slider = new StepSlider(WheelSizes.All.Count, (int)size, step =>
        {
            size = (WheelSize)step;
            Settings.WheelSize = size;
            value.Text = size.Label();
        }, L("Size"));
        var small = Kit.Symbol("circle", 9, secondary: true);
        var large = Kit.Symbol("circle", 15, secondary: true);
        var track = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        track.ColumnDefinitions.Add(new ColumnDefinition());
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        track.Children.Add(small);
        Grid.SetColumn(slider, 1);
        track.Children.Add(slider);
        Grid.SetColumn(large, 2);
        track.Children.Add(large);
        stack.Children.Add(track);

        var toggle = new Toggle(names, L("Names under icons"));
        toggle.Changed += on => { names = on; Settings.WheelNames = on; RefreshPreview(); };
        var namesRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(toggle, Dock.Right);
        namesRow.Children.Add(toggle);
        namesRow.Children.Add(Kit.Text(L("Names under icons"), 13, wrap: false));
        stack.Children.Add(namesRow);
        stack.Children.Add(Kit.Text(L("A name too long for its slice ends in “…”; the centre shows it whole."), 11, secondary: true));

        var colour = new Segmented(LookColors.All.Select(c => c.Label()).ToList(), LookColors.All.ToList().IndexOf(Settings.LookColor), i => Look.Color = LookColors.All[i], L("Color"), 160);
        var colourRow = new DockPanel { Margin = new Thickness(0, 10, 0, 4) };
        DockPanel.SetDock(colour, Dock.Right);
        colourRow.Children.Add(colour);
        var colourLabel = Kit.Text(L("Color"), 13, wrap: false);
        colourLabel.VerticalAlignment = VerticalAlignment.Center;
        colourRow.Children.Add(colourLabel);
        stack.Children.Add(colourRow);
        stack.Children.Add(Kit.Text(L("Black or white for the wheel, the editors and every window."), 11, secondary: true));
        return stack;
    }

    // MARK: Changes

    private List<string> ChosenSlices => draft.Entries.Where(e => !e.IsGroup && selection.Contains(e.Id)).Select(e => e.Id).ToList();

    /// <summary>Puts the selected slices behind a new group and opens it.</summary>
    public void MakeGroup(IReadOnlyList<string> slices)
    {
        if (draft.MakeGroup(slices) is not { } id) return;
        Save();
        OpenGroup(id);
    }

    private void TakeOut(string id, string from)
    {
        draft.TakeOut(id);
        selection.Remove(id);
        if (draft.Entry(from) is null) { CloseGroup(); Save(); return; }
        Save();
        Redo();
    }

    public void OpenGroup(string id)
    {
        group = id;
        selection.Clear();
        listOffset = 0;
        Redo();
    }

    public void CloseGroup()
    {
        var left = group;
        group = null;
        selection.Clear();
        if (left is not null) selection.Add(left);
        listOffset = 0;
        Redo();
    }

    private void MoveRow(string id, DraftMove move)
    {
        draft.Move(id, move);
        selection.Clear();
        selection.Add(id);
        Save();
        Redo();
    }

    private void SetShown(string id, bool show)
    {
        if (show) draft.Hidden.Remove(id); else draft.Hidden.Add(id);
        Save();
        Redo();
    }

    /// <summary>Unchecks or checks a slice or group, as its box does.</summary>
    public void SetVisible(string id, bool show) => SetShown(id, show);

    private void RestoreDefaults()
    {
        draft = new WheelDraft(new WheelArrangement(), Ids, defaultGroups);
        selection.Clear();
        group = null;
        Save();
        Redo();
    }

    // MARK: Lookups

    private WheelChoice? ChoiceOf(string id) => choices.FirstOrDefault(c => c.Id == id);

    private string TitleOf(WheelEntry entry) =>
        WheelCatalog.GroupTitle(entry.Id, draft.Names.GetValueOrDefault(entry.Id), entry.Members?.FirstOrDefault() is { } first ? ChoiceOf(first)?.Title : null);

    private string? SymbolOf(WheelEntry entry) =>
        WheelCatalog.GroupSymbol(entry.Id, entry.Members?.FirstOrDefault() is { } first ? ChoiceOf(first)?.Symbol : null);
}

/// <summary>The desktop picture behind the preview, so the wheel is seen over what it will really cover; a plain gradient when there is none to read.</summary>
public static class DesktopPicture
{
    private static readonly Lazy<BitmapSource?> Picture = new(Read);

    /// <summary>Read once, small.</summary>
    private static BitmapSource? Read()
    {
        try
        {
            if (Split.Windows.Desktop.WallpaperPath() is not { } path) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = 900;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    public static bool Exists => Picture.Value is not null;

    public static FrameworkElement Element(double side)
    {
        if (Picture.Value is { } image)
            return new Image { Source = image, Stretch = Stretch.UniformToFill, Width = side, Height = side, IsHitTestVisible = false };
        return new Border
        {
            Width = side, Height = side, IsHitTestVisible = false,
            Background = new LinearGradientBrush(Color.FromRgb(107, 107, 107), Color.FromRgb(51, 51, 51), 45),
        };
    }
}
