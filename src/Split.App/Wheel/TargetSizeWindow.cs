using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Split.App;

/// <summary>
/// "Compress To…" asks for the largest size the result may have: a text field that reads "2 MB", "800 KB" and the like,
/// and a list of common sizes and upload limits to start from.
/// </summary>
public sealed class TargetSizeWindow : AppWindow
{
    private readonly IReadOnlyList<string> paths;
    private readonly Action<long> chosen;
    private TextBlock info = new();
    private string text = Settings.LastTargetSize;

    public TargetSizeWindow(IReadOnlyList<string> paths, Action<long> chosen)
        : base(L("Compress To…"), new Size(380, 0), canMinimize: false, sizeToHeight: true)
    {
        this.paths = paths;
        this.chosen = chosen;
        Topmost = true;
        Rebuild();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        Loaded += (_, _) =>
        {
            Activate();
            Field.Box.Focus();
            Field.Box.SelectAll();
        };
    }

    protected override bool ClosesOnEscape => true;

    /// <summary>The size field, for the tests.</summary>
    public Field Field { get; private set; } = null!;

    /// <summary>What the window says under its title: what the files weigh, or what to write.</summary>
    public string Message => info.Text;

    /// <summary>The "Compress" button, for the tests.</summary>
    public SplitButton CompressButton { get; private set; } = null!;

    /// <summary>The "Common sizes" menu entries, for the tests.</summary>
    public IReadOnlyList<PopupItem> CommonSizes => Sizes();

    /// <summary>Asks the window to take the size written, as the Compress button does.</summary>
    public void Accept()
    {
        if (TargetSize.Parse(text) is { } bytes && bytes >= TargetSize.Minimum)
        {
            Settings.LastTargetSize = text;
            Close();
            chosen(bytes);
            return;
        }
        info.Text = L("Write a size like “2 MB” or “800 KB” (at least 10 KB).");
    }

    public static TargetSizeWindow Open(IReadOnlyList<string> paths, Action<long> chosen) =>
        SingleWindows.Single("target-size", () => new TargetSizeWindow(paths, chosen));

    protected override void Rebuild()
    {
        Title = L("Compress To…");
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(Kit.Text(L("Compress to what size?"), 15, weight: FontWeights.Bold));

        info = Kit.Text(paths.Count == 1
            ? L("“%@” is %@. The result will be at most the size you choose.", Path.GetFileName(paths[0]), TargetSize.Describe(Tools.FileSize(paths[0])))
            : L("Each of the %@ files will be at most the size you choose.", paths.Count.ToString()), 13, secondary: true);
        info.Margin = new Thickness(0, 6, 0, 14);
        stack.Children.Add(info);

        Field = new Field(text, "2 MB", value => text = value, L("Size"));
        stack.Children.Add(Field);

        var common = Kit.Link(L("Common sizes"), () => PopupMenu.Show(Field, Sizes()), "chevron.down");
        common.HorizontalAlignment = HorizontalAlignment.Left;
        common.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(common);

        CompressButton = new SplitButton(L("Compress"), ButtonKind.Primary);
        CompressButton.Click += (_, _) => Accept();
        var cancel = new SplitButton(L("Cancel")) { Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(CompressButton);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        Body = stack;
    }

    private List<PopupItem> Sizes()
    {
        void Pick(long bytes) => Field.Box.Text = TargetSize.Plain(bytes);
        var items = TargetSize.Presets.Select(bytes => new PopupItem(TargetSize.Plain(bytes), () => Pick(bytes))).ToList();
        items.Add(new PopupItem(null));
        items.AddRange(TargetSize.Uses.Select(use => new PopupItem($"{use.Title} · {TargetSize.Plain(use.Bytes)}", () => Pick(use.Bytes))));
        return items;
    }
}
