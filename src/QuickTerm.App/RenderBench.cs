namespace QuickTerm.App;

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

/// <summary>
/// Offscreen frame cost: <c>QuickTerm --bench</c> lays a <see cref="TerminalView" /> out without a window,
/// feeds it through <see cref="TerminalView.Inject" /> and paints it into a scratch render target, so the
/// render layer is measured on a machine where a shell child cannot be spawned.
/// </summary>
/// <remarks>
/// What this can and cannot say. It measures the paint path — dirty-row selection, glyph runs, the per-frame
/// blit — and reports the parse cost beside it rather than buried inside it. It does not measure a real
/// window's present path (composition, vsync, the GPU backend's own copy), which is why the FPS gate still
/// needs a desktop run; these numbers are that one's ceiling.
///
/// The three scenarios differ only in how many rows they dirty per frame, so flood-vs-churn is the
/// apples-to-apples reading of what owning the surface bought.
/// </remarks>
internal static class RenderBench
{
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 720;
    private const int DefaultFrames = 60;

    public static int Run(string[] args)
    {
        // The paint-path comparison is its own question: it does not touch the control, it times four ways of
        // painting the same screen so the atlas decision rests on a ratio rather than on a hunch.
        if (args.Length > 1 && args[1] == "paths")
        {
            PaintPathShootout.Run(args);
            return 0;
        }

        // `--bench metrics` answers a placement question, not a cost one: where a row's ink actually lands
        // depends on whether the grid's cell width and the font's own advance are the same number.
        if (args.Length > 1 && args[1] == "metrics")
        {
            GlyphMetrics.Run();
            return 0;
        }

        // `--bench ink`: paints one screenful into a PNG so the placement reading can be looked at instead of
        // argued from. The CPU raster here draws where the same code draws on screen; only the present path
        // differs, and that one cannot move ink inside a row.
        if (args.Length > 1 && args[1] == "ink")
        {
            var inkWidth = Read(args, 2, 760);
            var inkHeight = Read(args, 3, 130);
            var inkView = new TerminalView();
            inkView.Measure(new Size(inkWidth, inkHeight));
            inkView.Arrange(new Rect(new Size(inkWidth, inkHeight)));
            inkView.Inject(Encoding.UTF8.GetBytes(
                "Microsoft Windows [版本 10.0.26300.9818]\r\n" +
                "(c) Microsoft Corporation。保留所有权利。\r\n" +
                "\x1b[32mE:\\Resource\\QuickNet\\term\\src\\QuickTerm.App\x1b[0m> dir  编译 main.c 中\r\n" +
                "14 个   文件      107,016 字节\r\n" +
                "4 个  目录  20,804,874,208 字节\r\n"));

            using var target = new RenderTargetBitmap(new PixelSize(inkWidth, inkHeight), new Vector(96, 96));
            using (var context = target.CreateDrawingContext())
                inkView.Render(context);

            var path = Path.Combine(Path.GetTempPath(), "quickterm-ink.png");
            WritePng(target, "quickterm-ink.png");
            Console.WriteLine($"ink written to {path}");
            return 0;
        }

        // `--bench select`: drags a selection through the same public seam the pointer handlers use, then
        // writes the highlight to a PNG and the extracted text to a file. Both halves of copying are things I
        // can look at — the ink and the string — so "the highlight is on the wrong row" and "the paste has a
        // phantom newline in it" stop being desktop-only readings.
        if (args.Length > 1 && args[1] == "select")
        {
            var selWidth = Read(args, 2, 420);
            var selHeight = Read(args, 3, 200);
            var selector = new TerminalView();
            selector.Measure(new Size(selWidth, selHeight));
            selector.Arrange(new Rect(new Size(selWidth, selHeight)));
            selector.Inject(Encoding.UTF8.GetBytes(
                "line one is short\r\n" +
                "this logical line is long enough that it has to wrap into the next row at this width\r\n" +
                "last line 中文结尾\r\n"));

            var text = new StringBuilder();

            // One paint before anything is read: the grid only takes the window's size inside Render, and a
            // selection computed against the constructor's 80 columns is not a selection over what is shown.
            using var selected = new RenderTargetBitmap(new PixelSize(selWidth, selHeight), new Vector(96, 96));
            using (var warm = selected.CreateDrawingContext())
                selector.Render(warm);

            text.Append($"columns {selector.GridColumns}\r\n");

            // Cell drag: rows 0..2, starting mid-row and ending mid-row, so the first and last fragments are
            // partial and the wrapped middle has to come back as one logical line.
            selector.BeginSelection(new QuickTerm.Terminal.TermPosition(0, 5));
            selector.ExtendSelection(new QuickTerm.Terminal.TermPosition(2, 4));
            text.Append($"cell :: [{selector.SelectedText()}]\r\n");

            selector.BeginSelection(new QuickTerm.Terminal.TermPosition(0, 5), QuickTerm.Terminal.TermSelectMode.Word);
            text.Append($"word :: [{selector.SelectedText()}]\r\n");

            selector.BeginSelection(new QuickTerm.Terminal.TermPosition(1, 20), QuickTerm.Terminal.TermSelectMode.Line);
            text.Append($"line :: [{selector.SelectedText()}]\r\n");

            selector.ExtendSelection(new QuickTerm.Terminal.TermPosition(2, 9));
            text.Append($"line+drag :: [{selector.SelectedText()}]\r\n");

            selector.BeginSelection(new QuickTerm.Terminal.TermPosition(2, 5));
            selector.ExtendSelection(new QuickTerm.Terminal.TermPosition(2, 10));
            text.Append($"cjk  :: [{selector.SelectedText()}]\r\n");

            // Paint the first reading again so the PNG shows a drag that crosses a wrapped pair and a partial
            // row at each end — the shape that would put a highlight on the wrong row.
            selector.BeginSelection(new QuickTerm.Terminal.TermPosition(0, 5));
            selector.ExtendSelection(new QuickTerm.Terminal.TermPosition(2, 4));

            using (var context = selected.CreateDrawingContext())
                selector.Render(context);
            WritePng(selected, "quickterm-select.png");

            var textPath = Path.Combine(Path.GetTempPath(), "quickterm-select.txt");
            File.WriteAllText(textPath, text.ToString());
            Console.WriteLine($"selection readings written to {textPath}");
            return 0;
        }

        // `--bench color`: the palette as a picture. Rows of 16/256/truecolour samples, then the same rows
        // after OSC 4 and OSC 11 have moved the table — which is the difference between a palette the engine
        // owns and one baked into the renderer, where those requests would do nothing at all.
        if (args.Length > 1 && args[1] == "color")
        {
            var colorWidth = Read(args, 2, 900);
            var colorHeight = Read(args, 3, 260);
            var colored = new TerminalView();
            colored.Measure(new Size(colorWidth, colorHeight));
            colored.Arrange(new Rect(new Size(colorWidth, colorHeight)));

            var base16 = new StringBuilder();
            var cube = new StringBuilder();
            for (var slot = 0; slot < 16; slot++)
                base16.Append($"\u001b[38;5;{slot}m {slot:X2} ");
            for (var slot = 16; slot < 256; slot += 12)
                cube.Append($"\u001b[38;5;{slot}m {slot:X2} ");

            void Snapshot(string name)
            {
                using var target = new RenderTargetBitmap(new PixelSize(colorWidth, colorHeight), new Vector(96, 96));
                using var context = target.CreateDrawingContext();
                colored.Render(context);
                WritePng(target, name);
            }

            colored.Inject(Encoding.UTF8.GetBytes(
                $"{base16}\u001b[0m\r\n{cube}\u001b[0m\r\n" +
                "\u001b[31mred\u001b[32mgreen\u001b[33myellow\u001b[34mblue\u001b[35mmagenta\u001b[36mcyan\u001b[0m  " +
                "\u001b[1mbold\u001b[0m  \u001b[41mred floor\u001b[0m  \u001b[7minverse\u001b[0m  \u001b[38;2;255;100;0mtruecolour\u001b[0m\r\n" +
                "before the requests above\r\n"));
            Snapshot("quickterm-color-before.png");

            colored.Inject(Encoding.UTF8.GetBytes(
                "\u001b]4;1;#FF6666;2;#66FF66;4;#6699FF\u0007" +
                "\u001b]11;#102030\u0007\u001b]10;#E8F0FF\u0007" +
                "\u001b[31mred\u001b[32mgreen\u001b[34mblue\u001b[0m after OSC 4 (1/2/4) + OSC 10/11\r\n" +
                "\u001b]104;1\u0007\u001b[31mred back\u001b[0m \u001b[32mgreen still\u001b[0m after OSC 104;1\r\n"));
            Snapshot("quickterm-color.png");
            Console.WriteLine("colour pair written to quickterm-color-before.png and quickterm-color.png");
            return 0;
        }

        // `--bench sync`: DEC 2026 as a count. The same screenful split into the same pieces, once with the
        // brackets and once without — the number of frames held back is the whole claim, and it is a number,
        // not a screenshot.
        if (args.Length > 1 && args[1] == "sync")
        {
            var pieces = Math.Max(2, Read(args, 2, 8));
            var body = new StringBuilder();
            for (var line = 0; line < 24; line++)
                body.Append($"row {line,2} \u001b[32mok\u001b[0m {new string('x', 40)}\r\n");

            var plain = new TerminalView();
            plain.Measure(new Size(1000, 500));
            plain.Arrange(new Rect(new Size(1000, 500)));
            var bracketed = new TerminalView();
            bracketed.Measure(new Size(1000, 500));
            bracketed.Arrange(new Rect(new Size(1000, 500)));

            var lines = body.ToString().Split("\r\n");
            for (var piece = 0; piece < pieces; piece++)
            {
                var slice = Encoding.UTF8.GetBytes(string.Join("\r\n", lines.Skip(piece * lines.Length / pieces).Take(lines.Length / pieces)) + "\r\n");
                plain.Inject(slice);

                var prefix = piece == 0 ? "\u001b[?2026h" : string.Empty;
                var suffix = piece == pieces - 1 ? "\u001b[?2026l" : string.Empty;
                bracketed.Inject(Encoding.UTF8.GetBytes(prefix + Encoding.UTF8.GetString(slice) + suffix));
            }

            var without = plain.SyncReadings;
            var with = bracketed.SyncReadings;
            Console.WriteLine($"  {pieces} pieces, one screenful split between them");
            Console.WriteLine($"  without brackets : deferred {without.Deferred} released {without.Released} open {(without.Open ? 1 : 0)}");
            Console.WriteLine($"  with    brackets : deferred {with.Deferred} released {with.Released} open {(with.Open ? 1 : 0)}");
            var saved = with.Deferred > 0 && without.Deferred == 0 ? $"{with.Deferred} of {pieces}" : "none - the coalescing did not fire";
            Console.WriteLine($"  frames saved     : {saved}");

            // The other half of the claim: a program that opens the bracket and never closes it must not wedge
            // the display. Same split, no reset frame, then a wait past the grace and one more piece.
            var wedged = new TerminalView();
            wedged.Measure(new Size(1000, 500));
            wedged.Arrange(new Rect(new Size(1000, 500)));
            wedged.Inject(Encoding.UTF8.GetBytes("\u001b[?2026h" + string.Join("\r\n", lines.Take(lines.Length / pieces)) + "\r\n"));
            Thread.Sleep(320);
            wedged.Inject(Encoding.UTF8.GetBytes(string.Join("\r\n", lines.Skip(lines.Length / pieces).Take(lines.Length / pieces)) + "\r\n"));
            var grace = wedged.SyncReadings;
            Console.WriteLine($"  bracket left open: deferred {grace.Deferred} released {grace.Released} open {(grace.Open ? 1 : 0)}"
                + $" -> {(grace.Released > 0 ? "the grace let a frame through" : "THE SCREEN IS WEDGED")}");
            return 0;
        }

        // `--bench resize`: what one column of a window drag costs. Reflow touches every row still held, and a
        // drag fires it tens of times a second — the answer decides whether this needs a cap.
        if (args.Length > 1 && args[1] == "resize")
        {
            var history = Read(args, 2, 5000);
            var resizes = Math.Max(1, Read(args, 3, 60));
            var engine = new QuickTerm.Terminal.Terminal(120, 42, history);
            var chunk = Encoding.UTF8.GetBytes(new string('x', 118) + "\r\n");
            for (var line = 0; line < history + 42; line++)
                engine.Write(chunk);

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < resizes; i++)
                engine.Resize(i % 2 == 0 ? 80 : 120, 42);
            var elapsed = watch.Elapsed.TotalMilliseconds;

            Console.WriteLine($"  history {engine.ScrollbackCount} rows, {engine.TotalRows} total");
            Console.WriteLine($"  {resizes} resizes: {elapsed:F1} ms total, {elapsed / resizes:F2} ms each"
                + $" | {(elapsed / resizes > 16 ? "OVER one frame - a drag would stutter" : "fits inside a frame")}");
            return 0;
        }

