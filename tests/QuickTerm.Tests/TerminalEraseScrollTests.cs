namespace QuickTerm.Tests;

using QuickTerm.Terminal;

/// <summary>Erase, insert/delete and the scroll region: the commands a TUI leans on every frame.</summary>
public sealed class TerminalEraseScrollTests
{
    private static Terminal Filled(int columns = 10, int rows = 5)
    {
        var term = new Terminal(columns, rows);
        for (var i = 0; i < rows; i++)
            term.Send($"\u001b[{i + 1};1HROW{i}"); // one label per row, addressed before it is written
        term.Send("\u001b[H");
        term.Damage.Consume();
        return term;
    }

    [Fact]
    public void Erase_In_Line_To_End_Of_Line()
    {
        var term = new Terminal(10, 3);
        term.Send("abcdefghij");
        term.Send("\u001b[1;4H\u001b[K");

        Assert.Equal("abc", term.RowText(0));
        Assert.Equal((3, 0), (term.CursorColumn, term.CursorRow)); // EL never moves the cursor
    }

    [Fact]
    public void Erase_In_Line_To_Start_Of_Line_Includes_The_Cell_Under_The_Cursor()
    {
        var term = new Terminal(10, 3);
        term.Send("abcdefghij");
        term.Send("\u001b[1;4H\u001b[1K");

        Assert.Equal("    efghij", term.RowText(0));
    }

    [Fact]
    public void Erase_In_Line_Two_Takes_The_Whole_Row()
    {
        var term = new Terminal(10, 3);
        term.Send("abcdefghij");
        term.Send("\u001b[1;5H\u001b[2K");

        Assert.Equal(string.Empty, term.RowText(0));
        Assert.Equal((4, 0), (term.CursorColumn, term.CursorRow));
    }

    [Fact]
    public void Erase_In_Display_Below_The_Cursor()
    {
        var term = Filled();
        term.Send("\u001b[3;1H\u001b[0J"); // ED 0 also eats the rest of the cursor row

        Assert.Equal("ROW0", term.RowText(0));
        Assert.Equal("ROW1", term.RowText(1));
        Assert.Equal(string.Empty, term.RowText(2));
        Assert.Equal(string.Empty, term.RowText(4));
    }

    [Fact]
    public void Erase_In_Display_Above_The_Cursor()
    {
        var term = Filled();
        term.Send("\u001b[5;1H\u001b[1J");

        Assert.Equal(string.Empty, term.RowText(0));
        Assert.Equal(string.Empty, term.RowText(3));
        Assert.Equal(" OW4", term.RowText(4)); // ED 1 takes the cell under the cursor as well
    }

    [Fact]
    public void Erase_In_Display_Two_Clears_Everything()
    {
        var term = Filled();
        term.Send("\u001b[2J");
        Assert.Equal(string.Empty, term.ScreenText());
    }

    [Fact]
    public void Erase_In_Display_Three_Also_Drops_History()
    {
        var term = new Terminal(10, 2);
        term.Send("a\r\nb\r\nc");
        Assert.Equal(1, term.ScrollbackCount);

        term.Send("\u001b[3J");
        Assert.Equal(0, term.ScrollbackCount);
    }

    [Fact]
    public void Unknown_Erase_Modes_Are_Ignored_Not_Treated_As_Full()
    {
        var term = Filled();
        term.Send("\u001b[5J");
        Assert.NotEqual(string.Empty, term.ScreenText());
    }

    [Fact]
    public void Insert_Characters_Blanks_At_The_Cursor()
    {
        var term = new Terminal(10, 2);
        term.Send("abcdef");
        term.Send("\u001b[1;3H\u001b[2@");
        Assert.Equal("ab  cdef", term.RowText(0));
    }

    [Fact]
    public void Delete_Characters_Shifts_Left_Without_Moving_The_Cursor()
    {
        var term = new Terminal(10, 2);
        term.Send("abcdef");
        term.Send("\u001b[1;3H\u001b[P");
        Assert.Equal("abdef", term.RowText(0));
        Assert.Equal(2, term.CursorColumn);
    }

    [Fact]
    public void Erase_Characters_Blanks_Without_Shifting()
    {
        var term = new Terminal(10, 2);
        term.Send("abcdef");
        term.Send("\u001b[1;3H\u001b[2X");
        Assert.Equal("ab  ef", term.RowText(0));
    }

    [Fact]
    public void Insert_Mode_Prints_By_Shifting()
    {
        var term = new Terminal(10, 2);
        term.Send("abcdef");
        term.Send("\u001b[4h\u001b[1;1HX");

        Assert.Equal("Xabcdef", term.RowText(0));
        Assert.True(term.InsertMode);
    }

    [Fact]
    public void Insert_Line_Shifts_The_Region_Down()
    {
        var term = Filled(10, 5);
        term.Send("\u001b[2;4r"); // DECSTBM: region is 0-based rows 1..3
        term.Send("\u001b[3;1H\u001b[L");

        Assert.Equal("ROW0", term.RowText(0)); // outside the region: untouched
        Assert.Equal("ROW1", term.RowText(1));
        Assert.Equal(string.Empty, term.RowText(2));
        Assert.Equal("ROW2", term.RowText(3));
        Assert.Equal("ROW4", term.RowText(4));
    }

    [Fact]
    public void Delete_Line_Shifts_The_Region_Up()
    {
        var term = Filled(10, 5);
        term.Send("\u001b[2;4r");
        term.Send("\u001b[3;1H\u001b[M");

        Assert.Equal("ROW0", term.RowText(0));
        Assert.Equal("ROW1", term.RowText(1));
        Assert.Equal("ROW3", term.RowText(2));
        Assert.Equal(string.Empty, term.RowText(3));
        Assert.Equal("ROW4", term.RowText(4));
    }

