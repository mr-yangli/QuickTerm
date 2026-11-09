namespace QuickTerm.Terminal;

using System;

/// <summary>
/// One stretch of a row's ink that a host can hand to a single text-drawing call: one ink key, and the number
/// of grid columns it owes.
/// </summary>
/// <remarks>
/// The column count is the reason this exists rather than a plain string split: a double-width glyph's tail
/// sits inside the run's cell range and contributes no character, so the columns the ink must be scaled onto
/// is not the number of cells it spans. Getting that wrong is what leaves a CJK phrase either full of gaps or
/// squeezed into half its cells.
///
/// Runs break on colour and on erased cells, and deliberately not on glyph width: cutting a line at every CJK
/// boundary costs five shaping calls where it used to cost one, measured as 29 ms against 11 ms for a
/// screenful. Stretching one run onto its own band instead puts its last glyph exactly on its last cell and
/// shares the small remainder among the rest.
/// </remarks>
public readonly struct InkRun
{
    public InkRun(int start, int end, int columns, uint key)
    {
        Start = start;
        End = end;
        Columns = columns;
        Key = key;
    }

    /// <summary>Index of the run's first cell.</summary>
    public int Start { get; }

    /// <summary>One past the run's last cell index. Tails are inside the range and carry no character.</summary>
    public int End { get; }

    /// <summary>Grid columns the run occupies, which is what its drawn width has to map onto.</summary>
    public int Columns { get; }

    /// <summary>The ink key the run was grouped by — opaque here, and the host's colour lookup result.</summary>
    public uint Key { get; }
}

/// <summary>
/// Splits a row into the runs a host draws. Kept out of the renderer because the only thing that can go wrong
/// in it is index arithmetic, and index arithmetic is what an offline book can pin.
/// </summary>
public static class InkRuns
{
    /// <summary>
    /// Fills <paramref name="output" /> and returns how many runs went in. A row that needs more slots than
    /// the buffer holds stops early rather than writing past it: the host's remaining cells simply go undrawn
    /// for that frame, which is a visible glitch, not a memory bug.
    /// </summary>
    /// <remarks>
    /// The key callback gets the column as well as the cell because a host's ink can depend on position —
    /// a selected cell is a different colour from the same cell unselected — and the splitter has to break
    /// where the drawing would.
    /// </remarks>
    public static int Build(ReadOnlySpan<TermCell> cells, Func<TermCell, int, uint> inkKey, Span<InkRun> output)
    {
        var count = 0;
        var index = 0;
        while (index < cells.Length)
        {
            var first = cells[index];
            if (first.Rune == 0 || (first.Flags & CellFlags.Tail) != 0)
            {
                // Blank, or the second half of a wide glyph whose head is on another row: nothing starts here.
                index++;
                continue;
            }

            if (count == output.Length)
                break;

            var start = index;
            var key = inkKey(first, index);
            var columns = 0;
            while (index < cells.Length)
            {
                var cell = cells[index];
                if ((cell.Flags & CellFlags.Tail) != 0)
                {
                    // Its head already counted the column; the character belongs to the head's drawing.
                    index++;
                    continue;
                }

                if (cell.Rune == 0 || inkKey(cell, index) != key)
                    break;

                columns += (cell.Flags & CellFlags.Wide) != 0 ? 2 : 1;
                index++;
            }

            // Trailing typed spaces come off the end. They carry no ink, and a text measurement does not
            // count them — measured here, "AB" and "AB      " both come back as 16.406 px — so leaving them
            // in makes any width-derived scale stretch the ink that precedes them. That is what put the
            // cursor block several columns early after cmd erased typed characters with spaces.
            var end = index;
            while (end > start && cells[end - 1].Rune == ' ')
            {
                end--;
                columns--;
            }

            if (end == start)
                continue; // the whole run was blanks: nothing to draw, and the cells are already stepped over

            output[count++] = new InkRun(start, end, columns, key);
        }

        return count;
    }
}
