namespace QuickTerm.Terminal;

/// <summary>
/// Recycles <see cref="TermLine"/> objects and their cell arrays. Scrolling is the hot path of a busy
/// terminal, and without this every scrolled row costs a fresh cell array (a 200-column row is 3.2 KB).
/// </summary>
internal sealed class TermLinePool
{
    private const int MaxHeld = 8192;
    private readonly Stack<TermLine> _free = new();

    public TermLine Rent(int columns)
    {
        var line = _free.Count > 0 ? _free.Pop() : new TermLine();
        line.Initialize(columns);
        return line;
    }

    public void Return(TermLine line)
    {
        if (_free.Count < MaxHeld)
            _free.Push(line);
    }

    public void ReturnMany(TermLine[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
            Return(lines[i]);
    }
}

/// <summary>
/// One screen surface: the rows, the scroll region, the cursor, its saved cursor and the tab stops.
/// The main screen and the alternate screen each own one of these, which is why cursor state that must
/// survive an alt-screen round trip lives here rather than on <see cref="Terminal"/>.
/// Scroll-out routing (scrollback versus pool) is deliberately <em>not</em> decided here.
/// </summary>
internal sealed class Screen
{
    private readonly TermLinePool _pool;

    public TermLine[] Lines = [];
    public int Columns = 1;
    public int Rows = 1;

    /// <summary>Inclusive top row of the scroll region (DECSTBM).</summary>
    public int ScrollTop;

    /// <summary>Inclusive bottom row of the scroll region (DECSTBM).</summary>
    public int ScrollBottom;

    public int CursorColumn;
    public int CursorRow;
    public int SavedColumn;
    public int SavedRow;
    public TermEffects SavedEffects;
    public TerminalColor SavedFg;
    public TerminalColor SavedBg;

    public bool[] TabStops = [];

    public Screen(TermLinePool pool, int columns, int rows)
    {
        _pool = pool;
        Reset(columns, rows);
    }

    public TermLine this[int row] => Lines[row];

    /// <summary>Rebuilds every row and the tab stops, as RIS or alt-screen entry requires.</summary>
    public void Reset(int columns, int rows)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);

        for (var i = 0; i < Rows; i++)
        {
            if (i < Lines.Length && Lines[i] is { } spare)
                _pool.Return(spare);
        }
        if (Lines.Length != Rows)
            Array.Resize(ref Lines, Rows);
        for (var i = 0; i < Rows; i++)
            Lines[i] = _pool.Rent(Columns);

        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        CursorColumn = CursorRow = SavedColumn = SavedRow = 0;
        RebuildTabStops();
    }

    /// <summary>
    /// Pads or truncates every row to the new width and drops or appends rows at the bottom.
    /// No re-flow: a soft-wrapped line stays split, which is the documented P0 gap.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        var old = Lines;
        Lines = new TermLine[rows];
        var sharedRows = Math.Min(old.Length, rows);
        for (var i = 0; i < sharedRows; i++)
            Lines[i] = _pool.Rent(columns);
        for (var i = sharedRows; i < rows; i++)
            Lines[i] = _pool.Rent(columns);

        Columns = columns;
        Rows = rows;

        for (var i = 0; i < sharedRows; i++)
        {
            var src = old[i].Cells;
            var dst = Lines[i].Cells;
            Array.Copy(src, 0, dst, 0, Math.Min(src.Length, columns));
            Lines[i].Wrapped = old[i].Wrapped;
        }
        for (var i = 0; i < old.Length; i++)
            _pool.Return(old[i]);

        ScrollTop = 0;
        ScrollBottom = rows - 1;
        RebuildTabStops();
        ClampCursor();
    }

    public void ClampCursor()
    {
        CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, Columns - 1);
    }

    public void RebuildTabStops()
    {
        TabStops = new bool[Columns];
        for (var i = 8; i < Columns; i += 8)
            TabStops[i] = true;
    }

    /// <summary>Shifts the region up by one and hands back the row that left the top.</summary>
    public TermLine ScrollUp()
    {
        var top = ScrollTop;
        var bottom = ScrollBottom;
        var evicted = Lines[top];
        Array.Copy(Lines, top + 1, Lines, top, bottom - top);
        Lines[bottom] = _pool.Rent(Columns);
        return evicted;
    }

    /// <summary>Shifts the region down by one and hands back the row that left the bottom.</summary>
    public TermLine ScrollDown()
    {
        var top = ScrollTop;
        var bottom = ScrollBottom;
        var evicted = Lines[bottom];
        Array.Copy(Lines, top, Lines, top + 1, bottom - top);
        Lines[top] = _pool.Rent(Columns);
        return evicted;
    }

    /// <summary>
    /// Inserts <c>count</c> blank rows at <paramref name="row"/>, evicting the tail of the region.
    /// The evicted rows are captured before the shift and recycled only after the new rows are rented:
    /// returning them first lets the pool hand the very same object back and leave two rows aliased.
    /// </summary>
    public void InsertLines(int row, int count)
    {
        count = Math.Min(count, ScrollBottom - row + 1);
        var moved = ScrollBottom - row - count + 1;
        var tail = ScrollBottom - count + 1;
        var evicted = new TermLine[count];
        Array.Copy(Lines, tail, evicted, 0, count);
        if (moved > 0)
            Array.Copy(Lines, row, Lines, row + count, moved);
        for (var i = 0; i < count; i++)
            Lines[row + i] = _pool.Rent(Columns);
        _pool.ReturnMany(evicted);
    }

    /// <summary>Deletes <c>count</c> rows at <paramref name="row"/>, evicting the tail of the region.</summary>
    public void DeleteLines(int row, int count)
    {
        count = Math.Min(count, ScrollBottom - row + 1);
        var moved = ScrollBottom - row - count + 1;
        var tail = ScrollBottom - count + 1;
        var evicted = new TermLine[count];
        Array.Copy(Lines, tail, evicted, 0, count);
        if (moved > 0)
            Array.Copy(Lines, row + count, Lines, row, moved);
        for (var i = 0; i < count; i++)
            Lines[tail + i] = _pool.Rent(Columns);
        _pool.ReturnMany(evicted);
    }

    /// <summary>Blank cells starting at <paramref name="fromColumn"/>; <c>count</c> -1 runs to the end of the row.</summary>
    public void EraseCells(int row, int fromColumn, int count = -1)
    {
        var cells = Lines[row].Cells;
        var from = Math.Max(0, fromColumn);
        var to = count < 0 ? Columns : Math.Min(Columns, fromColumn + count);
        for (var i = from; i < to; i++)
            cells[i] = default;
    }

    public void InsertCells(int row, int fromColumn, int count)
    {
        var cells = Lines[row].Cells;
        count = Math.Min(count, Columns - fromColumn);
        for (var i = Columns - 1; i >= fromColumn + count; i--)
            cells[i] = cells[i - count];
        for (var i = fromColumn; i < fromColumn + count; i++)
            cells[i] = default;
    }

    public void DeleteCells(int row, int fromColumn, int count)
    {
        var cells = Lines[row].Cells;
        count = Math.Min(count, Columns - fromColumn);
        for (var i = fromColumn; i < Columns - count; i++)
            cells[i] = cells[i + count];
        for (var i = Columns - count; i < Columns; i++)
            cells[i] = default;
    }
}
