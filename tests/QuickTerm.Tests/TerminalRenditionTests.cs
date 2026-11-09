namespace QuickTerm.Tests;

using System.Text;
using QuickTerm.Terminal;

/// <summary>Rendition, modes, charset designation, the responses an app waits for, and the two resets.</summary>
public sealed class TerminalRenditionTests
{
    private static string Replies(Terminal term) => Encoding.ASCII.GetString(term.DrainReplies().Span);

    [Fact]
    public void Basic_Colors_And_Attributes_Land_On_The_Cell()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b[31mR");
        Assert.Equal(1, term.Cell(0, 0).Fg.Index);
        Assert.Equal(1, term.Cell(0, 0).Fg.Kind);

        term.Send("\u001b[104mB");
        Assert.Equal(12, term.Cell(0, 1).Bg.Index);

        term.Send("\u001b[1;4;7mX");
        Assert.Equal(TermEffects.Bold | TermEffects.Underline | TermEffects.Inverse, term.Cell(0, 2).Effects);
    }

    [Fact]
    public void Normal_Background_Range_40_To_47()
    {
        // This range had no case at all until the reflow test asked for a painted line: `SGR 41` fell through
        // to the unimplemented default, so every coloured background — `ls --color`, a vim status line, a
        // git diff — was being dropped on the floor while the foreground beside it looked right.
        var term = new Terminal(20, 3);
        term.Send("\u001b[41mA");
        Assert.Equal((1, 1), (term.Cell(0, 0).Bg.Kind, term.Cell(0, 0).Bg.Index));

        term.Send("\u001b[40mB");
        Assert.Equal(0, term.Cell(0, 1).Bg.Index);

        term.Send("\u001b[47mC");
        Assert.Equal(7, term.Cell(0, 2).Bg.Index);

        term.Send("\u001b[4m\u001b[45mD");
        Assert.Equal(5, term.Cell(0, 3).Bg.Index);
        Assert.Equal(TermEffects.Underline, term.Cell(0, 3).Effects); // the underline survives its colour

        term.Send("\u001b[49mE");
        Assert.True(term.Cell(0, 4).Bg.IsDefault);
    }

    [Fact]
    public void Two_Hundred_And_Five_And_Truecolour_Forms()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b[38;5;196mA");
        Assert.Equal((1, 196), (term.Cell(0, 0).Fg.Kind, term.Cell(0, 0).Fg.Index));

        term.Send("\u001b[48;2;10;20;30mB");
        Assert.Equal(0x0A141E, term.Cell(0, 1).Bg.RgbValue);

        term.Send("\u001b[38:2::1:2:3mC"); // colon form with an empty colour-space id
        Assert.Equal(0x010203, term.Cell(0, 2).Fg.RgbValue);

        term.Send("\u001b[39;49mD");
        Assert.True(term.Cell(0, 3).Fg.IsDefault && term.Cell(0, 3).Bg.IsDefault);
    }

    [Fact]
    public void Colon_Sub_Parameters_Do_Not_Leak_As_Codes()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b[4:3mC"); // curly underline, and no stray italic from the 3
        Assert.Equal(TermEffects.CurlyUnderline, term.Cell(0, 0).Effects);

        term.Send("\u001b[4:0m");
        term.Send("D");
        Assert.Equal(TermEffects.None, term.Cell(0, 1).Effects);
    }

    [Theory]
    [InlineData("\u001b[0m", TermEffects.None)]
    [InlineData("\u001b[m", TermEffects.None)] // no parameter at all also means reset
    [InlineData("\u001b[22m", TermEffects.Underline)]
    [InlineData("\u001b[24m", TermEffects.Bold)]
    public void Partial_Attribute_Resets(string sequence, TermEffects expected)
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b[1;4m");
        term.Send(sequence);
        term.Send("X");
        Assert.Equal(expected, term.Cell(0, 0).Effects);
    }

    [Fact]
    public void Malformed_Extended_Color_Swallows_Its_Tail()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b[38;9;7mX"); // 38 with an unknown mode: the 7 must not repaint as SGR 7
        Assert.Equal(TermEffects.None, term.Cell(0, 0).Effects);
        Assert.True(term.Cell(0, 0).Fg.IsDefault);
    }

    [Fact]
    public void Cursor_Visible_And_Application_Modes()
    {
        var term = new Terminal(20, 3);
        Assert.True(term.CursorVisible);

        term.Send("\u001b[?25l");
        Assert.False(term.CursorVisible);
        term.Send("\u001b[?25h");
        Assert.True(term.CursorVisible);

        term.Send("\u001b[?1h\u001b[?2004h\u001b[?1000h\u001b[?1006h");
        Assert.True(term.ApplicationCursorKeys);
        Assert.True(term.BracketedPaste);
        Assert.True(term.MouseTracking);
        Assert.True(term.MouseSgrEncoding);

        term.Send("\u001b[?1000l");
        Assert.False(term.MouseTracking);
    }

