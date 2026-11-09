namespace QuickTerm.App;

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;

/// <summary>
/// <c>QuickTerm --bench metrics</c>: prints the two numbers the paint path is judged against — the cell width
/// the grid reserves and the advance the font actually gives — for the shipping font stack.
/// </summary>
/// <remarks>
/// A terminal grid is a promise that cell k starts at k * cellWidth. A text run drawn with the font's own
/// advances keeps that promise only while the advance equals the cell width, and a double-width glyph only
/// while its advance equals twice it. When they differ, the run's tail drifts away from its cell and the
/// next run on the row starts with a visible gap in front of it — which is what a screenshot of a CJK line
/// looks like, and which no test of the engine can see, because the engine's grid is correct.
/// </remarks>
internal static class GlyphMetrics
{
    private const double Size = 14;

    public static void Run()
    {
        var typeface = new Typeface(TerminalView.GridFontFamily, FontStyle.Normal, FontWeight.Normal);

        var zero = Width("0", typeface);
        var cell = Math.Max(1, Math.Ceiling(zero));
        var probe = Text("0", typeface);

        Console.WriteLine($"font stack            : {typeface.FontFamily.Name}");
        Console.WriteLine($"size                  : {Size} px");
        Console.WriteLine($"advance '0'           : {zero:F3} px   cell width used by the grid: {cell:F3} px");
        Console.WriteLine($"probe height/baseline : {probe.Height:F3} / {probe.Baseline:F3}");
        Console.WriteLine();
        Console.WriteLine("advance per glyph, and what the grid reserves for it:");
        Report("ASCII      'A'", "A", typeface, 1 * cell);
        Report("ASCII      'W'", "W", typeface, 1 * cell);
        Report("ASCII      'i'", "i", typeface, 1 * cell);
        Report("CJK        '版'", "版", typeface, 2 * cell);
        Report("CJK        '保'", "保", typeface, 2 * cell);
        Report("fullwidth  '。'", "。", typeface, 2 * cell);
        Report("halfwidth  'ｱ'", "ｱ", typeface, 1 * cell);
        Console.WriteLine();

        // Whether the width a run is measured by includes its trailing spaces. If it does not, scaling a run
        // onto its cell band stretches the visible glyphs — which is what put the cursor block several
        // columns early after cmd erased typed characters with spaces.
        var ink = "AB";
        var withSpaces = "AB      ";
        var inkWidth = Width(ink, typeface);
        var paddedWidth = Width(withSpaces, typeface);
        Console.WriteLine("trailing spaces in a run's measured width:");
        Console.WriteLine($"  \"AB\"            : {inkWidth:F3} px");
        Console.WriteLine($"  \"AB      \"      : {paddedWidth:F3} px  (8 cells reserved = {8 * cell:F3} px)");
        Console.WriteLine($"  counted or not  : {(Math.Abs(paddedWidth - inkWidth) < 0.01 ? "NOT COUNTED - a scale built on this stretches the ink" : "counted - width tracks the cell span")}");
        Console.WriteLine();

        // as one run, then the next run starting at its own cell boundary.
        var prompt = "E:\\Resource\\QuickNet\\term\\src\\QuickTerm.App>";
        var banner = "(c) Microsoft Corporation";
        Console.WriteLine($"drift over a whole run (natural width minus reserved cells):");
        Drift(prompt, typeface, prompt.Length * cell);
        Drift(banner, typeface, banner.Length * cell);
        Drift("Microsoft Windows [版本 10.0.26300.9818]", typeface, 40 * cell);
        Drift("保留所有权利。", typeface, 14 * cell);
        Console.WriteLine();

        var rows = new List<string>();
        rows.Add("what the row looks like when each run is placed at its own cell boundary:");
        rows.Add(Place("(c) Microsoft Corporation", "。", typeface, cell));
        Console.WriteLine(string.Join(Environment.NewLine, rows));
        Console.WriteLine();

        // Settings have to reach the typeface, not sit in a file: a control built with a different size is the
        // only proof that the plumbing is live rather than decorative.
        var plain = new TerminalView();
        var larger = new TerminalView(new QuickTermSettings { FontSize = 20 });
        Console.WriteLine("settings reach the typeface:");
        Console.WriteLine($"  default (14 px) : cell {plain.CellWidth:F3} px");
        Console.WriteLine($"  settings 20 px  : cell {larger.CellWidth:F3} px"
            + $" -> {(larger.CellWidth > plain.CellWidth ? "the setting moved the grid" : "THE SETTING WAS IGNORED")}");
    }

    private static void Report(string label, string text, Typeface typeface, double reserved)
    {
        var advance = Width(text, typeface);
        Console.WriteLine($"  {label} : {advance:F3} px vs {reserved:F3} px reserved  -> {(advance - reserved):+0.000;-0.000;0} px per glyph");
    }

    private static void Drift(string text, Typeface typeface, double reserved)
    {
        var advance = Width(text, typeface);
        Console.WriteLine($"  {text.Length,3} cells \"{Shorten(text)}\": {advance:F1} px drawn into {reserved:F1} px reserved -> {(advance - reserved):+0.0;-0.0;0} px");
    }

    /// <summary>Where the ink of one run ends against the cell its neighbour starts in — the gap a screenshot shows.</summary>
    private static string Place(string ascii, string wide, Typeface typeface, double cell)
    {
        var asciiWidth = Width(ascii, typeface);
        var wideCell = ascii.Length * cell;
        return $"  \"{ascii}\" ends at {asciiWidth:F1} px; \"{wide}\" is drawn at cell {ascii.Length} = {wideCell:F1} px -> gap {wideCell - asciiWidth:F1} px";
    }

    private static FormattedText Text(string text, Typeface typeface)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Size, Brushes.White);

    private static double Width(string text, Typeface typeface) => Text(text, typeface).Width;

    private static string Shorten(string text) => text.Length <= 26 ? text : text[..23] + "...";
}
