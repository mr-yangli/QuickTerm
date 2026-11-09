namespace QuickTerm.App;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

/// <summary>
/// Four ways to paint the same screen, timed on the pattern the W2 gate actually stresses: a full repaint
/// every frame where almost all the content is text that was already on screen one frame ago (that is what a
/// scroll looks like to the dirty-row path — the engine marks the whole region, and 41 of 42 rows carry the
/// same glyphs shifted up).
/// </summary>
/// <remarks>
/// Simplifications, stated because they bound what the table can say: one colour run per row, every cell one
/// cell wide (CJK is not doubled), and the present leg is absent — these numbers are the half of the real
/// window's 21 ms that we can actually go after, not the whole of it.
///
/// And one limitation that cost a whole implementation: path C's bake here draws a single FormattedText with
/// no background runs and no row floor, while the control's row painter does all three. That made C read
/// 3.97 ms against a real cost of 0.52 ms per row baked — the control measured 12.8-13.9 ms with the cache
/// wired in, against 11.08 ms without it. Treat this table as a comparison of draw strategies, not as a
/// substitute for the phase split in <c>--bench</c>, which is measured inside the control itself.
/// </remarks>
internal static class PaintPathShootout
{
    private const int Columns = 124;
    private const int Rows = 42;
    private const double CellWidth = 8;
    private const double CellHeight = 16;
    private const double FontSize = 14;
    private const int DistinctLines = 4000;

    /// <summary>
    /// New lines arriving per frame, which is what sets the repeat ratio a real flood sees: 24 lines in and 42
    /// rows on screen means 18 rows are last frame's content shifted up and 24 are brand new. An earlier
    /// version of this table advanced one line per frame and reported row strips at 0.49 ms — that number was
    /// a model artifact, not a measurement of the gate scenario.
    /// </summary>
    private const int NewLinesPerFrame = 24;

    private static readonly FontFamily Family = new("Cascadia Mono, Consolas, Microsoft JhengHei Mono, monospace");

