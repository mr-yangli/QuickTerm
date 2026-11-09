namespace QuickTerm.Terminal;

/// <summary>
/// Coarse damage: the span of rows touched since the last paint, or a full repaint flag.
/// Row spans are enough for a terminal because almost every write dirties whole rows, and a
/// per-cell or per-rectangle tracker costs more than it saves on the repaint side.
/// </summary>
public sealed class TerminalDamage
{
    private int _top = int.MaxValue;
    private int _bottom = -1;

    /// <summary>Set when the whole surface must be repainted: resize, screen switch, reset.</summary>
    public bool IsFull { get; private set; }

    public void Mark(int row)
    {
        if (IsFull)
            return;
        if (row < _top)
            _top = row;
        if (row > _bottom)
            _bottom = row;
    }

    /// <summary>Marks a contiguous run at once: scrolling dirties the whole region, not one row.</summary>
    public void MarkRange(int top, int bottom)
    {
        if (IsFull || bottom < top)
            return;
        if (top < _top)
            _top = top;
        if (bottom > _bottom)
            _bottom = bottom;
    }

    public void MarkAll() => IsFull = true;

    public bool HasDamage => IsFull || _bottom >= _top;

    /// <summary>First row the caller should repaint; clamp to 0 when <see cref="IsFull"/>.</summary>
    public int Top => IsFull ? 0 : _top;

    /// <summary>Last row the caller should repaint; clamp to the row count when <see cref="IsFull"/>.</summary>
    public int Bottom => IsFull ? int.MaxValue : _bottom;

    /// <summary>Number of rows the last frame would repaint — the throughput gate counts this.</summary>
    public int RowSpan(int rows)
        => IsFull ? rows : _bottom >= _top ? _bottom - _top + 1 : 0;

    /// <summary>Clears the tracker; the renderer calls it once it has consumed the frame.</summary>
    public void Consume()
    {
        _top = int.MaxValue;
        _bottom = -1;
        IsFull = false;
    }
}
