namespace QuickTerm.App;

using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

/// <summary>
/// The small monochrome line icons the shell draws, in the flat outline style of the reference (Termora /
/// IntelliJ): one grey stroke, ~16 px, no colour emoji. Geometry is authored in a 24x24 box and scaled uniformly
/// so every glyph sits on the same optical size next to 12 px text.
/// </summary>
internal static class TermIcons
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x5F, 0x69, 0x6E));

    public const string Person = "M12 8.2 a3.4 3.4 0 1 0 0.01 0 M5 19.2 c0-3.6 3.1-5.6 7-5.6 s7 2 7 5.6";
    public const string Folder = "M3 6.2 h6 l2 2.2 h10 v10.4 h-18 z";
    public const string Terminal = "M3 5 h18 v12 h-18 z M9.5 20.6 h5 M12 17 v3.6 M7 9 l3 2.4 l-3 2.4 M12.6 13.8 h4";
    public const string ChevronRight = "M9.9 6 l6 6 l-6 6";
    public const string ChevronDown = "M6 9.9 l6 6 l6-6";
    public const string Plus = "M12 5 v14 M5 12 h14";
    public const string Sidebar = "M3.2 5 h17.6 v14 h-17.6 z M9.4 5 v14";
    public const string Search = "M10.5 4.4 a6.1 6.1 0 1 0 0.01 0 M15.2 15.2 L20.6 20.6";
    public const string Gear = "M12 7 a5 5 0 1 0 0.01 0 M12 10.2 a1.8 1.8 0 1 0 0.01 0 M17 12 h2.5 M14.5 16.33 l1.25 2.17 M9.5 16.33 l-1.25 2.17 M7 12 h-2.5 M9.5 7.67 l-1.25 -2.17 M14.5 7.67 l1.25 -2.17";
    public const string Minimize = "M5 12.4 h14";
    public const string Maximize = "M5.4 5.4 h13.2 v13.2 h-13.2 z";
    public const string Restore = "M8.2 8.2 h10.4 v10.4 h-10.4 z M5.4 15.8 v-10.4 h10.4";
    public const string Close = "M6.9 6.9 l10.2 10.2 M17.1 6.9 l-10.2 10.2";
    public const string Eye = "M2.6 12 c2.6-4.4 6-6.6 9.4-6.6 s6.8 2.2 9.4 6.6 c-2.6 4.4-6 6.6-9.4 6.6 s-6.8-2.2-9.4-6.6 z M12 9.4 a2.6 2.6 0 1 0 0.01 0";
    public const string Sliders = "M4 7 h10 M18 7 h2 M4 12 h4 M12 12 h8 M4 17 h12 M20 17 h0 M14 5 v4 M8 10 v4 M16 15 v4";
    public const string Prompt = "M4 5 h16 v14 h-16 z M8 10 l3 2.5 l-3 2.5 M13 14 h3";
    public const string Grid = "M4 4 h7 v7 h-7 z M13 4 h7 v7 h-7 z M4 13 h7 v7 h-7 z M13 13 h7 v7 h-7 z";
    public const string File = "M6 3 h8 l4 4 v14 h-12 z M14 3 v4 h4";

    /// <summary>
    /// One glyph at its authored 24x24 coordinates, unstretched. Drawing geometry at fixed coordinates (rather
    /// than Stretch-ing each path to its own bounding box) keeps every icon on the same optical grid and stops
    /// zero-height glyphs like the minimize bar from being squashed or clipped.
    /// </summary>
    public static Path Glyph(string geometry) => new()
    {
        Data = StreamGeometry.Parse(geometry),
        Stroke = Ink,
        StrokeThickness = 1.5,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Width = 24,
        Height = 24,
        Stretch = Stretch.None,
    };

    public static Viewbox Icon(string geometry, double size = 16) => new()
    {
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Child = Glyph(geometry),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A filled manila-yellow folder with a darker rim: reads as "folder" at a glance, unlike the grey outline.</summary>
    public static Viewbox FolderIcon(double size = 16) => new()
    {
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Path
        {
            Data = StreamGeometry.Parse("M3 5.5 h6.2 l2 2.3 H21 V19 H3 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x4E)),
            Stroke = new SolidColorBrush(Color.FromRgb(0xC9, 0x97, 0x1F)),
            StrokeThickness = 1,
            StrokeJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
        },
    };
}