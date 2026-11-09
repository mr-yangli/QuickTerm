namespace QuickTerm.App;

using System;
using System.IO;

/// <summary>
/// Optional raw byte tap: <c>QUICKTERM_DUMP=&lt;directory&gt;</c> makes the running app write the pty's output
/// to <c>quickterm-pty-out.bin</c> and everything typed or pasted to <c>quickterm-pty-in.bin</c> there.
/// </summary>
/// <remarks>
/// It exists because "the screen shows something that doesn't match what I did" splits into three different
/// bugs — what the shell sent, what the engine turned it into, what the renderer put where — and only the
/// first one is answerable from a screenshot. The captured file then goes through <see cref="Replay" /> on
/// this machine, so the second and third get answered too.
///
/// Off unless the variable is set: the tap is a file write per read, and nothing about the shipping path
/// should pay for a diagnostic. <see cref="Open" /> runs once, on the UI thread before the reader starts, so
/// the two writers below never race with the decision to start.
/// </remarks>
internal static class ByteTap
{
    private static readonly object Gate = new();
    private static FileStream? _out;
    private static FileStream? _in;

    /// <summary>Opens both files when <c>QUICKTERM_DUMP</c> names a directory. Silent otherwise.</summary>
    public static void Open()
    {
        var directory = Environment.GetEnvironmentVariable("QUICKTERM_DUMP");
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            Directory.CreateDirectory(directory);
            _out = new FileStream(Path.Combine(directory, "quickterm-pty-out.bin"), FileMode.Create, FileAccess.Write);
            _in = new FileStream(Path.Combine(directory, "quickterm-pty-in.bin"), FileMode.Create, FileAccess.Write);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A dump that cannot be written is not a reason to lose the terminal.
            _out?.Dispose();
            _in?.Dispose();
            _out = _in = null;
        }
    }

    /// <summary>Bytes the pty delivered. Replay reads the same file back with <c>--replay cols rows file</c>.</summary>
    public static void PtyOutput(ReadOnlySpan<byte> bytes)
    {
        if (_out is null)
            return;

        lock (Gate)
        {
            // Flushed per read rather than left in the buffer: a capture that loses its tail because the
            // process exited is a capture that says the shell sent nothing.
            _out?.Write(bytes);
            _out?.Flush();
        }
    }

    /// <summary>Closes both files. The app never needs it (process exit does), the probe paths do.</summary>
    public static void Close()
    {
        lock (Gate)
        {
            _out?.Dispose();
            _in?.Dispose();
            _out = _in = null;
        }
    }

    /// <summary>Bytes the host sent to the shell: keystrokes, paste, mouse reports.</summary>
    public static void PtyInput(ReadOnlySpan<byte> bytes)
    {
        if (_in is null)
            return;

        lock (Gate)
        {
            _in?.Write(bytes);
            _in?.Flush();
        }
    }
}