        // `--bench local`: what the host editor's shell list will actually offer on this machine. The dialog itself
        // needs a real top level — a Window's content rendered offscreen comes out blank, because the theme
        // styles only resolve once the tree reaches one — so the layout stays a desktop reading and this mode
        // only covers discovery.
        if (args.Length > 1 && args[1] == "local")
        {
            var choices = HostEditorPanel.DiscoverShells();
            Console.WriteLine($"shells offered on this machine ({choices.Count}):");
            foreach (var choice in choices)
                Console.WriteLine($"  {choice}");
            if (choices.Count == 0)
                Console.WriteLine("  none - the box starts empty and the session runs whatever COMSPEC names");
            return 0;
        }

        // `--bench grid`: a self-measuring screen. Rows of digit rulers, a cursor parked at a named column by
        // CUP, and one row where a wide glyph sits at a known column — so "the block is four cells early" is
        // read off the picture instead of argued from pixel guesses.
        // CUP, and one row where a wide glyph sits at a known column — so "the block is four cells early" is
        // read off the picture instead of argued from pixel guesses.
        if (args.Length > 1 && args[1] == "grid")
        {
            var gridWidth = Read(args, 2, 1100);
            var gridHeight = Read(args, 3, 200);
            var column = Read(args, 4, 44);
            var grid = new TerminalView();
            grid.Measure(new Size(gridWidth, gridHeight));
            grid.Arrange(new Rect(new Size(gridWidth, gridHeight)));

            var ruler = new StringBuilder();
            for (var block = 0; block < 12; block++)
                ruler.Append("0123456789");
            grid.Inject(Encoding.UTF8.GetBytes(
                ruler.ToString() + "\r\n" + ruler.ToString() + "\r\n" +
                "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789\r\n" +
                "wide row: 中文 in the middle\r\n" +
                $"\u001b[3;{column + 1}H"));

            using var gridded = new RenderTargetBitmap(new PixelSize(gridWidth, gridHeight), new Vector(96, 96));
            using (var context = gridded.CreateDrawingContext())
                grid.Render(context);
            WritePng(gridded, "quickterm-grid.png");
            Console.WriteLine(
                $"grid {grid.GridColumns}x{grid.GridRows} cell {grid.CellWidth:F3} px | cursor {grid.CursorPosition.Row},{grid.CursorPosition.Column}"
                + $" | CUP asked for column {column} | the block should sit on the digit {column % 10} of run {column / 10}");
            return 0;
        }

