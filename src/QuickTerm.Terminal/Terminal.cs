namespace QuickTerm.Terminal;

/// <summary>
/// Cursor shape requested by DECSCUSR, numbered exactly like the sequence's parameter so the parser
/// can cast instead of translate. The renderer picks the brush; the engine only remembers.
/// </summary>
public enum TerminalCursorStyle
{
    BlockBlink = 1,
    BlockSteady = 2,
    UnderlineBlink = 3,
    UnderlineSteady = 4,
    BarBlink = 5,
    BarSteady = 6,
}

/// <summary>
/// The VT100/XTerm engine. Bytes go in through <see cref="Write"/>, the grid and its damage come out.
/// One instance is single-threaded by contract: the host marshals pty bytes onto the thread that paints,
/// because the cell grid has no cheap way to be read consistently mid-write.
/// </summary>
public sealed class Terminal
{
    private const int ReplyCapacity = 4096;

    private readonly VtParser _parser;
    private readonly TermLinePool _pool = new();
    private readonly Screen _main;
    private readonly Screen _alt;
    private Screen _screen;

    /// <summary>Ring of scrolled-off main-screen rows; index 0 is the oldest row still held.</summary>
    private TermLine[] _scrollback;
    private int _scrollbackHead;
    private int _scrollbackCount;

    private readonly byte[] _reply = new byte[ReplyCapacity];
    private int _replyLength;

    private bool _wrapPending;
    private bool _glUsesG1;
    private int _altReturnColumn;
    private int _altReturnRow;

    public Terminal(int columns = 80, int rows = 24, int scrollbackLines = 5000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        _main = new Screen(_pool, columns, rows);
        _alt = new Screen(_pool, columns, rows);
        _screen = _main;
        _scrollback = new TermLine[Math.Max(0, scrollbackLines)];
        _parser = new VtParser(this);
        Damage = new TerminalDamage();
    }

    /// <summary>Rows touched since the last <see cref="TerminalDamage.Consume"/>.</summary>
    public TerminalDamage Damage { get; }

    /// <summary>
    /// The colour table a renderer resolves cells against. On the engine, because OSC 4/10/11/104 rewrite it
    /// while programs run and whatever draws next has to see it.
    /// </summary>
    public TermPalette Palette { get; } = new();

    /// <summary>
    /// Marks every row dirty without changing a cell. A palette edit is exactly that case: the cells still
    /// say "slot 1", but slot 1 now means a different colour, so whatever is on screen has to be drawn again.
    /// </summary>
    internal void RepaintEverything() => Damage.MarkAll();

    public int Columns => _screen.Columns;

    public int Rows => _screen.Rows;

    public int CursorColumn => _screen.CursorColumn;

    public int CursorRow => _screen.CursorRow;

    /// <summary>Rows scrolled off the top of the main screen and still held.</summary>
    public int ScrollbackCount => _scrollbackCount;

    /// <summary>Row capacity of the scrollback ring; shrinking drops the oldest rows.</summary>
    public int ScrollbackLimit
    {
        get => _scrollback.Length;
        set => SetScrollbackLimit(value);
    }

    public bool InAltScreen => ReferenceEquals(_screen, _alt);

    public bool CursorVisible { get; private set; } = true;

    public bool AutoWrap { get; private set; } = true;

    public bool OriginMode { get; private set; }

    public bool InsertMode { get; private set; }

    public bool ApplicationCursorKeys { get; private set; }

    public bool ApplicationKeypad { get; private set; }

    public bool BracketedPaste { get; private set; }

    /// <summary>DEC 1000: report a press and a release.</summary>
    public bool MouseClicks { get; private set; }

    /// <summary>DEC 1002: report motion only while a button is held.</summary>
    public bool MouseDrag { get; private set; }

    /// <summary>DEC 1003: report motion at all times.</summary>
    public bool MouseAnyMove { get; private set; }

    /// <summary>Any of DEC 1000/1002/1003 — the encoder's first question, computed so clearing one mode cannot clear the others.</summary>
    public bool MouseTracking => MouseClicks || MouseDrag || MouseAnyMove;

    public bool MouseSgrEncoding { get; private set; }

    /// <summary>DEC 1007 (xterm's alternateScroll), on by default: in the alt screen the wheel becomes cursor keys.</summary>
    public bool MouseAlternateScroll { get; private set; } = true;

