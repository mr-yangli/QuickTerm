namespace QuickTerm.Terminal;

/// <summary>Non-printable keys the encoder can name. Printable input arrives via <see cref="TermInput.Char"/> or <see cref="TermInput.Text"/>.</summary>
public enum TermKey
{
    None = 0,
    Enter,
    Tab,
    BackTab,
    Backspace,
    Escape,
    Insert,
    Delete,
    Home,
    End,
    PageUp,
    PageDown,
    Up,
    Down,
    Left,
    Right,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

/// <summary>Modifier state, named for what a host reads off its own event type.</summary>
[Flags]
public enum TermModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Ctrl = 4,
}

/// <summary>
/// One keyboard event in platform-neutral form. The toolkit control maps its own key args onto this and
/// keeps none of its types inside the engine — that is what makes the encoder testable without a window.
/// </summary>
public readonly struct TermInput
{
    /// <summary>Special key, or <c>TermKey.None</c> for text input.</summary>
    public TermKey Key { get; init; }

    /// <summary>The character a printable key produced, 0 when the toolkit gave none.</summary>
    public char Char { get; init; }

    public TermModifiers Modifiers { get; init; }

    /// <summary>Composed text (IME, dead keys). Wins over <see cref="Char"/> when set.</summary>
    public string? Text { get; init; }
}

public enum TermMouseButton
{
    Left = 0,
    Middle = 1,
    Right = 2,
    NoButton = 3,
    WheelUp = 4,
    WheelDown = 5,
}

public enum TermMouseAction
{
    Press,
    Release,
    Drag,
    Motion,
}

/// <summary>One mouse event in platform-neutral form; coordinates are 0-based like the grid.</summary>
public readonly struct TermMouseEvent
{
    public TermMouseButton Button { get; init; }

    public TermMouseAction Action { get; init; }

    public int Column { get; init; }

    public int Row { get; init; }

    public TermModifiers Modifiers { get; init; }
}

/// <summary>
/// Turns neutral input into the bytes a VT program expects: DECCKM-aware cursor keys, xterm's modifier
/// arithmetic, SGR (DEC 1006) and legacy X10 mouse encodings, and DEC 2004 paste wrapping.
/// <c>modifyOtherKeys</c> and the Kitty keyboard protocol are not implemented — the engine recognises
/// their setup sequences and drops them, which is the honest shape until W2 needs them.
/// </summary>
public static class TerminalInputEncoder
{
    /// <summary>Bytes written into <paramref name="dest"/>. 0 means "nothing to send", including "dest too small".</summary>
    public static int EncodeKey(in TermInput input, Terminal term, Span<byte> dest)
    {
        var ctrl = (input.Modifiers & TermModifiers.Ctrl) != 0;
        var alt = (input.Modifiers & TermModifiers.Alt) != 0;

        if (input.Key == TermKey.None)
        {
            // A host may report the character without a composed string (no IME involved); either form
            // has to work, or Ctrl+A arrives as nothing.
            var text = input.Text;
            if (string.IsNullOrEmpty(text) && input.Char is not '\0')
                text = input.Char.ToString();
            if (string.IsNullOrEmpty(text))
                return 0;

            // A composed single character with Ctrl is a control code, not text: IME never produces these.
            if (ctrl && text!.Length == 1)
                return WriteControlChar(text[0], alt, dest);

            return alt ? WriteAltThen(text, dest) : WriteUtf8(text, dest);
        }

        // A key that has a character (letters, digits, punctuation) but is asked for with Ctrl loses its
        // text form: xterm masks it. Everything else with Ctrl still goes through the special-key table.
        if (ctrl && input.Char is not '\0' && SpecialKeySequence(input.Key, term, TermModifiers.None).Length == 0)
        {
            var masked = WriteControlChar(input.Char, alt, dest);
            if (masked > 0)
                return masked;
        }

        var sequence = SpecialKeySequence(input.Key, term, input.Modifiers);
        return sequence.Length == 0
            ? 0
            : alt ? WriteAltThen(sequence, dest) : WriteAsciiOrUtf8(sequence, dest);
    }

