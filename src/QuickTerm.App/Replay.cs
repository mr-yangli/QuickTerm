namespace QuickTerm.App;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuickTerm.Terminal;

/// <summary>
/// <c>QuickTerm --replay [cols] [rows] [file]</c>: feeds a captured stream through the engine and writes
/// <c>%TEMP%\quickterm-replay.log</c> — the raw stream with its escapes named, then one line per grid row with
/// the wide-glyph columns marked and the run table beside it.
/// </summary>
/// <remarks>
/// The question this settles is which side of the host split a line. A screenshot can say "the banner is in
/// two pieces" but not whether the bytes contained a newline, whether the engine wrapped at its own width,
/// or whether the paint put a row's text in the wrong band — and those three have three different fixes.
/// Feeding a captured stream through the engine alone separates them.
///
/// This is a reading tool, not a gate: the file it replays is a machine's shell output, so it belongs in
/// TEMP and not in the test book.
/// </remarks>
internal static class Replay
{
    public static int Run(string[] args)
    {
        var columns = Number(args, 1, 50);
        var rows = Number(args, 2, 10);

        // A file argument reaches a QUICKTERM_DUMP capture; with none it replays what --pty-capture left.
        var capture = args.Length > 3 && Path.IsPathRooted(args[3])
            ? args[3]
            : Path.Combine(Path.GetTempPath(), "quickterm-pty-capture.bin");
        var logPath = Path.Combine(Path.GetTempPath(), "quickterm-replay.log");
        var lines = new List<string>();
        void Log(string line) => lines.Add(line);

        Log($"replay {DateTime.Now:O} file={capture} grid={columns}x{rows}");
        if (!File.Exists(capture))
        {
            Log("no capture file - run QuickTerm --pty-capture first");
            File.WriteAllLines(logPath, lines);
            return 1;
        }

        var bytes = File.ReadAllBytes(capture);
        Log($"bytes={bytes.Length}");
        Log($"raw: {Readable(bytes)}");

        var term = new Terminal(columns, rows, 0);

        // A fifth argument feeds the stream in pieces instead of all at once, which is what the pump does. A
        // sequence split across two reads has to resume, not be dropped — and the difference between those
        // two outcomes only shows up when the split lands inside it.
        var piece = args.Length > 4 && int.TryParse(args[4], out var size) && size > 0 ? size : bytes.Length;
        for (var offset = 0; offset < bytes.Length; offset += piece)
            term.Write(bytes.AsSpan(offset, Math.Min(piece, bytes.Length - offset)));

        Log($"cursor={term.CursorRow},{term.CursorColumn} autowrap={term.AutoWrap} alt={term.InAltScreen} damage={term.Damage.Top}..{term.Damage.Bottom}");
        var runBuf = new InkRun[Math.Max(1, term.Columns)];
        for (var row = 0; row < term.Rows; row++)
        {
            var cells = term.Row(row);
            var text = new StringBuilder();
            var wide = new StringBuilder();
            for (var column = 0; column < cells.Length; column++)
            {
                var cell = cells[column];
                if ((cell.Flags & CellFlags.Tail) != 0)
                    continue;
                text.Append(cell.Rune == 0 ? ' ' : char.ConvertFromUtf32(cell.Rune));
                if ((cell.Flags & CellFlags.Wide) != 0)
                    wide.Append(CultureInfo.InvariantCulture, $"{column}={char.ConvertFromUtf32(cell.Rune)} ");
            }

            Log($"row {row,-2} wrap={(term.RowWrapped(row) ? 1 : 0)} |{text.ToString().TrimEnd()}|");
            if (wide.Length > 0)
                Log($"      wide @ {wide.ToString().TrimEnd()}");

            var count = InkRuns.Build(cells, InkKey, runBuf);
            var table = new StringBuilder();
            for (var run = 0; run < count; run++)
                table.Append(CultureInfo.InvariantCulture, $"[{runBuf[run].Start}..{runBuf[run].End} x{runBuf[run].Columns}] ");
            Log($"      runs {count} :: {table.ToString().TrimEnd()}");
        }

        File.WriteAllLines(logPath, lines);
        return 0;
    }

    /// <summary>The host's colour key, reduced to what groups a run: same colour, one drawing call.</summary>
    private static uint InkKey(TermCell cell, int column) => (uint)(cell.Fg.Kind * 256 + cell.Fg.Index);

    /// <summary>The stream with its controls named, so a newline from the shell is distinguishable from a wrap.</summary>
    private static string Readable(byte[] bytes)
    {
        var builder = new StringBuilder();
        foreach (var value in bytes)
        {
            if (builder.Length > 6000)
            {
                builder.Append(" ...truncated");
                break;
            }

            builder.Append(value switch
            {
                0x1b => "ESC",
                0x0d => "CR",
                0x0a => "LF",
                0x08 => "BS",
                0x07 => "BEL",
                0x09 => "TAB",
                >= 0x20 and <= 0x7e => ((char)value).ToString(),
                _ => $"<{value:X2}>",
            });
        }

        return builder.ToString();
    }

    private static int Number(string[] args, int index, int fallback)
        => args.Length > index && int.TryParse(args[index], out var value) && value > 0 ? value : fallback;
}