    public TerminalCursorStyle CursorStyle { get; private set; } = TerminalCursorStyle.BlockSteady;

    public string? WindowTitle { get; private set; }

    /// <summary>Raised for OSC 0/1/2. The host is expected to already be on its UI thread.</summary>
    public event Action<string?>? TitleChanged;

    /// <summary>
    /// Raised for OSC 52 with the payload already base64-decoded and UTF-8 decoded, because that decoding is
    /// engine-side protocol work and can be tested without a clipboard. A malformed payload arrives as null —
    /// xterm ignores those, and a host that writes whatever it gets would paste garbage.
    /// </summary>
    public event Action<string?>? ClipboardRequested;

    /// <summary>Visible row of the current surface, top to bottom.</summary>
    public ReadOnlySpan<TermCell> Row(int row) => _screen.Lines[row].Cells;

    /// <summary>True when this visible row is the first fragment of a soft-wrapped logical line.</summary>
    public bool RowWrapped(int row) => _screen.Lines[row].Wrapped;

    /// <summary>Scrollback row by age, 0 being the oldest still held.</summary>
    public ReadOnlySpan<TermCell> ScrollbackRow(int index)
        => _scrollback[(_scrollbackHead + index) % _scrollback.Length].Cells;

    /// <summary>True when that scrollback row wrapped into the next one.</summary>
    public bool ScrollbackRowWrapped(int index)
        => _scrollback[(_scrollbackHead + index) % _scrollback.Length].Wrapped;

    /// <summary>
    /// Rows in one address space: scrollback first, then the visible grid. Selection has to span the boundary
    /// without the caller reasoning about which side a row index belongs to, and a drag that starts on
    /// history and ends on the prompt is normal, not an edge case.
    /// </summary>
    public int TotalRows => _scrollbackCount + Rows;

    public ReadOnlySpan<TermCell> AbsoluteRow(int index)
        => index < _scrollbackCount ? ScrollbackRow(index) : Row(index - _scrollbackCount);

    /// <summary>The wrap flag for <see cref="AbsoluteRow" />. Only a fragment's own row answers.</summary>
    public bool AbsoluteRowWrapped(int index)
        => index < _scrollbackCount ? ScrollbackRowWrapped(index) : RowWrapped(index - _scrollbackCount);

    /// <summary>Feeds output bytes. Everything the engine knows about the session arrives here.</summary>
    public void Write(ReadOnlySpan<byte> bytes) => _parser.Feed(bytes);

    /// <summary>Bytes the engine owes the program (DA, DSR, ENLQ answers). Valid until the next write.</summary>
    public ReadOnlyMemory<byte> DrainReplies()
    {
        if (_replyLength == 0)
            return default;
        var reply = _reply.AsMemory(0, _replyLength);
        _replyLength = 0;
        return reply;
    }

    /// <summary>
    /// Reshapes the surface. No re-flow yet: soft-wrapped lines stay split and rows past the new bottom
    /// are dropped rather than re-joined. Both are on the ledger in term/docs/notes.md.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        if (columns == Columns && rows == Rows)
            return;
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        // The alternate screen is a fixed grid — a TUI that painted it will repaint it, and re-wrapping its
        // boxes is how a full-screen editor ends up overlapping itself. xterm does the same.
        _alt.Resize(columns, rows);

        if (!ReflowMain(columns, rows))
            _main.Resize(columns, rows); // a custom scroll region: the truncating path, see the remarks

