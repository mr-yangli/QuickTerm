namespace QuickTerm.Terminal;

using System;
using System.Text;

/// <summary>A cell in the absolute row space: history rows first, then the visible grid.</summary>
/// <param name="Row">Row index in <see cref="Terminal.TotalRows" />.</param>
/// <param name="Column">Cell column, 0-based.</param>
public readonly record struct TermPosition(int Row, int Column);

/// <summary>
/// A resolved range in the absolute row space. Both ends are exclusive at the column — the same convention a
/// drag produces — so a single cell is <c>(row, c)</c> to <c>(row, c + 1)</c>, and a whole row is 0 to the
/// row's width.
/// </summary>
public readonly record struct TermRange(TermPosition Start, TermPosition End)
{
    public int RowCount => End.Row - Start.Row + 1;

    /// <summary>
    /// The text of the range, logical lines rejoined where a soft wrap split them. Trailing blanks go per
    /// fragment; a newline is written before a row that is not a continuation of the one before it, so the
    /// last row never carries a phantom line break and a wrapped line never splits in the middle.
    /// </summary>
    public readonly string Text(Terminal term)
    {
        if (term.TotalRows == 0)
            return string.Empty;

        var builder = new StringBuilder();
        var lastRow = Math.Clamp(End.Row, 0, term.TotalRows - 1);

        for (var row = Math.Clamp(Start.Row, 0, term.TotalRows - 1); row <= lastRow; row++)
        {
            var cells = term.AbsoluteRow(row);
            var from = Math.Min(row == Start.Row ? Start.Column : 0, cells.Length);
            var to = Math.Min(row == End.Row ? End.Column : cells.Length, cells.Length);

            var piece = new StringBuilder();
            for (var column = from; column < to; column++)
            {
                var cell = cells[column];
                if ((cell.Flags & CellFlags.Tail) != 0)
                    continue; // the second half of a wide glyph contributes nothing

                piece.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
            }

            var trimmed = piece.ToString().TrimEnd();

            // A range that covered real cells but found no ink there selected a blank — that is what was
            // pointed at, and it is what a paste should produce.
            if (trimmed.Length == 0 && to > from)
                trimmed = " ";

            // A fragment that wrapped into this one is the same logical line: no newline between them.
            if (builder.Length > 0 && !(row > 0 && term.AbsoluteRowWrapped(row - 1)))
                builder.Append('\n');

            builder.Append(trimmed);
        }

        return builder.ToString();
    }
}

/// <summary>How a click or double-click grows into a range.</summary>
public enum TermSelectMode
{
    /// <summary>Exact cell range, extended by dragging.</summary>
    Cell,

    /// <summary>Whole word, as a double-click picks one; dragging extends by whole words.</summary>
    Word,

    /// <summary>Whole logical line; dragging extends by whole lines.</summary>
    Line,
}

/// <summary>
/// A selection over a <see cref="Terminal" />. It holds only the two endpoints and the mode, so it costs
/// nothing to keep across frames and cannot go stale when the grid scrolls — the endpoints are re-read
/// against whatever the grid holds at the moment the text is asked for.
/// </summary>
/// <remarks>
/// Deliberately not a snapshot of cells: a terminal selection that froze its content would keep showing text
/// the program has already overwritten, which is wrong in the way users notice.
/// </remarks>
public sealed class TermSelection
{
    private TermPosition _anchor;
    private TermPosition _focus;

    public TermPosition Anchor => _anchor;

    public TermPosition Focus => _focus;

    public TermSelectMode Mode { get; private set; }

    public bool IsEmpty => _anchor.Row == _focus.Row && _anchor.Column == _focus.Column;

    /// <summary>Starts a fresh selection at one cell, in the given mode.</summary>
    public void Start(TermPosition anchor, TermSelectMode mode)
    {
        _anchor = anchor;
        _focus = anchor;
        Mode = mode;
    }

    /// <summary>Moves the far end. The anchor stays where the drag began, as xterm does.</summary>
    public void Extend(TermPosition focus) => _focus = focus;

    public void Clear()
    {
        _anchor = default;
        _focus = default;
        Mode = TermSelectMode.Cell;
    }

