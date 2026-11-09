namespace QuickTerm.Tests;

using System;
using System.Threading.Tasks;
using QuickTerm.App;
using Xunit;

/// <summary>
/// The SSH transport's offline edges. A real login needs a live server and the user's own credentials, so the
/// book only pins what can be proven without one: the session honours the pty contract and a refused connect
/// surfaces as an exception the host can turn into a notice, never a hang or a crash.
/// </summary>
public sealed class SshSessionTests
{
    [Fact]
    public async Task A_Refused_Connect_Throws_Rather_Than_Hanging()
    {
        var profile = new HostProfile
        {
            Kind = SessionKind.Ssh,
            Host = "127.0.0.1",
            Port = 1, // tcpmux: nothing listens here on the box
            User = "nobody",
            AuthKind = SshAuthKind.Password,
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            SshSession.ConnectAsync(profile, "sentinel", 80, 24, trustHostKey: null));
    }

    [Fact]
    public void SshSession_Is_An_IPtySession()
    {
        // The whole point of the adapter: the terminal's reader/resize/exit path must accept it unchanged.
        Assert.True(typeof(QuickTerm.Pty.IPtySession).IsAssignableFrom(typeof(SshSession)));
    }
}