        // `--bench paint [file]`: feeds a captured stream through the renderer instead of only through the
        // engine, and writes a PNG. `--replay` can already say the grid is right; this is the other half of
        // the question — whether what got painted still matches it.
        if (args.Length > 1 && args[1] == "paint")
        {
            var paintWidth = Read(args, 2, 1100);
            var paintHeight = Read(args, 3, 260);
            var file = args.Length > 4 && Path.IsPathRooted(args[4])
                ? args[4]
                : Path.Combine(Path.GetTempPath(), "quickterm-pty-capture.bin");

            if (!File.Exists(file))
            {
                Console.WriteLine($"no capture at {file}");
                return 1;
            }

            var painter = new TerminalView();
            painter.Measure(new Size(paintWidth, paintHeight));
            painter.Arrange(new Rect(new Size(paintWidth, paintHeight)));

            // Fed in small pieces with a paint after each, because that is the shape the pump has: one all-at-
            // once Inject would make the erase land in the same frame as the characters it erased, and the
            // dirty-row path — the one that can leave stale ink — would never be exercised.
            var stream = File.ReadAllBytes(file);
            using var painted = new RenderTargetBitmap(new PixelSize(paintWidth, paintHeight), new Vector(96, 96));
            const int piece = 24;
            for (var offset = 0; offset < stream.Length; offset += piece)
            {
                painter.Inject(stream.AsSpan(offset, Math.Min(piece, stream.Length - offset)));
                using var context = painted.CreateDrawingContext();
                painter.Render(context);
            }

            WritePng(painted, "quickterm-paint.png");

            // A ruler row written underneath, so the block's column is read off the picture by vertical
            // alignment instead of off pixel guesses. Every tenth column carries its decade digit.
            var rulerRow = 5;
            var before = painter.CursorPosition;
            var rulerText = new StringBuilder();
            for (var column = 0; column < painter.GridColumns; column++)
                rulerText.Append(column % 10 == 0 ? (column / 10).ToString(CultureInfo.InvariantCulture)[^1] : '-');
            painter.Inject(Encoding.UTF8.GetBytes(
                $"\u001b[{rulerRow};1H{rulerText}\u001b[{before.Row + 1};{before.Column + 1}H"));
            using (var ruler = painted.CreateDrawingContext())
                painter.Render(ruler);
            WritePng(painted, "quickterm-paint.png");
            Console.WriteLine(
                $"grid {painter.GridColumns}x{painter.GridRows} cell {painter.CellWidth:F3} px | cursor before the ruler {before.Row},{before.Column}"
                + $" (block drawn at x={before.Column * painter.CellWidth:F1})");
            Console.WriteLine($"painted {new FileInfo(file).Length} bytes from {file} to {Path.Combine(Path.GetTempPath(), "quickterm-paint.png")}");
            return 0;
        }