    /// <summary>
    /// Mouse bytes for one event, or 0 when the mode bits say nobody asked. Motion bytes are gated the
    /// way DEC 1002/1003 differ: a held button versus any movement.
    /// </summary>
    public static int EncodeMouse(in TermMouseEvent mouse, Terminal term, Span<byte> dest)
    {
        if (!term.MouseTracking)
            return 0;

        var motion = mouse.Action is TermMouseAction.Drag or TermMouseAction.Motion;
        if (motion && !(mouse.Action == TermMouseAction.Drag ? term.MouseDrag : term.MouseAnyMove))
            return 0;

        var column = Math.Clamp(mouse.Column, 0, term.Columns - 1) + 1;
        var row = Math.Clamp(mouse.Row, 0, term.Rows - 1) + 1;

        if (term.MouseSgrEncoding)
        {
            var code = ReportCode(mouse, motion);
            var suffix = mouse.Action == TermMouseAction.Release ? 'm' : 'M';
            return WriteAsciiOrUtf8($"\x1b[<{code};{column};{row}{suffix}", dest);
        }

        // Legacy X10: three bytes of header, then button+32, column+32, row+32 — and "release" is the
        // single code 3 rather than a suffix, which is why the two encodings cannot share a helper.
        if (dest.Length < 6)
            return 0;

        var button = mouse.Action == TermMouseAction.Release ? 3 : ReportCode(mouse, motion);
        ref var head = ref dest[0];
        head = 0x1B;
        dest[1] = (byte)'[';
        dest[2] = (byte)'M';
        dest[3] = (byte)(button + 32);
        dest[4] = (byte)Math.Min(column + 32, 255);
        dest[5] = (byte)Math.Min(row + 32, 255);
        return 6;
    }

    /// <summary>
    /// The wheel when mouse reporting is off. Inside the alt screen and with DEC 1007 left on, the wheel
    /// becomes cursor keys — otherwise it is the host's own scrollback and nothing goes on the wire, which
    /// is what the 0 return tells the caller.
    /// </summary>
    public static int EncodeWheelAsKeys(TermMouseButton wheel, TermModifiers modifiers, Terminal term, Span<byte> dest)
    {
        if (wheel is not (TermMouseButton.WheelUp or TermMouseButton.WheelDown))
            return 0;
        if (!term.InAltScreen || !term.MouseAlternateScroll)
            return 0;

        var up = wheel == TermMouseButton.WheelUp;
        if ((modifiers & TermModifiers.Shift) != 0)
            return WriteAsciiOrUtf8(up ? "\x1b[5~" : "\x1b[6~", dest);

        return WriteAsciiOrUtf8(term.ApplicationCursorKeys
            ? (up ? "\x1bOA" : "\x1bOB")
            : (up ? "\x1b[A" : "\x1b[B"), dest);
    }

    /// <summary>Wraps a paste in DEC 2004's 200~ / 201~ when the program asked for it.</summary>
    public static int EncodePaste(ReadOnlySpan<byte> utf8, Terminal term, Span<byte> dest)
    {
        var overhead = term.BracketedPaste ? 12 : 0;
        if (dest.Length < utf8.Length + overhead)
            return 0;

        var written = 0;
        if (term.BracketedPaste)
        {
            "\x1b[200~"u8.CopyTo(dest);
            written = 6;
        }

        utf8.CopyTo(dest[written..]);
        written += utf8.Length;

        if (term.BracketedPaste)
        {
            "\x1b[201~"u8.CopyTo(dest[written..]);
            written += 6;
        }

        return written;
    }

