namespace QuickTerm.Terminal;

using System.Globalization;
using System.Text;

/// <summary>
/// Per-byte VT100/XTerm state machine over the output stream. It owns the live rendition and the
/// character-set designation (both are parse-time facts, not screen state) and hands every structural
/// action to <see cref="Terminal"/>.
/// Deliberately byte-at-a-time rather than span-at-a-time: a sequence that starts mid-chunk and ends in
/// the next one is the whole reason this is a state machine, and the W1 throughput gate measures its cost.
/// </summary>
internal sealed class VtParser
{
    private const int MaxParameters = 64;
    private const int MaxStringLength = 4096;

    private enum State : byte
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParameter,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsPassthrough,
        StringAborted,
    }

    private readonly Terminal _term;
    private readonly int[] _parameters = new int[MaxParameters];
    private readonly byte[] _separators = new byte[MaxParameters];
    private readonly byte[] _stringBuffer = new byte[MaxStringLength];

    private State _state;
    private State _suspended = State.Ground;
    private int _parameterCount;
    private int _current;
    private bool _currentGiven;
    private bool _truncated;
    private byte _private;
    private byte _intermediate;
    private int _stringLength;
    private int _utf8Remaining;
    private int _utf8Accumulator;
    private int _utf8Min;
    private bool _g0IsDec;
    private bool _g1IsDec;

    internal TerminalColor Fg;
    internal TerminalColor Bg;
    internal TermEffects Effects;

    public VtParser(Terminal term) => _term = term;

    internal bool G0IsDEC => _g0IsDec;

    internal bool G1IsDEC => _g1IsDec;

    internal void ResetCharsets()
    {
        _g0IsDec = false;
        _g1IsDec = false;
    }

    internal void ResetRendition()
    {
        Fg = TerminalColor.Default;
        Bg = TerminalColor.Default;
        Effects = TermEffects.None;
    }

    internal void SaveRendition(out TerminalColor fg, out TerminalColor bg, out TermEffects effects)
    {
        fg = Fg;
        bg = Bg;
        effects = Effects;
    }

    internal void RestoreRendition(TerminalColor fg, TerminalColor bg, TermEffects effects)
    {
        Fg = fg;
        Bg = bg;
        Effects = effects;
    }

    internal void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            Step(b);
    }

    private void Step(byte b)
    {
        switch (_state)
        {
            case State.Ground: Ground(b); break;
            case State.Escape: Escape(b); break;
            case State.EscapeIntermediate: EscapeIntermediate(b); break;
            case State.CsiEntry: CsiEntry(b); break;
            case State.CsiParameter: CsiParameter(b); break;
            case State.CsiIntermediate: CsiIntermediate(b); break;
            case State.CsiIgnore: CsiIgnore(b); break;
            case State.OscString: OscString(b); break;
            case State.DcsPassthrough: DcsPassthrough(b); break;
            default: StringAborted(b); break;
        }
    }

    // ---------------------------------------------------------------- ground

    private void Ground(byte b)
    {
        if (_utf8Remaining > 0)
        {
            Utf8(b);
            return;
        }

        if (b < 0x20)
        {
            Control(b);
            return;
        }

        if (b < 0x7F)
        {
            Put(b);
            return;
        }

        if (b == 0x7F)
            return;

        if (b is >= 0xC2 and <= 0xDF)
            StartUtf8(b, 1, 0x1F, 0x80);
        else if (b is >= 0xE0 and <= 0xEF)
            StartUtf8(b, 2, 0x0F, 0x800);
        else if (b is >= 0xF0 and <= 0xF4)
            StartUtf8(b, 3, 0x07, 0x10000);
        // 0x80-0xBF cannot lead a sequence and 0xF5+ is out of range: dropped rather than replaced,
        // because a terminal that invents characters hides the bug worth reporting.
    }

    private void StartUtf8(byte lead, int remaining, int mask, int min)
    {
        _utf8Accumulator = lead & mask;
        _utf8Remaining = remaining;
        _utf8Min = min;
    }

    private void Utf8(byte b)
    {
        if ((b & 0xC0) != 0x80)
        {
            // Broken sequence: forget the partial and re-process this byte as a fresh start.
            _utf8Remaining = 0;
            Ground(b);
            return;
        }

        _utf8Accumulator = (_utf8Accumulator << 6) | (b & 0x3F);
        if (--_utf8Remaining != 0)
            return;

        var rune = _utf8Accumulator;
        _utf8Remaining = 0;
        if (rune is >= 0xD800 and <= 0xDFFF)
            return;
        if (rune < _utf8Min || rune > 0x10FFFF)
            return; // overlong or past the scalar limit — not a character. One that slips through
                    // reaches ConvertFromUtf32 in the renderer and the selection, which throw on it.
        Put(rune);
    }

    private void Put(int rune)
    {
        if (rune is >= 0x5F and <= 0x7E && _term.DECCharsetActive)
            rune = DecSpecialGraphics(rune);
        _term.PutRune(rune);
    }

    /// <summary>DEC special graphics: the box-drawing set every legacy TUI asks for with <c>ESC ( 0</c>.</summary>
    private static int DecSpecialGraphics(int r) => r switch
    {
        0x5F => 0x20,   // blank
        0x60 => 0x25C6, // diamond
        0x61 => 0x2592, // checkerboard
        0x62 => 0x2409,
        0x63 => 0x240C,
        0x64 => 0x240D,
        0x65 => 0x240A,
        0x66 => 0x00B0,
        0x67 => 0x00B1,
        0x68 => 0x2424,
        0x69 => 0x240B,
        0x6A => 0x2518,
        0x6B => 0x2510,
        0x6C => 0x250C,
        0x6D => 0x2514,
        0x6E => 0x253C,
        0x6F => 0x23BA,
        0x70 => 0x23BB,
        0x71 => 0x2500,
        0x72 => 0x23BC,
        0x73 => 0x23BD,
        0x74 => 0x251C,
        0x75 => 0x2524,
        0x76 => 0x2534,
        0x77 => 0x252C,
        0x78 => 0x2502,
        0x79 => 0x2264,
        0x7A => 0x2265,
        0x7B => 0x2227,
        0x7C => 0x2260,
        0x7D => 0x00A3,
        0x7E => 0x00B7,
        _ => r,
    };

    private void Control(byte b)
    {
        switch (b)
        {
            case 0x08: _term.MoveCursorBy(-1, 0); break;
            case 0x09: _term.NextTabStop(1); break;
            case 0x0A:
            case 0x0B:
            case 0x0C: _term.LineFeed(); break;
            case 0x0D: _term.MoveCursorTo(0, _term.CursorRow); break;
            case 0x0E: _term.SelectOtherCharset(); break;
            case 0x0F: _term.SelectDefaultCharset(); break;
            case 0x1B: EnterEscape(); break;
            default: break; // BEL, NUL and friends: recognised, acted upon by nobody yet
        }
    }

    /// <summary>
    /// C0 controls arriving inside an incomplete escape sequence. CAN (0x18) and SUB (0x1C) abort it;
    /// the rest are executed and the sequence carries on, exactly as xterm's collector table says.
    /// </summary>
    private void ExecuteInSequence(byte b)
    {
        if (b is 0x18 or 0x1C)
        {
            _state = State.Ground;
            _suspended = State.Ground;
            return;
        }

        if (b != 0x7F)
            Control(b);
    }

    // ---------------------------------------------------------------- escape

    private void EnterEscape()
    {
        _state = State.Escape;
        _intermediate = 0;
        ClearParameters();
    }

    private void BeginCsi()
    {
        _state = State.CsiEntry;
        _private = 0;
        _intermediate = 0;
        ClearParameters();
    }

    private void Escape(byte b)
    {
        if (b < 0x20)
        {
            if (b == 0x1B)
                EnterEscape();
            else
                ExecuteInSequence(b);
            return;
        }

        if (b == 0x7F)
            return;

        switch (b)
        {
            case (byte)'[':
                BeginCsi();
                break;
            case (byte)'(':
            case (byte)')':
            case (byte)'#':
            case (byte)'%':
            case (byte)' ':
                _intermediate = b;
                _state = State.EscapeIntermediate;
                break;
            case (byte)']':
                BeginString(State.OscString);
                break;
            case (byte)'P':
                BeginString(State.DcsPassthrough);
                break;
            case (byte)'X':
            case (byte)'^':
            case (byte)'_':
                // SOS / PM / APC: swallowed to ST and never dispatched.
                _state = State.StringAborted;
                break;
            case (byte)'7':
            case (byte)'8':
            case (byte)'D':
            case (byte)'E':
            case (byte)'M':
            case (byte)'H':
            case (byte)'Z':
            case (byte)'=':
            case (byte)'>':
            case (byte)'\\':
            case (byte)'c':
                EscapeFinal(b);
                if (_state == State.Escape)
                    _state = State.Ground;
                break;
            default:
                _state = State.Ground;
                break; // ESC F/G (Latin-2/6), N/O, V/w: recognised, unused
        }
    }

    private void EscapeFinal(byte b)
    {
        switch (b)
        {
            case (byte)'7': _term.SaveCursor(); break;
            case (byte)'8': _term.RestoreCursor(); break;
            case (byte)'D': _term.LineFeed(); break; // IND
            case (byte)'E': _term.MoveCursorTo(0, _term.CursorRow); _term.LineFeed(); break; // NEL
            case (byte)'M': _term.ReverseIndex(); break;
            case (byte)'H': _term.SetTabStop(); break;
            case (byte)'Z': _term.PrevTabStop(1); break;
            case (byte)'=': _term.SetApplicationKeypad(true); break;
            case (byte)'>': _term.SetApplicationKeypad(false); break;
            case (byte)'\\': EndString(); break; // ST
            case (byte)'c':
                _term.Reset();
                ResetRendition();
                ResetCharsets();
                _state = State.Ground;
                _suspended = State.Ground;
                break;
            default: break;
        }
    }

    private void EscapeIntermediate(byte b)
    {
        if (b < 0x20)
        {
            if (b == 0x1B)
                EnterEscape();
            else
                ExecuteInSequence(b);
            return;
        }

        if (b == 0x7F || b is >= 0x20 and <= 0x2F)
            return; // keep the first intermediate; a second one means a sequence we do not claim

        if (_intermediate == '#' && b == '8')
        {
            _term.AlignmentTest(); // DECALTBTN: the screen full of 'E' every VT buyer was asked to eyeball
            _state = State.Ground;
            return;
        }

        DesignateCharset(b);
        _state = State.Ground;
    }

    /// <summary>Charset designation (SCOSC): only DEC special graphics versus ASCII is remembered.</summary>
    private void DesignateCharset(int final)
    {
        if (_intermediate is not ((byte)'(' or (byte)')'))
            return; // ESC # 8 handled below, ESC % latin-1: nothing to remember

        if (final == '#')
            return;

        var dec = final == '0';
        if (!dec && final is not ('B' or '<' or '>' or 'U'))
            return; // unknown designator: keep the previous choice, xterm agrees

        if (_intermediate == ')')
            _g1IsDec = dec;
        else
            _g0IsDec = dec;
    }

    // ---------------------------------------------------------------- csi

    private void ClearParameters()
    {
        _parameterCount = 0;
        _current = 0;
        _currentGiven = false;
        _truncated = false;
    }

    private void CsiEntry(byte b)
    {
        if (b is (byte)'?' or (byte)'>' or (byte)'<' or (byte)'=')
        {
            _private = b;
            _state = State.CsiParameter;
            return;
        }

        if (b is >= 0x30 and <= 0x39)
        {
            Digit(b);
            _state = State.CsiParameter;
            return;
        }

        if (b is (byte)';' or (byte)':')
        {
            Flush(b);
            _state = State.CsiParameter;
            return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediate = b;
            _state = State.CsiIntermediate;
            return;
        }

        if (b == 0x1B)
        {
            EnterEscape();
            return;
        }

        if (b < 0x20 || b == 0x7F)
        {
            ExecuteInSequence(b);
            return;
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            Dispatch(b);
            return;
        }

        _state = State.CsiIgnore;
    }

    private void CsiParameter(byte b)
    {
        if (b is >= 0x30 and <= 0x39)
        {
            Digit(b);
            return;
        }

        if (b is (byte)';' or (byte)':')
        {
            Flush(b);
            return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediate = b;
            _state = State.CsiIntermediate;
            return;
        }

        if (b == 0x1B)
        {
            EnterEscape();
            return;
        }

        if (b < 0x20 || b == 0x7F)
        {
            ExecuteInSequence(b);
            return;
        }

        if (b is >= 0x3C and <= 0x3F)
        {
            _state = State.CsiIgnore;
            return;
        }

        Dispatch(b);
    }

    private void CsiIntermediate(byte b)
    {
        if (b is >= 0x20 and <= 0x2F)
            return;

        if (b == 0x1B)
        {
            EnterEscape();
            return;
        }

        if (b < 0x20 || b == 0x7F)
        {
            ExecuteInSequence(b);
            return;
        }

        if (b is >= 0x30 and <= 0x3F)
        {
            _state = State.CsiIgnore;
            return;
        }

        Dispatch(b);
    }

    private void CsiIgnore(byte b)
    {
        if (b < 0x20 || b == 0x7F)
        {
            ExecuteInSequence(b);
            return;
        }

        if (b == 0x1B)
            EnterEscape();
        else if (b is >= 0x40 and <= 0x7E)
            _state = State.Ground;
    }

    private void Digit(byte b)
    {
        var digit = b - '0';
        if (_truncated || _current > (9_999_999 - digit) / 10)
        {
            _truncated = true;
            return;
        }

        _current = _current * 10 + digit;
        _currentGiven = true;
    }

    private void Flush(byte separator)
    {
        if (_parameterCount == MaxParameters)
        {
            _current = 0;
            _currentGiven = false;
            _truncated = false;
            return;
        }

        _parameters[_parameterCount] = _currentGiven ? _current : -1; // -1 = absent, resolved per command
        _separators[_parameterCount] = separator;
        _parameterCount++;
        _current = 0;
        _currentGiven = false;
        _truncated = false;
    }

    /// <summary>Pushes the trailing number, and a sentinel for a parameter-less <c>CSI</c> so lookups stay uniform.</summary>
    private void TerminateParameters()
    {
        if (_currentGiven || _parameterCount == 0)
            Flush(separator: 0);
    }

    private int At(int index, int fallback)
        => index >= _parameterCount || _parameters[index] < 0 ? fallback : _parameters[index];

    /// <summary>
    /// True when the parameter at <paramref name="index"/> is a sub-parameter of the one before it. The
    /// separator recorded on a parameter is the delimiter that <em>closed</em> it, so the colon that makes
    /// <c>4:3</c> one command rather than two lives at index - 1.
    /// </summary>
    private bool ColonSeparated(int index) => index > 0 && _separators[index - 1] == ':';

    private int FirstParameter => At(0, 0);

    // ---------------------------------------------------------------- dispatch

    private void Dispatch(int final)
    {
        TerminateParameters();
        switch (final)
        {
            case '@': _term.InsertChars(At(0, 1)); break;
            case 'A': _term.MoveCursorBy(0, -At(0, 1)); break;
            case 'B': _term.MoveCursorBy(0, At(0, 1)); break;
            case 'C': _term.MoveCursorBy(At(0, 1), 0); break;
            case 'D': _term.MoveCursorBy(-At(0, 1), 0); break;
            case 'E': _term.CursorNextLine(At(0, 1)); break;
            case 'F': _term.CursorPreviousLine(At(0, 1)); break;
            case 'G':
            case '`': _term.MoveCursorTo(At(0, 1) - 1, _term.CursorRow); break;
            case 'H':
            case 'f': _term.MoveCursorTo(At(1, 1) - 1, _term.ResolveRow(At(0, 1))); break;
            case 'I': _term.NextTabStop(At(0, 1)); break;
            case 'J': _term.EraseInDisplay(At(0, 0)); break;
            case 'K': _term.EraseInLine(At(0, 0)); break;
            case 'L': _term.InsertLines(At(0, 1)); break;
            case 'M': _term.DeleteLines(At(0, 1)); break;
            case 'P': _term.DeleteChars(At(0, 1)); break;
            case 'S': _term.ScrollUp(At(0, 1)); break;
            case 'T': _term.ScrollDown(At(0, 1)); break;
            case 'X': _term.EraseChars(At(0, 1)); break;
            case 'Z': _term.PrevTabStop(At(0, 1)); break;
            case 'a': _term.MoveCursorBy(At(0, 1), 0); break;
            case 'c': DeviceAttributes(); break;
            case 'd':
            case 'e': _term.MoveCursorTo(_term.CursorColumn, _term.ResolveRow(At(0, 1))); break;
            case 'g': _term.ClearTabStop(At(0, 0)); break;
            case 'h': _term.SetMode(_private, _parameters.AsSpan(0, _parameterCount), true); break;
            case 'l': _term.SetMode(_private, _parameters.AsSpan(0, _parameterCount), false); break;
            case 'm': Sgr(); break;
            case 'n': DeviceStatus(); break;
            case 'p':
                if (_intermediate == '!')
                {
                    _term.SoftReset();
                    ResetRendition();
                }

                break;
            case 'q':
                if (_intermediate == ' ')
                    _term.SetCursorStyle(ToCursorStyle(At(0, 1)));
                break;
            case 'r': SetScrollRegion(); break;
            case 's': if (_private == 0) _term.SaveCursor(); break;
            case 'u': if (_private == 0) _term.RestoreCursor(); break;
            default: break; // 't' window ops, DECRQSS, modifyOtherKeys: recognised, unused
        }

        _state = State.Ground;
        ClearParameters();
    }

    /// <summary>DECSCUSR parameter to shape: 0 is defined as "as if 1", anything else out of range falls back.</summary>
    private static TerminalCursorStyle ToCursorStyle(int request) => request switch
    {
        >= 1 and <= 6 => (TerminalCursorStyle)request,
        0 => TerminalCursorStyle.BlockBlink,
        _ => TerminalCursorStyle.BlockSteady,
    };

    private void SetScrollRegion()
    {
        var top = At(0, 1) - 1;
        var bottom = At(1, _term.Rows) - 1;
        if (top < 0 || bottom >= _term.Rows || top >= bottom)
            return; // a one-line region is not a region: DEC says ignore rather than clamp
        _term.SetScrollRegion(top, bottom);
    }

    private void DeviceAttributes()
    {
        switch (_private)
        {
            case 0: _term.Reply("\x1b[?6c"); break;               // VT102 with advanced video option
            case (byte)'>': _term.Reply("\x1b[>1;114;0c"); break; // xterm 114, the number everything accepts
            default: break;                                       // '<' XTVERSION: not answered
        }
    }

    private void DeviceStatus()
    {
        switch (FirstParameter)
        {
            case 5: _term.Reply("\x1b[0n"); break;
            case 6: _term.Reply($"\x1b[{_term.CurrentRowForApps()};{_term.CursorColumn + 1}R"); break;
            default: break;
        }
    }

    // ---------------------------------------------------------------- sgr

    private void Sgr()
    {
        if (_parameterCount == 0)
        {
            ResetRendition();
            return;
        }

        for (var i = 0; i < _parameterCount; i++)
        {
            var code = _parameters[i] < 0 ? 0 : _parameters[i];
            var sub = i + 1 < _parameterCount && ColonSeparated(i + 1);
            switch (code)
            {
                case 0:
                    ResetRendition();
                    break;
                case 1:
                    Effects |= TermEffects.Bold;
                    i += sub ? 1 : 0; // intensity sub-parameter (1:x) noted, not modelled
                    break;
                case 2:
                    Effects |= TermEffects.Faint;
                    i += sub ? 1 : 0;
                    break;
                case 3:
                    Effects |= TermEffects.Italic;
                    break;
                case 4:
                    Underline(sub ? _parameters[i + 1] : -1);
                    i += sub ? 1 : 0;
                    break;
                case 5:
                case 6:
                    Effects |= TermEffects.Blink;
                    break;
                case 7:
                    Effects |= TermEffects.Inverse;
                    break;
                case 8:
                    Effects |= TermEffects.Hidden;
                    break;
                case 9:
                    Effects |= TermEffects.StrikeThrough;
                    break;
                case 21:
                    Effects |= TermEffects.DoubleUnderline;
                    break;
                case 22:
                    Effects &= ~(TermEffects.Bold | TermEffects.Faint);
                    break;
                case 23:
                    Effects &= ~TermEffects.Italic;
                    break;
                case 24:
                    Effects &= ~(TermEffects.Underline | TermEffects.DoubleUnderline | TermEffects.CurlyUnderline);
                    break;
                case 25:
                    Effects &= ~TermEffects.Blink;
                    break;
                case 27:
                    Effects &= ~TermEffects.Inverse;
                    break;
                case 28:
                    Effects &= ~TermEffects.Hidden;
                    break;
                case 29:
                    Effects &= ~TermEffects.StrikeThrough;
                    break;
                case >= 30 and <= 37:
                    Fg = TerminalColor.Palette(code - 30);
                    break;
                case >= 40 and <= 47:
                    // The normal background range. Its absence was invisible in every screenshot so far
                    // because nothing in this family painted one until the reflow test asked for it.
                    Bg = TerminalColor.Palette(code - 40);
                    break;
                case >= 90 and <= 97:
                    Fg = TerminalColor.Palette(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    Bg = TerminalColor.Palette(code - 100 + 8);
                    break;
                case 38:
                    Fg = ReadColor(ref i);
                    break;
                case 48:
                    Bg = ReadColor(ref i);
                    break;
                case 39:
                    Fg = TerminalColor.Default;
                    break;
                case 49:
                    Bg = TerminalColor.Default;
                    break;
                case 58:
                    ReadColor(ref i); // underline colour parsed and dropped: see the ledger
                    break;
                default:
                    break; // 26/53/59/73/74 and the rest: recognised, unimplemented
            }
        }

        _term.DamageCursorRow();
    }

    private void Underline(int sub)
    {
        Effects &= ~(TermEffects.Underline | TermEffects.DoubleUnderline | TermEffects.CurlyUnderline);
        switch (sub)
        {
            case 0: break;
            case 2: Effects |= TermEffects.DoubleUnderline; break;
            case 3: Effects |= TermEffects.CurlyUnderline; break;
            default: Effects |= TermEffects.Underline; break; // none, single, dotted and dashed collapse
        }
    }

    private TerminalColor ReadColor(ref int index)
    {
        var mode = At(index + 1, -1);
        if (mode == 5)
        {
            var slot = At(index + 2, 0);
            index += 2;
            return TerminalColor.Palette(slot);
        }

        if (mode == 2)
        {
            // "38:2::r:g:b" puts an optional colour-space id between the 2 and the components.
            var offset = ColonSeparated(index + 2) && _parameters[index + 2] < 0 ? 3 : 2;
            var rgb = (At(index + offset, 0) << 16) | (At(index + offset + 1, 0) << 8) | At(index + offset + 2, 0);
            index += offset + 2;
            return TerminalColor.Rgb(rgb);
        }

        index = _parameterCount; // malformed: swallow the tail rather than mis-colour the next cell
        return TerminalColor.Default;
    }

    // ---------------------------------------------------------------- osc / dcs

    private void BeginString(State state)
    {
        _state = state;
        _suspended = state;
        _stringLength = 0;
    }

    private void OscString(byte b)
    {
        if (b == 0x07)
        {
            DispatchOsc();
            _state = State.Ground;
            _suspended = State.Ground;
            return;
        }

        if (b == 0x1B)
        {
            EnterEscape();
            return;
        }

        if (b is 0x18 or 0x1C)
        {
            _state = State.Ground;
            _suspended = State.Ground;
            return;
        }

        if (b is 0x7F or < 0x20)
            return;

        if (_stringLength < MaxStringLength)
            _stringBuffer[_stringLength++] = b;
    }

    private void DcsPassthrough(byte b)
    {
        if (b == 0x1B)
        {
            EnterEscape();
            return;
        }

        if (b is 0x18 or 0x1C)
        {
            _state = State.Ground;
            _suspended = State.Ground;
        }
        // Never dispatched: sixel, DECRQSS and Kitty graphics are swallowed whole. See the ledger.
    }

    private void StringAborted(byte b)
    {
        if (b == 0x1B)
            EnterEscape();
        else if (b is 0x18 or 0x1C)
            _state = State.Ground;
    }

    private void EndString()
    {
        if (_suspended == State.OscString)
            DispatchOsc();
        _state = State.Ground;
        _suspended = State.Ground;
    }

    private void DispatchOsc()
    {
        _stringLength = Math.Min(_stringLength, MaxStringLength);
        var separator = Array.IndexOf(_stringBuffer, (byte)';', 0, _stringLength);
        var designator = separator < 0 ? _stringLength : separator;

        if (designator > 0 && TryParseDesignator(designator, out var code))
        {
            // Without a ';' there is no payload at all — the designator is the whole string. Reading it as a
            // payload is what made `OSC 104` (reset everything) look like `OSC 104;104`.
            var payload = separator < 0 ? string.Empty : DecodeText(separator + 1, _stringLength - separator - 1);
            switch (code)
            {
                case 0:
                case 2:
                    _term.SetTitle(payload);
                    break;
                case 52 when separator >= 0:
                    _term.RequestClipboard(DecodeClipboard(payload));
                    break;
                case 4 or 10 or 11 or 104 or 110 or 111:
                    ApplyPalette(code, payload);
                    break;
            }
        }

        _stringLength = 0;
    }

    /// <summary>
    /// OSC 4 / 10 / 11 / 104 / 110 / 111 — the palette half of OSC. Payloads are semicolon lists; 4 and 104 take
    /// <c>index;spec</c> pairs, and 104 is also legal with bare indices or with nothing at all, which means
    /// "reset the whole table". A spec that cannot be read, or a <c>?</c> query, leaves the table alone: xterm
    /// ignores those too, and half-applying a colour request is how a theme ends up with one wrong slot.
    /// </summary>
    private void ApplyPalette(int code, string payload)
    {
        // An empty payload means "no arguments" — OSC 104 with none is the reset-everything form. Splitting one
        // would hand back a single empty string, and removing empty entries instead would misalign the pairs
        // of a malformed OSC 4.
        var args = payload.Length == 0 ? Array.Empty<string>() : payload.Split(';');
        var changed = false;

        switch (code)
        {
            case 4:
                for (var i = 0; i + 1 < args.Length; i += 2)
                {
                    if (TrySlot(args[i], out var slot) && TermPalette.TryParseColor(args[i + 1], out var rgb))
                    {
                        _term.Palette.Set(slot, rgb);
                        changed = true;
                    }
                }

                break;

            case 10:
                if (args.Length > 0 && TermPalette.TryParseColor(args[0], out var foreground))
                {
                    _term.Palette.SetForeground(foreground);
                    changed = true;
                }

                break;

            case 11:
                if (args.Length > 0 && TermPalette.TryParseColor(args[0], out var background))
                {
                    _term.Palette.SetBackground(background);
                    changed = true;
                }

                break;

            case 104:
                if (args.Length == 0)
                {
                    _term.Palette.ResetAll();
                    changed = true;
                }
                else
                {
                    foreach (var arg in args)
                    {
                        if (!TrySlot(arg, out var reset))
                            continue;

                        _term.Palette.Reset(reset);
                        changed = true;
                    }
                }

                break;

            case 110:
                _term.Palette.ResetForeground();
                changed = true;
                break;

            case 111:
                _term.Palette.ResetBackground();
                changed = true;
                break;
        }

        // Cells still say "slot 1"; only what slot 1 means moved. Without this the screen keeps the colours it
        // drew before the request and the program's repaint is invisible until something else dirties a row.
        if (changed)
            _term.RepaintEverything();
    }

    private static bool TrySlot(string argument, out int slot)
        => int.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) && slot >= 0 && slot < TermPalette.Slots;

    private bool TryParseDesignator(int length, out int code)
    {
        code = 0;
        if (length > 8)
            return false;
        for (var i = 0; i < length; i++)
        {
            var b = _stringBuffer[i];
            if (b < (byte)'0' || b > (byte)'9')
                return false;
            code = code * 10 + (b - '0');
        }

        return true;
    }

    private string DecodeText(int start, int length)
        => length <= 0 ? string.Empty : Encoding.UTF8.GetString(_stringBuffer, start, length);

    /// <summary>
    /// OSC 52's payload is <c>Pc;Pd</c> where <c>Pc</c> is the clipboard selector and <c>Pd</c> base64. The
    /// selector is dropped here — a terminal has one clipboard, and xterm's distinction between cut buffer and
    /// selection is not something we can honour on Windows anyway — and a payload that is not valid base64
    /// becomes null, which is what xterm ignores. Decoding lives in the engine because it is protocol, not
    /// presentation, and only here can it be tested without a clipboard.
    /// </summary>
    private static string? DecodeClipboard(string payload)
    {
        var separator = payload.IndexOf(';');
        var encoded = separator < 0 ? payload : payload[(separator + 1)..];
        if (encoded.Length == 0)
            return null;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return null; // not base64: ignore the request rather than paste its literal text
        }
    }
}
