using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Split.App;

/// <summary>
/// Choose a format to see what Split turns it into and what it makes it from. A format that waits for a
/// free program reads dimmed, with the program's name.
/// </summary>
public sealed class FormatsTab : TabPage
{
    private Format? chosen = Format.Jpg;
    private string query = "";
    private StackPanel list = new();
    private Border detail = new();
    private ScrollViewer listScroll = new();

    /// <summary>The format whose details show, for the tests.</summary>
    public Format? Chosen => chosen;

    public void Choose(Format format)
    {
        query = "";
        chosen = format;
        Rebuild();
    }

    /// <summary>Searching leaves the text box alone and only changes the list.</summary>
    public void Search(string text)
    {
        query = text;
        FillList();
    }

    private IEnumerable<Format> Matching(SourceKind kind)
    {
        var words = query.Trim().ToLowerInvariant();
        return kind.Formats().Where(f => words.Length == 0 || f.Label().ToLowerInvariant().Contains(words) || f.Ext().Contains(words));
    }

    protected override UIElement Build()
    {
        var search = new Field(query, L("Search Formats"), text => { query = text; FillList(); }, L("Search Formats"));
        list = new StackPanel();
        listScroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var box = Kit.Panel(listScroll, 8, 0.05);
        box.BorderBrush = Palette.Brush(Palette.With(Palette.Primary, 0.1));
        box.BorderThickness = new Thickness(0.5);
        box.Margin = new Thickness(0, 8, 0, 0);
        var left = new Grid { Width = 220 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition());
        left.Children.Add(search);
        Grid.SetRow(box, 1);
        left.Children.Add(box);
        FillList();

        detail = new Border();
        Grid.SetColumn(detail, 1);
        detail.Margin = new Thickness(18, 0, 0, 0);
        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(left);
        grid.Children.Add(detail);
        FillDetail();
        return grid;
    }

    private void FillList()
    {
        list.Children.Clear();
        foreach (var kind in SourceKinds.All)
        {
            var formats = Matching(kind).ToList();
            if (formats.Count == 0) continue;
            var header = Kit.Text(kind.Title(), 11, secondary: true, weight: FontWeights.SemiBold);
            header.Margin = new Thickness(10, 10, 10, 4);
            list.Children.Add(header);
            foreach (var format in formats) list.Children.Add(ListRow(format));
        }
    }

    private UIElement ListRow(Format format)
    {
        var grid = new Grid { Margin = new Thickness(8, 5, 8, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Kit.Text(format.Label(), 13, wrap: false));
        var ext = Kit.Text("." + format.Ext(), 11, secondary: true, wrap: false);
        Grid.SetColumn(ext, 1);
        grid.Children.Add(ext);
        var row = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(5), Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Arrow, Focusable = true, Tag = format,
            Background = format == chosen ? Palette.Brush(Palette.With(Palette.Primary, 0.16)) : Brushes.Transparent,
        };
        System.Windows.Automation.AutomationProperties.SetName(row, format.Label());
        row.MouseLeftButtonUp += (_, _) => { chosen = format; FillList(); FillDetail(); };
        row.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { chosen = format; FillList(); FillDetail(); } };
        return row;
    }

    private void FillDetail()
    {
        if (chosen is not { } format)
        {
            detail.Child = Kit.Text(L("Choose a format to see what Split makes from it."), 13, secondary: true);
            return;
        }
        var targets = Conversions.Targets(format);
        var sources = Formats.All.Where(f => f != format && Conversions.Targets(f).Contains(format)).ToList();
        var waiting = targets.Any(t => Conversions.MissingProgram(format, t) is not null) || sources.Any(s => Conversions.MissingProgram(s, format) is not null);

        var tile = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(12), Background = Palette.Brush(Palette.With(Palette.Primary, 0.08)),
            Child = new Viewbox { StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(4, 0, 4, 0), Child = Kit.Text("." + format.Ext(), 13, weight: FontWeights.SemiBold, wrap: false) },
        };
        var title = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Kit.Text(format.Label(), 20, weight: FontWeights.Bold));
        title.Children.Add(Kit.Text(SourceKinds.Of(format).Title(), 12, secondary: true));
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(tile);
        head.Children.Add(title);

        var stack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        stack.Children.Add(head);
        var summary = Kit.Text(format.Summary(), 13);
        summary.Margin = new Thickness(0, 20, 0, 0);
        stack.Children.Add(summary);
        stack.Children.Add(Part(L("Turns into"), targets, t => Conversions.MissingProgram(format, t),
            L("Nothing on this PC opens %@ files for Split yet. Settings › Engines lists the apps that help.", format.Label())));
        stack.Children.Add(Part(L("Made from"), sources, s => Conversions.MissingProgram(s, format), L("Split reads %@ files but doesn’t make them.", format.Label())));
        if (waiting)
        {
            var note = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
            var icon = Kit.Symbol("shippingbox", 13, secondary: true);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 1, 6, 0);
            note.Children.Add(icon);
            var text = Kit.Text(L("Dimmed formats need a free program first. Settings › Engines has the command that installs it."), 11, secondary: true);
            text.MaxWidth = 420;
            note.Children.Add(text);
            stack.Children.Add(note);
        }
        detail.Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    /// <summary>One side of the conversions, in families, each format a button that shows that format.</summary>
    private UIElement Part(string title, IReadOnlyList<Format> formats, Func<Format, string?> missing, string empty)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Kit.Text(title, 13, weight: FontWeights.SemiBold, wrap: false));
        if (formats.Count > 0)
        {
            var count = Kit.Text(formats.Count.ToString(), 11, secondary: true, weight: FontWeights.Medium, wrap: false);
            count.Margin = new Thickness(6, 2, 0, 0);
            head.Children.Add(count);
        }
        stack.Children.Add(head);
        if (formats.Count == 0) stack.Children.Add(Kit.Text(empty, 12, secondary: true));
        foreach (var kind in SourceKinds.All.Where(k => formats.Any(f => SourceKinds.Of(f) == k)))
        {
            var heading = Kit.Text(kind.Title(), 11, secondary: true);
            heading.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(heading);
            var flow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var other in formats.Where(f => SourceKinds.Of(f) == kind)) flow.Children.Add(Chip(other, missing(other)));
            stack.Children.Add(flow);
        }
        return stack;
    }

    private UIElement Chip(Format format, string? missing)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Kit.Text(format.Label(), 12, weight: FontWeights.Medium, wrap: false));
        if (missing is not null)
        {
            var program = Kit.Text(missing, 10, secondary: true, wrap: false);
            program.Margin = new Thickness(4, 0, 0, 0);
            content.Children.Add(program);
        }
        var tip = missing is null ? format.Summary() : L("Needs %@, which Settings › Engines installs.", missing);
        var button = new SplitButton(content, compact: true) { Margin = new Thickness(0, 0, 6, 6), Opacity = missing is null ? 1 : 0.55, ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(button, format.Label());
        System.Windows.Automation.AutomationProperties.SetHelpText(button, tip);
        button.Click += (_, _) => { Choose(format); Dispatcher.BeginInvoke(() => ScrollToChosen()); };
        return button;
    }

    private void ScrollToChosen()
    {
        foreach (var row in list.Children.OfType<Border>().Where(b => b.Tag is Format f && f == chosen)) row.BringIntoView();
    }
}
