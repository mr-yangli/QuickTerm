namespace QuickTerm.Terminal;

using System;
using System.Globalization;

/// <summary>
/// The terminal's colour table: 256 slots plus the two defaults a program can repaint with OSC 10 and 11.
/// This is the piece that decides what <c>SGR 31</c> actually looks like, so it belongs to the engine rather
/// than to a renderer — OSC 4/104 rewrite these slots at run time, and a program that sets them has to be
/// seen by whatever draws next, not by whichever control happens to be attached.
/// </summary>
/// <remarks>
/// Slots are stored as packed 24-bit rgb. The built-in table is the one this family has drawn with since
/// W2-c: a sixteen-colour base, xterm's 6×6×6 cube from 16, and its grey ramp from 232 — the cube and ramp
/// are arithmetic every terminal agrees on, the base sixteen is a theme and is what a settings screen will
/// replace.
///
/// Queries (<c>OSC 4;1;?</c>, <c>OSC 10;?</c>) are parsed and ignored: nothing in this engine answers a
/// device-control query yet, and pretending to would be worse than staying quiet.
/// </remarks>
public sealed class TermPalette
{
    /// <summary>Slots in the table: the 16 base colours, the 216-step cube, and the 24-step grey ramp.</summary>
    public const int Slots = 256;

    private readonly uint[] _rgb = new uint[Slots];
    private readonly uint[] _builtIn = new uint[Slots];

    public TermPalette()
    {
        Build();
        Array.Copy(_rgb, _builtIn, Slots);
        Foreground = DefaultForeground;
        Background = DefaultBackground;
    }

    /// <summary>The theme's foreground, which is what an unset cell draws with.</summary>
    public uint Foreground { get; private set; }

    /// <summary>The theme's background: the floor every row is painted on before any cell's own colour.</summary>
    public uint Background { get; private set; }

    /// <summary>What <see cref="Foreground" /> returns before any OSC 10 touched it.</summary>
    public const uint DefaultForeground = 0xD7D7D7;

    /// <summary>What <see cref="Background" /> returns before any OSC 11 touched it.</summary>
    public const uint DefaultBackground = 0x181A1E;

    /// <summary>Packed rgb of one slot. Out-of-range indices read slot 0 rather than throwing: a bad SGR is a display glitch.</summary>
    public uint Rgb(int index) => _rgb[(uint)index < Slots ? index : 0];

    /// <summary>Rewrites one slot.</summary>
    public void Set(int index, uint rgb)
    {
        if ((uint)index < Slots)
            _rgb[index] = rgb & 0x00FF_FFFF;
    }

    /// <summary>Puts one slot back to the built-in table.</summary>
    public void Reset(int index)
    {
        if ((uint)index < Slots)
            _rgb[index] = _builtIn[index];
    }

    public void ResetAll() => Array.Copy(_builtIn, _rgb, Slots);

    public void SetForeground(uint rgb) => Foreground = rgb & 0x00FF_FFFF;

    public void SetBackground(uint rgb) => Background = rgb & 0x00FF_FFFF;

    public void ResetForeground() => Foreground = DefaultForeground;

    public void ResetBackground() => Background = DefaultBackground;

    /// <summary>
    /// Reads one slot back into <paramref name="rgb" />. False for a query (<c>?</c>) and for anything that is
    /// not a colour this table can hold — a program asking for a colour we cannot parse is ignored, as xterm
    /// ignores it.
    /// </summary>
    public bool TryRead(int index, out uint rgb)
    {
        if ((uint)index >= Slots)
        {
            rgb = 0;
            return false;
        }

        rgb = _rgb[index];
        return true;
    }

