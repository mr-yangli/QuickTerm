namespace QuickTerm.Pty;

/// <summary>What the shell process reported on exit; <see cref="Signal"/> is the terminating signal on Unix, else 0.</summary>
public readonly record struct PtyExitInfo(int ExitCode, int Signal);

/// <summary>
/// A local shell behind a pseudo-terminal. One instance is one process: it reports its exit on the
/// thread-pool thread that was reading from it, so <see cref="Read"/> stops when the shell does.
/// </summary>
public interface IPtySession : IDisposable
{
    /// <summary>Rows to paint for the terminal's own resize.</summary>
    int Columns { get; }

    int Rows { get; }

    /// <summary>True once the process is gone; a dead process fails reads rather than throwing first.</summary>
    bool HasExited { get; }

    /// <summary>Raised from a background thread when the process exits.</summary>
    event Action<PtyExitInfo>? Exited;

    /// <summary>
    /// Reads output bytes into <paramref name="buffer"/>, returning the count, or -1 at end of stream.
    /// Blocking by design: the host keeps this on its own thread and marshals bytes to the UI.
    /// </summary>
    int Read(Span<byte> buffer);

    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Tells the shell the view changed size. Safe to call before the process is running.</summary>
    void Resize(int columns, int rows);
}