    [Fact]
    public void Insert_And_Delete_Lines_Outside_The_Region_Do_Nothing()
    {
        var term = Filled(10, 5);
        term.Send("\u001b[2;4r");
        term.Send("\u001b[1;1H\u001b[L"); // row 0 sits above the region
        Assert.Equal("ROW0", term.RowText(0));
        Assert.Equal("ROW1", term.RowText(1));
    }

    [Fact]
    public void Scroll_Up_And_Down_Operate_On_The_Region()
    {
        var term = Filled(10, 5);
        term.Send("\u001b[2;4r");
        term.Send("\u001b[S"); // SU 1

        Assert.Equal("ROW0", term.RowText(0));
        Assert.Equal("ROW2", term.RowText(1));
        Assert.Equal("ROW3", term.RowText(2));
        Assert.Equal(string.Empty, term.RowText(3));
        Assert.Equal("ROW4", term.RowText(4));

        term.Send("\u001b[T"); // DI 1
        Assert.Equal(string.Empty, term.RowText(1));
        Assert.Equal("ROW2", term.RowText(2));
        Assert.Equal("ROW3", term.RowText(3));
    }

    [Fact]
    public void Region_Changes_Home_The_Cursor_And_Csi_R_Defaults_Back()
    {
        var term = new Terminal(10, 6);
        term.Send("\u001b[4;4Hx");
        term.Send("\u001b[2;5r");

        Assert.Equal((0, 0), (term.CursorColumn, term.CursorRow));

        term.Send("\u001b[r"); // reset to the whole surface
        term.Send("\u001b[6;1H");
        Assert.Equal(5, term.CursorRow);
    }

    [Fact]
    public void Degenerate_Scroll_Regions_Are_Ignored()
    {
        var term = new Terminal(10, 6);
        term.Send("\u001b[4;4r"); // top >= bottom is not a region
        term.Send("\u001b[6;1H");
        Assert.Equal(5, term.CursorRow);

        term.Send("\u001b[1;99r"); // bottom past the last row
        term.Send("\u001b[6;1H");
        Assert.Equal(5, term.CursorRow);
    }

    [Fact]
    public void Origin_Mode_Addresses_Relative_To_The_Region()
    {
        var term = new Terminal(10, 6);
        term.Send("x");
        term.Send("\u001b[3;5r\u001b[?6h");

        Assert.True(term.OriginMode);
        term.Send("\u001b[1;1Hy"); // row 1 of the region is absolute row 2
        Assert.Equal('y', term.Cell(2, 0).Rune);

        term.Send("\u001b[?6l\u001b[1;1Hz"); // leaving DECOM re-homes to the surface
        Assert.Equal('z', term.Cell(0, 0).Rune);
    }

    [Fact]
    public void Cursor_Relative_Movement_Stays_In_The_Region_Under_Origin_Mode()
    {
        var term = new Terminal(10, 6);
        term.Send("\u001b[3;5r\u001b[?6h");
        term.Send("\u001b[A\u001b[A\u001b[A");
        Assert.Equal(2, term.CursorRow); // pinned to ScrollTop

        term.Send("\u001b[B\u001b[B\u001b[B\u001b[B\u001b[B");
        Assert.Equal(4, term.CursorRow); // pinned to ScrollBottom
    }

    [Fact]
    public void Scrollback_Ring_Drops_The_Oldest_And_Shrinks_From_The_Top()
    {
        var term = new Terminal(10, 2, scrollbackLines: 2);
        term.Send("a\r\nb\r\nc\r\nd"); // three rows leave the surface, history holds two

        Assert.Equal("c", term.RowText(0));
        Assert.Equal("d", term.RowText(1));
        Assert.Equal(2, term.ScrollbackCount);
        Assert.Equal("a", term.ScrollbackText(0)); // 'a' is still the oldest held
        Assert.Equal("b", term.ScrollbackText(1));

        term.ScrollbackLimit = 1;
        Assert.Equal(1, term.ScrollbackCount);
        Assert.Equal("b", term.ScrollbackText(0)); // the newest survives a shrink
    }

    [Fact]
    public void Zero_History_Keeps_The_Top_Row_Off_The_Screen()
    {
        var term = new Terminal(10, 2, scrollbackLines: 0);
        term.Send("a\r\nb\r\nc");

        Assert.Equal(0, term.ScrollbackCount);
        Assert.Equal("b", term.RowText(0));
        Assert.Equal("c", term.RowText(1));
    }

    [Fact]
    public void Alt_Screen_Holds_Its_Own_World()
    {
        var term = new Terminal(10, 4);
        term.Send("main");
        term.Send("\u001b[3;4H");
        term.Damage.Consume();

        term.Send("\u001b[?1049h");
        Assert.True(term.InAltScreen);
        Assert.Equal(string.Empty, term.ScreenText());
        Assert.True(term.Damage.IsFull);

        term.Send("alt");
        term.Send("\u001b[?1049l");

        Assert.False(term.InAltScreen);
        Assert.Equal("main", term.RowText(0));
        Assert.Equal((3, 2), (term.CursorColumn, term.CursorRow)); // the position it left is the one it returns to
    }

    [Fact]
    public void Alt_Screen_Scrolling_Leaves_No_History()
    {
        var term = new Terminal(10, 2);
        term.Send("\u001b[?1049h");
        term.Send("a\r\nb\r\nc");
        Assert.Equal(0, term.ScrollbackCount);
        term.Send("\u001b[?1049l");
        Assert.Equal(0, term.ScrollbackCount);
    }
}
