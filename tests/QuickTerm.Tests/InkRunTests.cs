namespace QuickTerm.Tests;

using System.Text;
using QuickTerm.Terminal;

/// <summary>
/// How a row is cut into the pieces a renderer draws in one call. This is the layer that decides where ink
/// sits inside the cell grid, and it is pure index arithmetic — which is exactly the kind of thing that
/// produced a CJK line full of gaps while every engine test stayed green.
/// </summary>
public sealed class InkRunTests
{
    /// <summary>The host's colour lookup, reduced to what the splitter actually looks at: an opaque key.</summary>
    private static uint Key(TermCell cell, int column) => (uint)(cell.Fg.Kind * 256 + cell.Fg.Index);

    private static InkRun[] Runs(Terminal term, int row = 0, int capacity = 64)
    {
        var cells = term.Row(row);
        var output = new InkRun[capacity];
        var count = InkRuns.Build(cells, Key, output);
        return output[..count];
    }

    /// <summary>The characters a host would append for one run — tails and blanks contribute none.</summary>
    private static string Text(Terminal term, InkRun run)
    {
        var cells = term.Row(0);
        var builder = new StringBuilder();
        for (var i = run.Start; i < run.End; i++)
        {
            ref readonly var cell = ref cells[i];
            if (cell.Rune != 0 && (cell.Flags & CellFlags.Tail) == 0)
                builder.Append(char.ConvertFromUtf32(cell.Rune));
        }

        return builder.ToString();
    }

    [Fact]
    public void Plain_Row_Is_One_Run_Over_All_Its_Columns()
    {
        var term = new Terminal(20, 5);
        term.Send("abcdef");

        var runs = Runs(term);
        Assert.Single(runs);
        Assert.Equal(0, runs[0].Start);
        Assert.Equal(6, runs[0].End);
        Assert.Equal(6, runs[0].Columns);
    }

    [Fact]
    public void Typed_Spaces_Stay_Inside_The_Run()
    {
        // A space costs exactly one cell width, the same as its neighbours, so folding it into the run costs
        // nothing and saves a shaping call. The renderer's blank is the *erased* cell (Rune 0), not U+0020.
        var term = new Terminal(20, 5);
        term.Send("ab  cd");

        var runs = Runs(term);
        Assert.Single(runs);
        Assert.Equal((0, 6, 6), (runs[0].Start, runs[0].End, runs[0].Columns));
        Assert.Equal("ab  cd", Text(term, runs[0]));
    }

    [Fact]
    public void Erased_Cells_End_A_Run_And_Count_No_Columns()
    {
        var term = new Terminal(20, 5);
        term.Send("ab\u001b[K"); // EL leaves Rune 0 behind, which is what the renderer reads as blank
        term.Send("\u001b[1;5Hcd");

        var runs = Runs(term);
        Assert.Equal(2, runs.Length);
        Assert.Equal((0, 2, 2), (runs[0].Start, runs[0].End, runs[0].Columns));
        Assert.Equal((4, 6, 2), (runs[1].Start, runs[1].End, runs[1].Columns));
    }

    [Fact]
    public void Wide_Glyph_Owes_Two_Columns_And_Its_Tail_Stays_Inside_The_Run()
    {
        var term = new Terminal(20, 5);
        term.Send("a保b");

        var runs = Runs(term);
        Assert.Single(runs);
        Assert.Equal((0, 4, 4), (runs[0].Start, runs[0].End, runs[0].Columns));
        Assert.Equal("a保b", Text(term, runs[0]));
    }

    [Fact]
    public void Mixed_Widths_Stay_One_Run()
    {
        // The cost decision, on the record: cutting a line at every CJK boundary is five shaping calls where
        // one used to do, measured as 29 ms against 11 ms for a screenful. A mixed run stretches onto its own
        // band instead, so what has to be right is the band — the column count, not the character count.
        var term = new Terminal(20, 5);
        term.Send("ab保cd");

        var runs = Runs(term);
        Assert.Single(runs);
        Assert.Equal("ab保cd", Text(term, runs[0]));
        Assert.Equal(6, runs[0].Columns);
        Assert.Equal(6, runs[0].End);
    }

    [Fact]
    public void Consecutive_Wide_Glyphs_Stay_One_Run()
    {
        // The reading that matters for CJK: a phrase is one drawing call, so the host scales the whole phrase
        // onto its band instead of leaving a gap after every second glyph.
        var term = new Terminal(20, 5);
        term.Send("保留所有权利");

        var runs = Runs(term);
        Assert.Single(runs);
        Assert.Equal(12, runs[0].Columns);
        Assert.Equal(12, runs[0].End); // six heads and six tails
        Assert.Equal("保留所有权利", Text(term, runs[0]));
    }

