namespace QuickTerm.Benchmarks;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using QuickTerm.Pty;
using QuickTerm.Terminal;

/// <summary>
/// The W1 gate readings, in one artifact so the ledger can quote numbers rather than adjectives.
/// Run with no argument for everything, or one of <c>parse</c>, <c>churn</c>, <c>pty</c>.
/// </summary>
internal static class Program
{
    private const int PayloadBytes = 8 * 1024 * 1024;

    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "all";
        if (mode is "all" or "parse")
            ParseThroughput();
        if (mode is "all" or "churn")
            LineChurn();
        if (mode is "all" or "pty")
        {
            if (OperatingSystem.IsWindows())
                PtyProbe();
            else
                Console.WriteLine("pty     skipped: ConPTY is Windows-only");
        }
        return 0;
    }

    /// <summary>W1 gate 3 and 4: escape-sequence throughput and the allocation it costs.</summary>
    private static void ParseThroughput()
    {
        var chunk = BuildShellOutputChunk(64 * 1024);
        Console.WriteLine($"parse   chunk={chunk.Length / 1024} KiB target={PayloadBytes / (1024 * 1024)} MiB");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var term = new Terminal(120, 40, scrollbackLines: 1000);

            // Warm the scrollback ring first: its rows are a one-time cost, and counting them against
            // per-MiB allocation would report a steady-state number that is really a startup number.
            term.Write(chunk);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            long total = 0;
            while (total < PayloadBytes)
            {
                term.Write(chunk);
                total += chunk.Length;
            }

            watch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var mibPerSecond = total / (1024.0 * 1024.0) / watch.Elapsed.TotalSeconds;
            Console.WriteLine(
                $"  {(attempt == 0 ? "warmup" : "run     ")} {total / (1024.0 * 1024.0):F1} MiB in {watch.Elapsed.TotalMilliseconds,7:F1} ms"
                + $" = {mibPerSecond,7:F1} MiB/s   alloc {allocated / 1024.0,6:F1} KiB"
                + $" ({allocated * 1024.0 / total:F2} KiB per MiB)");
        }

        Console.WriteLine($"  gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
    }

    /// <summary>
    /// W1 gate 1's shape without a live process: a line rewritten in place, which is what <c>yes</c> and a
    /// progress bar do. The only honest reading is "rows dirtied per frame", because a correct engine
    /// repaints one row and a wrong one repaints the screen.
    /// </summary>
    private static void LineChurn()
    {
        var term = new Terminal(120, 40);
        var line = new byte[120];
        Array.Fill(line, (byte)'x');
        var frame = new byte[121];
        frame[0] = 0x0D; // CR, then the whole width again
        line.AsSpan().CopyTo(frame.AsSpan(1));

        var watch = Stopwatch.StartNew();
        var rows = 0L;
        var iterations = 0;
        while (watch.Elapsed.TotalSeconds < 2)
        {
            term.Write(frame);
            term.Write("\x1b[2K"u8); // a progress bar's usual "clear, then redraw"
            rows += term.Damage.RowSpan(term.Rows);
            term.Damage.Consume();
            iterations++;
        }

        watch.Stop();
        Console.WriteLine("churn");
        Console.WriteLine($"  frames={iterations:N0} in {watch.Elapsed.TotalSeconds:F1} s = {iterations / watch.Elapsed.TotalSeconds:F0} frame/s");
        Console.WriteLine($"  rows dirtied per frame = {(double)rows / iterations:F2}");
    }

    /// <summary>
    /// What a live ConPTY hands back. Both the drained and the undrained shape are probed, because the
    /// first is what a terminal does and the second is what a careless host does — and they differ.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void PtyProbe()
    {
        (string Command, bool Drain)[] cases =
        [
            ("cmd.exe /c exit 7", false),
            ("cmd.exe /c exit 7", true),
            ("cmd.exe /c echo QUICKTERM", true),
            ("cmd.exe /c exit 7 & echo done", true),
        ];

        foreach (var (command, drain) in cases)
        {
            using var pty = new ConPtySession(80, 24, command);
            var exited = new ManualResetEventSlim(false);
            PtyExitInfo info = default;
            pty.Exited += code =>
            {
                info = code;
                exited.Set();
            };

            var bytes = 0L;
            var text = new StringBuilder();
            var reader = new Thread(() =>
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var read = pty.Read(buffer);
                    if (read < 0)
                        break;
                    bytes += read;
                    if (text.Length < 400)
                        text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            })
            {
                IsBackground = true,
            };

            if (drain)
                reader.Start();

            var signalled = exited.Wait(5000);
            if (drain)
                reader.Join(2000);

            Console.WriteLine("pty");
            Console.WriteLine(
                $"  {(drain ? "drained" : "silent ")} {command,-34} exited={signalled} code={info.ExitCode,11} (0x{unchecked((uint)info.ExitCode):X8})"
                + $" bytes={bytes}");
        }
    }

    /// <summary>Text, CRLF, the occasional SGR and a wrapped tail: the mix a build log is made of.</summary>
    private static byte[] BuildShellOutputChunk(int size)
    {
        var builder = new StringBuilder(size + 256);
        var row = 0;
        while (builder.Length < size)
        {
            builder.Append("\x1b[0m\x1b[K")
                .Append(row++ % 97)
                .Append(" abcdefghijklmnopqrstuvwxyz 0123456789 中文宽字符对齐")
                .Append("\r\n");
            if (row % 500 == 0)
                builder.Append("\x1b[1;38;5;196mERROR\x1b[0m something\r\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
