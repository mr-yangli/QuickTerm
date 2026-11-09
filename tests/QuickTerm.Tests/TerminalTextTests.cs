namespace QuickTerm.Tests;

using QuickTerm.Terminal;

/// <summary>Text, cursor and wrap: the baseline every escape sequence sits on.</summary>
public sealed class TerminalTextTests
{
    [Fact]
    public void Plain_Text_Lands_In_Order()
    {
        var term = new Terminal(20, 5);
        term.Send("abc");

        Assert.Equal("abc", term.RowText(0));
        Assert.Equal(3, term.CursorColumn);
        Assert.Equal(0, term.CursorRow);
    }

    [Fact]
    public void Utf8_Glyph_Split_Across_Two_Writes_Still_Decodes()
    {
        var term = new Terminal(20, 5);
        term.Write([0xE6, 0xB1]); // 汉, cut mid-sequence
        term.Write([0x89]);

        Assert.Equal("汉", term.RowText(0));
        Assert.Equal(2, term.CursorColumn); // the pair is two cells, and the cursor steps past both
    }

    [Fact]
    public void Broken_Utf8_Resumes_At_The_Next_Lead_Byte()
    {
        var term = new Terminal(20, 5);
        term.Write([(byte)'a', 0xE6, (byte)'b', (byte)'c']); // 0xE6 wants continuations and gets 'b'

        Assert.Equal("abc", term.RowText(0)); // the orphan lead is dropped, not turned into a glyph
    }

