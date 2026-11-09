namespace QuickTerm.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using QuickTerm.App;

/// <summary>
/// The two files behind the host tree. Everything here is checked against a throwaway directory — the real
/// <c>%APPDATA%\QuickTerm</c> is never touched by the book.
/// </summary>
public sealed class StoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quickterm-store-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Writes into the throwaway directory, creating it first — `File.WriteAllText` does not create
    /// directories, and a test that fails on a missing folder says nothing about the parser it was checking.
    /// </summary>
    private void WriteFile(string name, string text)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, name), text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void First_Run_Has_No_Files_And_No_Problem()
    {
        var store = QuickTermStore.Load(_root);

        Assert.Null(store.LoadProblem);
        Assert.Equal(5000, store.Settings.ScrollbackLines);
        Assert.Empty(store.Hosts);
        Assert.False(File.Exists(Path.Combine(_root, QuickTermStore.HostsFile)));
    }

    [Fact]
    public void Terminal_Overrides_Stack_Over_Global_And_Null_Means_Inherit()
    {
        var global = new QuickTermSettings { FontFamily = "Global", FontSize = 16, ScrollbackLines = 2000 };
        var host = new HostProfile { Name = "h", Terminal = new TerminalOverrides { FontSize = 20 } };

        var effective = QuickTermStore.EffectiveTerminal(global, host);

        Assert.Equal(20d, effective.FontSize);          // the host's own value wins
        Assert.Equal(2000, effective.ScrollbackLines);  // unset means inherit, not zero
        Assert.Equal("Global", effective.FontFamily);
        Assert.Equal(16d, global.FontSize);             // the global layer itself is left alone

        // A host without a Terminal page opens on the global settings as-is.
        Assert.Same(global, QuickTermStore.EffectiveTerminal(global, new HostProfile { Name = "h" }));
    }

    [Fact]
    public void Round_Trip_Keeps_Every_Field()
    {
        var store = QuickTermStore.Load(_root);
        store.Settings.FontSize = 16;
        store.Settings.Language = "zh-Hans";
        store.Settings.Shell = "pwsh.exe";
        store.Hosts.Add(new HostProfile
        {
            Name = "跳板机",
            Kind = SessionKind.Ssh,
            Group = "内部",
            Host = "10.0.0.8",
            Port = 2222,
            User = "ops",
            Note = "只在办公网可达",
            Terminal = new TerminalOverrides { FontSize = 12, ScrollbackLines = 100 },
        });
        store.Hosts.Add(new HostProfile
        {
            Name = "pwsh here",
            Kind = SessionKind.Local,
            Shell = @"C:\Program Files\PowerShell\7\pwsh.exe",
            StartDirectory = @"E:\Resource\QuickNet",
        });
        store.Save();

        var again = QuickTermStore.Load(_root);

        Assert.Equal(16, again.Settings.FontSize);
        Assert.Equal("zh-Hans", again.Settings.Language);
        Assert.Equal(2, again.Hosts.Count);
        var host = again.Hosts[0];
        Assert.Equal("跳板机", host.Name);
        Assert.Equal(SessionKind.Ssh, host.Kind);
        Assert.Equal("内部", host.Group);
        Assert.Equal(2222, host.Port);
        Assert.Equal("只在办公网可达", host.Note);
        Assert.Equal(12, host.Terminal!.FontSize);
        Assert.Null(host.Terminal.FontFamily); // unset stays unset — inherit is not zero

        // The Local page's two fields: a null shell means "the OS default", so it has to survive as null
        // rather than being written back as an empty string.
        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", again.Hosts[1].Shell);
        Assert.Equal(@"E:\Resource\QuickNet", again.Hosts[1].StartDirectory);
        Assert.Null(host.Shell);
        Assert.Null(host.StartDirectory);
    }

    [Fact]
    public void Both_Files_Land_And_No_Temp_Is_Left()
    {
        var store = QuickTermStore.Load(_root);
        store.Hosts.Add(new HostProfile { Name = "web" });
        store.Save();

        Assert.True(File.Exists(Path.Combine(_root, QuickTermStore.SettingsFile)));
        Assert.True(File.Exists(Path.Combine(_root, QuickTermStore.HostsFile)));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void A_Hand_Edited_File_With_A_Stray_Comma_Still_Loads()
    {
        // People edit this file. A trailing comma is the most common hand edit and must not brick the app.
        WriteFile(QuickTermStore.HostsFile, """
            [
              { "name": "web", "port": 22, },
            ]
            """);

        var store = QuickTermStore.Load(_root);

        Assert.Null(store.LoadProblem);
        Assert.Equal("web", store.Hosts[0].Name);
    }

    [Fact]
    public void A_Corrupt_File_Falls_Back_Instead_Of_Throwing()
    {
        WriteFile(QuickTermStore.SettingsFile, "{ this is not json");

        var store = QuickTermStore.Load(_root);

        Assert.Equal(QuickTermStore.SettingsFile, store.LoadProblem);
        Assert.Equal(5000, store.Settings.ScrollbackLines); // defaults, not a crash
    }

    [Fact]
    public void A_Field_From_A_Newer_Version_Is_Ignored()
    {
        // Downgrade has to work: a file written by a later build still loads, just without the new field.
        WriteFile(QuickTermStore.HostsFile, """
            [ { "name": "web", "proxy": { "host": "127.0.0.1" } } ]
            """);

        var store = QuickTermStore.Load(_root);

        Assert.Null(store.LoadProblem);
        Assert.Equal("web", store.Hosts[0].Name);
    }

    [Fact]
    public void The_Password_Never_Sits_In_The_File_As_Text()
    {
        var store = QuickTermStore.Load(_root);
        store.Hosts.Add(new HostProfile { Name = "web", Kind = SessionKind.Ssh, Secret = QuickTermStore.Protect("hunter2") });
        store.Save();

        var raw = File.ReadAllText(Path.Combine(_root, QuickTermStore.HostsFile));

        Assert.DoesNotContain("hunter2", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("hunter2")), raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Protect_And_Unprotect_Round_Trip()
    {
        var protectedValue = QuickTermStore.Protect("p@ss/中文");

        Assert.NotEqual("p@ss/中文", protectedValue);
        Assert.Equal("p@ss/中文", QuickTermStore.Unprotect(protectedValue));
    }

    [Fact]
    public void An_Empty_Password_Stays_Unset()
    {
        Assert.Equal(string.Empty, QuickTermStore.Protect(string.Empty));
        Assert.Equal(string.Empty, QuickTermStore.Unprotect(string.Empty));
    }

    [Fact]
    public void A_Blob_We_Cannot_Decrypt_Is_No_Secret_Rather_Than_A_Crash()
    {
        // The copied-to-another-machine case: DPAPI refuses, and refusing must read as "ask the user", not throw.
        Assert.Null(QuickTermStore.Unprotect("not base64 at all"));
        Assert.Null(QuickTermStore.Unprotect("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public void Rdp_Profiles_Drop_The_Secret_Before_Writing()
    {
        // mstsc takes no password on its command line, so a stored one here would be a secret that is never
        // used — and its real place is the credential store the system client reads itself (§6 item 7).
        var store = QuickTermStore.Load(_root);
        store.Hosts.Add(new HostProfile { Name = "desktop", Kind = SessionKind.Rdp, Secret = QuickTermStore.Protect("hunter2") });
        store.Hosts.Add(new HostProfile { Name = "server", Kind = SessionKind.Ssh, Secret = QuickTermStore.Protect("hunter2") });
        store.ForgetUnusableSecrets();
        store.Save();

        var again = QuickTermStore.Load(_root);
        Assert.Null(again.Hosts.Find(host => host.Name == "desktop")!.Secret);
        Assert.NotNull(again.Hosts.Find(host => host.Name == "server")!.Secret);
    }

    [Fact]
    public void Enum_Values_Are_Written_As_Names()
    {
        var store = QuickTermStore.Load(_root);
        store.Hosts.Add(new HostProfile { Name = "web", Kind = SessionKind.Telnet });
        store.Save();

        var raw = File.ReadAllText(Path.Combine(_root, QuickTermStore.HostsFile));

        Assert.Contains("Telnet", raw, StringComparison.Ordinal);
        Assert.Equal(SessionKind.Telnet, QuickTermStore.Load(_root).Hosts[0].Kind);
    }

    [Fact]
    public void Ssh_Auth_Kind_And_Key_Path_Round_Trip()
    {
        var store = QuickTermStore.Load(_root);
        store.Hosts.Add(new HostProfile
        {
            Name = "keyed",
            Kind = SessionKind.Ssh,
            Host = "10.0.0.9",
            User = "ops",
            AuthKind = SshAuthKind.PrivateKey,
            KeyPath = @"C:\Users\ops\.ssh\id_ed25519",
        });
        store.Hosts.Add(new HostProfile { Name = "agent", Kind = SessionKind.Ssh, AuthKind = SshAuthKind.Agent });
        store.Save();

        var raw = File.ReadAllText(Path.Combine(_root, QuickTermStore.HostsFile));
        Assert.Contains("PrivateKey", raw, StringComparison.Ordinal); // enum written as a name, not a number

        var again = QuickTermStore.Load(_root);
        var keyed = again.Hosts.Find(host => host.Name == "keyed")!;
        Assert.Equal(SshAuthKind.PrivateKey, keyed.AuthKind);
        Assert.Equal(@"C:\Users\ops\.ssh\id_ed25519", keyed.KeyPath);

        var agent = again.Hosts.Find(host => host.Name == "agent")!;
        Assert.Equal(SshAuthKind.Agent, agent.AuthKind);
        Assert.Null(agent.KeyPath); // unset stays unset
    }

    [Fact]
    public void A_Host_Without_Auth_Kind_Defaults_To_Password()
    {
        // A file written before the SSH fields existed has no authKind; it must read as Password, not throw.
        WriteFile(QuickTermStore.HostsFile, """
            [ { "name": "legacy", "kind": "Ssh", "host": "10.0.0.8" } ]
            """);

        var store = QuickTermStore.Load(_root);

        Assert.Null(store.LoadProblem);
        Assert.Equal(SshAuthKind.Password, store.Hosts[0].AuthKind);
        Assert.Null(store.Hosts[0].KeyPath);
    }
}