        _wrapPending = false;
        Damage.MarkAll();
    }

    /// <summary>
    /// Re-wraps the main screen and its history onto the new column count: physical rows are joined back into
    /// logical lines by their wrap flags, then split again at the new width.
    /// </summary>
    /// <remarks>
    /// Without this, shrinking a window *destroys text* — measured before this existed, 40→20 columns lost
    /// "e lazy dog" and "eeps" outright, because each physical row was simply cut to the new width and the
    /// fragment it continued into was never rejoined.
    ///
    /// Refusing to reflow a custom scroll region is deliberate, not unfinished: DECSTBM makes rows mean
    /// something to the program, and silently re-laying them would move content out from under a region the
    /// program still thinks it owns. Those programs resize in full-screen mode anyway.
    /// </remarks>
    private bool ReflowMain(int columns, int rows)
    {
        if (_main.ScrollTop != 0 || _main.ScrollBottom != _main.Rows - 1)
            return false;

        // 1. Every physical row in one list, history first — the same address space the viewport reads.
        var physical = new List<TermLine>(_scrollbackCount + _main.Rows);
        for (var i = 0; i < _scrollbackCount; i++)
            physical.Add(_scrollback[(_scrollbackHead + i) % _scrollback.Length]);
        for (var i = 0; i < _main.Rows; i++)
            physical.Add(_main.Lines[i]);

        if (physical.Count == 0)
            return false;

        var cursorRow = Math.Min(_scrollbackCount + _main.CursorRow, physical.Count - 1);
        var cursorColumn = Math.Min(_main.CursorColumn, physical[cursorRow].Cells.Length - 1);

        // Blank rows under the cursor are screen space, not content: keeping them as lines is what made a
        // 6-row window push its own text into history on the first narrow resize. Everything up to the last
        // row that still carries ink — or the cursor, whichever is lower — is the document; the rest is
        // padding the new grid gets for free.
        var lastUsed = cursorRow;
        for (var row = physical.Count - 1; row > lastUsed; row--)
        {
            if (HasInk(physical[row]))
            {
                lastUsed = row;
                break;
            }
        }

        // 2. Group into logical lines, and remember where the cursor's glyph sits inside its own line.
        var lines = new List<List<TermCell>>();
        var current = new List<TermCell>();
        var cursorLine = 0;
        var cursorOrdinal = 0;
        for (var row = 0; row <= lastUsed; row++)
        {
            var cells = physical[row].Cells;
            var rowWasCursor = row == cursorRow;

            for (var column = 0; column < cells.Length; column++)
            {
                ref readonly var cell = ref cells[column];
                if ((cell.Flags & CellFlags.Tail) != 0)
                    continue; // its head carried the glyph and both columns

                // `current` already holds every head from the rows above, so its count is the cursor's
                // ordinal within this logical line — adding the row's own column on top double-counts it.
                if (rowWasCursor && column == cursorColumn)
                {
                    cursorLine = lines.Count;
                    cursorOrdinal = current.Count;
                }

                current.Add(cell);
            }

            if (physical[row].Wrapped && row < lastUsed)
                continue; // the next physical row is the rest of this logical line

            // Trailing void cells come off before the re-split. A blank row is one logical line, not forty
            // cells: leaving them in turned every empty line into two after a halving, which pushed content
            // up into history on a resize that should have kept it on screen. Colours are not void — a
            // full-width painted line has to stay full width.
            while (current.Count > 0 && IsVoid(current[^1]))
                current.RemoveAt(current.Count - 1);

            lines.Add(current);
            current = new List<TermCell>();
        }

        // 3. Split each logical line again at the new width, and find where the cursor landed while doing it.
        var reflowed = new List<TermLine>(lines.Count + 8);
        var newCursorRow = 0;
        var newCursorColumn = 0;
        var cursorFound = false;

        for (var line = 0; line < lines.Count; line++)
        {
            var heads = lines[line];
            var fragment = new List<TermCell>(columns);
            var used = 0;
            var fragmentStart = 0;

            for (var i = 0; i < heads.Count; i++)
            {
                var cost = (heads[i].Flags & CellFlags.Wide) != 0 ? 2 : 1;
                if (used + cost > columns)
                {
                    reflowed.Add(BuildFragment(fragment, columns, wrapped: true));
                    fragment.Clear();
                    used = 0;
                    fragmentStart = i;
                }

                if (line == cursorLine && i == cursorOrdinal)
                {
                    cursorFound = true;
                    newCursorRow = reflowed.Count;
                    newCursorColumn = used;
                }

                fragment.Add(heads[i]);
                used += cost;
            }

            // The cursor on a line whose cells were all void — the empty line under a prompt — never met a
            // head to be compared against, so it lands at the start of the fragment about to be written.
            if (line == cursorLine && !cursorFound)
            {
                cursorFound = true;
                newCursorRow = reflowed.Count;
                newCursorColumn = ColumnsOf(fragment);
            }

            reflowed.Add(BuildFragment(fragment, columns, wrapped: false));
        }

        // 4. The bottom `rows` fragments stay on screen; everything above them is history again.
        var history = Math.Max(0, reflowed.Count - rows);
        var capacity = Math.Max(0, _scrollback.Length);
        if (capacity > 0)
        {
            var dropped = Math.Max(0, history - capacity);
            var kept = Math.Min(history, capacity);
            for (var i = 0; i < kept; i++)
            {
                var slot = (_scrollbackHead + i) % capacity;
                if (_scrollback[slot] is not null && _scrollback[slot]!.Cells.Length != columns)
                    _pool.Return(_scrollback[slot]!);
                _scrollback[slot] = reflowed[dropped + i];
            }

            _scrollbackHead = 0;
            _scrollbackCount = kept;
            history -= dropped;
        }
        else
        {
            _scrollbackCount = 0;
        }

        _main.Resize(columns, rows);
        for (var i = 0; i < rows; i++)
        {
            var source = history + i;
            if (source < reflowed.Count)
            {
                _pool.Return(_main.Lines[i]);
                _main.Lines[i] = reflowed[source];
            }
        }

        _main.CursorRow = Math.Clamp(newCursorRow - history, 0, rows - 1);
        _main.CursorColumn = Math.Clamp(newCursorColumn, 0, columns - 1);
        _main.SavedRow = Math.Clamp(_main.SavedRow + (newCursorRow - history - _main.CursorRow), 0, rows - 1);
        _main.SavedColumn = Math.Min(_main.SavedColumn, columns - 1);
        return true;
    }

    private TermLine BuildFragment(List<TermCell> heads, int columns, bool wrapped)
    {
        var line = _pool.Rent(columns);
        var at = 0;
        foreach (var cell in heads)
        {
            line.Cells[at] = cell;
            at++;

            // A wide glyph needs its partner column back, and a pair does not straddle a fragment: if the
            // tail would fall off the edge, the head was already moved to the next fragment above.
            if ((cell.Flags & CellFlags.Wide) != 0 && at < columns)
            {
                line.Cells[at] = new TermCell
                {
                    Fg = cell.Fg,
                    Bg = cell.Bg,
                    Effects = cell.Effects,
                    Flags = CellFlags.Tail,
                };
                at++;
            }
        }

        line.Wrapped = wrapped;
        return line;
    }

    private static int ColumnsOf(List<TermCell> heads)
    {
        var used = 0;
        foreach (var cell in heads)
            used += (cell.Flags & CellFlags.Wide) != 0 ? 2 : 1;
        return used;
    }

    /// <summary>True when a cell carries neither a glyph nor a colour — the grid's own floor.</summary>
    private static bool IsVoid(TermCell cell)
        => cell.Rune == 0 && cell.Bg.IsDefault && cell.Fg.IsDefault && cell.Effects == TermEffects.None;

    /// <summary>True when a row carries a glyph or a colour of its own, which is what makes it content.</summary>
    private static bool HasInk(TermLine line)
    {
        foreach (var cell in line.Cells)
        {
            if (!IsVoid(cell))
                return true;
        }

        return false;
    }

    /// <summary>Full reset (RIS): modes, grids, scrollback, title.</summary>
    public void Reset()
    {
        _screen = _main;
        ClearScrollback();
        _main.Reset(Columns, Rows);
        _alt.Reset(Columns, Rows);
        RestoreDefaultModes();
        SetTitle(null);
        Damage.MarkAll();
    }

    private void RestoreDefaultModes()
    {
        _wrapPending = false;
        _glUsesG1 = false;
        InsertMode = false;
        OriginMode = false;
        AutoWrap = true;
        CursorVisible = true;
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        BracketedPaste = false;
        MouseClicks = false;
        MouseDrag = false;
        MouseAnyMove = false;
        MouseSgrEncoding = false;
        MouseAlternateScroll = true;
        CursorStyle = TerminalCursorStyle.BlockSteady;
        _parser.ResetCharsets();
    }

    internal void PutRune(int rune)
    {
        var width = UnicodeWidth.Of(rune);
        if (width == 0)
            return; // marks are folded into the base glyph and dropped: see the ledger

        if (AutoWrap && _wrapPending)
            WrapNow();

        var screen = _screen;
        var row = screen.CursorRow;
        var column = screen.CursorColumn;
        if (column >= screen.Columns)
            return;

        if (width == 2 && column == screen.Columns - 1)
        {
            // No room for the pair: paint the tail half so the background stays continuous, then wrap
            // and write the glyph on the next row — xterm's rule, and the one CJK text obeys.
            screen.Lines[row].Cells[column] = RenditionCell(CellFlags.Tail, rune: 0);
            Damage.Mark(row);
            if (!AutoWrap || screen.Columns < 2)
                return;
            WrapNow();
            row = screen.CursorRow;
            column = screen.CursorColumn;
            width = 2;
        }

        if (InsertMode)
            screen.InsertCells(row, column, width);

        var cells = screen.Lines[row].Cells;
        var pair = width == 2 && column + 1 < screen.Columns;
        cells[column] = RenditionCell(pair ? CellFlags.Wide : CellFlags.None, rune);
        // The wrap decision uses the *unclamped* next column: clamping first is what makes the fifth
        // character on a four-column line overwrite the fourth instead of starting the next row.
        var next = column + width;
        if (pair)
            cells[column + 1] = RenditionCell(CellFlags.Tail, rune: 0);

        _wrapPending = AutoWrap && next >= screen.Columns;
        // A pending wrap then parks the cursor on the last column, as xterm does: the next printable
        // performs the wrap first, so this is a position report that lies by one cell, not an off-grid cursor.
        screen.CursorColumn = Math.Min(next, screen.Columns - 1);
        Damage.Mark(row);
    }

    /// <summary>A cell carrying only the parser's live rendition — used for blanks and wide tails.</summary>
    private TermCell RenditionCell(CellFlags flags, int rune)
        => new()
        {
            Rune = rune,
            Fg = _parser.Fg,
            Bg = _parser.Bg,
            Effects = _parser.Effects,
            Flags = flags,
        };

    private void WrapNow()
    {
        var screen = _screen;
        screen.Lines[screen.CursorRow].Wrapped = true;
        screen.CursorColumn = 0;
        LineFeed();
    }

    /// <summary>Line feed at the cursor, scrolling the region when the cursor sits on its last row.</summary>
    internal void LineFeed()
    {
        var screen = _screen;
        if (screen.CursorRow == screen.ScrollBottom)
            ScrollUp(1);
        else if (screen.CursorRow < screen.Rows - 1)
            screen.CursorRow++;
        Damage.Mark(screen.CursorRow);
    }

    /// <summary>ESC M: move up, scrolling the region down when already on its first row.</summary>
    internal void ReverseIndex()
    {
        var screen = _screen;
        if (screen.CursorRow == screen.ScrollTop)
            ScrollDown(1);
        else if (screen.CursorRow > 0)
            screen.CursorRow--;
        Damage.Mark(screen.CursorRow);
    }

    internal void ScrollUp(int count)
    {
        var screen = _screen;
        for (var i = 0; i < count; i++)
        {
            var evicted = screen.ScrollUp();
            if (ReferenceEquals(screen, _main) && screen.ScrollTop == 0 && !InAltScreen)
                PushScrollback(evicted);
            else
                _pool.Return(evicted);
        }
        Damage.MarkRange(screen.ScrollTop, screen.ScrollBottom);
    }

    internal void ScrollDown(int count)
    {
        var screen = _screen;
        for (var i = 0; i < count; i++)
            _pool.Return(screen.ScrollDown());
        Damage.MarkRange(screen.ScrollTop, screen.ScrollBottom);
    }

    internal void MoveCursorTo(int column, int row)
    {
        var screen = _screen;
        screen.CursorColumn = Math.Clamp(column, 0, screen.Columns - 1);
        screen.CursorRow = Math.Clamp(row, 0, screen.Rows - 1);
        _wrapPending = false;
    }

    /// <summary>Resolves a 1-based CUP row against the scroll region when origin mode is on.</summary>
    internal int ResolveRow(int oneBasedRow)
        => OriginMode ? Math.Clamp(_screen.ScrollTop + oneBasedRow - 1, 0, _screen.Rows - 1)
                      : Math.Clamp(oneBasedRow - 1, 0, _screen.Rows - 1);

    internal int CurrentRowForApps()
        => OriginMode ? _screen.CursorRow - _screen.ScrollTop + 1 : _screen.CursorRow + 1;

    /// <summary>Relative move; the vertical extent is the scroll region under DECOM, the whole screen otherwise.</summary>
    internal void MoveCursorBy(int columns, int rows)
    {
        var screen = _screen;
        var top = OriginMode ? screen.ScrollTop : 0;
        var bottom = OriginMode ? screen.ScrollBottom : screen.Rows - 1;
        MoveCursorTo(screen.CursorColumn + columns, Math.Clamp(screen.CursorRow + rows, top, bottom));
    }

    internal void CursorNextLine(int rows)
    {
        MoveCursorBy(0, rows);
        MoveCursorTo(0, _screen.CursorRow);
    }

    internal void CursorPreviousLine(int rows)
    {
        MoveCursorBy(0, -rows);
        MoveCursorTo(0, _screen.CursorRow);
    }

    internal void NextTabStop(int count)
    {
        var screen = _screen;
        for (var i = 0; i < count; i++)
        {
            var column = screen.CursorColumn + 1;
            while (column < screen.Columns && !screen.TabStops[column])
                column++;
            screen.CursorColumn = Math.Min(column, screen.Columns - 1);
        }

        _wrapPending = false;
    }

    internal void PrevTabStop(int count)
    {
        var screen = _screen;
        for (var i = 0; i < count; i++)
        {
            var column = screen.CursorColumn - 1;
            while (column > 0 && !screen.TabStops[column])
                column--;
            screen.CursorColumn = Math.Max(0, column);
        }

        _wrapPending = false;
    }

    internal void SetTabStop() => _screen.TabStops[Math.Min(_screen.CursorColumn, _screen.Columns - 1)] = true;

    /// <summary>DEC TBC: 0 clears the stop at the cursor, 3 wipes every one (and does not restore defaults).</summary>
    internal void ClearTabStop(int mode)
    {
        var screen = _screen;
        if (mode == 3)
            Array.Clear(screen.TabStops);
        else if (mode == 0)
            screen.TabStops[Math.Min(screen.CursorColumn, screen.Columns - 1)] = false;
    }

    internal void SetApplicationKeypad(bool on) => ApplicationKeypad = on;

    /// <summary>DECSTR: modes and rendition back to their power-on values, screen content and scrollback kept.</summary>
    internal void SoftReset()
    {
        RestoreDefaultModes();
        _main.ScrollTop = 0;
        _main.ScrollBottom = _main.Rows - 1;
        _alt.ScrollTop = 0;
        _alt.ScrollBottom = _alt.Rows - 1;
        MoveCursorTo(0, 0);
        Damage.MarkAll();
    }

    internal void DamageCursorRow() => Damage.Mark(_screen.CursorRow);

    internal void SetScrollRegion(int topZero, int bottomZero)
    {
        var screen = _screen;
        screen.ScrollTop = topZero;
        screen.ScrollBottom = bottomZero;
        MoveCursorTo(0, OriginMode ? topZero : 0);
        Damage.MarkRange(topZero, bottomZero);
    }

    internal void InsertLines(int count)
    {
        var screen = _screen;
        if (!InRange(screen.CursorRow))
            return;
        screen.InsertLines(screen.CursorRow, count);
        _wrapPending = false;
        Damage.MarkRange(screen.CursorRow, screen.ScrollBottom);
    }

    internal void DeleteLines(int count)
    {
        var screen = _screen;
        if (!InRange(screen.CursorRow))
            return;
        screen.DeleteLines(screen.CursorRow, count);
        _wrapPending = false;
        Damage.MarkRange(screen.CursorRow, screen.ScrollBottom);
    }

    internal void EraseInLine(int mode)
    {
        var screen = _screen;
        var row = screen.CursorRow;
        switch (mode)
        {
            case 0:
                screen.EraseCells(row, ColumnForErase());
                break;
            case 1:
                screen.EraseCells(row, 0, screen.CursorColumn + 1);
                break;
            case 2:
                screen.EraseCells(row, 0, screen.Columns);
                break;
            default:
                break; // EL 3 and up: ignored
        }
        _wrapPending = false;
        Damage.Mark(row);
    }

    /// <summary>
    /// EL and ED treat the right margin specially: with a wrap armed the cursor is logically one cell past
    /// the last column, so "erase to end of line" has nothing left to eat there. xterm agrees, and it is
    /// what keeps a console's clear-to-eol honest after a line that filled the width exactly.
    /// </summary>
    private int ColumnForErase()
        => _wrapPending && _screen.CursorColumn == _screen.Columns - 1 ? _screen.Columns : _screen.CursorColumn;

    internal void EraseInDisplay(int mode)
    {
        var screen = _screen;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var row = screen.CursorRow + 1; row < screen.Rows; row++)
                    screen.EraseCells(row, 0);
                break;
            case 1:
                for (var row = 0; row < screen.CursorRow; row++)
                    screen.EraseCells(row, 0);
                EraseInLine(1);
                break;
            case 3:
                ClearScrollback(); // xterm's extension: ED 3 wipes the held history too
                goto case 2;
            case 2:
                for (var row = 0; row < screen.Rows; row++)
                    screen.EraseCells(row, 0);
                break;
            default:
                break; // ED 4..6 and friends: ignored rather than treated as a full erase
        }
        _wrapPending = false;
        Damage.MarkAll();
    }

    internal void EraseChars(int count)
    {
        _screen.EraseCells(_screen.CursorRow, _screen.CursorColumn, count);
        Damage.Mark(_screen.CursorRow);
    }

    internal void InsertChars(int count)
    {
        if (InsertMode)
            count = 0; // xterm ignores IC when IRM is already on
        _screen.InsertCells(_screen.CursorRow, _screen.CursorColumn, count);
        _wrapPending = false;
        Damage.Mark(_screen.CursorRow);
    }

    internal void DeleteChars(int count)
    {
        _screen.DeleteCells(_screen.CursorRow, _screen.CursorColumn, count);
        _wrapPending = false;
        Damage.Mark(_screen.CursorRow);
    }

    internal void SetMode(byte marker, ReadOnlySpan<int> parameters, bool on)
    {
        if (marker == '>')
            return; // xterm 256-colour / COLON-separated presentation requests: remembered nowhere
        foreach (var code in parameters)
        {
            switch (code)
            {
                case 4 when marker != '?': InsertMode = on; break;
                case 12 when marker != '?': CursorVisible = !on; break;
                case 25 when marker == '?': CursorVisible = on; break;
                case 1 when marker == '?': ApplicationCursorKeys = on; break;
                case 6 when marker == '?': SetOriginMode(on); break;
                case 7 when marker == '?': AutoWrap = on; break;
                case 8 when marker == '?': /* DEC autorepeat: harmless to ignore */ break;
                case 2004 when marker == '?': BracketedPaste = on; break;
                case 1000 when marker == '?': MouseClicks = on; break;
                case 1002 when marker == '?': MouseDrag = on; break;
                case 1003 when marker == '?': MouseAnyMove = on; break;
                case 1006 when marker == '?': MouseSgrEncoding = on; break;
                case 1007 when marker == '?': MouseAlternateScroll = on; break;
                case 1049 or 1047 or 47 when marker == '?': SetAltScreen(on); break;
                case 2026 when marker == '?': SynchronizedOutput = on; break;
                case 9041 when marker == '?': /* contour sixel keypress protocol: swallow */ break;
                default: break;
            }
        }
        Damage.Mark(_screen.CursorRow);
    }

    /// <summary>DEC 2026. The renderer may wait for the reset frame; the engine only records it.</summary>
    public bool SynchronizedOutput { get; private set; }

    private void SetOriginMode(bool on)
    {
        OriginMode = on;
        var screen = _screen;
        MoveCursorTo(0, on ? screen.ScrollTop : 0);
    }

    /// <summary>
    /// DEC 47 / 1047 / 1049 all take the same shape here: the main screen's cursor is remembered, the
    /// alternate surface is cleared, and leaving restores both. Splitting them would only re-create the
    /// legacy difference no client depends on; the ledger carries the note.
    /// </summary>
    private void SetAltScreen(bool on)
    {
        if (on == InAltScreen)
            return;

        if (on)
        {
            _altReturnColumn = _main.CursorColumn;
            _altReturnRow = _main.CursorRow;
            _alt.Reset(Columns, Rows);
            _screen = _alt;
        }
        else
        {
            _screen = _main;
            MoveCursorTo(_altReturnColumn, _altReturnRow);
        }

        _wrapPending = false;
        Damage.MarkAll();
    }

    /// <summary>Alignment test (DECALTBTN): the 'E' screen every VT buyer was asked to eyeball.</summary>
    internal void AlignmentTest()
    {
        var screen = _screen;
        for (var row = 0; row < screen.Rows; row++)
        {
            var cells = screen.Lines[row].Cells;
            for (var column = 0; column < cells.Length; column++)
                cells[column] = new TermCell { Rune = 'E' };
        }
        Damage.MarkAll();
    }

    internal void SetTitle(string? title)
    {
        if (string.Equals(title, WindowTitle, StringComparison.Ordinal))
            return;
        WindowTitle = title;
        TitleChanged?.Invoke(title);
    }

    internal void RequestClipboard(string? selection) => ClipboardRequested?.Invoke(selection);

    internal void Reply(ReadOnlySpan<byte> bytes)
    {
        if (_replyLength + bytes.Length > ReplyCapacity)
            return;
        bytes.CopyTo(_reply.AsSpan(_replyLength));
        _replyLength += bytes.Length;
    }

    internal void Reply(string ascii)
    {
        Span<byte> buffer = stackalloc byte[ascii.Length];
        for (var i = 0; i < ascii.Length; i++)
            buffer[i] = (byte)ascii[i];
        Reply(buffer);
    }

    internal void SaveCursor()
    {
        var screen = _screen;
        screen.SavedColumn = screen.CursorColumn;
        screen.SavedRow = screen.CursorRow;
        _parser.SaveRendition(out screen.SavedFg, out screen.SavedBg, out screen.SavedEffects);
    }

    internal void RestoreCursor()
    {
        var screen = _screen;
        MoveCursorTo(screen.SavedColumn, screen.SavedRow);
        _parser.RestoreRendition(screen.SavedFg, screen.SavedBg, screen.SavedEffects);
        Damage.Mark(screen.CursorRow);
    }

    internal void SetCursorStyle(TerminalCursorStyle style) => CursorStyle = style;

    /// <summary>True when the charset in use should map ASCII punctuation into DEC line graphics.</summary>
    internal bool DECCharsetActive => _glUsesG1 ? _parser.G1IsDEC : _parser.G0IsDEC;

    internal void SelectOtherCharset() => _glUsesG1 = true;

    internal void SelectDefaultCharset() => _glUsesG1 = false;

    private bool InRange(int row)
    {
        var screen = _screen;
        return row >= screen.ScrollTop && row <= screen.ScrollBottom;
    }

    private void SetScrollbackLimit(int value)
    {
        value = Math.Max(0, value);
        if (value == _scrollback.Length)
            return;
        if (value == 0)
        {
            ClearScrollback();
            _scrollback = [];
            return;
        }

        var kept = new TermLine[value];
        var copy = Math.Min(value, _scrollbackCount);
        // Newest rows survive a shrink; the oldest are released to the pool.
        var firstToKeep = _scrollbackCount - copy;
        for (var i = 0; i < copy; i++)
            kept[i] = _scrollback[(_scrollbackHead + firstToKeep + i) % _scrollback.Length];
        for (var i = 0; i < _scrollbackCount - copy; i++)
            _pool.Return(_scrollback[(_scrollbackHead + i) % _scrollback.Length]);
        _scrollback = kept;
        _scrollbackHead = 0;
        _scrollbackCount = copy;
    }

    private void PushScrollback(TermLine line)
    {
        if (_scrollback.Length == 0)
        {
            _pool.Return(line);
            return;
        }
        var slot = (_scrollbackHead + _scrollbackCount) % _scrollback.Length;
        if (_scrollbackCount == _scrollback.Length)
        {
            _pool.Return(_scrollback[slot]);
            _scrollbackHead = (_scrollbackHead + 1) % _scrollback.Length;
        }
        else
        {
            _scrollbackCount++;
        }
        _scrollback[slot] = line;
    }

    private void ClearScrollback()
    {
        for (var i = 0; i < _scrollbackCount; i++)
            _pool.Return(_scrollback[(_scrollbackHead + i) % _scrollback.Length]);
        _scrollbackHead = _scrollbackCount = 0;
    }
}