    /// <summary>True when a printable character has a Ctrl mask at all; anything else is dropped, not guessed.</summary>
    public static bool IsControlMappable(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or ' ' or '@' or '[' or '\\' or ']' or '^' or '_' or '?';

    private static int ReportCode(in TermMouseEvent mouse, bool motion)
    {
        var code = mouse.Button switch
        {
            TermMouseButton.Left => 0,
            TermMouseButton.Middle => 1,
            TermMouseButton.Right => 2,
            TermMouseButton.WheelUp => 64,
            TermMouseButton.WheelDown => 65,
            // A movement with no button held reports 3 — the same value the legacy encoding reserves for
            // release, which is why the SGR path needs the numeric code and not the button enum.
            _ => 3,
        };

        if ((mouse.Modifiers & TermModifiers.Shift) != 0)
            code += 4;
        if ((mouse.Modifiers & TermModifiers.Alt) != 0)
            code += 8;
        if ((mouse.Modifiers & TermModifiers.Ctrl) != 0)
            code += 16;
        if (motion)
            code += 32;

        return code;
    }

    private static string SpecialKeySequence(TermKey key, Terminal term, TermModifiers modifiers)
    {
        var parameter = 1
            + ((modifiers & TermModifiers.Shift) != 0 ? 1 : 0)
            + ((modifiers & TermModifiers.Alt) != 0 ? 2 : 0)
            + ((modifiers & TermModifiers.Ctrl) != 0 ? 4 : 0);
        // xterm sends the modifier parameter only when something is actually held.
        var modified = parameter > 1 ? $";{parameter}" : string.Empty;

        return key switch
        {
            TermKey.Enter => "\r",
            TermKey.Tab => "\t",
            TermKey.BackTab => "\x1b[Z",
            TermKey.Backspace => "\x7f",
            TermKey.Escape => "\x1b",
            TermKey.Up => CursorKey('A', term.ApplicationCursorKeys, modified),
            TermKey.Down => CursorKey('B', term.ApplicationCursorKeys, modified),
            TermKey.Right => CursorKey('C', term.ApplicationCursorKeys, modified),
            TermKey.Left => CursorKey('D', term.ApplicationCursorKeys, modified),
            TermKey.Home => CursorKey('H', term.ApplicationCursorKeys, modified),
            TermKey.End => CursorKey('F', term.ApplicationCursorKeys, modified),
            TermKey.Insert => Tilde(2, modified),
            TermKey.Delete => Tilde(3, modified),
            TermKey.PageUp => Tilde(5, modified),
            TermKey.PageDown => Tilde(6, modified),
            // F1-F4 have no tilde spelling: SS3 with the modifier parameter in front of the letter.
            TermKey.F1 => Ss3('P', modified),
            TermKey.F2 => Ss3('Q', modified),
            TermKey.F3 => Ss3('R', modified),
            TermKey.F4 => Ss3('S', modified),
            TermKey.F5 => Tilde(15, modified),
            TermKey.F6 => Tilde(17, modified),
            TermKey.F7 => Tilde(18, modified),
            TermKey.F8 => Tilde(19, modified),
            TermKey.F9 => Tilde(20, modified),
            TermKey.F10 => Tilde(21, modified),
            TermKey.F11 => Tilde(23, modified),
            TermKey.F12 => Tilde(24, modified),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Cursor-family keys: DECCKM switches plain <c>ESC [ A</c> to SS3 <c>ESC O A</c>, and a modifier is
    /// spelled <c>ESC [ 1 ; m A</c> in both flavours — xterm keeps the <c>1</c> even under DECCKM.
    /// </summary>
    private static string CursorKey(char final, bool application, string modified)
        => modified.Length == 0
            ? (application ? $"\x1bO{final}" : $"\x1b[{final}")
            : $"\x1b[1{modified}{(application ? "O" : string.Empty)}{final}";

    private static string Tilde(int code, string modified) => modified.Length == 0
        ? $"\x1b[{code}~"
        : $"\x1b[{code}{modified}~";

    private static string Ss3(char final, string modified) => modified.Length == 0
        ? $"\x1bO{final}"
        : $"\x1b[1{modified}O{final}";

    private static int WriteControlChar(char c, bool alt, Span<byte> dest)
    {
        if (!IsControlMappable(c))
            return 0;

        var code = c switch
        {
            >= 'a' and <= 'z' => (byte)(c - 'a' + 1),
            >= 'A' and <= 'Z' => (byte)(c - 'A' + 1),
            ' ' or '@' => 0,
            '[' => 0x1B,
            '\\' => 0x1C,
            ']' => 0x1D,
            '^' => 0x1E,
            '_' => 0x1F,
            _ => (byte)0x7F, // '?'
        };

        if (dest.Length < (alt ? 2 : 1))
            return 0;

        var written = 0;
        if (alt)
            dest[written++] = 0x1B;
        dest[written++] = (byte)code;
        return written;
    }

    private static int WriteAltThen(string text, Span<byte> dest)
    {
        if (dest.Length < 1)
            return 0;
        dest[0] = 0x1B;
        var written = WriteAsciiOrUtf8(text, dest[1..]);
        return written == 0 ? 0 : 1 + written;
    }

    private static int WriteUtf8(string text, Span<byte> dest) => WriteAsciiOrUtf8(text, dest);

    /// <summary>Sequences are ASCII, text may not be: one entry point, byte-per-char when it is all ASCII.</summary>
    private static int WriteAsciiOrUtf8(string text, Span<byte> dest)
    {
        if (dest.Length == 0 || text.Length == 0)
            return 0;

        try
        {
            return System.Text.Encoding.UTF8.GetBytes(text, dest);
        }
        catch (ArgumentException)
        {
            return 0; // dest too small: drop the event rather than send half a sequence
        }
    }
}
