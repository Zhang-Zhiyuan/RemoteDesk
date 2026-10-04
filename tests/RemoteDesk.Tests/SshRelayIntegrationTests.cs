using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SshRelayIntegrationTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task PinnedTlsSshGateRoundTripDesktopTransitionsAndRevocation(string relayAddress)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var relay = await OwnedRelay.StartAsync(relayAddress, timeout.Token);
        using var sshListener = new TcpListener(IPAddress.Loopback, 0);
        using var desktopListener = new TcpListener(IPAddress.Loopback, 0);
        sshListener.Start(); desktopListener.Start();
        var sshOptions = new RelaySshHostOptions(true, ((IPEndPoint)sshListener.LocalEndpoint).Port, "SSH 设备测试 key");
        using var host = new RelayHostConnector(sshOptionsProvider: () => Volatile.Read(ref sshOptions));
        await host.StartAsync(relay.Options, 0, timeout.Token);
        var device = await WaitForDeviceAsync(relay.Options, row => row.SupportsSsh && !row.SupportsDesktop, timeout.Token);
        Assert.False(device.Busy);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SshRelayClient.OpenAsync(relay.Options, "wrong-key", timeout.Token));
        Assert.False(sshListener.Pending()); // Wrong device key must not even dial sshd.
        using var ssh = await SshRelayClient.OpenAsync(relay.Options, sshOptions.DeviceKey, timeout.Token);
        using var sshTarget = await sshListener.AcceptTcpClientAsync(timeout.Token);
        await RoundTripAsync(ssh.Stream, sshTarget.GetStream(), timeout.Token);

        int desktopPort = ((IPEndPoint)desktopListener.LocalEndpoint).Port;
        await host.StartAsync(relay.Options, desktopPort, timeout.Token);
        await WaitForDeviceAsync(relay.Options, row => row.SupportsDesktop && row.SupportsSsh, timeout.Token);
        await RoundTripAsync(ssh.Stream, sshTarget.GetStream(), timeout.Token);
        using var desktop = new TcpClient();
        await RelayTunnelClient.ConnectViewerIntoAsync(desktop, relay.Options, timeout.Token);
        using var desktopTarget = await desktopListener.AcceptTcpClientAsync(timeout.Token);
        await RoundTripAsync(desktop.GetStream(), desktopTarget.GetStream(), timeout.Token);

        await host.StartAsync(relay.Options, 0, timeout.Token);
        await WaitForDeviceAsync(relay.Options, row => !row.SupportsDesktop && row.SupportsSsh, timeout.Token);
        await RoundTripAsync(ssh.Stream, sshTarget.GetStream(), timeout.Token);
        // Configuration revocation closes SSH only, not another independent bridge.
        sshOptions = sshOptions with { Enabled = false };
        Assert.True(host.RequestAddressRefresh());
        await WaitForDeviceAsync(relay.Options, row => !row.SupportsSsh, timeout.Token);
        await AssertClosedAsync(ssh.Stream, timeout.Token);
        await RoundTripAsync(desktop.GetStream(), desktopTarget.GetStream(), timeout.Token);
        await Assert.ThrowsAsync<RelayProtocolException>(() => SshRelayClient.OpenAsync(relay.Options, "key", timeout.Token));
    }

    [Fact]
    public async Task ClosedSshPortHasActionableErrorAndControlRegistrationSurvives()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var relay = await OwnedRelay.StartAsync("127.0.0.1", timeout.Token);
        // A bound, non-listening socket reserves a port without a TOCTOU claim.
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var options = new RelaySshHostOptions(true, ((IPEndPoint)reserved.LocalEndPoint!).Port, "key");
        using var host = new RelayHostConnector(sshOptionsProvider: () => options);
        await host.StartAsync(relay.Options, 0, timeout.Token);
        await WaitForDeviceAsync(relay.Options, d => d.SupportsSsh, timeout.Token);
        IOException failure = await Assert.ThrowsAsync<IOException>(() => SshRelayClient.OpenAsync(relay.Options, "key", timeout.Token));
        Assert.Contains("SSH 服务不可达", failure.Message);
        Assert.True(host.IsRunning);
        Assert.False((await RelayTunnelClient.ListDevicesAsync(relay.Options, timeout.Token)).Single().Busy);
    }

    [Fact]
    public async Task Ipv6OnlySshServiceIsReachableAndHostStopClosesBothEnds()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var relay = await OwnedRelay.StartAsync("127.0.0.1", timeout.Token);
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = false;
        listener.Start();
        var options = new RelaySshHostOptions(true, ((IPEndPoint)listener.LocalEndpoint).Port, "key");
        using var host = new RelayHostConnector(sshOptionsProvider: () => options);
        await host.StartAsync(relay.Options, 0, timeout.Token);
        await WaitForDeviceAsync(relay.Options, row => row.SupportsSsh, timeout.Token);
        using var ssh = await SshRelayClient.OpenAsync(relay.Options, "key", timeout.Token);
        using var target = await listener.AcceptTcpClientAsync(timeout.Token);
        await RoundTripAsync(ssh.Stream, target.GetStream(), timeout.Token);
        await host.StopAsync().WaitAsync(timeout.Token);
        await AssertClosedAsync(ssh.Stream, timeout.Token);
        await AssertClosedAsync(target.GetStream(), timeout.Token);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task FourSshSessionsRejectFifthWithoutDisturbingDesktopAndReleaseCapacity()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var relay = await OwnedRelay.StartAsync("127.0.0.1", timeout.Token);
        using var sshListener = new TcpListener(IPAddress.Loopback, 0);
        using var desktopListener = new TcpListener(IPAddress.Loopback, 0);
        sshListener.Start(); desktopListener.Start();
        var options = new RelaySshHostOptions(true, ((IPEndPoint)sshListener.LocalEndpoint).Port, "key");
        using var host = new RelayHostConnector(sshOptionsProvider: () => options);
        await host.StartAsync(relay.Options, ((IPEndPoint)desktopListener.LocalEndpoint).Port, timeout.Token);
        await WaitForDeviceAsync(relay.Options, row => row.SupportsSsh && row.SupportsDesktop, timeout.Token);
        using var desktop = new TcpClient();
        await RelayTunnelClient.ConnectViewerIntoAsync(desktop, relay.Options, timeout.Token);
        using var desktopTarget = await desktopListener.AcceptTcpClientAsync(timeout.Token);
        var sessions = new List<SshRelayConnection>();
        var targets = new List<TcpClient>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                sessions.Add(await SshRelayClient.OpenAsync(relay.Options, "key", timeout.Token));
                targets.Add(await sshListener.AcceptTcpClientAsync(timeout.Token));
            }
            var failure = await Assert.ThrowsAsync<RelayProtocolException>(() =>
                SshRelayClient.OpenAsync(relay.Options, "key", timeout.Token));
            Assert.Contains("4", failure.Message);
            Assert.False(sshListener.Pending());
            for (int i = 0; i < 4; i++)
                await RoundTripAsync(sessions[i].Stream, targets[i].GetStream(), timeout.Token);
            await RoundTripAsync(desktop.GetStream(), desktopTarget.GetStream(), timeout.Token);

            sessions[0].Dispose();
            await AssertClosedAsync(targets[0].GetStream(), timeout.Token);
            SshRelayConnection? replacement = null;
            while (replacement is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { replacement = await SshRelayClient.OpenAsync(relay.Options, "key", timeout.Token); }
                catch (RelayProtocolException error) when (error.Message.Contains("4", StringComparison.Ordinal))
                { await Task.Delay(25, timeout.Token); }
            }
            sessions.Add(replacement);
            using var replacementTarget = await sshListener.AcceptTcpClientAsync(timeout.Token);
            await RoundTripAsync(replacement.Stream, replacementTarget.GetStream(), timeout.Token);
            await RoundTripAsync(desktop.GetStream(), desktopTarget.GetStream(), timeout.Token);
        }
        finally
        {
            foreach (var session in sessions) session.Dispose();
            foreach (var target in targets) target.Dispose();
        }
    }

    private static async Task RoundTripAsync(Stream viewer, Stream target, CancellationToken token)
    {
        byte[] data = "SSH\0中文\u001b[24;80H\r\n"u8.ToArray();
        await viewer.WriteAsync(data, token);
        byte[] observed = new byte[data.Length];
        await target.ReadExactlyAsync(observed, token);
        Assert.Equal(data, observed);
        await target.WriteAsync(observed, token);
        await viewer.ReadExactlyAsync(observed, token);
        Assert.Equal(data, observed);
    }

    private static async Task AssertClosedAsync(Stream stream, CancellationToken token)
    {
        try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
        catch (IOException) { }
    }

    private static async Task<RelayOnlineDevice> WaitForDeviceAsync(RelayConnectionOptions options,
        Func<RelayOnlineDevice, bool> predicate, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var devices = await RelayTunnelClient.ListDevicesAsync(options, token);
            var found = devices.FirstOrDefault(predicate);
            if (found is not null) return found;
            await Task.Delay(25, token);
        }
    }

    private sealed class OwnedRelay(string directory, Process process, RelayConnectionOptions options) : IAsyncDisposable
    {
        internal RelayConnectionOptions Options { get; } = options;
        internal static async Task<OwnedRelay> StartAsync(string address, CancellationToken token)
        {
            string directory = Path.Combine(Path.GetTempPath(), "RemoteDeskSshTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string cert = Path.Combine(directory, "relay.crt"), key = Path.Combine(directory, "relay.key");
            byte[] certificate = RelayTunnelIntegrationTests.CreateCertificate(cert, key);
            string config = Path.Combine(directory, "config.json");
            string access = new('a', 64);
            File.WriteAllText(config, JsonSerializer.Serialize(new { access_token = access, bind = address, port = 56567,
                cert_file = cert, key_file = key }));
            Process process = RelayTunnelIntegrationTests.StartPythonServer(
                RelayTunnelIntegrationTests.FindWorkspaceFile("scripts/relay/remotedesk_relay_server.py")!, config);
            try
            {
                int port = await RelayTunnelIntegrationTests.ReadBoundPortAsync(process, token);
                return new OwnedRelay(directory, process, new RelayConnectionOptions(address, port, access,
                    RelayTls.GetSha256Fingerprint(certificate), Guid.NewGuid().ToString("D")));
            }
            catch { RelayTunnelIntegrationTests.TryStopProcess(process); process.Dispose(); Directory.Delete(directory, true); throw; }
        }

        public ValueTask DisposeAsync()
        {
            RelayTunnelIntegrationTests.TryStopProcess(process);
            process.Dispose();
            Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
