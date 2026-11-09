namespace QuickTerm.Tests;

using System;
using QuickTerm.App;

/// <summary>
/// The one credential-store use in this family: <c>mstsc</c> reads <c>TERMSRV/&lt;host&gt;</c> from it, so that is
/// where an RDP password has to be written. The book checks the round trip against the real store — with a
/// throwaway host name, and it deletes what it wrote.
/// </summary>
public sealed class CredentialStoreTests
{
    [Fact]
    public void A_Credential_Round_Trips_And_Is_Removed()
    {
        var target = WindowsCredentialStore.TargetFor("quickterm-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            Assert.True(WindowsCredentialStore.TryWrite(target, "tester", "p@ss 中文", out var problem), problem);
            Assert.Equal("p@ss 中文", WindowsCredentialStore.Read(target));

            Assert.True(WindowsCredentialStore.TryDelete(target, out var deleteProblem), deleteProblem);
            Assert.Null(WindowsCredentialStore.Read(target));
        }
        finally
        {
            WindowsCredentialStore.TryDelete(target, out _);
        }
    }

    [Fact]
    public void Writing_Twice_Replaces_Instead_Of_Failing()
    {
        // Editing a host and typing a new password must overwrite the old one, not trip over it.
        var target = WindowsCredentialStore.TargetFor("quickterm-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            Assert.True(WindowsCredentialStore.TryWrite(target, "tester", "first", out _));
            Assert.True(WindowsCredentialStore.TryWrite(target, "tester", "second", out _));
            Assert.Equal("second", WindowsCredentialStore.Read(target));
        }
        finally
        {
            WindowsCredentialStore.TryDelete(target, out _);
        }
    }

    [Fact]
    public void The_Target_Is_The_Name_The_Rdp_Client_Looks_For()
    {
        Assert.Equal("TERMSRV/10.0.0.8", WindowsCredentialStore.TargetFor("  10.0.0.8 "));
    }
}