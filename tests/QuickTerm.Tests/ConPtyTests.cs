namespace QuickTerm.Tests;

using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using QuickTerm.Pty;
using QuickTerm.Terminal;

/// <summary>
/// End-to-end against the real ConPTY on this machine. These are the only tests here that launch a
/// process, so they are the ones that say whether the engine reads a live shell rather than my own input.
/// The engine is single-threaded by contract, so the reader thread only ever collects bytes: the test
/// thread feeds them to a <see cref="Terminal" />, which keeps the grid from being written on two threads.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtyTests
{
    private const int TimeoutMilliseconds = 20_000;
    private static bool? _ptyAvailable;

    /// <summary>
    /// ConPTY needs a desktop shell: launched from a service session or a plain pipe, the child dies at
    /// console init with 0xC0000142 and no amount of engine work changes that. The live tests ask the OS
    /// once and stand down — the same shape as QuickSql's live-database profiles standing down with no
    /// target configured. Standing down is not passing: a machine that can spawn a shell runs these.
    /// </summary>
    private static bool PtyAvailable()
    {
        if (_ptyAvailable.HasValue)
            return _ptyAvailable.Value;

        _ptyAvailable = Probe();
        return _ptyAvailable.Value;
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var pty = new ConPtySession(40, 8, "cmd.exe /c echo QUICKTERM-PREFLIGHT");
            var text = new StringBuilder();
            var reader = new Thread(() =>
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var read = pty.Read(buffer);
                    if (read < 0)
                        return;
                    lock (text)
                        text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            })
            {
                IsBackground = true,
            };
            reader.Start();
            if (!reader.Join(5000))
                return false;

            lock (text)
                return text.ToString().Contains("QUICKTERM-PREFLIGHT");
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private sealed class LivePump : IDisposable
    {
        private readonly ConPtySession _pty;
        private readonly List<byte> _bytes = [];
        private readonly object _gate = new();
        private bool _eof;

        public LivePump(ConPtySession pty)
        {
            _pty = pty;
            Reader = new Thread(Run) { IsBackground = true };
            Reader.Start();
        }

        public Thread Reader { get; }

        public bool AtEof { get { lock (_gate) return _eof; } }

        private void Run()
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = _pty.Read(buffer);
                if (read < 0)
                {
                    lock (_gate)
                        _eof = true;
                    return;
                }

                lock (_gate)
                    _bytes.AddRange(buffer.AsSpan(0, read));
            }
        }

        /// <summary>Everything seen so far, written onto the grid by the calling thread.</summary>
        public string Feed(Terminal term)
        {
            byte[] snapshot;
            lock (_gate)
                snapshot = [.. _bytes];
            term.Write(snapshot);
            return term.ScreenText();
        }

        public void Dispose() => _pty.Dispose();
    }

    private static string PumpToEof(ConPtySession pty, Terminal term)
    {
        var pump = new LivePump(pty);
        Assert.True(pump.Reader.Join(TimeoutMilliseconds), "the pty output stream never reached EOF");
        return pump.Feed(term);
    }

    /// <summary>
    /// The counted verdict on whether the live runs below meant anything. They stand down by returning, which
    /// xunit v2 counts as a pass — there is no runtime skip — so without this row a machine that never spawned
    /// a shell still reports the class all green. This assertion is what keeps "162 颗全绿" from being read as
    /// "the pty path works here": red on this row means every live run above proved nothing.
    /// </summary>
    [Fact]
    public void ConPty_Is_Available_In_This_Context()
    {
        Assert.True(PtyAvailable(),
            "ConPTY could not spawn a shell in this context (the child dies at console init, 0xC0000142, from a "
            + "service or piped session). The live runs in this class stand down silently, so treat them as unrun.");
    }

    [Fact]
    public void ConPty_Prints_A_Live_Command_On_The_Grid()
    {
        if (!PtyAvailable())
            return; // ConPTY is the Windows half; the Unix half gets its turn on the first Linux box

        using var pty = new ConPtySession(80, 24, "cmd.exe /c echo QUICKTERM-SENTINEL");
        Assert.Contains("QUICKTERM-SENTINEL", PumpToEof(pty, new Terminal(80, 24)));
    }

    [Fact]
    public void ConPty_Scrolls_Off_The_Rows_That_Do_Not_Fit()
    {
        if (!PtyAvailable())
            return;

        using var pty = new ConPtySession(80, 3, "cmd.exe /c echo L1& echo L2& echo L3& echo L4");
        var term = new Terminal(80, 3, scrollbackLines: 200);
        var screen = PumpToEof(pty, term);

        Assert.Contains("L4", screen);
        Assert.DoesNotContain("L1", screen); // three rows held; the first was pushed out
        Assert.Equal(1, term.ScrollbackCount);
    }

    [Fact]
    public void ConPty_Reports_The_Exit_Code_Of_The_Command_It_Ran()
    {
        if (!PtyAvailable())
            return;

        using var pty = new ConPtySession(80, 24, "cmd.exe /c exit 7");
        var exited = new ManualResetEventSlim(false);
        PtyExitInfo info = default;
        pty.Exited += code =>
        {
            info = code;
            exited.Set();
        };

        Assert.True(exited.Wait(TimeoutMilliseconds), "the exit event never arrived");
        Assert.Equal(7, info.ExitCode);
        Assert.True(pty.HasExited);
    }

    [Fact]
    public void ConPty_Echos_What_The_Host_Types_And_Goes_Where_It_Was_Resized()
    {
        if (!PtyAvailable())
            return;

        using var pty = new ConPtySession(40, 8, "cmd.exe /Q /K");
        pty.Resize(70, 12);
        Assert.Equal((70, 12), (pty.Columns, pty.Rows));

        var term = new Terminal(70, 12, scrollbackLines: 200);
        using var pump = new LivePump(pty);

        pty.Write("echo QUICKTERM-TYPED\r"u8);
        Assert.True(WaitUntil(() => pump.Feed(term).Contains("QUICKTERM-TYPED"), TimeoutMilliseconds),
            "the typed command never came back on the grid");

        pty.Write("exit\r"u8);
        Assert.True(pump.Reader.Join(TimeoutMilliseconds), "the shell never closed the pty");
        Assert.True(pty.HasExited);
    }

    [Fact]
    public void Interleaved_Bytes_Survive_A_Chunk_Boundary()
    {
        // Not a pty test, but it belongs with the live ones: a sequence cut anywhere must still parse.
        var term = new Terminal(20, 4);
        var stream = System.Text.Encoding.UTF8.GetBytes("\u001b[1;3H\u001b[31mAB\u001b[0mC\u001b]0;t\x07");
        for (var i = 0; i < stream.Length; i++)
            term.Write(stream.AsSpan(i, 1));

        Assert.Equal("ABC", term.RowText(0).TrimStart());
        Assert.Equal('A', term.RuneAt(0, 2));
        Assert.Equal(1, term.Cell(0, 2).Fg.Kind);
        Assert.Equal("t", term.WindowTitle);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(25);
        }

        return condition();
    }
}
