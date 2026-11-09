namespace QuickTerm.App;

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// The Windows credential store, for the one case that needs it: <c>mstsc</c> takes no password on its command
/// line, and the place it looks instead is a generic credential named <c>TERMSRV/&lt;host&gt;</c>.
/// </summary>
/// <remarks>
/// This is not a second choice of password storage for our own files — those use DPAPI (§6 item 6). It exists
/// because the RDP client owns that lookup, and writing the same place it reads is the only way to connect
/// without a prompt. Nothing here is written to <c>hosts.json</c>.
/// </remarks>
internal static partial class WindowsCredentialStore
{
    /// <summary>The target the Remote Desktop client reads for a host.</summary>
    public static string TargetFor(string host) => "TERMSRV/" + host.Trim();

    /// <summary>Stores or replaces one generic credential. False with a reason rather than an exception.</summary>
    public static bool TryWrite(string target, string user, string password, out string? problem)
    {
        problem = null;
        if (!OperatingSystem.IsWindows())
        {
            problem = "not-windows";
            return false;
        }

        var name = Marshal.StringToHGlobalUni(target);
        var account = Marshal.StringToHGlobalUni(user);
        var secret = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocHGlobal(Math.Max(1, secret.Length));

        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = name,
                CredentialBlobSize = (uint)secret.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = account,
            };

            if (!Native.CredWriteW(ref credential, 0))
            {
                problem = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(account);
            Marshal.FreeHGlobal(blob);
        }
    }

    /// <summary>Reads one back — used by the book to prove the round trip without needing mstsc.</summary>
    public static string? Read(string target)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        if (!Native.CredReadW(target, CredTypeGeneric, 0, out var handle) || handle == IntPtr.Zero)
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return string.Empty;

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            Native.CredFree(handle);
        }
    }

    /// <summary>Removes it. A missing credential is a success, not an error.</summary>
    public static bool TryDelete(string target, out string? problem)
    {
        problem = null;
        if (!OperatingSystem.IsWindows())
        {
            problem = "not-windows";
            return false;
        }

        return Native.CredDeleteW(target, CredTypeGeneric, 0) || Marshal.GetLastWin32Error() == ErrorNotFound;
    }

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten; // FILETIME is two DWORDs; a long keeps the layout and stays blittable
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private static partial class Native
    {
        [LibraryImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredWriteW(ref Credential credential, uint flags);

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredDeleteW(string target, uint type, uint flags);

        [LibraryImport("advapi32.dll")]
        public static partial void CredFree(IntPtr buffer);
    }
}