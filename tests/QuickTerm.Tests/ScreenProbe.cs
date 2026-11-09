namespace QuickTerm.Tests;

using System.Text;
using QuickTerm.Terminal;

/// <summary>Reading the grid the way a renderer would, so assertions say what a user would see.</summary>
internal static class ScreenProbe
{
    public static void Send(this Terminal term, string text) => term.Write(Encoding.UTF8.GetBytes(text));

    /// <summary>Visible row as text; wide tails skipped so a CJK glyph counts as one character.</summary>
    public static string RowText(this Terminal term, int row)
    {
        var cells = term.Row(row);
        var builder = new StringBuilder(cells.Length);
        for (var i = 0; i < cells.Length; i++)
        {
            ref readonly var cell = ref cells[i];
            if ((cell.Flags & CellFlags.Tail) != 0)
                continue;
            builder.Append(cell.Rune == 0 ? ' ' : char.ConvertFromUtf32(cell.Rune));
        }

        return builder.ToString().TrimEnd();
    }

    public static string ScreenText(this Terminal term)
    {
        var lines = new string[term.Rows];
        for (var i = 0; i < term.Rows; i++)
            lines[i] = term.RowText(i);
        return string.Join('\n', lines).TrimEnd('\n');
    }

    public static TermCell Cell(this Terminal term, int row, int column) => term.Row(row)[column];

    /// <summary>Codepoint at a slot as a <c>char</c> — assertions compare glyphs, not their storage width.</summary>
    public static char RuneAt(this Terminal term, int row, int column) => (char)term.Cell(row, column).Rune;

    public static string ScrollbackText(this Terminal term, int index)
    {
        var cells = term.ScrollbackRow(index);
        var builder = new StringBuilder(cells.Length);
        for (var i = 0; i < cells.Length; i++)
        {
            ref readonly var cell = ref cells[i];
            if ((cell.Flags & CellFlags.Tail) != 0)
                continue;
            builder.Append(cell.Rune == 0 ? ' ' : char.ConvertFromUtf32(cell.Rune));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Text of one row of the address space — history rows first, then the grid.</summary>
    public static string AbsoluteRowText(this Terminal term, int index)
    {
        var cells = term.AbsoluteRow(index);
        var builder = new StringBuilder(cells.Length);
        for (var i = 0; i < cells.Length; i++)
        {
            ref readonly var cell = ref cells[i];
            if ((cell.Flags & CellFlags.Tail) != 0)
                continue;
            builder.Append(cell.Rune == 0 ? ' ' : char.ConvertFromUtf32(cell.Rune));
        }

        return builder.ToString().TrimEnd();
    }
}