    [Fact]
    public void Cursor_Style_Sequences_Use_The_Decscusr_Numbering()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b[4 q");
        Assert.Equal(TerminalCursorStyle.UnderlineSteady, term.CursorStyle);

        term.Send("\u001b[5 q");
        Assert.Equal(TerminalCursorStyle.BarBlink, term.CursorStyle);

        term.Send("\u001b[0 q");
        Assert.Equal(TerminalCursorStyle.BlockBlink, term.CursorStyle); // 0 is defined as "as if 1"

        term.Send("\u001b[9 q");
        Assert.Equal(TerminalCursorStyle.BlockSteady, term.CursorStyle); // out of range falls back
    }

    [Fact]
    public void Synchronized_Output_Mode_Is_Remembered()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b[?2026h");
        Assert.True(term.SynchronizedOutput);
        term.Send("\u001b[?2026l");
        Assert.False(term.SynchronizedOutput);
    }

    [Fact]
    public void Damage_Keeps_Accumulating_While_A_Sync_Update_Is_Open()
    {
        // The host holds frames back inside DEC 2026, which is only safe because the engine keeps counting
        // what it touched. A deferred frame that also dropped its damage would paint a stale row and then
        // never be told to fix it.
        var term = new Terminal(20, 3);
        term.Send("\u001b[?2026h");
        term.Send("first line");
        term.Damage.Consume();

        term.Send("\r\nsecond line");

        Assert.True(term.SynchronizedOutput);
        Assert.True(term.Damage.HasDamage);
    }

    [Fact]
    public void Modify_Other_Keys_Pop_Does_Not_Clobber_The_Cursor()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b[5;7H");
        term.Send("\u001b[>1u"); // Kitty/modifyOtherKeys pop, not RCSR
        Assert.Equal((6, 2), (term.CursorColumn, term.CursorRow)); // the row was already clamped by the 3-row surface
    }

    [Fact]
    public void Save_And_Restore_Cursor_Both_Spells()
    {
        var term = new Terminal(20, 6);
        term.Send("\u001b[3;5H");
        term.Send("\u001b[s");
        term.Send("\u001b[1;1H");
        term.Send("\u001b[u");
        Assert.Equal((4, 2), (term.CursorColumn, term.CursorRow));

        term.Send("\u001b[6;2H\u001b7\u001b[1;1H\u001b8");
        Assert.Equal((1, 5), (term.CursorColumn, term.CursorRow));
    }

    [Fact]
    public void Dec_Special_Graphics_Map_Box_Drawing_Then_Come_Back()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b(B\u001b(0lqqk\u001b(B+");

        Assert.Equal('┌', term.Cell(0, 0).Rune);
        Assert.Equal('─', term.Cell(0, 1).Rune);
        Assert.Equal('┐', term.Cell(0, 3).Rune);
        Assert.Equal('+', term.Cell(0, 4).Rune);
    }

    [Fact]
    public void Shift_Out_And_In_Swap_G1_In()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b)0x\x0Ex\x0Fx");

        Assert.Equal('x', term.Cell(0, 0).Rune);
        Assert.Equal('│', term.Cell(0, 1).Rune); // 'x' in DEC special graphics is the vertical rule
        Assert.Equal('x', term.Cell(0, 2).Rune);
    }