        // `--bench scroll`: the same screen painted at the bottom and then pulled up over history, so
        // "内容无法滚动" is a reading I can take myself instead of waiting for a screenshot.
        if (args.Length > 1 && args[1] == "scroll")
        {
            var scrollWidth = Read(args, 2, 760);
            var scrollHeight = Read(args, 3, 220);
            var back = Read(args, 4, 30);
            var scroller = new TerminalView();
            scroller.Measure(new Size(scrollWidth, scrollHeight));
            scroller.Arrange(new Rect(new Size(scrollWidth, scrollHeight)));
            for (var line = 1; line <= 120; line++)
                scroller.Inject(Encoding.UTF8.GetBytes($"line {line} 第{line}行\r\n"));

            using var bottom = new RenderTargetBitmap(new PixelSize(scrollWidth, scrollHeight), new Vector(96, 96));
            using (var context = bottom.CreateDrawingContext())
                scroller.Render(context);
            WritePng(bottom, "quickterm-scroll-bottom.png");

            scroller.ScrollBy(back);
            using var pulled = new RenderTargetBitmap(new PixelSize(scrollWidth, scrollHeight), new Vector(96, 96));
            using (var context = pulled.CreateDrawingContext())
                scroller.Render(context);
            WritePng(pulled, "quickterm-scroll-back.png");

            Console.WriteLine(
                $"offset now {scroller.ScrollOffset} of {scroller.ScrollMaximum} | written to quickterm-scroll-bottom.png and -back.png");
            return 0;
        }

