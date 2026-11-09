namespace QuickTerm.App;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>How an SSH session authenticates. The transport is not wired yet; these fields are stored now so
/// hosts can be configured ahead of it (§6 item 7, P1).</summary>
public enum SshAuthKind
{
    Password,
    PrivateKey,
    Agent,
}

/// <summary>What a saved entry opens. RDP is here as a launcher, not as a protocol we speak (§6 item 7).</summary>
public enum SessionKind
{
    Local,
    Ssh,
    Telnet,
    Wsl,
    Rdp,
}

/// <summary>The terminal page of a host. Every field is nullable so "unset" means inherit, not "zero".</summary>
public sealed class TerminalOverrides
{
    public string? FontFamily { get; set; }

    public double? FontSize { get; set; }

    public int? ScrollbackLines { get; set; }
}

/// <summary>One row of the host tree.</summary>
public sealed class HostProfile
{
    public string Name { get; set; } = string.Empty;

    public SessionKind Kind { get; set; } = SessionKind.Local;

    /// <summary>The tree's folder. Empty is the top level; the same name may repeat.</summary>
    public string Group { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = string.Empty;

    /// <summary>SSH only: how to authenticate. Ignored by other kinds.</summary>
    public SshAuthKind AuthKind { get; set; } = SshAuthKind.Password;

    /// <summary>SSH only: path to the private key file. Null when <see cref="AuthKind" /> is not PrivateKey.</summary>
    public string? KeyPath { get; set; }

    public string Note { get; set; } = string.Empty;

    /// <summary>Local sessions only: the command line to run. Null uses the shell the OS points at.</summary>
    public string? Shell { get; set; }

    /// <summary>Local sessions only: where to start it. Null uses the process's own directory.</summary>
    public string? StartDirectory { get; set; }

    /// <summary>
    /// The password as a base64 DPAPI blob — never plaintext, and never written for <see cref="SessionKind.Rdp" />,
    /// whose client reads its own credential out of the store Windows already has.
    /// </summary>
    public string? Secret { get; set; }

    public TerminalOverrides? Terminal { get; set; }

    /// <summary>False for RDP: <c>mstsc</c> takes no password on its command line, so ours would never be used.</summary>
    public bool AllowsStoredSecret => Kind != SessionKind.Rdp;
}

/// <summary>The one global file. A host's own <see cref="HostProfile.Terminal" /> overrides this layer.</summary>
public sealed class QuickTermSettings
{
    /// <summary>Null follows the system; the shipped values are the two the family translates into.</summary>
    public string? Language { get; set; }

    public string FontFamily { get; set; } = "Cascadia Mono, Consolas, Microsoft JhengHei Mono, monospace";

    public double FontSize { get; set; } = 14;

    public int ScrollbackLines { get; set; } = 5000;

    /// <summary>Null uses the shell the OS points at (<c>COMSPEC</c> / <c>SHELL</c>).</summary>
    public string? Shell { get; set; }

    public string? StartDirectory { get; set; }

    /// <summary>Full-window background image; null or missing means the plain theme floor.</summary>
    public string? BackgroundImagePath { get; set; }

    /// <summary>uniform (fit) / uniformToFill (fill) / fill (stretch) / tile.</summary>
    public string BackgroundMode { get; set; } = "uniformToFill";

    /// <summary>Light scrim opacity over the wallpaper for the whole window, percent (0 = image as-is; higher fades
    /// it lighter). ~20 keeps the picture clearly one piece; raise it when a dark image makes text hard to read.</summary>
    public int BackgroundOpacity { get; set; } = 55;
}

/// <summary>
/// The two files behind the host tree: <c>settings.json</c> and <c>hosts.json</c> under
/// <c>%APPDATA%\QuickTerm</c>. Source-generated serialisation, because reflection-based JSON is the one thing
/// this family has measured as AOT-hostile, and a config store is exactly the code that has to survive trimming.
/// </summary>
/// <remarks>
/// Loading never throws. A hand-edited file with a stray comma is a one-line diagnostic on screen
/// (<see cref="LoadProblem" />) and a working default session — bricking the terminal on its own settings file
/// is the failure mode users cannot fix.
///
/// Secrets go through DPAPI in the current user's scope. The cost is stated plainly in the plan: copy the file
/// to another machine and the passwords do not travel. That is the deal being made against writing plaintext.
/// </remarks>
public sealed partial class QuickTermStore
{
    public const string SettingsFile = "settings.json";

    public const string HostsFile = "hosts.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },

        // Written camelCase and read either way, because this file is meant to be hand-edited: with the
        // defaults a lowercase `"name"` parsed to an empty profile and nothing said so.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private QuickTermStore(string directory, QuickTermSettings settings, List<HostProfile> hosts, string? problem)
    {
        RootDirectory = directory;
        Settings = settings;
        Hosts = hosts;
        LoadProblem = problem;
    }

    public string RootDirectory { get; }

    public QuickTermSettings Settings { get; private set; }

    public List<HostProfile> Hosts { get; private set; }

    /// <summary>Set when a file existed but could not be read. Nothing else about this type changes.</summary>
    public string? LoadProblem { get; }

