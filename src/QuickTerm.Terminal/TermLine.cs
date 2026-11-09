namespace QuickTerm.Terminal;

/// <summary>
/// A row of cells plus the two facts a renderer needs about the row as a whole: whether it is the
/// first fragment of a wrapped logical line, and whether it has been touched since the last paint.
/// </summary>
internal sealed class TermLine
{
    /// <summary>Exactly <c>columns</c> long; slot 0 is the leftmost cell.</summary>
    public TermCell[] Cells = [];

    /// <summary>Set when this line was split by auto-wrap, so scrollback can rejoin the logical line.</summary>
    public bool Wrapped;

    public void Initialize(int columns)
    {
        if (Cells.Length != columns)
            Cells = new TermCell[columns];
        else
            Array.Clear(Cells);
        Wrapped = false;
    }

    public void CopyFrom(TermLine other, int columns)
    {
        var shared = Math.Min(other.Cells.Length, columns);
        Initialize(columns);
        Array.Copy(other.Cells, Cells, shared);
    }
}