    /// <summary>
    /// Parses the colour specs a terminal program actually sends: <c>#RRGGBB</c> (with the optional alpha xterm
    /// allows and we discard), <c>rgb:RR/GG/BB</c> where each component may be one or two hex digits and a short
    /// one is scaled up, and the sixteen system names.
    /// </summary>
    public static bool TryParseColor(string? spec, out uint rgb)
    {
        rgb = 0;
        if (string.IsNullOrWhiteSpace(spec))
            return false;

        spec = spec.Trim();
        if (spec.Length > 1 && spec[0] == '#')
            return TryParseHex(spec.AsSpan(1), out rgb);

        if (spec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
            return TryParseRgbTriple(spec.AsSpan(4), out rgb);

        var named = Names;
        for (var i = 0; i < named.Length; i++)
        {
            if (string.Equals(spec, named[i], StringComparison.OrdinalIgnoreCase))
            {
                rgb = BaseRgb[i];
                return true;
            }
        }

        return false;
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, out uint rgb)
    {
        // 6 digits, or 8 with the alpha xterm accepts (and drops — a terminal's colours are opaque).
        if (hex.Length is not (6 or 8))
        {
            rgb = 0;
            return false;
        }

        var value = 0u;
        for (var i = 0; i < 6; i++)
        {
            var digit = Hex(hex[i]);
            if (digit < 0)
            {
                rgb = 0;
                return false;
            }

            value = (value << 4) | (uint)digit;
        }

        rgb = value;
        return true;
    }

    private static bool TryParseRgbTriple(ReadOnlySpan<char> rest, out uint rgb)
    {
        // Exactly three slash-separated components, read by hand: a span split enumerates rather than
        // allocates, and "how many pieces were there" is the check that decides whether this is a colour at all.
        var value = 0u;
        var seen = 0;
        while (true)
        {
            var slash = rest.IndexOf('/');
            var part = slash < 0 ? rest : rest[..slash];
            var component = ReadComponent(part);
            if (component < 0)
            {
                rgb = 0;
                return false;
            }

            value = (value << 8) | (uint)component;
            seen++;
            if (slash < 0)
                break;

            rest = rest[(slash + 1)..];
        }

        if (seen != 3)
        {
            rgb = 0;
            return false;
        }

        rgb = value;
        return true;
    }

    /// <summary>
    /// One component of an <c>rgb:</c> triple. xterm's rule: the digits are a fraction of the widest value of
    /// that width, so <c>f</c> is 255 and <c>ff</c> is also 255 — programs rely on the short form.
    /// </summary>
    private static int ReadComponent(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || text.Length > 4)
            return -1;

        var value = 0;
        foreach (var c in text)
        {
            var digit = Hex(c);
            if (digit < 0)
                return -1;
            value = (value << 4) | digit;
        }

        return text.Length switch
        {
            1 => value * 17, // 0-F scaled across the whole byte, the same rule as #RGB shorthand
            2 => value,
            3 => value >> 4,
            _ => value >> 8,
        };
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static readonly string[] Names =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightblack", "brightred", "brightgreen", "brightyellow", "brightblue", "brightmagenta", "brightcyan", "brightwhite",
    ];

    private static readonly uint[] BaseRgb =
    [
        0x171717, 0xB22222, 0x4E9A06, 0xC4A000, 0x3465A4, 0x7C3FBD, 0x2AA198, 0xD8D8D8,
        0x555555, 0xEF2929, 0x8AE234, 0xFCE94F, 0x729FCF, 0xAD7FA8, 0x34E2E2, 0xEEEEEE,
    ];

    private void Build()
    {
        Array.Copy(BaseRgb, _rgb, 16);

        for (var i = 0; i < 216; i++)
        {
            var r = Level(i / 36);
            var g = Level((i / 6) % 6);
            var b = Level(i % 6);
            _rgb[16 + i] = ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        for (var i = 0; i < 24; i++)
        {
            uint grey = (uint)(8 + i * 10);
            _rgb[232 + i] = (grey << 16) | (grey << 8) | grey;
        }
    }

    private static int Level(int component) => component == 0 ? 0 : 55 + component * 40;

    /// <summary>Slot index a name maps to, or -1. Kept for hosts that show a colour's origin.</summary>
    public static int NamedSlot(string name) => Array.FindIndex(Names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    internal string Describe(int index) => $"#{Rgb(index).ToString("X6", CultureInfo.InvariantCulture)}";
}