        var width = Read(args, 1, DefaultWidth);
        var height = Read(args, 2, DefaultHeight);
        var frames = Math.Max(3, Read(args, 3, DefaultFrames));

        // `--bench threads` is a negative control, not a measurement: it asks whether anything but the UI
        // thread can drive the renderer. The answer came back as a thrown VerifyAccess, which kills the
        // "text work serialises on a lock across threads" hypothesis outright — there is no second thread
        // painting, so nothing competes with the pump for the text system.
        var threaded = args.Contains("threads");
        var size = new Size(width, height);
        var pixels = new PixelSize(width, height);
        var dpi = new Vector(96, 96);

        Console.WriteLine($"bench offscreen {width}x{height} logical px @1x, {frames} frames per scenario");
        ProbeNesting(pixels, dpi, Math.Max(frames, 200));
        Console.WriteLine($"  CreateDrawingContext(clear: false) retains   : {Describe(ProbeRetention(pixels, dpi))}");
        Console.WriteLine($"  whole-surface blit (every frame pays it)     : {Format(Blit(pixels, dpi, frames))}");

        var view = new TerminalView();
        view.Measure(size);
        view.Arrange(new Rect(size));
        using var scratch = new RenderTargetBitmap(pixels, dpi);

        if (threaded)
        {
            var threw = string.Empty;
            var pump = new Thread(() =>
            {
                try
                {
                    view.Inject(Encoding.UTF8.GetBytes("pump thread shaping text\r\n"));
                    using var context = scratch.CreateDrawingContext(clear: false);
                    view.Render(context);
                }
                catch (Exception ex)
                {
                    threw = $"{ex.GetType().Name}: {ex.Message}";
                }
            })
            { IsBackground = true, Name = "QuickTerm.bench.pump" };

            pump.Start();
            pump.Join(5000);
            Console.WriteLine($"  background thread paints the control : {(threw.Length == 0 ? "it did NOT throw - the hypothesis is alive" : "threw " + threw)}");
        }

