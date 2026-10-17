using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SshTerminalTests
{
    [Fact]
    public async Task FailedTerminalLauncherReleasesTunnelAndCannotBeRecalled()
    {
        using var owner = new CountingOwner(new MemoryStream());
        using var forwarder = new SshLocalForwarder(new MemoryStream(), owner);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "/d", "/c", "exit", "7" }) start.ArgumentList.Add(arg);
        using var terminal = new SshTerminalSession(forwarder, start, "owned-alias", "remotedesk-ssh-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => terminal.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(terminal.IsActive);
        Assert.False(await terminal.RecallAsync());
        Assert.Equal(1, owner.Disposals);
        using var local = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => local.ConnectAsync(IPAddress.Loopback, forwarder.Port));
    }

    [Fact]
    public void QuickConnectRequiresSameTargetSavedUsernameDeviceKeyAndExistingIdentity()
    {
        string alias = SshTerminalLauncher.HostKeyAlias(new string('A', 64), Guid.NewGuid().ToString());
        var profile = new SshTerminalProfile(alias, "user", null);
        Assert.True(profile.CanQuickConnect(alias, "owned-device-key"));
        Assert.False(profile.CanQuickConnect(alias, ""));
        Assert.False(profile.CanQuickConnect(alias + "other", "owned-device-key"));
        Assert.False((profile with { Username = "" }).CanQuickConnect(alias, "owned-device-key"));
        string identity = Path.GetTempFileName();
        try { Assert.True((profile with { IdentityFile = identity }).CanQuickConnect(alias, "owned-device-key")); }
        finally { File.Delete(identity); }
        Assert.False((profile with { IdentityFile = identity }).CanQuickConnect(alias, "owned-device-key"));
    }

    [Fact]
    public void NamedTerminalRecallOnlyFocusesOriginalWindowAndDoesNotStartAnotherSsh()
    {
        string id = "remotedesk-ssh-" + Guid.NewGuid().ToString("N");
        string alias = SshTerminalLauncher.HostKeyAlias(new string('A', 64), Guid.NewGuid().ToString());
        var ssh = SshTerminalLauncher.CreateStartInfo("owned-ssh.exe", 32123, "user", alias, "known hosts");
        var start = SshTerminalLauncher.CreateWindowStartInfo(ssh, "owned-wt.exe", id)!;
        Assert.Equal("owned-wt.exe", start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Equal(id, start.ArgumentList[1]);
        Assert.Contains("RemoteDesk SSH [" + id[^8..] + "]", start.ArgumentList);
        Assert.Equal(ssh.ArgumentList, start.ArgumentList.Skip(7));
        var recall = SshTerminalLauncher.CreateRecallStartInfo("owned-wt.exe", id);
        Assert.Equal(new[] { "-w", id, "focus-tab", "-t", "0" }, recall.ArgumentList);
        Assert.Throws<ArgumentException>(() => SshTerminalLauncher.CreateRecallStartInfo("owned-wt.exe", "user-window"));
        ssh.ArgumentList.Add("literal;path");
        Assert.Null(SshTerminalLauncher.CreateWindowStartInfo(ssh, "owned-wt.exe", id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulIpcLauncherExitKeepsTunnelButLegacyProcessExitClosesIt(bool ipc)
    {
        using var owner = new CountingOwner(new MemoryStream());
        using var forwarder = new SshLocalForwarder(new MemoryStream(), owner);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "/d", "/c", "exit", "0" }) start.ArgumentList.Add(arg);
        using var terminal = new SshTerminalSession(forwarder, start, "owned-alias", ipc ? "remotedesk-ssh-" + Guid.NewGuid().ToString("N") : null);
        if (ipc)
        {
            await Task.Delay(250);
            Assert.True(terminal.IsActive);
            Assert.False(terminal.Completion.IsCompleted);
            Assert.Equal(0, owner.Disposals);
            terminal.Dispose();
        }
        await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(terminal.IsActive);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public void CommandUsesRealSystemTerminalWithoutSecretsOrShellInterpolation()
    {
        string alias = SshTerminalLauncher.HostKeyAlias(new string('A', 64), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var start = SshTerminalLauncher.CreateStartInfo("owned-ssh.exe", 32123, "DOMAIN\\user", alias,
            Path.Combine(Path.GetTempPath(), "Owned SSH fixture", "known_hosts"));
        Assert.Equal("owned-ssh.exe", start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.False(start.RedirectStandardInput);
        Assert.False(start.RedirectStandardOutput);
        Assert.Empty(start.Arguments);
        Assert.Contains("HostKeyAlias=" + alias, start.ArgumentList);
        Assert.Contains("StrictHostKeyChecking=ask", start.ArgumentList);
        Assert.Contains("EscapeChar=none", start.ArgumentList);
        Assert.Contains("ForwardAgent=no", start.ArgumentList);
        Assert.Contains("ClearAllForwardings=yes", start.ArgumentList);
        Assert.Contains("UserKnownHostsFile=\"" + Path.Combine(Path.GetTempPath(), "Owned SSH fixture", "known_hosts").Replace('\\', '/') + "\"", start.ArgumentList);
        Assert.Equal("127.0.0.1", start.ArgumentList.Last());
        Assert.Equal("-F", start.ArgumentList.First());
        Assert.Equal("NUL", start.ArgumentList[1]);
    }

    [Fact]
    public void HostIdentityIsStableAcrossRandomPortsAndDistinctAcrossRelayOrDevice()
    {
        string id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        string alias = SshTerminalLauncher.HostKeyAlias(new string('A', 64), id);
        Assert.Equal(alias, SshTerminalLauncher.HostKeyAlias(new string('a', 64), id.ToUpperInvariant()));
        Assert.NotEqual(alias, SshTerminalLauncher.HostKeyAlias(new string('B', 64), id));
        Assert.NotEqual(alias, SshTerminalLauncher.HostKeyAlias(new string('A', 64), "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
        Assert.Throws<ArgumentException>(() => SshTerminalLauncher.HostKeyAlias("bad", id));
    }

    [Theory]
    [InlineData("user", true)]
    [InlineData("DOMAIN\\user", true)]
    [InlineData("user@domain", true)]
    [InlineData("", false)]
    [InlineData("user\n-oProxyCommand=bad", false)]
    [InlineData("user;cmd", false)]
    [InlineData("user with space", false)]
    [InlineData(null, false)]
    public void UsernameValidationRejectsControlAndCommandSyntax(string? value, bool expected) =>
        Assert.Equal(expected, SshTerminalLauncher.IsValidUsername(value));

    [Fact]
    public void OptionalIdentityFileIsOneArgumentAndDefaultsRemainAgentCompatible()
    {
        string alias = SshTerminalLauncher.HostKeyAlias(new string('A', 64), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        string identity = Path.Combine(Path.GetTempPath(), "Owned SSH fixture", "key with spaces;literal");
        var start = SshTerminalLauncher.CreateStartInfo("owned-ssh.exe", 32123, "user", alias, "known_hosts", identity);
        Assert.Contains("IdentityFile=\"" + identity.Replace('\\', '/') + "\"", start.ArgumentList);
        Assert.Contains("IdentitiesOnly=yes", start.ArgumentList);
        start = SshTerminalLauncher.CreateStartInfo("owned-ssh.exe", 32123, "user", alias, "known_hosts");
        Assert.DoesNotContain("-i", start.ArgumentList);
        Assert.DoesNotContain("IdentitiesOnly=yes", start.ArgumentList);
    }

    [Fact]
    public void IdentityAndKnownHostsEscapeOpenSshPercentTokensWithoutCopyingKeys()
    {
        string alias = SshTerminalLauncher.HostKeyAlias(new string('A', 64), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        string identity = Path.Combine(Path.GetTempPath(), "literal %h %d %% key");
        string knownHosts = Path.Combine(Path.GetTempPath(), "literal %u %% known_hosts");
        var start = SshTerminalLauncher.CreateStartInfo("owned-ssh.exe", 32123, "user", alias, knownHosts, identity);
        Assert.Contains("IdentityFile=\"" + identity.Replace('\\', '/').Replace("%", "%%") + "\"", start.ArgumentList);
        Assert.Contains("UserKnownHostsFile=\"" + knownHosts.Replace('\\', '/').Replace("%", "%%") + "\"", start.ArgumentList);
        Assert.DoesNotContain("-i", start.ArgumentList);
        Assert.Throws<ArgumentException>(() => SshTerminalLauncher.CreateStartInfo("owned-ssh.exe", 32123, "user", alias, knownHosts,
            Path.Combine(Path.GetTempPath(), "literal ${ENV} key")));
    }

    [Fact]
    public void ProfilesAreScopedByRelayCertificateAndDeviceWithNewestFirstAndBoundedSize()
    {
        string identity = Path.Combine(Path.GetTempPath(), "owned-key");
        var profiles = Enumerable.Range(1, 65).Select(index => new SshTerminalProfile(
            SshTerminalLauncher.HostKeyAlias(new string('A', 64), $"aaaaaaaa-aaaa-4aaa-8aaa-{index:D12}"), "user" + index, identity)).ToList();
        string firstAlias = profiles[0].HostKeyAlias;
        var normalized = SshTerminalProfile.Normalize(new[] { profiles[0] with { HostKeyAlias = firstAlias.ToUpperInvariant().Replace(":SSH", ":ssh"), Username = "newest" } }.Concat(profiles));
        Assert.Equal(50, normalized.Count);
        Assert.Equal("newest", normalized[0].Username);
        Assert.Equal(firstAlias, normalized[0].HostKeyAlias);
        Assert.Equal(identity, normalized[0].IdentityFile);
        string otherRelay = SshTerminalLauncher.HostKeyAlias(new string('B', 64), "aaaaaaaa-aaaa-4aaa-8aaa-000000000001");
        Assert.Equal(2, SshTerminalProfile.Normalize([profiles[0], profiles[0] with { HostKeyAlias = otherRelay }]).Count);
    }

    [Fact]
    public void MalformedProfilesDoNotBreakSettingsNormalizationOrPersistSecrets()
    {
        string alias = SshTerminalLauncher.HostKeyAlias(new string('A', 64), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var valid = new SshTerminalProfile(alias, "user", null);
        var settings = new RemoteDeskSettings { Viewer = new ViewerSettings { SshProfiles = [
            null!, valid with { Username = null! }, valid with { HostKeyAlias = null! },
            valid with { IdentityFile = "relative-key" }, valid with { Username = "bad\nname" }, valid] } };
        AppSettingsService.NormalizeSettings(settings);
        Assert.Equal(valid, Assert.Single(settings.Viewer.SshProfiles));
        var properties = typeof(SshTerminalProfile).GetProperties().Select(property => property.Name).Order().ToArray();
        Assert.Equal(new[] { "HostKeyAlias", "IdentityFile", "Username" }, properties);
    }

    [Fact]
    public void HostSettingsDefaultOffAndInvalidPortsNormalizeWithoutChangingAdminSsh()
    {
        var settings = new RemoteDeskSettings();
        Assert.False(settings.Host.AllowSsh);
        Assert.Equal(22, settings.Host.SshPort);
        settings.Host.SshPort = 65536;
        settings.Relay.SshPort = 2222;
        AppSettingsService.NormalizeSettings(settings);
        Assert.Equal(22, settings.Host.SshPort);
        Assert.Equal(2222, settings.Relay.SshPort);
        Assert.False(settings.Host.AllowSsh);
    }

    [Fact]
    public async Task ForwarderCarriesBothDirectionsAndAllowsOnlyOneLocalSession()
    {
        using var remoteListener = new TcpListener(IPAddress.Loopback, 0);
        remoteListener.Start();
        using var remote = new TcpClient();
        await remote.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)remoteListener.LocalEndpoint).Port);
        using var host = await remoteListener.AcceptTcpClientAsync();
        using var owner = new CountingOwner(remote);
        using var forwarder = new SshLocalForwarder(remote.GetStream(), owner);
        using var local = new TcpClient();
        await local.ConnectAsync(IPAddress.Loopback, forwarder.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] banner = Encoding.ASCII.GetBytes("SSH-2.0-OwnedFixture\r\n");
        await host.GetStream().WriteAsync(banner, deadline.Token);
        byte[] received = new byte[banner.Length];
        await local.GetStream().ReadExactlyAsync(received, deadline.Token);
        Assert.Equal(banner, received);
        byte[] outgoing = Enumerable.Range(0, 255).Select(value => (byte)value).ToArray();
        await local.GetStream().WriteAsync(outgoing, deadline.Token);
        received = new byte[outgoing.Length];
        await host.GetStream().ReadExactlyAsync(received, deadline.Token);
        Assert.Equal(outgoing, received);
        using var second = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => second.ConnectAsync(IPAddress.Loopback, forwarder.Port));
        local.Dispose();
        await forwarder.Completion.WaitAsync(deadline.Token);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task UnusedListenerTimesOutAndReleasesAuthenticatedTunnel()
    {
        using var owner = new CountingOwner(new MemoryStream());
        using var forwarder = new SshLocalForwarder(new MemoryStream(), owner, TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<TimeoutException>(() => forwarder.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, owner.Disposals);
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, forwarder.Port));
    }

    [Fact]
    public async Task CancellationBeforeAcceptClosesPortAndIsIdempotent()
    {
        using var owner = new CountingOwner(new MemoryStream());
        using var forwarder = new SshLocalForwarder(new MemoryStream(), owner);
        forwarder.Dispose(); forwarder.Dispose();
        await forwarder.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, owner.Disposals);
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, forwarder.Port));
    }

    private sealed class CountingOwner(IDisposable resource) : IDisposable
    {
        internal int Disposals;
        public void Dispose() { Interlocked.Increment(ref Disposals); resource.Dispose(); }
    }
}
