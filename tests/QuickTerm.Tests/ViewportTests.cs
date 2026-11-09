namespace QuickTerm.Tests;

using QuickTerm.Terminal;

/// <summary>
/// The window-over-rows mapping. It lives in the kernel because it was written twice inside the renderer and
/// wrong both times in ways only a desktop could see: first the wheel moved an offset no slot ever read, then
/// the clamp ran past what the layout could honour.
/// </summary>
public sealed class ViewportTests
{
    [Fact]
    public void Offset_Zero_Shows_The_Bottom_Of_The_Space()
    {
        var view = TermViewport.Map(visibleRows: 30, totalRows: 100, scrollbackRows: 70, cursorRow: 29, scrollOffset: 0);

        Assert.Equal(70, view.TopAbsolute);
        Assert.True(view.AtBottom);
    }

    [Fact]
    public void Scrolling_Up_Moves_The_Window_Over_History()
    {
        // The reading the old layout could never produce: with a window exactly as tall as the grid, pulling
        // the view up still changes which absolute row is on top.
        var view = TermViewport.Map(visibleRows: 30, totalRows: 100, scrollbackRows: 70, cursorRow: 29, scrollOffset: 10);

        Assert.Equal(60, view.TopAbsolute);
        Assert.False(view.AtBottom);
    }

    [Fact]
    public void Offset_Clamps_At_The_Top_Of_The_Buffer()
    {
        var view = TermViewport.Map(visibleRows: 30, totalRows: 100, scrollbackRows: 70, cursorRow: 29, scrollOffset: 999);

        Assert.Equal(TermViewport.MaxOffset(30, 100), view.Offset);
        Assert.Equal(0, view.TopAbsolute);
    }

    [Fact]
    public void Short_Buffer_Sits_At_The_Top_Of_The_Window()
    {
        // A fresh screen in a tall window: the grid is the content, so the empty space belongs under it.
        var view = TermViewport.Map(visibleRows: 40, totalRows: 24, scrollbackRows: 0, cursorRow: 0, scrollOffset: 0);

        Assert.Equal(0, view.TopAbsolute);
        Assert.Equal(0, view.CursorSlot);
        Assert.Equal(0, TermViewport.MaxOffset(40, 24));
    }

    [Fact]
    public void Cursor_Slot_Follows_The_Viewport()
    {
        var view = TermViewport.Map(visibleRows: 30, totalRows: 50, scrollbackRows: 40, cursorRow: 5, scrollOffset: 0);

        Assert.Equal(20, view.TopAbsolute);
        Assert.Equal(25, view.CursorSlot); // 40 history rows put the grid's row 5 at absolute 45
    }

    [Fact]
    public void Rows_Outside_The_Window_Have_No_Slot()
    {
        var view = TermViewport.Map(visibleRows: 30, totalRows: 100, scrollbackRows: 70, cursorRow: 29, scrollOffset: 0);

        Assert.Equal(0, view.SlotOfAbsolute(70));
        Assert.Equal(29, view.SlotOfAbsolute(99));
        Assert.Equal(-1, view.SlotOfAbsolute(69));
        Assert.Equal(-1, view.SlotOfAbsolute(100));
    }

    [Fact]
    public void Scrolled_Back_Reading_Reaches_Old_Lines()
    {
        // End to end on the engine's own rows: 200 lines through a 20-row screen, then read what the viewport
        // says is in the top slot 100 rows up. This is the "内容无法滚动" case, told without a window.
        var term = new Terminal(20, 20, 5000);
        for (var line = 1; line <= 200; line++)
            term.Write(System.Text.Encoding.UTF8.GetBytes($"line {line}\r\n"));

        // 200 lines through a 20-row screen: the last LF lands the cursor on a fresh bottom row, so 181 lines
        // went to history and the grid holds 182..200 plus that blank row.
        Assert.Equal(181, term.ScrollbackCount);
        Assert.Equal(201, term.TotalRows);

        var view = TermViewport.Map(20, term.TotalRows, term.ScrollbackCount, term.CursorRow, 100);
        var top = term.AbsoluteRow(view.TopAbsolute);

        Assert.Equal(81, view.TopAbsolute);
        Assert.Equal("line 82", RowText(top));

        // And the bottom of the space is still the live grid.
        var live = TermViewport.Map(20, term.TotalRows, term.ScrollbackCount, term.CursorRow, 0);
        Assert.Equal(181, live.TopAbsolute);
        Assert.Equal("line 182", RowText(term.AbsoluteRow(live.TopAbsolute)));

        // The whole history is reachable, down to the first line.
        var oldest = TermViewport.Map(20, term.TotalRows, term.ScrollbackCount, term.CursorRow, 9999);
        Assert.Equal(0, oldest.TopAbsolute);
        Assert.Equal("line 1", RowText(term.AbsoluteRow(0)));
    }

    private static string RowText(System.ReadOnlySpan<TermCell> cells)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var cell in cells)
        {
            if ((cell.Flags & CellFlags.Tail) != 0)
                continue;
            builder.Append(cell.Rune == 0 ? ' ' : char.ConvertFromUtf32(cell.Rune));
        }

        return builder.ToString().TrimEnd();
    }
}
