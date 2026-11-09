namespace QuickTerm.Tests;

using QuickTerm.Terminal;

/// <summary>
/// The colour table: what the built-in slots mean, and what OSC 4/10/11/104/110/111 are allowed to change.
/// This used to live inside the renderer as a baked array, which made every one of those requests a no-op —
/// and a program that repaints its theme (vim, `dircolors`, most prompts) would silently keep the old colours.
/// </summary>
public sealed class PaletteTests
{
    [Fact]
    public void The_First_Sixteen_Are_The_Theme_Base()
    {
        var palette = new TermPalette();

        Assert.Equal(0x171717u, palette.Rgb(0));
        Assert.Equal(0xB22222u, palette.Rgb(1));
        Assert.Equal(0x2AA198u, palette.Rgb(6));
        Assert.Equal(0xEF2929u, palette.Rgb(9));
        Assert.Equal(0xEEEEEEu, palette.Rgb(15));
    }

    [Fact]
    public void The_Cube_Is_Xterms_Arithmetic()
    {
        // Pinned as values rather than as the loop that makes them: every program that emits 256-colour SGR
        // assumes these numbers, and a changed formula is a changed screen.
        var palette = new TermPalette();

        Assert.Equal(0x000000u, palette.Rgb(16));
        Assert.Equal(0xFF0000u, palette.Rgb(196));
        Assert.Equal(0x00FF00u, palette.Rgb(46));
        Assert.Equal(0x878700u, palette.Rgb(100));
        Assert.Equal(0xFFFFFFu, palette.Rgb(231));
    }

    [Fact]
    public void The_Grey_Ramp_Starts_At_Eight_And_Steps_By_Ten()
    {
        var palette = new TermPalette();

        Assert.Equal(0x080808u, palette.Rgb(232));
        Assert.Equal(0x6C6C6Cu, palette.Rgb(242));
        Assert.Equal(0xEEEEEEu, palette.Rgb(255));
    }

    [Fact]
    public void Defaults_Start_At_The_Themes_Own_Pair()
    {
        var palette = new TermPalette();

        Assert.Equal(TermPalette.DefaultForeground, palette.Foreground);
        Assert.Equal(TermPalette.DefaultBackground, palette.Background);
    }

    [Fact]
    public void Osc_4_Rewrites_A_Slot()
    {
        var term = new Terminal(20, 5);
        term.Send("\u001b]4;1;#FF0000\u0007");

        Assert.Equal(0xFF0000u, term.Palette.Rgb(1));
    }

    [Fact]
    public void Osc_4_Accepts_The_Triple_And_Its_Short_Form()
    {
        // xterm reads each component as a fraction of the widest value of that width, so `f` means 255.
        // Programs send both forms and neither is a mistake.
        var term = new Terminal(20, 5);
        term.Send("\u001b]4;2;rgb:ff/00/00\u0007");
        term.Send("\u001b]4;3;rgb:f/0/0\u0007");

        Assert.Equal(0xFF0000u, term.Palette.Rgb(2));
        Assert.Equal(0xFF0000u, term.Palette.Rgb(3));
    }

    [Fact]
    public void Osc_4_Accepts_System_Names()
    {
        var term = new Terminal(20, 5);
        term.Send("\u001b]4;5;BrightCyan\u0007");

        Assert.Equal(0x34E2E2u, term.Palette.Rgb(5));
    }

    [Fact]
    public void Osc_4_Takes_Multiple_Pairs_In_One_Request()
    {
        var term = new Terminal(20, 5);
        term.Send("\u001b]4;1;#FF0000;2;#00FF00;3;#0000FF\u0007");

        Assert.Equal(0xFF0000u, term.Palette.Rgb(1));
        Assert.Equal(0x00FF00u, term.Palette.Rgb(2));
        Assert.Equal(0x0000FFu, term.Palette.Rgb(3));
    }

    [Fact]
    public void Osc_104_Resets_One_Slot_Or_The_Whole_Table()
    {
        var term = new Terminal(20, 5);
        term.Send("\u001b]4;1;#FF0000;2;#00FF00\u0007");
        term.Send("\u001b]104;1\u0007");

        Assert.Equal(0xB22222u, term.Palette.Rgb(1));
        Assert.Equal(0x00FF00u, term.Palette.Rgb(2));

        term.Send("\u001b]104\u0007");
        Assert.Equal(0x4E9A06u, term.Palette.Rgb(2));
    }

    [Fact]
    public void Osc_10_And_11_Move_The_Defaults_And_110_111_Put_Them_Back()
    {
        var term = new Terminal(20, 5);
        term.Send("\u001b]10;#123456\u0007\u001b]11;rgb:00/ff/00\u0007");

        Assert.Equal(0x123456u, term.Palette.Foreground);
        Assert.Equal(0x00FF00u, term.Palette.Background);

        term.Send("\u001b]110\u0007\u001b]111\u0007");

        Assert.Equal(TermPalette.DefaultForeground, term.Palette.Foreground);
        Assert.Equal(TermPalette.DefaultBackground, term.Palette.Background);
    }

    [Fact]
    public void A_Query_Changes_Nothing()
    {
        // Nothing in this engine answers a device-control query yet, so `?` is parsed and dropped. Replying
        // with a wrong value would be worse than silence, and answering at all is its own open item.
        var term = new Terminal(20, 5);
        term.Send("\u001b]4;1;?\u0007\u001b]10;?\u0007\u001b]11;?\u0007");

        Assert.Equal(0xB22222u, term.Palette.Rgb(1));
        Assert.Equal(TermPalette.DefaultForeground, term.Palette.Foreground);
        Assert.Equal(TermPalette.DefaultBackground, term.Palette.Background);
    }

    [Fact]
    public void Unreadable_Specs_And_Out_Of_Range_Slots_Are_Dropped()
    {
        var term = new Terminal(20, 5);
        term.Send("\u001b]4;1;notacolor\u0007");
        term.Send("\u001b]4;999;#FFFFFF\u0007");
        term.Send("\u001b]4;-3;#FFFFFF\u0007");
        term.Send("\u001b]11;rgb:12\u0007");
        term.Send("\u001b]11;#12345\u0007");

        Assert.Equal(0xB22222u, term.Palette.Rgb(1));
        Assert.Equal(TermPalette.DefaultBackground, term.Palette.Background);
    }

    [Fact]
    public void A_Palette_Change_Dirties_The_Whole_Screen()
    {
        // The cells still say "slot 1"; only what slot 1 means moved. Without this the renderer keeps the
        // colours it drew before the request until something else happens to dirty a row.
        var term = new Terminal(20, 5);
        term.Send("abc");
        term.Damage.Consume();

        term.Send("\u001b]4;1;#FF0000\u0007");

        Assert.True(term.Damage.IsFull);
    }

    [Fact]
    public void An_Ignored_Request_Dirties_Nothing()
    {
        var term = new Terminal(20, 5);
        term.Send("abc");
        term.Damage.Consume();

        term.Send("\u001b]4;1;?\u0007");

        Assert.False(term.Damage.IsFull);
    }

    [Fact]
    public void Out_Of_Range_Slots_Read_Back_As_Slot_Zero()
    {
        // Out-of-range is a glitch, not an exception: a program that sends slot 256 should get a colour, and
        // the one it gets has to be stable.
        var palette = new TermPalette();

        Assert.Equal(palette.Rgb(0), palette.Rgb(256));
        Assert.Equal(palette.Rgb(0), palette.Rgb(-1));
    }
}
