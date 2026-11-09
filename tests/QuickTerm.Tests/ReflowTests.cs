namespace QuickTerm.Tests;

using QuickTerm.Terminal;

/// <summary>
/// Re-wrapping on resize. Before this existed a narrow window ate text outright: 40→20 columns lost
/// "e lazy dog" and "eeps" from a wrapped sentence, because every physical row was cut to the new width and
/// the fragment it continued into was never rejoined.
/// </summary>
public sealed class ReflowTests
{
    [Fact]
    public void Shrinking_Re_wraps_Without_Losing_Text()
    {
        var term = new Terminal(40, 6, 100);
        term.Send("the quick brown fox jumps over the lazy dog while the cat sleeps\r\nshort\r\n");

        term.Resize(20, 6);

        Assert.Equal("the quick brown fox", term.RowText(0));
        Assert.Equal("jumps over the lazy", term.RowText(1));
        Assert.Equal("dog while the cat sl", term.RowText(2));
        Assert.Equal("eeps", term.RowText(3));
        Assert.Equal("short", term.RowText(4));
        Assert.Equal(string.Empty, term.RowText(5));
    }

    [Fact]
    public void Wrap_Flags_Survive_The_Re_wrap()
    {
        // Selection and copy depend on these: a rejoined logical line is only rejoined if the flags say so.
        var term = new Terminal(40, 6, 100);
        term.Send("the quick brown fox jumps over the lazy dog while the cat sleeps\r\n");

        term.Resize(20, 6);

        Assert.True(term.RowWrapped(0));
        Assert.True(term.RowWrapped(1));
        Assert.True(term.RowWrapped(2));
        Assert.False(term.RowWrapped(3));
    }

    [Fact]
    public void Growing_Joins_The_Fragments_Back()
    {
        var term = new Terminal(20, 6, 100);
        term.Send("the quick brown fox jumps over\r\n");
        Assert.Equal("the quick brown fox", term.RowText(0));
        Assert.Equal("jumps over", term.RowText(1));

        term.Resize(40, 6);

        Assert.Equal("the quick brown fox jumps over", term.RowText(0));
        Assert.False(term.RowWrapped(0));
        Assert.Equal(string.Empty, term.RowText(1));
    }    [Fact]
    public void The_Cursor_Stays_On_Its_Own_Logical_Line()
    {
        var term = new Terminal(40, 6, 100);
        term.Send("the quick brown fox jumps over the lazy dog while the cat sleeps\r\nshort\r\n");
        Assert.Equal(3, term.CursorRow);

        term.Resize(20, 6);

        // The cursor was on the empty line under "short"; after re-wrapping that line is the screen's last row.
        Assert.Equal(5, term.CursorRow);
        Assert.Equal(0, term.CursorColumn);
    }

    [Fact]
    public void A_Cursor_Mid_Sentence_Lands_On_The_Same_Glyph()
    {
        var term = new Terminal(40, 6, 100);
        term.Send("0123456789012345678901234567890123456789");
        term.Send("\u001b[1;25H"); // column 24 of the same row

        term.Resize(20, 6);

        Assert.Equal(1, term.CursorRow);
        Assert.Equal(4, term.CursorColumn); // cell 24 of the logical line is offset 4 in the second fragment
    }

    [Fact]
    public void A_Wide_Glyph_Never_Straddles_A_Fragment_Edge()
    {
        var term = new Terminal(40, 6, 100);
        term.Send("1234567890123456789012345678901234567890\r\n");
        term.Send("aaaaaaaaaaaaaaaaaaaaaaaa中文bbbb\r\n");

        term.Resize(20, 6);

        // Whatever the split looks like, the pair and its tail stay together and the text still reads back.
        var joined = string.Concat(Enumerable.Range(0, term.Rows).Select(row => term.RowText(row)));
        Assert.Contains("中文", joined);
        for (var row = 0; row < term.Rows; row++)
        {
            var cells = term.Row(row);
            if ((cells[0].Flags & CellFlags.Tail) != 0)
                Assert.Fail($"row {row} starts with a tail — its head was left on the row above");
        }
    }

    [Fact]
    public void A_Painted_Line_Keeps_Its_Width()
    {
        // Trailing blanks are trimmed when they are the grid's own floor, but a coloured line is content:
        // dropping its blanks would shrink a full-width status bar to the length of its text.
        // A status bar painted to the edge is content, not blanks: dropping its spaces would shrink it to
        // the length of its text on every resize.
        var term = new Terminal(40, 4, 100);
        term.Send("\u001b[41mred bar" + new string(' ', 33) + "\u001b[0m\r\n");

        term.Resize(20, 4);

        Assert.False(term.Row(0)[19].Bg.IsDefault); // still painted all the way to the new edge
    }

    [Fact]
    public void History_Re_flows_With_The_Screen()
    {
        var term = new Terminal(40, 4, 100);
        term.Send("line one is long enough to wrap across two rows at forty columns here\r\n");
        term.Send("two\r\nthree\r\nfour\r\nfive\r\n");
        Assert.True(term.ScrollbackCount > 0);

        var historyBefore = term.ScrollbackCount;
        term.Resize(20, 4);

        Assert.True(term.ScrollbackCount >= historyBefore, "a narrower grid needs at least as many rows");
        var all = string.Concat(Enumerable.Range(0, term.TotalRows).Select(row => term.AbsoluteRowText(row)));
        Assert.Contains("columns here", all);
    }

    [Fact]
    public void The_Alt_Screen_Is_Not_Re_wrapped()
    {
        var term = new Terminal(40, 6, 100);
        term.Send("\u001b[?1049h");
        term.Send("box drawing stays a box\r\n");

        term.Resize(20, 6);

        Assert.Equal("box drawing stays a", term.RowText(0));
        Assert.True(term.InAltScreen);
    }

    [Fact]
    public void A_Custom_Scroll_Region_Falls_Back_Instead_Of_Re_laying()
    {
        // DECSTBM gives rows meaning to the program. Re-wrapping under it would move content out from under a
        // region the program still believes it owns, so the truncating path stays.
        var term = new Terminal(40, 6, 100);
        term.Send("\u001b[2;5r");
        term.Send("the quick brown fox jumps over the lazy dog while the cat sleeps\r\n");

        term.Resize(20, 6);

        // The truncating path: each physical row cut to the new width, exactly as it was before reflow existed.
        // That is what losing this test would look like — text gone.
        Assert.Equal("the quick brown fox", term.RowText(0));
        Assert.Equal("dog while the cat sl", term.RowText(1));
    }

    [Fact]
    public void Resizing_Wider_Than_The_Content_Keeps_It_At_The_Top()
    {
        var term = new Terminal(20, 6, 100);
        term.Send("short\r\n");

        term.Resize(60, 6);

        Assert.Equal("short", term.RowText(0));
        Assert.Equal(1, term.CursorRow);
        Assert.Equal(0, term.ScrollbackCount);
    }
}
