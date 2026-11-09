namespace QuickTerm.Tests;

using QuickTerm.App;
using QuickTerm.Terminal;

/// <summary>
/// The KeyDown / text-input boundary, in one table. KeyDown only knows the unshifted base character, so
/// claiming a printable key there is what made Shift+A type "a" and Shift+1 type "1" — the platform's
/// WM_CHAR (and with it the whole shifted shape of every layout) was dropped the moment the event was
/// handled. Ctrl+Alt is excluded on purpose: that is how the Win32 backend reports AltGr, whose real
/// character only ever arrives as text input.
/// </summary>
public sealed class KeyDownPolicyTests
{
    [Theory]
    [InlineData(TermKey.None, TermModifiers.None, false)]                     // plain letter: text input's
    [InlineData(TermKey.None, TermModifiers.Shift, false)]                    // Shift, CapsLock, "!", "€"
    [InlineData(TermKey.None, TermModifiers.Ctrl, true)]                      // Ctrl+letter: a control code
    [InlineData(TermKey.None, TermModifiers.Alt, true)]                       // Alt+letter: the ESC prefix
    [InlineData(TermKey.None, TermModifiers.Ctrl | TermModifiers.Alt, false)] // AltGr reports Ctrl+Alt
    [InlineData(TermKey.Up, TermModifiers.None, true)]                        // cursor keys are ours in every chord
    [InlineData(TermKey.Up, TermModifiers.Ctrl | TermModifiers.Alt, true)]
    [InlineData(TermKey.F5, TermModifiers.Shift, true)]
    [InlineData(TermKey.BackTab, TermModifiers.Shift, true)]
    public void The_Boundary_Keeps_Printables_With_Text_Input(TermKey key, TermModifiers modifiers, bool expected)
        => Assert.Equal(expected, TerminalView.EncodedAtKeyDown(key, modifiers));
}