    public static void Run(string[] args)
    {
        var frames = args.Length > 2 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(3, value)
            : 200;

        var lines = BuildLines();
        var typeface = new Typeface(Family, FontStyle.Normal, FontWeight.Normal);
        var foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xD7, 0xD7));
        var background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x1E));
        var area = new Rect(0, 0, Columns * CellWidth, Rows * CellHeight);
        var pixels = new PixelSize((int)area.Width, (int)area.Height);
        var dpi = new Vector(96, 96);
        using var surface = new RenderTargetBitmap(pixels, dpi);

        Console.WriteLine($"paths {Columns}x{Rows} cells, {DistinctLines} distinct lines, {frames} frames");
        Console.WriteLine($"  model: {NewLinesPerFrame} brand-new lines per frame → {(Rows - NewLinesPerFrame)}/{Rows} rows are repeats");
        Report("A fresh FormattedText per run", Time(frame => Fresh(surface, area, background, typeface, foreground, frame, lines), frames));
        var runCache = new Dictionary<string, FormattedText>();
        Report("B cached FormattedText per run", Time(frame => Cached(surface, area, background, typeface, foreground, frame, lines, runCache), frames));
        Report("C row strips + 42 blits", TimeRowStrips(surface, area, background, typeface, foreground, lines, frames));
        Report("D glyph atlas + cell blits", TimeGlyphAtlas(surface, area, background, typeface, foreground, lines, frames));
        Console.WriteLine($"note: present leg excluded; real window was 21.00 ms/frame at rows 42 — only the paint half is in play here.");
    }

    private static string LineAt(List<string> lines, int frame, int row)
        => lines[(frame * NewLinesPerFrame + row) % lines.Count];

    private static List<string> BuildLines()
    {
        var lines = new List<string>(DistinctLines);
        for (var i = 0; i < DistinctLines; i++)
        {
            var builder = new StringBuilder();
            builder.Append(i.ToString(CultureInfo.InvariantCulture).PadRight(6)).Append(' ');
            builder.Append("编译 main.c 中 ");
            while (builder.Length < Columns)
                builder.Append((char)('a' + (i + builder.Length) % 26));
            lines.Add(builder.ToString(0, Columns));
        }

        return lines;
    }
    private readonly record struct Cost(double Ms, long Bytes, long ExtraBytes);

    private static Cost Time(Action<int> paint, int frames)
    {
        paint(0); // warm: the first frame of every path pays the font/shaping setup
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var frame = 0; frame < frames; frame++)
            paint(frame);
        var ms = watch.Elapsed.TotalMilliseconds / frames;
        return new Cost(ms, (GC.GetAllocatedBytesForCurrentThread() - before) / frames, 0);
    }

    private static void Report(string name, Cost cost)
        => Console.WriteLine($"  {name,-32} : {cost.Ms:F2} ms | {cost.Bytes / 1024.0:F1} KiB/frame | {cost.ExtraBytes / 1024.0 / 1024.0:F1} MiB cached");

    private static void Fresh(
        RenderTargetBitmap surface, Rect area, IBrush background, Typeface typeface, IBrush foreground, int frame, List<string> lines)
    {
        using var context = surface.CreateDrawingContext();
        context.FillRectangle(background, area);
        for (var row = 0; row < Rows; row++)
        {
            var text = LineAt(lines, frame, row);
            context.DrawText(
                new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground),
                new Point(0, row * CellHeight));
        }
    }

    private static void Cached(
        RenderTargetBitmap surface, Rect area, IBrush background, Typeface typeface, IBrush foreground, int frame, List<string> lines,
        Dictionary<string, FormattedText> cache)
    {
        using var context = surface.CreateDrawingContext();
        context.FillRectangle(background, area);
        for (var row = 0; row < Rows; row++)
        {
            var text = LineAt(lines, frame, row);
            if (!cache.TryGetValue(text, out var shaped))
            {
                shaped = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground);
                cache[text] = shaped;
            }

            context.DrawText(shaped, new Point(0, row * CellHeight));
        }
    }

    private static Cost TimeRowStrips(
        RenderTargetBitmap surface, Rect area, IBrush background, Typeface typeface, IBrush foreground, List<string> lines, int frames)
    {
        var strips = new Dictionary<string, RenderTargetBitmap>();
        var cost = Time(frame =>
        {
            using var context = surface.CreateDrawingContext();
            context.FillRectangle(background, area);
            for (var row = 0; row < Rows; row++)
            {
                var text = LineAt(lines, frame, row);
                if (!strips.TryGetValue(text, out var strip))
                {
                    strip = BakeStrip(text, area.Width, typeface, foreground);
                    strips[text] = strip;
                }

                context.DrawImage(strip, new Rect(0, row * CellHeight, strip.PixelSize.Width, strip.PixelSize.Height));
            }
        }, frames);

        var cached = 0L;
        foreach (var strip in strips.Values)
            cached += (long)strip.PixelSize.Width * strip.PixelSize.Height * 4;
        foreach (var strip in strips.Values)
            strip.Dispose();

        return new Cost(cost.Ms, cost.Bytes, cached);
    }

    private static RenderTargetBitmap BakeStrip(string text, double width, Typeface typeface, IBrush foreground)
    {
        var pixels = new PixelSize((int)width, (int)CellHeight);
        var strip = new RenderTargetBitmap(pixels, new Vector(96, 96));
        using var context = strip.CreateDrawingContext();
        context.DrawText(
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground),
            new Point(0, 0));
        return strip;
    }

    private static Cost TimeGlyphAtlas(
        RenderTargetBitmap surface, Rect area, IBrush background, Typeface typeface, IBrush foreground, List<string> lines, int frames)
    {
        // Collect the glyph set first, then bake it into one 64x32-tile atlas; the timed loop only blits.
        var glyphs = new SortedSet<char>();
        foreach (var line in lines)
            foreach (var character in line)
                glyphs.Add(character);

        var atlas = new RenderTargetBitmap(new PixelSize((int)CellWidth * 64, (int)CellHeight * 32), new Vector(96, 96));
        var slots = new Dictionary<char, int>();
        using (var context = atlas.CreateDrawingContext())
        {
            var slot = 0;
            foreach (var character in glyphs)
            {
                if (slot >= 64 * 32)
                    break;
                var x = slot % 64 * CellWidth;
                var y = slot / 64 * CellHeight;
                context.DrawText(
                    new FormattedText(character.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground),
                    new Point(x, y));
                slots[character] = slot;
                slot++;
            }
        }

        var cost = Time(frame =>
        {
            using var context = surface.CreateDrawingContext();
            context.FillRectangle(background, area);
            for (var row = 0; row < Rows; row++)
            {
                var text = LineAt(lines, frame, row);
                var y = row * CellHeight;
                for (var column = 0; column < text.Length && column < Columns; column++)
                {
                    if (!slots.TryGetValue(text[column], out var slot))
                        continue;
                    context.DrawImage(atlas,
                        new Rect(slot % 64 * CellWidth, slot / 64 * CellHeight, CellWidth, CellHeight),
                        new Rect(column * CellWidth, y, CellWidth, CellHeight));
                }
            }
        }, frames);

        var cached = (long)atlas.PixelSize.Width * atlas.PixelSize.Height * 4;
        atlas.Dispose();
        return new Cost(cost.Ms, cost.Bytes, cached);
    }
}