        var flood = Measure(view, scratch, frames, FloodStep);
        var churn = Measure(view, scratch, frames, ChurnStep);
        var quiet = Measure(view, scratch, frames, QuietStep);

        Console.WriteLine($"  flood - one screenful of new lines per frame : {Format(flood)}");
        Console.WriteLine($"  churn - one progress line per frame          : {Format(churn)}");
        Console.WriteLine($"  quiet - cursor hops, no cell changes         : {Format(quiet)}");
        Console.WriteLine($"  paint ratio flood/churn (what dirty rows buy): {flood.PaintMs / Math.Max(0.001, churn.PaintMs):F1}x");
        Console.WriteLine($"  mode: concurrent pump thread                 : {(threaded ? "on (see the negative control above)" : "off")}");

        var chain = view.InputChain;
        Console.WriteLine($"  input chain offscreen keys/txt/empty/enc0/sent/err/foc: "
            + $"{chain.Keys}/{chain.Text}/{chain.EmptyText}/{chain.EncodeZero}/{chain.Sent}/{chain.Errors}/{(chain.Focused ? 1 : 0)}");
        Console.WriteLine("note: 1x scaling only - a detached visual reports no TopLevel, so a HiDPI window stays a desktop reading.");
        return 0;
    }

    /// <summary>One screenful with line feeds: the region scrolls, and a scroll dirties every row in it.</summary>
    private static byte[] FloodStep(int frame)
    {
        var builder = new StringBuilder();
        for (var line = 0; line < 24; line++)
        {
            builder.Append((frame * 24 + line).ToString(CultureInfo.InvariantCulture).PadRight(6))
                   .Append(" 编译 main.c 中 ")
                   .Append(new string('x', 60))
                   .Append("\u001b[0m\r\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>A progress bar on one row: carriage return, repaint, erase to end of line. Nothing scrolls.</summary>
    private static byte[] ChurnStep(int frame)
        => Encoding.UTF8.GetBytes(
            $"\u001b[10;1H\u001b[32m{new string('=', frame % 40)}\u001b[0m {frame % 40}% done\u001b[K");

    /// <summary>Cursor placement with no output at all — the frame a typing user generates most often.</summary>
    private static byte[] QuietStep(int frame)
        => Encoding.UTF8.GetBytes($"\u001b[{1 + frame % 20};{1 + frame % 70}H");

    private readonly record struct Cost(
        double ParseMs, double PaintMs, long BytesPerFrame, int Rows, double WriteMs = 0, double BlitMs = 0);

    private static Cost Measure(TerminalView view, RenderTargetBitmap scratch, int frames, Func<int, byte[]> step)
    {
        double parse = 0;
        double paint = 0;
        double write = 0;
        double blit = 0;
        var rows = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; frame < frames; frame++)
        {
            var watch = Stopwatch.StartNew();
            view.Inject(step(frame));
            parse += watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            using (var context = scratch.CreateDrawingContext())
                view.Render(context);
            paint += watch.Elapsed.TotalMilliseconds;
            rows = view.LastPaintedRows;
            var (writeMs, blitMs) = view.LastPhaseMilliseconds;
            write += writeMs;
            blit += blitMs;
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return new Cost(parse / frames, paint / frames, bytes / frames, rows, write / frames, blit / frames);
    }

    /// <summary>Cost of the bitmap copy alone, which is what the per-frame blit is.</summary>
    private static Cost Blit(PixelSize pixels, Vector dpi, int frames)
    {
        using var source = new RenderTargetBitmap(pixels, dpi);
        using var target = new RenderTargetBitmap(pixels, dpi);
        var area = new Rect(0, 0, pixels.Width, pixels.Height);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();

        for (var frame = 0; frame < frames; frame++)
        {
            using var context = target.CreateDrawingContext();
            context.DrawImage(source, area);
        }

        var elapsed = watch.Elapsed.TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return new Cost(0, elapsed / frames, bytes / frames, 0);
    }

    /// <summary>
    /// The promise the dirty-row path stands on, checked against this backend: paint a red field, then a blue
    /// stripe in a batch that says <c>clear: false</c>. If the red survived, the two batches' output differs
    /// from a target that only ever saw the stripe. If it did not survive, every row but the changed one would
    /// be blank on screen, and that is what this reading would have caught.
    /// </summary>
    private static bool ProbeRetention(PixelSize pixels, Vector dpi)
    {
        var field = new Rect(0, 0, pixels.Width, pixels.Height);
        var stripe = new Rect(0, 0, pixels.Width / 4, pixels.Height);

        using var layered = new RenderTargetBitmap(pixels, dpi);
        using (var context = layered.CreateDrawingContext())
            context.FillRectangle(Brushes.Red, field);
        using (var context = layered.CreateDrawingContext(clear: false))
            context.FillRectangle(Brushes.Blue, stripe);

        using var single = new RenderTargetBitmap(pixels, dpi);
        using (var context = single.CreateDrawingContext())
            context.FillRectangle(Brushes.Blue, stripe);

        return !Convert.ToHexString(Sha(Png(layered))).Equals(Convert.ToHexString(Sha(Png(single))), StringComparison.Ordinal);
    }

    /// <summary>
    /// Is opening a draw batch on one render target while another target's batch is still open — what every
    /// frame of this control does — legal, or does the backend eventually refuse to make progress? Run before
    /// anything else, because a stall here is a shape problem, not a measurement problem.
    /// </summary>
    private static void ProbeNesting(PixelSize pixels, Vector dpi, int rounds)
    {
        using var inner = new RenderTargetBitmap(pixels, dpi);
        using var outer = new RenderTargetBitmap(pixels, dpi);
        var area = new Rect(0, 0, pixels.Width, pixels.Height);
        var brush = new SolidColorBrush(Colors.Aquamarine);

        for (var round = 0; round < rounds; round++)
        {
            using (var context = outer.CreateDrawingContext())
            {
                using (var nested = inner.CreateDrawingContext(clear: round % 2 == 0))
                    nested.FillRectangle(brush, area);
                context.DrawImage(inner, area);
            }

            if (round == rounds - 1)
                Console.WriteLine($"  nested batches x{rounds}                           : ok");
        }
    }

    private static void WritePng(RenderTargetBitmap target, string name)
    {
        using var stream = File.Create(Path.Combine(Path.GetTempPath(), name));
        target.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private static byte[] Png(RenderTargetBitmap target)
    {
        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static byte[] Sha(byte[] bytes) => SHA256.HashData(bytes);

    private static string Describe(bool retains) => retains ? "yes (dirty-row repaint stands)" : "NO - the surface would be wiped each batch";

    private static string Format(Cost cost)
        => $"{cost.PaintMs:F2} ms paint (write {cost.WriteMs:F2} · blit {cost.BlitMs:F2})"
         + $" | {cost.ParseMs:F2} ms parse | {cost.Rows} rows | {cost.BytesPerFrame / 1024.0:F1} KiB/frame";

    private static int Read(string[] args, int index, int fallback)
        => args.Length > index && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
