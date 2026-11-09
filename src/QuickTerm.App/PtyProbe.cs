namespace QuickTerm.App;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using QuickTerm.Pty;

/// <summary>
/// <c>QuickTerm --pty-probe</c>: spawns a shell under ConPTY several ways and writes what each one did to
/// <c>%TEMP%\quickterm-pty-probe.log</c>. It exists because the failure it diagnoses is context-dependent and
/// invisible in a window: a child that dies at DLL init reports an NTSTATUS on the grid, nothing else.
/// </summary>
/// <remarks>
/// Written to a file on purpose — this runs as a windows-subsystem exe, so there is no console to read from,
/// and launching it from a piped shell reproduces exactly the parent shape a desktop double-click has.
///
/// A row that never prints is itself a reading: it means <c>CreateProcessW</c> stalled in the kernel's console
/// handshake for that shape. On this machine the piped-shell parent does that even for the no-pty control row,
/// which is why the probe exists to be run by whoever has a desktop to compare against — the shape rows only
/// mean anything next to the control row.
///
/// The first row calls the real <see cref="PtySession" /> so the log starts with the shipping path's verdict;
/// the rows after it re-implement the raw sequence with one knob changed each. The duplication is deliberate:
/// a spawn shape that varies needs somewhere to vary, and that somewhere must not become a switch inside the
/// kernel the shipping path pays for.
/// </remarks>
internal static class PtyProbe
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint UseStdHandles = 0x00000100;
    private const uint WaitTimeout = 0x00000102;
    private const int PseudoConsoleAttribute = 0x00020016;
    private const int WaitMs = 2500;

    private readonly record struct Shape(
        string Name,
        string? Desktop,
        bool StdHandles,
        uint ExtraFlags,
        string Command,
        bool Pty = true,
        bool AttributeByValue = false,
        bool InheritablePipes = true);

    public static int Run(string[] args)
    {
        var lines = new List<string>();
        void Log(string line)
        {
            lines.Add(line);
            Console.WriteLine(line);
        }

        Log($"probe {DateTime.Now:O} pid={Environment.ProcessId} exe={Environment.ProcessPath}");
        Log($"parent console window : {(Native.GetConsoleWindow() == IntPtr.Zero ? "none (windows-subsystem parent — the desktop shape)" : "present (console parent — the dotnet-test shape)")}");
        Log($"window station        : {WindowStationName()}");
        Log($"cwd                   : {Environment.CurrentDirectory}");

        Log($"real path             : {ProbeRealPath()}");

        // One shape per process: a shape that stalls (lpDesktop null does, inside CreateProcessW) would
        // otherwise take every later row down with it, and the row that matters is the one that never prints.
        var only = args.Length > 1 && int.TryParse(args[1], out var index) ? index : -1;
        var number = 0;
        foreach (var shape in Shapes())
        {
            if (only < 0 || only == number)
                Log($"shape {number,-2} {shape.Name,-18} : {ProbeShape(shape)}");
            number++;
        }

        var logPath = Path.Combine(Path.GetTempPath(), "quickterm-pty-probe.log");
        try
        {
            File.WriteAllLines(logPath, lines);
            Log($"log written to {logPath}");
        }
        catch (Exception ex)
        {
            Log($"log write failed: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// <c>QuickTerm --pty-capture [cols] [rows] [ms] [command]</c>: runs a session at one exact size and
    /// writes every byte the pty delivered, undecoded, to <c>%TEMP%\quickterm-pty-capture.bin</c>.
    /// </summary>
    /// <remarks>
    /// This is the only way to tell "the engine wrapped a line" from "the shell sent two lines" without a
    /// second pair of eyes on a screenshot: <see cref="Replay" /> feeds the file back through the engine and
    /// prints what the grid actually holds. The size is an argument because the wrap question is a size
    /// question — the same banner is one line at 80 columns and two at 40.
    ///
    /// A command is accepted because the shipping path's verdict depends on the parent context: launched from
    /// a piped shell, an interactive cmd writes its banner to the *inherited* stdout and exits at once, so the
    /// pty pipe reports 16 handshake bytes and nothing else. A marker string says which of the two happened.
    /// </remarks>
    public static int Capture(string[] args)
    {
        var columns = Number(args, 1, 50);
        var rows = Number(args, 2, 10);
        var waitMs = Number(args, 3, 2500);
        var command = args.Length > 4 ? string.Join(' ', args, 4, args.Length - 4) : null;
        var path = Path.Combine(Path.GetTempPath(), "quickterm-pty-capture.bin");
        var logPath = Path.Combine(Path.GetTempPath(), "quickterm-pty-capture.log");

        var captured = new MemoryStream();
        var end = new ManualResetEventSlim(false);
        var sent = string.Empty;
        var failure = string.Empty;

        // A probe that dies without a line in its log is the failure mode this family has already been bitten
        // by twice, so everything below is wrapped and the log is written whatever happens.
        try
        {
            // Same tap the running app uses, so the file format and the directory choice are one thing and not two.
            ByteTap.Open();
            using var session = PtySession.Start(columns, rows, command);

            // QUICKTERM_SEND is base64 of the bytes to type, so a complaint about what Backspace does can be
            // reproduced here rather than argued from a screenshot. Quoted arguments through a piped shell are
            // how control characters get mangled, and control characters are the whole question.
            //
            // Chunks separated by ';' go out with a gap between them. That is not decoration: ConPTY replays
            // the console's screen, so typing and deleting inside one refresh arrive here as the net change
            // only — a burst shows the erase and never the characters it erased, which is not what a hand does.
            var send = Environment.GetEnvironmentVariable("QUICKTERM_SEND");
            if (!string.IsNullOrEmpty(send))
            {
                var chunks = send.Split(';', StringSplitOptions.RemoveEmptyEntries);
                for (var chunk = 0; chunk < chunks.Length; chunk++)
                {
                    // Let the shell print its banner first: input that arrives before cmd has taken the line
                    // editor up is a different situation from the one being reproduced.
                    Thread.Sleep(chunk == 0 ? 500 : 400);
                    var typed = Convert.FromBase64String(chunks[chunk]);
                    session.Write(typed);
                    ByteTap.PtyInput(typed);
                    sent += $" sent{chunk}={typed.Length}[{Describe(typed)}]";
                }
            }

            var reader = new Thread(() =>
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = session.Read(buffer);
                    if (read <= 0)
                        break;
                    ByteTap.PtyOutput(buffer.AsSpan(0, read));
                    lock (captured)
                        captured.Write(buffer, 0, read);
                }

                end.Set();
            })
            {
                IsBackground = true,
                Name = "QuickTerm.capture.read",
            };
            reader.Start();

            Thread.Sleep(waitMs);
            File.WriteAllBytes(path, captured.ToArray());
            File.WriteAllText(logPath, $"capture {DateTime.Now:O} {columns}x{rows} wait={waitMs}ms cmd={command ?? "(default shell)"}"
                + $"{sent} bytes={captured.Length} eof={end.IsSet} exited={session.HasExited}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllBytes(path, captured.ToArray());
            File.WriteAllText(logPath, $"capture threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            failure = " (see the log)";
        }
        finally
        {
            ByteTap.Close();
        }

        return failure.Length == 0 ? 0 : 1;
    }

    private static int Number(string[] args, int index, int fallback)
        => args.Length > index && int.TryParse(args[index], out var value) && value > 0 ? value : fallback;

    private static IEnumerable<Shape> Shapes()
    {
        // The three rows that matter first: the shipping shape, then the same call with the pseudo-console
        // handed over by value (what the docs and Windows Terminal both do), then that plus non-inheritable
        // pipes (the docs pass NULL security attributes). One of these three is expected to change the child's
        // exit code; the rest of the rows are the older questions.
        yield return new Shape("attr by ref", "winsta0\\default", false, 0, "cmd.exe /c echo probe");
        yield return new Shape("attr by value", "winsta0\\default", false, 0, "cmd.exe /c echo probe", AttributeByValue: true);
        yield return new Shape("by value + no inherit", "winsta0\\default", false, 0, "cmd.exe /c echo probe", AttributeByValue: true, InheritablePipes: false);
        yield return new Shape("by ref + no inherit", "winsta0\\default", false, 0, "cmd.exe /c echo probe", InheritablePipes: false);
        yield return new Shape("desktop null", null, false, 0, "cmd.exe /c echo probe");
        yield return new Shape("desktop canonical", "WinSta0\\Default", false, 0, "cmd.exe /c echo probe");
        yield return new Shape("desktop + stdhdl", "winsta0\\default", true, 0, "cmd.exe /c echo probe");
        yield return new Shape("new console too", "winsta0\\default", false, 0x00000010, "cmd.exe /c echo probe");
        yield return new Shape("gui child", "winsta0\\default", false, 0, "notepad.exe");
        yield return new Shape("interactive shell", "winsta0\\default", false, 0, "cmd.exe");
        // The control row: no pseudo-console at all, just redirected pipes. If this child also dies at DLL
        // init, then the parent context cannot host console children, and nothing above is evidence about a
        // desktop launch.
        yield return new Shape("no pty (redirect)", "winsta0\\default", true, 0, "cmd.exe /c echo probe", Pty: false);
    }

    /// <summary>What the shipping code does in this parent context, with its own exit code and byte count.</summary>
    private static string ProbeRealPath()
    {
        try
        {
            using var session = PtySession.Start(120, 30);
            var exitCode = "still running";
            session.Exited += info => exitCode = $"0x{unchecked((uint)info.ExitCode):X8}";

            var buffer = new byte[4096];
            var total = 0;
            var head = string.Empty;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < WaitMs && !session.HasExited)
            {
                var read = session.Read(buffer);
                if (read <= 0)
                    break;
                if (total == 0)
                    head = Describe(buffer.AsSpan(0, read));
                total += read;
            }

            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 500 && session.HasExited == false)
                Thread.Sleep(20);

            return $"bytes={total} head={head} exited={session.HasExited} code={exitCode}";
        }
        catch (Exception ex)
        {
            return $"threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string ProbeShape(Shape shape)
    {
        var stages = new List<string>();
        void Stage(string stage)
        {
            stages.Add(stage);
            Console.WriteLine($"    {shape.Name} :: {stage}");
        }

        string Report(string result) => $"{result} [{string.Join(" -> ", stages)}]";

        var inputRead = IntPtr.Zero;
        var inputWrite = IntPtr.Zero;
        var outputRead = IntPtr.Zero;
        var outputWrite = IntPtr.Zero;
        var pseudoConsole = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var desktop = IntPtr.Zero;
        var command = IntPtr.Zero;
        var process = IntPtr.Zero;

        try
        {
            var security = new Native.SECURITY_ATTRIBUTES
            {
                Length = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
                InheritHandle = true,
            };

            var pipesOk = shape.InheritablePipes
                ? Native.CreatePipe(out inputRead, out inputWrite, ref security, 0) &&
                  Native.CreatePipe(out outputRead, out outputWrite, ref security, 0)
                : Native.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) &&
                  Native.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0);
            if (!pipesOk)
                return Report($"CreatePipe failed: {Win32()}");

            var size = new Native.COORD { X = 120, Y = 30 };
            var status = shape.Pty
                ? Native.CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole)
                : 0;
            if (status != 0)
                return Report($"CreatePseudoConsole 0x{status:X8}");

            var startup = new Native.STARTUPINFOEXW();
            startup.StartupInfo.Length = shape.Pty
                ? Marshal.SizeOf<Native.STARTUPINFOEXW>()
                : Marshal.SizeOf<Native.STARTUPINFOW>();

            if (shape.Desktop is not null)
                startup.StartupInfo.Desktop = desktop = Marshal.StringToHGlobalUni(shape.Desktop);

            if (shape.StdHandles || !shape.Pty)
            {
                startup.StartupInfo.Flags = (int)UseStdHandles;
                startup.StartupInfo.StandardInput = inputRead;
                startup.StartupInfo.StandardOutput = outputWrite;
                startup.StartupInfo.StandardError = outputWrite;
            }

            if (shape.Pty)
            {
                nuint attributeSize = 0;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
                attributes = Marshal.AllocHGlobal((nint)attributeSize);
                if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize))
                    return Report($"InitializeProcThreadAttributeList failed: {Win32()}");

                var pty = pseudoConsole;
                var attributeOk = shape.AttributeByValue
                    ? Native.UpdateProcThreadAttributeByValue(attributes, 0, PseudoConsoleAttribute, pty, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)
                    : Native.UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, ref pty, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);
                if (!attributeOk)
                    return Report($"UpdateProcThreadAttribute failed: {Win32()}");

                startup.Attributes = attributes;
            }

            command = Marshal.StringToHGlobalUni(shape.Command);
            Stage("ready");

            if (!Native.CreateProcessW(
                    IntPtr.Zero, command, IntPtr.Zero, IntPtr.Zero, shape.StdHandles || !shape.Pty,
                    (shape.Pty ? ExtendedStartupInfoPresent : 0) | shape.ExtraFlags, IntPtr.Zero, IntPtr.Zero, ref startup, out var info))
                return Report($"CreateProcess failed: {Win32()}");

            process = info.Process;
            Native.CloseHandle(info.Thread);
            Stage("spawned");

            // Bounded wait first, then one non-blocking drain: a pump loop here would itself be a thing to
            // diagnose, and the question this probe answers is about the spawn shape, not about reading.
            var waited = Native.WaitForSingleObject(process, WaitMs);
            Stage($"wait={waited}");

            var bytes = new MemoryStream();
            if (Native.PeekNamedPipe(outputRead, IntPtr.Zero, 0, out var available, IntPtr.Zero, IntPtr.Zero) && available > 0)
            {
                var chunk = new byte[Math.Min(available, 64 * 1024)];
                Native.ReadFile(outputRead, chunk, chunk.Length, out var got, IntPtr.Zero);
                bytes.Write(chunk, 0, got);
            }

            Native.GetExitCodeProcess(process, out var code);
            var result = $"bytes={bytes.Length} head={Describe(bytes.ToArray())} exited={waited != WaitTimeout} code=0x{code:X8}";
            Stage(result);
            return Report(result);
        }
        catch (Exception ex)
        {
            return Report($"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Stage("cleanup");
            if (process != IntPtr.Zero)
                Native.CloseHandle(process);
            if (command != IntPtr.Zero)
                Marshal.FreeHGlobal(command);
            if (desktop != IntPtr.Zero)
                Marshal.FreeHGlobal(desktop);
            if (attributes != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (pseudoConsole != IntPtr.Zero)
                Native.ClosePseudoConsole(pseudoConsole);
            foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite })
            {
                if (handle != IntPtr.Zero)
                    Native.CloseHandle(handle);
            }
        }
    }

    private static string Describe(byte[] bytes) => Describe(bytes.AsSpan());

    private static string Describe(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder();
        foreach (var value in bytes)
        {
            if (builder.Length >= 48)
                break;
            builder.Append(value switch
            {
                0x1b => "ESC",
                0x0d => "CR",
                0x0a => "LF",
                >= 0x20 and <= 0x7e => ((char)value).ToString(),
                _ => $"<{value:X2}>",
            });
            builder.Append(' ');
        }

        return builder.ToString().Trim();
    }

    private static string Win32() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    private static string WindowStationName()
    {
        var station = Native.GetProcessWindowStation();
        if (station == IntPtr.Zero)
            return "unknown";

        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            return Native.GetUserObjectInformationW(station, 2, buffer, 512, out _)
                ? Marshal.PtrToStringUni(buffer) ?? "unknown"
                : $"unknown ({Win32()})";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOW
        {
            public int Length;
            public IntPtr Reserved;
            public IntPtr Desktop;
            public IntPtr Title;
            public int X;
            public int Y;
            public int SizeX;
            public int SizeY;
            public int CountCharsX;
            public int CountCharsY;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2;
            public IntPtr Reserved3;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEXW
        {
            public STARTUPINFOW StartupInfo;
            public IntPtr Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr input, out IntPtr output, ref SECURITY_ATTRIBUTES attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr input, out IntPtr output, IntPtr attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, uint count, uint reserved, ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr list, uint reserved, IntPtr attribute, ref IntPtr value, nuint size, IntPtr returned, IntPtr reserved2);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttributeByValue(
            IntPtr list, uint reserved, IntPtr attribute, IntPtr value, nuint size, IntPtr returned, IntPtr reserved2);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(
            IntPtr applicationName,
            IntPtr commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            IntPtr currentDirectory,
            ref STARTUPINFOEXW startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr handle, byte[] buffer, int count, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool PeekNamedPipe(IntPtr handle, IntPtr buffer, int size, out int available, IntPtr messages, IntPtr leftover);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr handle, out uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetUserObjectInformationW(IntPtr handle, int index, IntPtr info, int length, out int needed);
    }
}
