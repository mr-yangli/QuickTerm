namespace QuickTerm.Pty;

/// <summary>
/// Where a shell gets started. Windows takes the ConPTY path; the Unix half is deliberately not faked
/// here — see the ledger entry, which still owes one decision (a native forkpty helper versus
/// posix_spawn with a wrapped child) before there is anything worth shipping.
/// </summary>
public static class PtySession
{
    /// <summary>The user's shell, the way a terminal emulator picks one.</summary>
    public static string DefaultShell =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";

    /// <summary>Windows only for now: this throws on Linux and macOS rather than pretending otherwise.</summary>
    public static IPtySession Start(int columns, int rows, string? commandLine = null, string? workingDirectory = null)
    {
        commandLine ??= DefaultShell;

        if (OperatingSystem.IsWindows())
            return new ConPtySession(columns, rows, commandLine, workingDirectory);

        throw new PlatformNotSupportedException(
            "QuickTerm has no Unix pty implementation yet — the openpty + posix_spawn path is still an "
            + "unmade decision (see term/docs/quickterm-notes.md), and a stub would only hide that.");
    }
}
