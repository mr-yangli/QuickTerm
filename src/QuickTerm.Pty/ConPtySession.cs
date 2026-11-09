namespace QuickTerm.Pty;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// A shell running under ConPTY (Windows 10 1809+). Two anonymous pipes carry the traffic, and
/// <c>CreatePseudoConsole</c> takes one end of each: the pseudo-console holds those handles for its own
/// lifetime, which is why they are closed here only on <see cref="Dispose"/> — that is also what makes
/// the blocked <see cref="Read"/> let go.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtySession : IPtySession
{
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly SafeHandle _inputWrite;
    private readonly SafeHandle _outputRead;
    private IntPtr _pseudoConsole;
    private IntPtr _process;
    private int _columns;
    private int _rows;
    private bool _disposed;

    public event Action<PtyExitInfo>? Exited;

    /// <summary>
    /// The default 4 KiB is smaller than one screenful of VT output (128x42 is ~5.4 KB of plain text before
    /// attributes), and a ConPTY server whose write does not fit its own pipe splits it and blocks waiting for
    /// the host to drain. Blocking on reads is not draining, so the server stalls and nothing reaches us —
    /// measured as bytes going in and zero coming back out. 64 KiB is a screenful of the widest thing a shell
    /// prints, with room for the escape sequences around it.
    /// </summary>
    private const int PipeBufferSize = 64 * 1024;

    public ConPtySession(int columns, int rows, string commandLine, string? workingDirectory = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        _columns = columns;
        _rows = rows;

        // Non-inheritable pipes, as the documented pseudoconsole sample has them (NULL security attributes).
        if (!Native.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, PipeBufferSize) ||
            !Native.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, PipeBufferSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");

        var size = new Native.COORD { X = (short)columns, Y = (short)rows };
        var status = Native.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pseudoConsole);
        if (status != 0)
            throw new Win32Exception(status, $"CreatePseudoConsole failed (0x{status:X8})");

        _pseudoConsole = pseudoConsole;
        _inputWrite = new SafeHandleUnmanaged(inputWrite);
        _outputRead = new SafeHandleUnmanaged(outputRead);
        _input = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: false), FileAccess.Write);
        _output = new FileStream(new SafeFileHandle(outputRead, ownsHandle: false), FileAccess.Read);

        StartProcess(commandLine, workingDirectory, pseudoConsole, inputRead, outputWrite);
    }

    private void StartProcess(string commandLine, string? workingDirectory, IntPtr pseudoConsole, IntPtr ptyInputRead, IntPtr ptyOutputWrite)
    {
        // First call with a null list only asks for the byte size; lpSizeList is a PSIZE_T, so the
        // out-parameter must be pointer-width (ref int would let the callee write past the stack slot).
        nuint attributeSize = 0;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
        var attributes = Marshal.AllocHGlobal((nint)attributeSize);
        var initialized = false;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "attribute list");
            initialized = true;

            // The pseudo-console goes into the list **by value**, the way the documented sample and Windows
            // Terminal both hand it over: lpValue is the HPCON itself, not the address of a variable holding it.
            // Passing `ref` here hands the kernel a managed address as if it were a console handle, and the
            // child then dies at console init with 0xC0000142 — see the ledger entry, which also records that
            // no test on this machine could have caught it, because none of them ever reached a child.
            if (!Native.UpdateProcThreadAttribute(
                    attributes, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "pseudo-console attribute");

            var startup = new Native.STARTUPINFOEXW();
            startup.StartupInfo.Length = Marshal.SizeOf<Native.STARTUPINFOEXW>();
            // Explicit, as Windows Terminal passes it: leaving lpDesktop null makes the child fail at
            // console init with 0xC0000142 when this process was not started from a desktop shell.
            var desktop = Marshal.StringToHGlobalUni("winsta0\\default");
            startup.StartupInfo.Desktop = desktop;
            startup.Attributes = attributes;

            var directory = string.IsNullOrEmpty(workingDirectory) ? IntPtr.Zero : Marshal.StringToHGlobalUni(workingDirectory);
            var command = Marshal.StringToHGlobalUni(commandLine);
            try
            {
                if (!Native.CreateProcessW(
                        IntPtr.Zero, command, IntPtr.Zero, IntPtr.Zero, inheritHandles: false,
                        Native.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, directory, ref startup, out var process))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"spawn {commandLine}");

                _process = process.Process;
                Native.CloseHandle(process.Thread);

                // The pseudo-console owns these two ends from here on. Holding our copies is what keeps
                // the output pipe from ever reaching EOF once conhost and the shell are both gone.
                Native.CloseHandle(ptyInputRead);
                Native.CloseHandle(ptyOutputWrite);
                ptyInputRead = ptyOutputWrite = IntPtr.Zero;
                WatchExit();
            }
            finally
            {
                if (directory != IntPtr.Zero)
                    Marshal.FreeHGlobal(directory);
                Marshal.FreeHGlobal(command);
                Marshal.FreeHGlobal(desktop);
            }
        }
        catch
        {
            Native.CloseHandle(ptyInputRead);
            Native.CloseHandle(ptyOutputWrite);
            DisposeCore();
            throw;
        }
        finally
        {
            // The list buffer's lifetime belongs here, after it is torn down — not in the catch, which only
            // ran on failure: every successful session start used to leak the block outright. Delete itself
            // only runs when initialization actually succeeded.
            if (initialized)
                Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private void WatchExit()
    {
        var process = _process;
        var watcher = new Thread(() =>
        {
            Native.WaitForSingleObject(process, Native.INFINITE);
            Native.GetExitCodeProcess(process, out var code);
            Exited?.Invoke(new PtyExitInfo(unchecked((int)code), 0));
        })
        {
            IsBackground = true,
            Name = "QuickTerm.pty.exit",
        };
        watcher.Start();
    }

    public int Columns => _columns;

    public int Rows => _rows;

    public bool HasExited => _process == IntPtr.Zero || Native.WaitForSingleObject(_process, 0) != Native.WAIT_TIMEOUT;

    public int Read(Span<byte> buffer)
    {
        try
        {
            var read = _output.Read(buffer);
            return read == 0 ? -1 : read;
        }
        catch (IOException)
        {
            return -1; // the pty went away mid-read: same end of stream as a clean EOF
        }
        catch (ObjectDisposedException)
        {
            return -1;
        }
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (HasExited)
            return;

        _input.Write(bytes);

        // FileStream buffers 4 KiB by default, and a keystroke is one byte: without this flush the shell
        // never sees what was typed — the host counts bytes "sent" while the pipe holds nothing. Measured as
        // exactly that: bytes going out, output stuck at the 225 bytes the shell printed at startup.
        _input.Flush();
    }

    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        _columns = columns;
        _rows = rows;
        if (_disposed)
            return;
        var size = new Native.COORD { X = (short)columns, Y = (short)rows };
        Native.ResizePseudoConsole(_pseudoConsole, size);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeCore();
    }

    private void DisposeCore()
    {
        // Order matters here. A host that is still blocked in Read would deadlock on the handle close,
        // because ClosePseudoConsole is what releases a pending read — so the child goes first, then the
        // pseudo-console, and only then the pipes.
        if (_process != IntPtr.Zero && !HasExited)
            Native.TerminateProcess(_process, 1);

        if (_pseudoConsole != IntPtr.Zero)
        {
            Native.ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        _input.Dispose();
        _output.Dispose();
        _inputWrite.Dispose();
        _outputRead.Dispose();

        if (_process != IntPtr.Zero)
        {
            Native.CloseHandle(_process);
            _process = IntPtr.Zero;
        }
    }

    private static class Native
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint INFINITE = 0xFFFFFFFF;
        public const uint WAIT_TIMEOUT = 0x00000102;
        public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

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
        public static extern bool CreatePipe(out IntPtr input, out IntPtr output, IntPtr attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr pseudoConsole, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr process, uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, uint count, uint reserved, ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(
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
    }

    private sealed class SafeHandleUnmanaged(IntPtr handle) : SafeHandle(handle, true)
    {
        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
}
