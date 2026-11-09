namespace QuickTerm.Tests;

using System.Text;
using QuickTerm.Terminal;

/// <summary>
/// Selection and OSC 52: the two halves of "copy what is on screen" that can be proven without a window.
/// The clipboard write itself is the host's job and stays unproven here, same as every other desktop-only
/// reading in this family.
/// </summary>
public sealed class SelectionTests
{
    [Fact]
    public void Cell_Selection_Covers_Exactly_the_Dragged_Cells()
    {
        var term = new Terminal(20, 3);
        term.Send("hello world");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 6), TermSelectMode.Cell);
        selection.Extend(new TermPosition(0, 10));

        Assert.Equal("world", selection.Text(term));
    }

    [Fact]
    public void A_Click_On_One_Cell_Selects_That_Cell()
    {
        var term = new Terminal(20, 3);
        term.Send("abcdef");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 2), TermSelectMode.Cell);

        Assert.Equal("c", selection.Text(term));
    }

    [Fact]
    public void Dragging_Backwards_Resolves_To_Reading_Order()
    {
        var term = new Terminal(20, 3);
        term.Send("hello world");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 10), TermSelectMode.Cell);
        selection.Extend(new TermPosition(0, 6));

        Assert.Equal("world", selection.Text(term));
    }

    [Fact]
    public void Multi_Row_Selection_Ends_With_Exactly_One_Newline()
    {
        var term = new Terminal(20, 3);
        term.Send("first\r\nsecond\r\n");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 0), TermSelectMode.Cell);
        selection.Extend(new TermPosition(1, 5));

        Assert.Equal("first\nsecond", selection.Text(term));
    }

    [Fact]
    public void Trailing_Blanks_Are_Not_Part_of_the_Text()
    {
        var term = new Terminal(20, 3);
        term.Send("abc");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 0), TermSelectMode.Cell);
        selection.Extend(new TermPosition(0, 19));

        Assert.Equal("abc", selection.Text(term));
    }

    [Fact]
    public void Double_Click_Takes_the_Whole_Word()
    {
        var term = new Terminal(40, 3);
        term.Send("cp quicksql.dll /tmp/out");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 4), TermSelectMode.Word); // inside "quicksql.dll"

        Assert.Equal("quicksql.dll", selection.Text(term));
    }

    [Fact]
    public void A_Path_Is_One_Word_So_Slashes_Do_Not_Break_It()
    {
        var term = new Terminal(40, 3);
        term.Send("ls /usr/local/bin");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 5), TermSelectMode.Word);

        Assert.Equal("/usr/local/bin", selection.Text(term));
    }

    [Fact]
    public void Double_Click_On_Whitespace_Selects_One_Cell()
    {
        var term = new Terminal(40, 3);
        term.Send("ab  cd");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 2), TermSelectMode.Word);

        Assert.Equal(" ", selection.Text(term));
    }

    [Fact]
    public void Triple_Click_Takes_the_Logical_Line_Including_Its_Wrapped_Fragment()
    {
        var term = new Terminal(4, 3);
        term.Send("abcdefgh"); // wraps: "abcd" then "efgh"

        Assert.True(term.RowWrapped(0));

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 1), TermSelectMode.Line);

        Assert.Equal("abcdefgh", selection.Text(term));
    }

    [Fact]
    public void Line_Mode_On_the_Second_Fragment_Still_Takes_the_Whole_Line()
    {
        var term = new Terminal(4, 3);
        term.Send("abcdefgh");

        var selection = new TermSelection();
        selection.Start(new TermPosition(1, 0), TermSelectMode.Line);

        Assert.Equal("abcdefgh", selection.Text(term));
    }

    [Fact]
    public void Selection_Spans_Scrollback_Into_the_Visible_Grid()
    {
        var term = new Terminal(10, 2, scrollbackLines: 20);
        term.Send("one\r\ntwo\r\nthree\r\n");

        // The trailing line feed at the bottom scrolls one more row into history: what sits on screen is
        // "three" and the empty prompt row, with "one" and "two" held above them.
        Assert.Equal(2, term.ScrollbackCount);
        Assert.Equal(4, term.TotalRows);
        Assert.Equal("one", term.ScrollbackText(0));
        Assert.Equal("three", term.RowText(0));

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 0), TermSelectMode.Cell); // oldest history row
        selection.Extend(new TermPosition(2, 4)); // "three", the first visible row

        Assert.Equal("one\ntwo\nthree", selection.Text(term));
    }

    [Fact]
    public void A_Selection_Reaching_An_Empty_Row_Ends_With_That_Blank_Line()
    {
        var term = new Terminal(10, 2, scrollbackLines: 20);
        term.Send("one\r\ntwo\r\nthree\r\n");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 0), TermSelectMode.Cell);
        selection.Extend(new TermPosition(3, 4)); // down to the empty prompt row

        // The blank row was selected, so the copy carries it. Dragging to the bottom of a screen really does
        // pick up the empty prompt line — pretending otherwise would make the rule depend on which row the
        // drag happened to stop on.
        Assert.Equal("one\ntwo\nthree\n ", selection.Text(term));
    }

    [Fact]
    public void A_Selection_Inside_An_Empty_Row_Yields_A_Blank_Line()
    {
        var term = new Terminal(10, 2, scrollbackLines: 20);
        term.Send("one\r\ntwo\r\nthree\r\n");

        var selection = new TermSelection();
        selection.Start(new TermPosition(3, 1), TermSelectMode.Cell);
        selection.Extend(new TermPosition(3, 4));

        // One line, and the trailing padding is not part of it — the same rule that keeps a selection of
        // "three" from carrying nine blanks to the end of the row.
        Assert.Equal(" ", selection.Text(term));
    }

    [Fact]
    public void Wide_Glyphs_Come_Out_Once_Not_As_Glyph_Plus_Hole()
    {
        var term = new Terminal(20, 3);
        term.Send("a口b");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 0), TermSelectMode.Cell);
        selection.Extend(new TermPosition(0, 3));

        Assert.Equal("a口b", selection.Text(term));
    }

    [Fact]
    public void Clear_Leaves_Nothing_To_Copy()
    {
        var term = new Terminal(20, 3);
        term.Send("hello");

        var selection = new TermSelection();
        selection.Start(new TermPosition(0, 0), TermSelectMode.Cell);
        selection.Extend(new TermPosition(0, 4));
        Assert.Equal("hello", selection.Text(term));

        selection.Clear();
        Assert.True(selection.IsEmpty);
    }

    [Fact]
    public void Osc_52_Decodes_Base64_Into_the_Clipboard_Text()
    {
        var term = new Terminal(20, 3);
        string? received = null;
        term.ClipboardRequested += text => received = text;

        term.Send("\u001b]52;c;aGVsbG8gd29ybGQ=\u0007"); // "hello world"

        Assert.Equal("hello world", received);
    }

    [Fact]
    public void Osc_52_Utf8_Payloads_Survive_the_Round_Trip()
    {
        var term = new Terminal(20, 3);
        string? received = null;
        term.ClipboardRequested += text => received = text;

        term.Send("\u001b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes("编译 main.c")) + "\u0007");

        Assert.Equal("编译 main.c", received);
    }

    [Fact]
    public void Osc_52_With_a_Non_Base64_Payload_Is_Ignored_Not_Pasted()
    {
        var term = new Terminal(20, 3);
        var requests = 0;
        string? received = "unset";
        term.ClipboardRequested += text =>
        {
            requests++;
            received = text;
        };

        term.Send("\u001b]52;c;not base64 at all!!\u0007");

        Assert.Equal(1, requests); // the request happened; what it carried did not
        Assert.Null(received);
    }

    [Fact]
    public void Osc_52_Selector_Without_a_Payload_Requests_Nothing_Useful()
    {
        var term = new Terminal(20, 3);
        string? received = "unset";
        term.ClipboardRequested += text => received = text;

        term.Send("\u001b]52;c;\u0007");

        Assert.Null(received);
    }
}
