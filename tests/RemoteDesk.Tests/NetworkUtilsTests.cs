using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NetworkUtilsTests
{
    [Fact]
    public void IsLikelyLocalEndpointMatchesKnownLocalAddress()
    {
        var localAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "192.0.2.249"
        };

        Assert.True(NetworkUtils.IsLikelyLocalEndpoint("192.0.2.249", "OtherMachine", localAddresses));
    }

    [Fact]
    public void IsLikelyLocalEndpointMatchesLoopbackAddress()
    {
        Assert.True(NetworkUtils.IsLikelyLocalEndpoint(
            "127.0.0.1",
            "OtherMachine",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void IsLikelyLocalEndpointMatchesLocalhostName()
    {
        Assert.True(NetworkUtils.IsLikelyLocalEndpoint(
            "localhost",
            "OtherMachine",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void IsLikelyLocalEndpointMatchesCurrentMachineName()
    {
        Assert.True(NetworkUtils.IsLikelyLocalEndpoint(
            "10.9.8.7",
            Environment.MachineName,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void IsLikelyLocalEndpointDoesNotMatchRemoteAddressAndMachineName()
    {
        var localAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "192.0.2.249"
        };

        Assert.False(NetworkUtils.IsLikelyLocalEndpoint("192.0.2.250", "RemoteDesk-2", localAddresses));
    }

    [Fact]
    public void ConfigureLowLatencyTcpClientEnablesNoDelay()
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);

        NetworkUtils.ConfigureLowLatencyTcpClient(
            client,
            receiveBufferSize: 16 * 1024,
            sendBufferSize: 16 * 1024);

        Assert.True(client.NoDelay);
    }

    [Fact]
    public void ConfigureLowLatencyTcpClientIgnoresDisposedClient()
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        client.Dispose();

        Exception? exception = Record.Exception(() =>
            NetworkUtils.ConfigureLowLatencyTcpClient(
                client,
                receiveBufferSize: 16 * 1024,
                sendBufferSize: 16 * 1024));

        Assert.Null(exception);
    }
}
