namespace QuickTerm.App;

using System;
using System.Diagnostics;

/// <summary>
/// RDP goes through the client Windows already ships (§6 item 7): we build the command line and hand it over.
/// No protocol, no COM control, no embedded desktop — the remote picture is the system client's business.
/// </summary>
internal static class RdpLauncher
{
    /// <summary>The default RDP port. Left out of the command line when it is the one in use, because
    /// <c>mstsc</c> writes <c>host:3389</c> into .rdp files with the port attached and a bare host is tidier.</summary>
    public const int DefaultPort = 3389;

    /// <summary>The arguments for one host — the half that can be checked without opening a window.</summary>
    public static string BuildArguments(HostProfile host, int defaultPort = DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(host);

        var address = host.Host.Trim();
        return host.Port > 0 && host.Port != defaultPort
            ? $"/v:{address}:{host.Port}"
            : $"/v:{address}";
    }

    /// <summary>
    /// Starts the system client. Returns false with a reason rather than throwing: a host with no address is a
    /// typo in a settings file, not a reason to take the terminal down.
    /// </summary>
    public static bool TryLaunch(HostProfile host, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(host.Host))
        {
            problem = "missing-host";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "mstsc.exe",
                Arguments = BuildArguments(host),
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            problem = ex.Message;
            return false;
        }
    }
}