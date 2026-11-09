namespace QuickTerm.Terminal;

using System;

/// <summary>
/// One frame's worth of "which rows does the window show": the absolute row that goes in the top slot, and
/// the slot the cursor lands in.
/// </summary>
public readonly struct Viewport
{
    public Viewport(int visibleRows, int topAbsolute, int totalRows, int cursorSlot, int offset)
    {
        VisibleRows = visibleRows;
        TopAbsolute = topAbsolute;
        TotalRows = totalRows;
        CursorSlot = cursorSlot;
        Offset = offset;
    }

    /// <summary>Cell slots the window can show.</summary>
    public int VisibleRows { get; }

    /// <summary>Absolute row (history first, then the grid) that goes in slot 0.</summary>
    public int TopAbsolute { get; }

    /// <summary>Rows in the address space: history plus the grid.</summary>
    public int TotalRows { get; }

    /// <summary>Slot the grid's cursor row lands in.</summary>
    public int CursorSlot { get; }

    /// <summary>Rows pulled up from the bottom, after clamping — what the scroll position actually became.</summary>
    public int Offset { get; }

    /// <summary>The window shows the bottom of the buffer when this is 0, which is where the cursor is live.</summary>
    public bool AtBottom => Offset == 0;

    /// <summary>Slot an absolute row lands in, or -1 when this frame does not show it.</summary>
    public int SlotOfAbsolute(int absoluteRow)
    {
        var slot = absoluteRow - TopAbsolute;
        return slot >= 0 && slot < VisibleRows ? slot : -1;
    }
}

/// <summary>
/// Maps a scroll position onto the row space. The window is a viewport over one address space — scrollback
/// rows first, then the grid — so the whole mapping is one number: which absolute row sits in slot 0.
/// </summary>
/// <remarks>
/// This arithmetic was once written twice inside the renderer, and both times it was wrong in a way only a
/// desktop could see: first history could not be reached at all, then the clamp let the offset run past what
/// the layout could honour. It lives here so a book can pin it without a window.
/// </remarks>
public static class TermViewport
{
    /// <summary>How far the view can be pulled up: the rows the window cannot show at the same time.</summary>
    public static int MaxOffset(int visibleRows, int totalRows) => Math.Max(0, totalRows - visibleRows);

    /// <summary>
    /// The viewport for one frame. A buffer shorter than the window is top-aligned, which is what leaves the
    /// empty space under a fresh screen rather than above it.
    /// </summary>
    public static Viewport Map(int visibleRows, int totalRows, int scrollbackRows, int cursorRow, int scrollOffset)
    {
        var offset = Math.Min(Math.Max(0, scrollOffset), MaxOffset(visibleRows, totalRows));
        var topAbsolute = Math.Max(0, totalRows - visibleRows - offset);
        return new Viewport(visibleRows, topAbsolute, totalRows, scrollbackRows + cursorRow - topAbsolute, offset);
    }
}