    [Fact]
    public void Out_Of_Range_Sequence_Is_Dropped_Not_Stored()
    {
        var term = new Terminal(20, 5);
        term.Write([0xF4, 0x90, 0x80, 0x80, (byte)'A']); // decodes to U+110000, one past the scalar limit

        Assert.Equal("A", term.RowText(0)); // the bad rune never reaches the grid
        Assert.Equal(1, term.CursorColumn);

        // Why the drop exists: every consumer spells cells with ConvertFromUtf32, which throws past the
        // limit — before this gate, selection copy was the crash a remote peer could trigger at will.
        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 0), TermSelectMode.Cell);
        selection.Extend(new TermPosition(0, 2));
        Assert.Equal("A", selection.Text(term));
    }

    [Fact]
    public void Overlong_Encodings_Are_Dropped()
    {
        var term = new Terminal(20, 5);
        term.Write([0xE0, 0x80, 0x80, (byte)'B']); // three-byte spelling of U+0000
        term.Write([0xC0, 0x80, (byte)'C']);       // two-byte spelling — 0xC0 cannot even lead

        Assert.Equal("BC", term.RowText(0));
    }

    [Fact]
    public void Carriage_Return_Line_Feed_And_Vertical_Tab_All_Move()
    {
        var term = new Terminal(20, 5);
        term.Send("ab\r\ncd\x0b");

        Assert.Equal("ab", term.RowText(0));
        Assert.Equal("cd", term.RowText(1));
        Assert.Equal(2, term.CursorRow);
    }

    [Fact]
    public void Backspace_Overwrites_The_Same_Cell()
    {
        var term = new Terminal(20, 5);
        term.Send("ab\bX");
        Assert.Equal("aX", term.RowText(0));
    }

    [Fact]
    public void Tab_Lands_On_The_Default_Eight_Column_Stops()
    {
        var term = new Terminal(40, 5);
        term.Send("a\tb\t\tc");

        Assert.Equal('a', term.Cell(0, 0).Rune);
        Assert.Equal('b', term.Cell(0, 8).Rune);
        Assert.Equal('c', term.Cell(0, 24).Rune);
        Assert.Equal(25, term.CursorColumn);
    }

    [Fact]
    public void Set_Tab_Stop_And_Clear_All_Stops()
    {
        var term = new Terminal(40, 5);
        term.Send("\u001b[3g");   // TBC 3: every stop gone
        term.Send("\tX");       // with no stops left the cursor parks on the last column

        Assert.Equal('X', term.Cell(0, 39).Rune);

        term.Send("\u001b[3g\u001b[20G\u001bH\u001b[5G\tY"); // set a stop at 19, step back to 4, tab
        Assert.Equal('Y', term.Cell(0, 19).Rune);
    }

    [Fact]
    public void Exact_Fill_Parks_The_Cursor_On_The_Last_Column()
    {
        var term = new Terminal(4, 3);
        term.Send("abcd");

        Assert.Equal("abcd", term.RowText(0)); // the fourth cell is still under the cursor
        Assert.Equal(3, term.CursorColumn);
    }

    [Fact]
    public void Pending_Wrap_Fires_Only_On_The_Next_Printable()
    {
        var term = new Terminal(4, 3);
        term.Send("abcde");

        Assert.Equal("abcd", term.RowText(0));
        Assert.Equal("e", term.RowText(1));
        Assert.Equal(1, term.CursorColumn);
        Assert.True(term.RowWrapped(0));
    }

    [Fact]
    public void Cursor_Movement_Clears_A_Pending_Wrap()
    {
        var term = new Terminal(4, 3);
        term.Send("abcd"); // wrap is armed here
        term.Send("\u001b[1;1Hx");

        Assert.Equal("xbcd", term.RowText(0));
        Assert.Equal(string.Empty, term.RowText(1));
    }

    [Fact]
    public void Auto_Wrap_Off_Overwrites_The_Last_Column()
    {
        var term = new Terminal(4, 3);
        term.Send("\u001b[?7l");
        term.Send("abcdefg");

        Assert.Equal("abcg", term.RowText(0)); // DECAWM off: the cursor holds at the margin
        Assert.Equal(string.Empty, term.RowText(1));
        Assert.False(term.AutoWrap);
    }

    [Theory]
    [InlineData("\u001b[3;5H", 4, 2)]
    [InlineData("\u001b[3;5f", 4, 2)]
    [InlineData("\u001b[H", 0, 0)]
    [InlineData("\u001b[;5H", 4, 0)]
    [InlineData("\u001b[9999;9999H", 7, 4)] // clamped to the last cell, not wrapped around
    public void Cursor_Addressing_Accepts_The_Variants(string sequence, int column, int row)
    {
        var term = new Terminal(8, 5);
        term.Send(sequence);
        Assert.Equal((column, row), (term.CursorColumn, term.CursorRow));
    }

    [Theory]
    [InlineData("\u001b[2C", 2)]
    [InlineData("\u001b[2D", 0)] // clamped at the left margin
    [InlineData("\u001b[C", 1)]
    public void Relative_Column_Move_Clamps(string sequence, int expected)
    {
        var term = new Terminal(10, 4);
        term.Send(sequence);
        Assert.Equal(expected, term.CursorColumn);
    }

    [Fact]
    public void Absolute_Row_And_Column_Sequences()
    {
        var term = new Terminal(10, 8);
        term.Send("\u001b[4d"); // VPA: row only
        Assert.Equal(3, term.CursorRow);
        term.Send("\u001b[7e"); // VPA spelled the other way
        Assert.Equal(6, term.CursorRow);
        term.Send("\u001b[3G"); // CHA: column only
        Assert.Equal(2, term.CursorColumn);
        term.Send("\u001b[9`"); // HPA
        Assert.Equal(8, term.CursorColumn);
    }

    [Fact]
    public void Next_And_Previous_Line_Home_The_Column()
    {
        var term = new Terminal(10, 6);
        term.Send("abc");
        term.Send("\u001b[2E"); // CNL
        Assert.Equal((0, 2), (term.CursorColumn, term.CursorRow));
        term.Send("x");
        Assert.Equal("x", term.RowText(2));

        term.Send("\u001b[3F"); // CPL, clamped at the top
        Assert.Equal((0, 0), (term.CursorColumn, term.CursorRow));
    }

    [Fact]
    public void Index_And_Reverse_Index_Around_The_Region_Edges()
    {
        var term = new Terminal(10, 3);
        term.Send("A\u001bM"); // RI at the region top scrolls down instead of moving the cursor

        Assert.Equal("A", term.RowText(1));
        Assert.Equal(0, term.CursorRow);
        Assert.Equal(0, term.ScrollbackCount);

        term.Send("\u001bE\u001bD"); // NEL then IND
        Assert.Equal(2, term.CursorRow);
    }

    [Fact]
    public void Scrolling_At_The_Bottom_Pushes_History()
    {
        var term = new Terminal(10, 3);
        term.Send("one\r\ntwo\r\nthree\r\nfour");

        Assert.Equal("two", term.RowText(0));
        Assert.Equal("four", term.RowText(2));
        Assert.Equal(1, term.ScrollbackCount);
        Assert.Equal("one", term.ScrollbackText(0));
    }

    [Fact]
    public void Cjk_At_The_Edge_Keeps_The_Pair_Together()
    {
        var term = new Terminal(4, 3);
        term.Send("abc汉"); // no room for the pair in the last column

        Assert.Equal("abc", term.RowText(0));
        Assert.Equal("汉", term.RowText(1));
        Assert.Equal(CellFlags.Tail, term.Cell(0, 3).Flags); // the margin cell keeps the background
    }

    [Fact]
    public void Combining_Mark_Does_Not_Eat_A_Column()
    {
        var term = new Terminal(10, 2);
        term.Write([0x65, 0xCC, 0x81]); // e + combining acute

        Assert.Equal("e", term.RowText(0));
        Assert.Equal(1, term.CursorColumn);
    }

    [Fact]
    public void Bel_Nul_And_Del_Reach_Nowhere()
    {
        var term = new Terminal(10, 2);
        term.Write([0x07, 0x00, 0x7F, (byte)'X']);

        Assert.Equal("X", term.RowText(0));
    }

    [Fact]
    public void Damage_Spans_Only_The_Rows_Touched()
    {
        var term = new Terminal(20, 8);
        term.Send("one\r\ntwo");

        Assert.Equal(2, term.Damage.RowSpan(term.Rows)); // rows 0 and 1, nothing else
        term.Damage.Consume();

        term.Send("\u001b[4;1Hx");
        Assert.Equal(1, term.Damage.RowSpan(term.Rows));
        Assert.Equal(3, term.Damage.Top);
    }

    [Fact]
    public void Resize_Keeps_Content_And_Demands_A_Full_Repaint()
    {
        var term = new Terminal(20, 4);
        term.Send("0123456789");
        term.Resize(6, 4);

        Assert.Equal("012345", term.RowText(0));
        Assert.True(term.Damage.IsFull);
    }
}