    /// <summary>
    /// Reads both files, creating the directory on demand. A missing file is not a problem — that is the first
    /// run. A present-but-unreadable one is: the defaults come back and <see cref="LoadProblem" /> says which.
    /// </summary>
    public static QuickTermStore Load(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickTerm");
        var problem = (string?)null;

        var settings = new QuickTermSettings();
        var settingsPath = Path.Combine(directory, SettingsFile);
        if (File.Exists(settingsPath))
        {
            try
            {
                settings = JsonSerializer.Deserialize(File.ReadAllText(settingsPath), Context.QuickTermSettings)
                    ?? new QuickTermSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                problem = SettingsFile;
                settings = new QuickTermSettings();
            }
        }

        var hosts = new List<HostProfile>();
        var hostsPath = Path.Combine(directory, HostsFile);
        if (File.Exists(hostsPath))
        {
            try
            {
                hosts = JsonSerializer.Deserialize(File.ReadAllText(hostsPath), Context.ListHostProfile)
                    ?? new List<HostProfile>();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                problem = problem is null ? HostsFile : problem + " 与 " + HostsFile;
                hosts = new List<HostProfile>();
            }
        }

        return new QuickTermStore(directory, settings, hosts, problem);
    }

    /// <summary>Writes both files through a temp name, so a crash mid-write cannot leave a half file behind.</summary>
    public void Save()
    {
        System.IO.Directory.CreateDirectory(RootDirectory);
        WriteAtomically(Path.Combine(RootDirectory, SettingsFile),
            JsonSerializer.Serialize(Settings, Context.QuickTermSettings));
        WriteAtomically(Path.Combine(RootDirectory, HostsFile),
            JsonSerializer.Serialize(Hosts, Context.ListHostProfile));
    }

    /// <summary>Drops secrets from profiles that cannot use them, before anything reaches the disk.</summary>
    public void ForgetUnusableSecrets()
    {
        foreach (var host in Hosts)
        {
            if (!host.AllowsStoredSecret)
                host.Secret = null;
        }
    }

    /// <summary>
    /// Global settings with one host's terminal page stacked on top. A null field means inherit — that is
    /// the whole reason <see cref="TerminalOverrides" /> is nullable. This is the only merger: without it the
    /// editor collected a host's font size and scrollback and nothing ever read them back.
    /// </summary>
    public static QuickTermSettings EffectiveTerminal(QuickTermSettings global, HostProfile? profile)
    {
        var page = profile?.Terminal;
        if (page is null)
            return global;

        return new QuickTermSettings
        {
            Language = global.Language,
            FontFamily = page.FontFamily ?? global.FontFamily,
            FontSize = page.FontSize ?? global.FontSize,
            ScrollbackLines = page.ScrollbackLines ?? global.ScrollbackLines,
            Shell = global.Shell,
            StartDirectory = global.StartDirectory,
        };
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>DPAPI in the current user's scope, base64. Empty input returns empty, so "no password" stays unset.</summary>
    public static unsafe string Protect(string plaintext)
    {
        if (plaintext.Length == 0)
            return string.Empty;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "QuickTerm has no secret store on this platform yet — a keyring path is still unmade "
                + "(see term/docs/quickterm-p0-plan.md §6 item 6), and plaintext on disk is not the fallback.");

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        var input = new DataBlob { CbData = bytes.Length, PbData = buffer };

        try
        {
            if (!Native.CryptProtectData(ref input, Description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectData failed");

            try
            {
                return Convert.ToBase64String(new Span<byte>(output.PbData.ToPointer(), output.CbData));
            }
            finally
            {
                Native.LocalFree(output.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The other half. Null when the blob is not ours (another machine, another user, or a truncated file).</summary>
    public static unsafe string? Unprotect(string protectedBase64)
    {
        if (protectedBase64.Length == 0)
            return string.Empty;
        if (!OperatingSystem.IsWindows())
            return null;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        var input = new DataBlob { CbData = bytes.Length, PbData = buffer };

        try
        {
            if (!Native.CryptUnprotectData(ref input, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output))
                return null; // cannot decrypt: this is the "copied to another machine" case, not a crash

            try
            {
                return Encoding.UTF8.GetString(new Span<byte>(output.PbData.ToPointer(), output.CbData));
            }
            finally
            {
                Native.LocalFree(output.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The generated serializers, built with the options above — without them a hand-edited
    /// trailing comma is a parse error and every enum is written as a number, both of which this book caught.
    /// </summary>
    private static readonly QuickTermJsonContext Context = new(ReadOptions);

    private const string Description = "QuickTerm";
    private const uint UiForbidden = 0x0000_0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    private static partial class Native
    {
        [LibraryImport("crypt32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CryptProtectData(
            ref DataBlob data, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob encrypted);

        [LibraryImport("crypt32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CryptUnprotectData(
            ref DataBlob encrypted, out string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob data);

        [LibraryImport("kernel32.dll")]
        public static partial IntPtr LocalFree(IntPtr handle);
    }
}

/// <summary>Source-generated JSON metadata: the two shapes the store writes, and nothing else.</summary>
[JsonSerializable(typeof(QuickTermSettings))]
[JsonSerializable(typeof(List<HostProfile>))]
public partial class QuickTermJsonContext : JsonSerializerContext;