    [Fact]
    public void Window_Title_From_Both_Terminators_And_Utf8()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b]0;first\x07");
        Assert.Equal("first", term.WindowTitle);

        term.Send("\u001b]2;second\u001b\\");
        Assert.Equal("second", term.WindowTitle);

        var bytes = new List<byte> { 0x1B, (byte)']' };
        bytes.AddRange("0;终端"u8.ToArray());
        bytes.Add(0x07);
        term.Write(bytes.ToArray());
        Assert.Equal("终端", term.WindowTitle);
    }

    [Fact]
    public void Title_Change_Fires_Once_Per_Distinct_Value()
    {
        var term = new Terminal(20, 3);
        var seen = new List<string?>();
        term.TitleChanged += title => seen.Add(title);

        term.Send("\u001b]0;a\x07\u001b]0;a\x07\u001b]0;b\x07");
        Assert.Equal(["a", "b"], seen);
    }

    [Fact]
    public void Osc_52_Raises_The_Payload_Decoded()
    {
        var term = new Terminal(20, 3);
        string? captured = null;
        term.ClipboardRequested += payload => captured = payload;

        term.Send("\u001b]52;c;QUlDRQ==\x07");
        Assert.Equal("AICE", captured); // "QUlDRQ==" is base64 for AICE: the selector and the encoding are protocol, not text to paste
    }

    [Fact]
    public void Dcs_Body_Is_Swallowed_Whole()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001bPq#0;2;0;0;0m#1;2;70;0;0m\u001b\\after");
        Assert.Equal("after", term.RowText(0));
    }

    [Fact]
    public void Apc_And_Sos_Bodies_Are_Swallowed_Whole()
    {
        var term = new Terminal(20, 3);
        term.Send("\u001b_Garbage\u001b\\\u001bXnoise\u001b\\ok");
        Assert.Equal("ok", term.RowText(0));
    }

    [Fact]
    public void Sequences_Split_Across_Writes_Still_Complete()
    {
        var term = new Terminal(20, 4);
        term.Send("\u001b");
        term.Send("[1;");
        term.Write([0x35, (byte)'H']); // cut mid-parameter
        term.Write("X"u8);
        Assert.Equal('X', term.Cell(0, 4).Rune);

        term.Send("\u001b]0;ti");
        term.Send("tle\x07");
        Assert.Equal("title", term.WindowTitle);
    }

    [Fact]
    public void Can_Aborts_A_Sequence_And_Its_Continuation_Prints()
    {
        var term = new Terminal(20, 4);
        term.Write([0x1B, (byte)'[', 0x33, 0x18, (byte)'H']); // CAN cancels the CSI, then 'H' is text
        Assert.Equal("H", term.RowText(0));
    }

    [Fact]
    public void Bel_Inside_A_Csi_Does_Not_Break_The_Parameters()
    {
        var term = new Terminal(20, 4);
        term.Write([0x1B, (byte)'[', 0x33, 0x07, (byte)';', 0x35, (byte)'H']);
        Assert.Equal((4, 2), (term.CursorColumn, term.CursorRow));
    }

    [Fact]
    public void Oversized_Osc_Payload_Is_Capped_Not_Stored()
    {
        var term = new Terminal(20, 4);
        term.Send("\u001b]0;");
        for (var i = 0; i < 5000; i++)
            term.Write([(byte)'x']);
        term.Send("\x07");

        Assert.NotNull(term.WindowTitle);
        Assert.Equal(4094, term.WindowTitle!.Length); // the 4 KiB cap covers the whole string field, designator included
    }

    [Fact]
    public void Device_Attributes_Both_Dishes()
    {
        var term = new Terminal(20, 4);
        term.Send("\u001b[c");
        Assert.Equal("\u001b[?6c", Replies(term));

        term.Send("\u001b[>c");
        Assert.Equal("\u001b[>1;114;0c", Replies(term));

        term.Send("\u001b[<c");
        Assert.Equal(string.Empty, Replies(term)); // XTVERSION is deliberately not answered
    }

    [Fact]
    public void Device_Status_Replies()
    {
        var term = new Terminal(20, 4);
        term.Send("\u001b[5n");
        Assert.Equal("\u001b[0n", Replies(term));

        term.Send("\u001b[2;3H\u001b[6n");
        Assert.Equal("\u001b[2;3R", Replies(term));

        term.Send("\u001b[2;4r\u001b[?6h\u001b[1;1H\u001b[?6n");
        Assert.Equal("\u001b[1;1R", Replies(term)); // under DECOM the report is region-relative
    }

    [Fact]
    public void Replies_Accumulate_And_Drain_Once()
    {
        var term = new Terminal(20, 4);
        term.Send("\u001b[5n\u001b[c");
        Assert.Equal("\u001b[0n\u001b[?6c", Replies(term));
        Assert.True(term.DrainReplies().IsEmpty);
    }

    [Fact]
    public void Alignment_Test_Fills_The_Surface_With_E()
    {
        var term = new Terminal(6, 3);
        term.Send("\u001b#8");

        Assert.Equal("EEEEEE", term.RowText(0));
        Assert.Equal("EEEEEE", term.RowText(2));
        Assert.True(term.Damage.IsFull);
    }

    [Fact]
    public void Hard_Reset_Clears_Everything_The_Session_Built_Up()
    {
        var term = new Terminal(10, 2);
        term.Send("a\r\nb\r\nc");
        term.Send("\u001b]0;t\x07\u001b[?7l\u001b[1;31m");

        term.Send("\u001bc");

        Assert.Equal(0, term.ScrollbackCount);
        Assert.Equal(string.Empty, term.ScreenText());
        Assert.Null(term.WindowTitle);
        Assert.True(term.AutoWrap);
        Assert.True(term.Cell(0, 0).Fg.IsDefault);
    }

    [Fact]
    public void Soft_Reset_Keeps_The_Surface_And_Drops_The_Modes()
    {
        var term = new Terminal(10, 3);
        term.Send("kept");
        term.Send("\u001b[?7l\u001b[1;31m");
        term.Damage.Consume();

        term.Send("\u001b[!p");

        Assert.Equal("kept", term.RowText(0));
        Assert.True(term.AutoWrap);
        Assert.True(term.Damage.IsFull);
    }

    [Fact]
    public void Inverse_And_Hidden_Ride_The_Cell_Not_The_Grid()
    {
        var term = new Terminal(10, 3);
        term.Send("\u001b[7mA\u001b[27mB\u001b[8mC");

        Assert.Equal(TermEffects.Inverse, term.Cell(0, 0).Effects);
        Assert.Equal(TermEffects.None, term.Cell(0, 1).Effects);
        Assert.Equal(TermEffects.Hidden, term.Cell(0, 2).Effects);
    }
}
