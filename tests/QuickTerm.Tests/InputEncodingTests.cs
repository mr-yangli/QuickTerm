namespace QuickTerm.Tests;

using System.Text;
using QuickTerm.Terminal;

/// <summary>Keyboard and mouse bytes: the half of W2 that can be proven without a window.</summary>
public sealed class InputEncodingTests
{
    private static string Bytes(Terminal term, in TermInput input)
    {
        var buffer = new byte[64];
        var written = TerminalInputEncoder.EncodeKey(input, term, buffer);
        return written == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, written);
    }

    private static string Mouse(Terminal term, in TermMouseEvent mouse)
    {
        var buffer = new byte[64];
        var written = TerminalInputEncoder.EncodeMouse(mouse, term, buffer);
        return written == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, written);
    }

    private static string Wheel(Terminal term, TermMouseButton wheel, TermModifiers modifiers = TermModifiers.None)
    {
        var buffer = new byte[64];
        var written = TerminalInputEncoder.EncodeWheelAsKeys(wheel, modifiers, term, buffer);
        return written == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, written);
    }

    private static Terminal Open(string modes)
    {
        var term = new Terminal(80, 24);
        if (modes.Length > 0)
            term.Send(modes);
        return term;
    }

    [Theory]
    [InlineData(TermKey.Up, "\x1b[A")]
    [InlineData(TermKey.Down, "\x1b[B")]
    [InlineData(TermKey.Right, "\x1b[C")]
    [InlineData(TermKey.Left, "\x1b[D")]
    [InlineData(TermKey.Home, "\x1b[H")]
    [InlineData(TermKey.End, "\x1b[F")]
    [InlineData(TermKey.Insert, "\x1b[2~")]
    [InlineData(TermKey.Delete, "\x1b[3~")]
    [InlineData(TermKey.PageUp, "\x1b[5~")]
    [InlineData(TermKey.PageDown, "\x1b[6~")]
    [InlineData(TermKey.Enter, "\r")]
    [InlineData(TermKey.Tab, "\t")]
    [InlineData(TermKey.BackTab, "\x1b[Z")]
    [InlineData(TermKey.Backspace, "\x7f")]
    [InlineData(TermKey.Escape, "\x1b")]
    [InlineData(TermKey.F1, "\x1bOP")]
    [InlineData(TermKey.F2, "\x1bOQ")]
    [InlineData(TermKey.F5, "\x1b[15~")]
    [InlineData(TermKey.F12, "\x1b[24~")]
    public void Plain_special_keys(TermKey key, string expected)
        => Assert.Equal(expected, Bytes(Open(string.Empty), new TermInput { Key = key }));

    [Theory]
    [InlineData(TermKey.Up, "\x1bOA")]
    [InlineData(TermKey.Down, "\x1bOB")]
    [InlineData(TermKey.Right, "\x1bOC")]
    [InlineData(TermKey.Left, "\x1bOD")]
    [InlineData(TermKey.Home, "\x1bOH")]
    [InlineData(TermKey.End, "\x1bOF")]
    public void Decckm_switches_the_cursor_family_to_ss3(TermKey key, string expected)
        => Assert.Equal(expected, Bytes(Open("\x1b[?1h"), new TermInput { Key = key }));

    [Theory]
    [InlineData(TermKey.Right, TermModifiers.Ctrl, "\x1b[1;5C")]
    [InlineData(TermKey.Right, TermModifiers.Shift, "\x1b[1;2C")]
    [InlineData(TermKey.Right, TermModifiers.Shift | TermModifiers.Ctrl, "\x1b[1;6C")]
    [InlineData(TermKey.PageUp, TermModifiers.Ctrl, "\x1b[5;5~")]
    [InlineData(TermKey.Delete, TermModifiers.Shift, "\x1b[3;2~")]
    [InlineData(TermKey.F1, TermModifiers.Ctrl, "\x1b[1;5OP")]
    public void Modifier_parameter_follows_xterm(TermKey key, TermModifiers modifiers, string expected)
        => Assert.Equal(expected, Bytes(Open(string.Empty), new TermInput { Key = key, Modifiers = modifiers }));

    [Theory]
    [InlineData(TermKey.Up, "\x1b[1;5OA")] // DECCKM keeps the 1 and gains the SS3 letter
    [InlineData(TermKey.F3, "\x1b[1;5OR")]
    public void Modifier_parameter_under_both_cursor_flavours(TermKey key, string expected)
        => Assert.Equal(expected, Bytes(Open("\x1b[?1h"), new TermInput { Key = key, Modifiers = TermModifiers.Ctrl }));

    [Theory]
    [InlineData('a', "\x01")]
    [InlineData('A', "\x01")]
    [InlineData('z', "\x1a")]
    [InlineData(' ', "\0")]
    [InlineData('[', "\x1b")]
    [InlineData('\\', "\x1c")]
    [InlineData(']', "\x1d")]
    [InlineData('^', "\x1e")]
    [InlineData('_', "\x1f")]
    [InlineData('?', "\x7f")]
    public void Ctrl_is_a_mask_not_a_table(char c, string expected)
        => Assert.Equal(expected, Bytes(Open(string.Empty), new TermInput { Char = c, Modifiers = TermModifiers.Ctrl }));

    [Theory]
    [InlineData('/')]
    [InlineData('1')]
    [InlineData('中')]
    public void Ctrl_on_something_without_a_control_code_is_dropped(char c)
        => Assert.Equal(string.Empty, Bytes(Open(string.Empty), new TermInput { Char = c, Modifiers = TermModifiers.Ctrl }));

    [Fact]
    public void Text_input_is_utf8_and_alt_prefixes_escape()
    {
        var term = Open(string.Empty);

        Assert.Equal("中文", Bytes(term, new TermInput { Text = "中文" }));
        Assert.Equal("\x1b中文", Bytes(term, new TermInput { Text = "中文", Modifiers = TermModifiers.Alt }));
    }

    [Fact]
    public void Composed_ime_text_survives_whatever_the_key_claimed()
    {
        var term = Open(string.Empty);
        var bytes = Bytes(term, new TermInput { Char = 'n', Text = "你好" });

        Assert.Equal("你好", bytes);
    }

    [Fact]
    public void Too_small_a_buffer_drops_the_event_instead_of_sending_half()
    {
        var term = Open(string.Empty);
        Span<byte> two = stackalloc byte[2];

        Assert.Equal(0, TerminalInputEncoder.EncodeKey(new TermInput { Text = "中文" }, term, two));
        Assert.Equal(0, TerminalInputEncoder.EncodeKey(new TermInput { Key = TermKey.PageUp }, term, stackalloc byte[3]));
    }

    [Fact]
    public void Mouse_reports_nothing_until_a_mode_asks()
    {
        var term = Open(string.Empty);
        var press = new TermMouseEvent { Button = TermMouseButton.Left, Action = TermMouseAction.Press };

        Assert.Equal(string.Empty, Mouse(term, press));

        term.Send("\x1b[?1000h");
        Assert.True(term.MouseTracking);
        Assert.False(term.MouseSgrEncoding);
    }
    [Fact]
    public void Legacy_x10_encoding_offsets_every_field_by_32()
    {
        var term = Open("\x1b[?1000h");
        var press = new TermMouseEvent { Button = TermMouseButton.Left, Action = TermMouseAction.Press, Column = 0, Row = 0 };

        Assert.Equal("\x1b[M !!", Mouse(term, press)); // button 0+32, column 1+32, row 1+32

        var release = new TermMouseEvent { Button = TermMouseButton.Left, Action = TermMouseAction.Release, Column = 3, Row = 4 };
        Assert.Equal("\x1b[M#$%", Mouse(term, release)); // release is the single code 3
    }

    [Fact]
    public void Sgr_encoding_uses_button_numbers_and_a_suffix()
    {
        var term = Open("\x1b[?1000h\x1b[?1006h");

        Assert.Equal("\x1b[<0;4;5M", Mouse(term, new TermMouseEvent { Button = TermMouseButton.Left, Column = 3, Row = 4 }));
        Assert.Equal("\x1b[<0;4;5m", Mouse(term, new TermMouseEvent { Button = TermMouseButton.Left, Action = TermMouseAction.Release, Column = 3, Row = 4 }));
        Assert.Equal("\x1b[<2;1;1M", Mouse(term, new TermMouseEvent { Button = TermMouseButton.Right }));
        Assert.Equal("\x1b[<64;1;1M", Mouse(term, new TermMouseEvent { Button = TermMouseButton.WheelUp }));
        Assert.Equal("\x1b[<65;1;1M", Mouse(term, new TermMouseEvent { Button = TermMouseButton.WheelDown }));
    }

    [Fact]
    public void Modifier_and_motion_bits_add_up_in_order()
    {
        var term = Open("\x1b[?1003h\x1b[?1006h");
        var hover = new TermMouseEvent
        {
            Button = TermMouseButton.NoButton,
            Action = TermMouseAction.Motion,
            Modifiers = TermModifiers.Shift | TermModifiers.Ctrl,
        };

        // no button (3) + shift (4) + ctrl (16) + motion (32) = 55
        Assert.Equal("\x1b[<55;1;1M", Mouse(term, hover));
    }

    [Theory]
    [InlineData("\x1b[?1000h", TermMouseAction.Drag, "")]      // 1000 alone: no motion bytes
    [InlineData("\x1b[?1000h", TermMouseAction.Press, "\x1b[<0;1;1M")]
    [InlineData("\x1b[?1000h\x1b[?1002h", TermMouseAction.Drag, "\x1b[<32;1;1M")]
    [InlineData("\x1b[?1000h\x1b[?1003h", TermMouseAction.Motion, "\x1b[<35;1;1M")]
    public void Motion_is_gated_by_1002_and_1003(string modes, TermMouseAction action, string expected)
    {
        var term = Open(modes + "\x1b[?1006h");
        // A press always carries the button that went down; only a hover is buttonless.
        var button = action == TermMouseAction.Motion ? TermMouseButton.NoButton : TermMouseButton.Left;
        Assert.Equal(expected, Mouse(term, new TermMouseEvent { Button = button, Action = action }));
    }

    [Fact]
    public void Clearing_one_mouse_mode_does_not_clear_the_others()
    {
        var term = Open("\x1b[?1000h\x1b[?1002h\x1b[?1006h");
        term.Send("\x1b[?1000l");

        Assert.True(term.MouseDrag);
        Assert.False(term.MouseClicks);
        Assert.True(term.MouseTracking); // 1002 alone still reports
    }

    [Fact]
    public void Coordinates_outside_the_grid_are_clamped_into_the_report()
    {
        var term = Open("\x1b[?1000h\x1b[?1006h");
        Assert.Equal("\x1b[<0;80;24M", Mouse(term, new TermMouseEvent { Column = 999, Row = 999 }));
    }

    [Fact]
    public void Wheel_becomes_cursor_keys_only_inside_the_alt_screen()
    {
        var term = Open("\x1b[?1049h");

        Assert.Equal("\x1b[A", Wheel(term, TermMouseButton.WheelUp));
        Assert.Equal("\x1b[B", Wheel(term, TermMouseButton.WheelDown));
        Assert.Equal("\x1b[5~", Wheel(term, TermMouseButton.WheelUp, TermModifiers.Shift));

        term.Send("\x1b[?1h");
        Assert.Equal("\x1bOA", Wheel(term, TermMouseButton.WheelUp));

        term.Send("\x1b[?1049l");
        Assert.Equal(string.Empty, Wheel(term, TermMouseButton.WheelUp)); // own scrollback, nothing on the wire
    }

    [Fact]
    public void Alternate_scroll_can_be_turned_off_by_dek_1007()
    {
        var term = Open("\x1b[?1049h\x1b[?1007l");
        Assert.True(term.InAltScreen);
        Assert.False(term.MouseAlternateScroll);
        Assert.Equal(string.Empty, Wheel(term, TermMouseButton.WheelUp));
    }

    [Theory]
    [InlineData("", "hello")]
    [InlineData("\x1b[?2004h", "\x1b[200~hello\x1b[201~")]
    public void Paste_wraps_only_when_the_program_asked(string modes, string expected)
    {
        var term = Open(modes);
        var buffer = new byte[64];
        var written = TerminalInputEncoder.EncodePaste("hello"u8, term, buffer);

        Assert.Equal(expected, Encoding.UTF8.GetString(buffer, 0, written));
    }

    [Fact]
    public void Paste_that_does_not_fit_is_refused_not_truncated()
    {
        var term = Open("\x1b[?2004h");
        Assert.Equal(0, TerminalInputEncoder.EncodePaste("hello"u8, term, new byte[10]));
    }
}