    /// <summary>The selected range in reading order, endpoints normalised and word/line modes grown.</summary>
    public TermRange Resolve(Terminal term)
    {
        if (term.TotalRows == 0)
            return new TermRange(default, default);

        var first = _anchor;
        var last = _focus;
        if (Before(last, first))
        {
            first = last;
            last = _anchor;
        }

        if (Mode == TermSelectMode.Cell)
        {
            first = Clamp(term, first);
            last = Clamp(term, last);
            // Both ends exclusive: a click on one cell is that cell, not nothing.
            return new TermRange(first, new TermPosition(last.Row, last.Column + 1));
        }

        var start = Mode == TermSelectMode.Word ? WordStart(term, first) : LineStart(term, first);
        var end = Mode == TermSelectMode.Word ? WordEnd(term, last) : LineEnd(term, last);

        // A double-click on trailing blanks would otherwise reach past the end of its own line.
        if (Before(end, start))
            end = start;

        // An empty span (a double-click on padding) still shows the one cell under the pointer.
        if (Equals(end, start) && end.Column == start.Column)
            end = new TermPosition(end.Row, Math.Min(end.Column + 1, term.AbsoluteRow(end.Row).Length));

        return new TermRange(start, end);
    }

    /// <summary>The selected text, with the range's rules for wraps, blanks and wide glyphs.</summary>
    public string Text(Terminal term) => Resolve(term).Text(term);

    private static TermPosition Clamp(Terminal term, TermPosition position)
    {
        var row = Math.Clamp(position.Row, 0, term.TotalRows - 1);
        var column = Math.Clamp(position.Column, 0, term.AbsoluteRow(row).Length - 1);
        return new TermPosition(row, column);
    }

    private static bool Before(TermPosition a, TermPosition b)
        => a.Row < b.Row || (a.Row == b.Row && a.Column < b.Column);

    /// <summary>The first cell of the word under the position.</summary>
    internal static TermPosition WordStart(Terminal term, TermPosition position)
    {
        var clamped = Clamp(term, position);
        var cells = term.AbsoluteRow(clamped.Row);
        var column = clamped.Column;
        var kind = Classify(cells[column]);
        if (kind == WordKind.None)
            return clamped;

        while (column > 0 && Classify(cells[column - 1]) == kind)
            column--;

        return new TermPosition(clamped.Row, column);
    }

    /// <summary>One cell past the word's last cell, which is the exclusive end a range wants.</summary>
    internal static TermPosition WordEnd(Terminal term, TermPosition position)
    {
        var clamped = Clamp(term, position);
        var cells = term.AbsoluteRow(clamped.Row);
        var column = clamped.Column;
        var kind = Classify(cells[column]);
        if (kind == WordKind.None)
            return new TermPosition(clamped.Row, column + 1);

        while (column + 1 < cells.Length && Classify(cells[column + 1]) == kind)
            column++;

        return new TermPosition(clamped.Row, column + 1);
    }

    /// <summary>Start of the logical line: walks back over fragments a soft wrap split apart.</summary>
    private static TermPosition LineStart(Terminal term, TermPosition position)
    {
        var row = Math.Clamp(position.Row, 0, term.TotalRows - 1);
        while (row > 0 && term.AbsoluteRowWrapped(row - 1))
            row--;

        return new TermPosition(row, 0);
    }

    /// <summary>End of the logical line: the last fragment of it, one cell past its final non-blank.</summary>
    private static TermPosition LineEnd(Terminal term, TermPosition position)
    {
        var row = Math.Clamp(position.Row, 0, term.TotalRows - 1);
        while (row + 1 < term.TotalRows && term.AbsoluteRowWrapped(row))
            row++;

        var cells = term.AbsoluteRow(row);
        var end = cells.Length;
        while (end > 0 && IsBlank(cells[end - 1]))
            end--;

        return new TermPosition(row, end);
    }

    private enum WordKind
    {
        None,
        Alphanumeric,
        Other,
    }

    private static WordKind Classify(in TermCell cell)
    {
        if (cell.Rune is 0 or ' ')
            return WordKind.None;

        // Paths and flags read as one word: double-clicking inside /usr/local/bin should not stop at a slash.
        return char.IsLetterOrDigit((char)cell.Rune) || cell.Rune is '_' or '-' or '.' or '/' or '\\'
            ? WordKind.Alphanumeric
            : WordKind.Other;
    }

    private static bool IsBlank(in TermCell cell) => cell.Rune is 0 or ' ' && (cell.Flags & CellFlags.Tail) == 0;
}
