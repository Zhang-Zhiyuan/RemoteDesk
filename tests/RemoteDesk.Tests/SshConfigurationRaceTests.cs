using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SshConfigurationRaceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RevocationBetweenSnapshotAndBridgePublicationCancelsBeforeNetwork(bool disable)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var original = new RelaySshHostOptions(true, 22, "old-device-key");
        var current = original;
        RelayHostConnector? host = null;
        CancellationToken? bridgeToken = null;
        int reads = 0;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo refresh = typeof(RelayHostConnector).GetMethod("RefreshSshConfiguration", flags)!;
        FieldInfo bridges = typeof(RelayHostConnector).GetField("_sshBridges", flags)!;
        RelaySshHostOptions ReadOptions()
        {
            if (++reads == 1)
            {
                // Reproduce the exact ordering without a timing-dependent sleep:
                // capture old config, revoke + refresh before publication, then
                // return the now stale snapshot to StartSshBridge.
                current = disable ? original with { Enabled = false }
                    : original with { DeviceKey = "replacement-device-key" };
                refresh.Invoke(host, null);
                return original;
            }
            foreach (object state in ((IDictionary)bridges.GetValue(host)!).Values)
            {
                var cancellation = (CancellationTokenSource)state.GetType()
                    .GetProperty("Cancellation", flags)!.GetValue(state)!;
                bridgeToken = cancellation.Token;
            }
            return current;
        }
        using (host = new RelayHostConnector(sshOptionsProvider: ReadOptions))
        {
            var options = new RelayConnectionOptions("127.0.0.1",
                ((IPEndPoint)listener.LocalEndpoint).Port, new string('a', 64),
                new string('b', 64), Guid.NewGuid().ToString("D"));
            typeof(RelayHostConnector).GetMethod("StartSshBridge", flags)!.Invoke(host,
                [options, Guid.NewGuid().ToString("D"), CancellationToken.None]);
            Assert.NotNull(bridgeToken);
            Assert.True(bridgeToken.Value.IsCancellationRequested);
            Assert.False(listener.Pending());
        }
    }

    [Fact]
    public void SshOnlyDeviceIsNotPresentedAsAReplaceableDesktopSession()
    {
        var device = new RelayOnlineDevice(Guid.NewGuid().ToString("D"), "SSH host", "Linux", null, true, 0)
        { SupportsSsh = true, SupportsDesktop = false };
        Assert.Equal("仅 SSH", device.StatusText);
    }
}