    [Fact]
    public void Colour_Change_Ends_A_Run()
    {
        var term = new Terminal(20, 5);
        term.Send("a\u001b[31mbc");

        var runs = Runs(term);
        Assert.Equal(2, runs.Length);
        Assert.Equal(1, runs[0].Columns);
        Assert.Equal(2, runs[1].Columns);
        Assert.NotEqual(runs[0].Key, runs[1].Key);
    }

    [Fact]
    public void Tail_With_Its_Head_On_Another_Row_Starts_Nothing()
    {
        // Auto-wrap off, so the wide glyph that does not fit leaves only its tail behind.
        var term = new Terminal(3, 5);
        term.Send("\u001b[?7lab保");

        Assert.Equal(CellFlags.Tail, term.Cell(0, 2).Flags & CellFlags.Tail);

        var runs = Runs(term);
        Assert.Single(runs);
        Assert.Equal(2, runs[0].Columns);
        Assert.Equal(3, runs[0].End);
        Assert.Equal("ab", Text(term, runs[0]));
    }

    [Fact]
    public void Runs_Account_For_Every_Column_They_Touch()
    {
        var term = new Terminal(20, 5);
        term.Send("a保 b 留 c");

        var runs = Runs(term);
        var columns = 0;
        foreach (var run in runs)
            columns += run.Columns;

        // Ten cells, none erased: a, 保(2), space, b, space, 留(2), space, c. A column counted twice, or one
        // that a tail stole from its head, shows up here as a wrong total.
        Assert.Equal(10, columns);

        var lastEnd = 0;
        foreach (var run in runs)
        {
            Assert.True(run.Start >= lastEnd, "runs must not overlap");
            lastEnd = run.End;
        }
    }

    [Fact]
    public void Small_Buffer_Stops_At_Its_Length()
    {
        var term = new Terminal(20, 5);
        term.Send("a\u001b[31mb\u001b[0mc");

        var output = new InkRun[2];
        var count = InkRuns.Build(term.Row(0), Key, output);

        Assert.Equal(2, count);
        Assert.Equal((0, 1, 1), (output[0].Start, output[0].End, output[0].Columns));
        Assert.Equal((1, 2, 1), (output[1].Start, output[1].End, output[1].Columns));
    }

    [Fact]
    public void Trailing_Blanks_Come_Off_The_Run()
    {
        // What cmd leaves behind when it erases typed characters: the line ends in spaces, and a text
        // measurement does not count them. Left in the run, they make the run's reserved span bigger than any
        // width the host can measure, and the scale built from that stretches the ink before them.
        var term = new Terminal(20, 5);
        term.Send("ab   ");

        var runs = Runs(term);
        Assert.Single(runs);
        Assert.Equal((0, 2, 2), (runs[0].Start, runs[0].End, runs[0].Columns));
        Assert.Equal("ab", Text(term, runs[0]));
    }

    [Fact]
    public void Spaces_Between_Ink_Still_Stay_And_Trailing_Ones_Still_Go()
    {
        var term = new Terminal(20, 5);
        term.Send("a  b   ");

        var runs = Runs(term);
        Assert.Single(runs);
        Assert.Equal((0, 4, 4), (runs[0].Start, runs[0].End, runs[0].Columns));
        Assert.Equal("a  b", Text(term, runs[0]));
    }

    [Fact]
    public void A_Run_Of_Nothing_But_Blanks_Produces_Nothing()
    {
        var term = new Terminal(20, 5);
        term.Send("   ");

        Assert.Empty(Runs(term));
    }

    [Fact]
    public void Leading_Blanks_Join_The_Ink_After_Them()
    {
        // Only the trailing ones come off: a measurement does count leading spaces, so the run's width still
        // matches its span and the indent is preserved without drawing anything.
        var term = new Terminal(20, 5);
        term.Send("   x");

        var runs = Runs(term);
        Assert.Single(runs);
        Assert.Equal((0, 4, 4), (runs[0].Start, runs[0].End, runs[0].Columns));
        Assert.Equal("   x", Text(term, runs[0]));
    }

    [Fact]
    public void Empty_Row_Produces_Nothing()
    {
        var term = new Terminal(20, 5);
        Assert.Empty(Runs(term));
    }

    [Fact]
    public void A_Position_Key_Splits_Where_The_Colour_Does_Not()
    {
        // What the column argument is for: a selection changes a cell's ink without changing anything the cell
        // itself says, so the run has to break at the selection's edge rather than paint over it.
        var term = new Terminal(20, 5);
        term.Send("abcdef");

        var output = new InkRun[8];
        var count = InkRuns.Build(term.Row(0), (cell, column) => (uint)(column < 3 ? 0 : 1), output);

        Assert.Equal(2, count);
        Assert.Equal((0, 3, 3), (output[0].Start, output[0].End, output[0].Columns));
        Assert.Equal((3, 6, 3), (output[1].Start, output[1].End, output[1].Columns));
    }
}
