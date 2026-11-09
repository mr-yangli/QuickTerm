namespace QuickTerm.Tests;

using QuickTerm.App;

/// <summary>
/// RDP is a launcher (§6 item 7), so the only part that can be checked without a desktop is the command line it
/// builds. That is still worth pinning: a wrong argument string opens a session that looks like "RDP is broken".
/// </summary>
public sealed class RdpLauncherTests
{
    private static HostProfile Host(string address, int port = RdpLauncher.DefaultPort) => new()
    {
        Name = "desktop",
        Kind = SessionKind.Rdp,
        Host = address,
        Port = port,
    };

    [Fact]
    public void The_Default_Port_Is_Left_Out()
    {
        Assert.Equal("/v:10.0.0.8", RdpLauncher.BuildArguments(Host("10.0.0.8")));
    }

    [Fact]
    public void Another_Port_Is_Attached_To_The_Host()
    {
        Assert.Equal("/v:10.0.0.8:3390", RdpLauncher.BuildArguments(Host("10.0.0.8", 3390)));
    }

    [Fact]
    public void A_Missing_Port_Is_Treated_As_The_Default()
    {
        Assert.Equal("/v:desk.example.com", RdpLauncher.BuildArguments(Host("desk.example.com", 0)));
        Assert.Equal("/v:desk.example.com", RdpLauncher.BuildArguments(Host("desk.example.com", -1)));
    }

    [Fact]
    public void Surrounding_Spaces_In_A_Hand_Edited_File_Are_Dropped()
    {
        Assert.Equal("/v:10.0.0.8", RdpLauncher.BuildArguments(Host("  10.0.0.8  ")));
    }

    [Fact]
    public void An_Empty_Address_Refuses_Instead_Of_Launching()
    {
        Assert.False(RdpLauncher.TryLaunch(Host("   "), out var problem));
        Assert.Equal("missing-host", problem);
    }
}