using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Split.App;

/// <summary>
/// The symbols of the interface, by the names the rest of the code gives them, drawn from the Fluent icon
/// font. A name ending in ".fill" is the filled form of the one before it.
/// </summary>
public static class Icons
{
    /// <summary>Name → Fluent icon (without its size and style).</summary>
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["1.circle"] = "number_circle_1",
        ["archivebox"] = "archive",
        ["arrow.down.right.and.arrow.up.left"] = "arrow_minimize",
        ["arrow.left.and.right.righttriangle.left.righttriangle.right"] = "flip_horizontal",
        ["arrow.left.arrow.right"] = "arrow_swap",
        ["arrow.triangle.2.circlepath"] = "arrow_sync",
        ["arrow.up.and.down.righttriangle.up.righttriangle.down"] = "flip_vertical",
        ["arrow.up.right"] = "arrow_up_right",
        ["chart.pie"] = "data_pie",
        ["checkmark.circle"] = "checkmark_circle",
        ["chevron.backward"] = "chevron_left",
        ["chevron.down"] = "chevron_down",
        ["chevron.right"] = "chevron_right",
        ["circle"] = "circle",
        ["clock"] = "clock",
        ["cpu"] = "developer_board",
        ["crop.rotate"] = "crop_arrow_rotate",
        ["cursorarrow"] = "cursor",
        ["doc.on.clipboard"] = "clipboard",
        ["doc.on.doc"] = "document_copy",
        ["doc.richtext"] = "document_text",
        ["dot.radiowaves.left.and.right"] = "live",
        ["drop"] = "drop",
        ["ellipsis.circle"] = "more_circle",
        ["exclamationmark.triangle"] = "warning",
        ["eye.slash"] = "eye_off",
        ["film.stack"] = "filmstrip",
        ["folder"] = "folder",
        ["gauge.with.dots.needle.33percent"] = "top_speed",
        ["gearshape"] = "settings",
        ["highlighter"] = "highlight",
        ["info.circle"] = "info",
        ["keyboard"] = "keyboard",
        ["line.diagonal"] = "line_horizontal_1",
        ["link"] = "link",
        ["list.bullet.rectangle"] = "text_bullet_list_square",
        ["lock.shield"] = "lock_shield",
        ["minus.circle"] = "subtract_circle",
        ["pause"] = "pause",
        ["pencil.tip.crop.circle"] = "pen",
        ["person.crop.rectangle"] = "contact_card",
        ["photo.on.rectangle"] = "image_multiple",
        ["play"] = "play",
        ["plus.square.on.square"] = "add_square_multiple",
        ["qrcode.viewfinder"] = "qr_code",
        ["questionmark.circle"] = "question_circle",
        ["rectangle"] = "rectangle_landscape",
        ["rectangle.split.1x2"] = "layout_row_two",
        ["rectangle.split.3x1"] = "layout_column_three",
        ["rotate.left"] = "arrow_rotate_counterclockwise",
        ["rotate.right"] = "arrow_rotate_clockwise",
        ["scissors"] = "cut",
        ["scribble"] = "calligraphy_pen",
        ["shippingbox"] = "box",
        ["slider.horizontal.3"] = "options",
        ["speaker.slash"] = "speaker_mute",
        ["speaker.wave.1"] = "speaker_1",
        ["speaker.wave.2"] = "speaker_2",
        ["square.and.arrow.up"] = "share",
        ["square.grid.2x2"] = "grid",
        ["square.grid.3x3"] = "grid_dots",
        ["square.on.square.dashed"] = "select_object",
        ["square.stack"] = "stack",
        ["square.stack.3d.up"] = "stack",
        ["stop.circle"] = "record_stop",
        ["terminal"] = "window_console",
        ["textformat"] = "text_font",
        ["timeline.selection"] = "timeline",
        ["trash"] = "delete",
        ["tray"] = "drawer",
        ["tray.and.arrow.down"] = "drawer_arrow_download",
        ["waveform"] = "data_bar_vertical",
        ["waveform.and.magnifyingglass"] = "data_bar_vertical_ascending",
        ["waveform.badge.minus"] = "data_bar_vertical_arrow_down",
        ["wrench.and.screwdriver"] = "wrench_screwdriver",
        ["xmark"] = "dismiss",
        ["xmark.circle"] = "dismiss_circle",
        ["xmark.octagon"] = "dismiss_square",
    };

    private static readonly Lazy<Dictionary<string, int>> Regular = new(() => Load("Regular"));
    private static readonly Lazy<Dictionary<string, int>> Filled = new(() => Load("Filled"));

    private static Dictionary<string, int> Load(string style)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fonts", $"FluentSystemIcons-{style}.json");
        return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? [];
    }

    /// <summary>The character that draws a symbol, or null when the name or its icon is unknown.</summary>
    public static (string Glyph, bool Filled)? Find(string name)
    {
        var filled = name.EndsWith(".fill", StringComparison.Ordinal);
        if (!Names.TryGetValue(filled ? name[..^5] : name, out var icon)) return null;
        var table = filled ? Filled.Value : Regular.Value;
        var style = filled ? "filled" : "regular";
        foreach (var size in new[] { 24, 20 })
        {
            if (table.TryGetValue($"ic_fluent_{icon}_{size}_{style}", out var code)) return (char.ConvertFromUtf32(code), filled);
        }
        return null;
    }

    /// <summary>A symbol as text of <paramref name="size"/> points, an empty block when the name is unknown.</summary>
    public static TextBlock Block(string name, double size, Color? color = null)
    {
        var found = Find(name);
        return new TextBlock
        {
            Text = found?.Glyph ?? "",
            FontFamily = found?.Filled == true ? Fonts.IconsFilled : Fonts.IconsRegular,
            FontSize = size,
            Foreground = Palette.Brush(color ?? Palette.Primary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
    }
}
