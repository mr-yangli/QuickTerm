namespace QuickTerm.Terminal;

/// <summary>
/// Colour in its pre-theme form: either the terminal default, one of the 256 palette slots, or a
/// truecolour value. Kept as packed bits rather than a <c>Color</c> type so the renderer can still
/// honour a user theme (palette slot 1 stays "the theme's red", not a fixed RGB).
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    internal const uint DefaultBits = 0;

    private readonly uint _bits;

    private TerminalColor(uint bits) => _bits = bits;

    /// <summary>The unset colour: the renderer's own foreground/background.</summary>
    public static readonly TerminalColor Default;

    /// <summary>Palette slot 0-255; out-of-range indices collapse to <see cref="Default"/>.</summary>
    public static TerminalColor Palette(int index)
        => (uint)index > 255 ? Default : new TerminalColor(0x0100_0000 | (uint)(index & 0xFF));

    /// <summary>24-bit colour; only the low 24 bits survive.</summary>
    public static TerminalColor Rgb(int rgb) => new(0x0200_0000 | ((uint)rgb & 0x00FF_FFFF));

    /// <summary>0 = default, 1 = palette, 2 = rgb.</summary>
    public readonly int Kind => (int)(_bits >> 24);

    /// <summary>Palette index when <see cref="Kind"/> is 1; 0 otherwise.</summary>
    public readonly int Index => (int)(_bits & 0xFF);

    /// <summary>Packed RGB when <see cref="Kind"/> is 2; 0 otherwise.</summary>
    public readonly int RgbValue => (int)(_bits & 0x00FF_FFFF);

    /// <summary>True when nothing was set and the renderer should use its theme colour.</summary>
    public readonly bool IsDefault => _bits == DefaultBits;

    public readonly bool Equals(TerminalColor other) => _bits == other._bits;

    public override readonly bool Equals(object? obj) => obj is TerminalColor c && Equals(c);

    public override readonly int GetHashCode() => (int)_bits;

    public override readonly string ToString() => Kind switch
    {
        1 => $"palette:{Index}",
        2 => $"#{RgbValue:X6}",
        _ => "default",
    };
}
