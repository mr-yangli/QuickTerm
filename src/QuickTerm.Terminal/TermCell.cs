namespace QuickTerm.Terminal;

/// <summary>Semantic attributes carried by a cell. Packed into the cell so a repaint can compare runs without a table lookup.</summary>
[Flags]
public enum TermEffects : ushort
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    StrikeThrough = 128,
    DoubleUnderline = 256,
    CurlyUnderline = 512,
}

/// <summary>Per-cell geometry flags.</summary>
[Flags]
public enum CellFlags : byte
{
    None = 0,

    /// <summary>Lead half of a double-width glyph; the next cell is a <see cref="Tail"/>.</summary>
    Wide = 1,

    /// <summary>Trailing half of a double-width glyph; must not be painted or overwritten on its own.</summary>
    Tail = 2,
}

/// <summary>
/// One grid slot. <see cref="Rune"/> 0 means blank, which makes a freshly allocated array already erased —
/// the reason blanks are 0 and not <c>' '</c>. Sized to 16 bytes so a row is one contiguous cache-friendly block.
/// </summary>
public struct TermCell
{
    /// <summary>Codepoint, or 0 for an empty slot.</summary>
    public int Rune;

    /// <summary>Foreground, pre-theme.</summary>
    public TerminalColor Fg;

    /// <summary>Background, pre-theme.</summary>
    public TerminalColor Bg;

    /// <summary>Active semantic attributes.</summary>
    public TermEffects Effects;

    /// <summary>Wide/tail marking for double-width glyphs.</summary>
    public CellFlags Flags;

    /// <inheritdoc />
    public override readonly string ToString()
        => Rune == 0 ? "·" : char.ConvertFromUtf32(Rune);
}
