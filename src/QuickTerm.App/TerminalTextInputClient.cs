namespace QuickTerm.App;

using Avalonia;
using Avalonia.Input.TextInput;
using Avalonia.VisualTree;

/// <summary>
/// A keyboard-only text-input client. Avalonia asks the focused element for one before it routes text input
/// to that element, and a stock <see cref="Avalonia.Controls.Control" /> declares none — which is the shape
/// "opens fine, live prompt on screen, types nothing" has when focus is demonstrably held.
/// </summary>
/// <remarks>
/// A terminal receives keystrokes rather than composing them: the program behind the pty decides what a key
/// means, so there is no preedit to draw and no surrounding text to hand an IME. Both capability flags say so
/// rather than leaving them at their defaults, and composition — when W2 takes it on — will arrive as ordinary
/// TextInput bytes and go straight to the pty.
/// </remarks>
internal sealed class TerminalTextInputClient : TextInputMethodClient
{
    /// <summary>The focused terminal grid: the coordinate space <see cref="CursorRectangle" /> is expressed in.</summary>
    public override Visual TextViewVisual => TerminalView.ImeTarget!;

    public override bool SupportsPreedit => false;

    public override bool SupportsSurroundingText => false;

    public override string SurroundingText => string.Empty;

    /// <summary>
    /// The caret cell, so an IME parks its candidate window on the cursor. Returning <c>default</c> here left
    /// the platform with no anchor and the candidate box appeared in a window corner instead of at the prompt.
    /// </summary>
    public override Rect CursorRectangle => TerminalView.ImeTarget?.ImeCursorRectangle ?? default;

    public override TextSelection Selection
    {
        get => default;
        set
        {
        }
    }

    public override void SetPreeditText(string? preeditText)
    {
    }
}
