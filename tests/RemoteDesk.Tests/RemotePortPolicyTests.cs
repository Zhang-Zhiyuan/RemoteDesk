using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemotePortPolicyTests
{
    [Fact]
    public void CompatibleHostProbePortsKeepLegacyFirstAndAddSafePorts()
    {
        Assert.Equal(
            [
                Protocol.DefaultPort,
                RemotePortPolicy
                    .PreferredFallbackHostPort,
                RemotePortPolicy
                    .AlternateFallbackHostPort
            ],
            RemotePortPolicy
                .GetCompatibleHostProbePorts(
                    Protocol.DefaultPort));
        Assert.Equal(
            [41234],
            RemotePortPolicy
                .GetCompatibleHostProbePorts(
                    41234));
    }

    [Fact]
    public void DiscoveryProbePortsPreserveCustomPortsAndPairDefaults()
    {
        Assert.Equal(
            [
                RemotePortPolicy
                    .DefaultDiscoveryPort,
                RemotePortPolicy
                    .FallbackDiscoveryPort
            ],
            RemotePortPolicy
                .GetDiscoveryProbePorts(
                    RemotePortPolicy
                        .DefaultDiscoveryPort));
        Assert.Equal(
            [41235],
            RemotePortPolicy
                .GetDiscoveryProbePorts(
                    41235));
    }

    [Fact]
    public void AccessDeniedRecognizesWinsockPortExclusionFailure()
    {
        var exception =
            new SocketException(
                (int)SocketError.AccessDenied);

        Assert.True(
            RemotePortPolicy.IsAccessDenied(
                exception));
    }

    [Theory]
    [InlineData(SocketError.AccessDenied, true)]
    [InlineData(SocketError.AddressAlreadyInUse, true)]
    [InlineData(SocketError.AddressNotAvailable, false)]
    public void DiscoveryFallbackRecognizesReservedOrOccupiedLegacyPort(
        SocketError socketError,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemotePortPolicy
                .CanFallbackFromDiscoveryBind(
                    new SocketException(
                        (int)socketError)));
    }

    [Fact]
    public async Task HostStartKeepsRequestedPortWhenItBinds()
    {
        var attemptedPorts =
            new List<int>();

        HostPortStartResult result =
            await RemotePortPolicy
                .StartHostWithAccessDeniedFallbackAsync(
                    Protocol.DefaultPort,
                    port =>
                    {
                        attemptedPorts.Add(port);
                        return Task.CompletedTask;
                    });

        Assert.Equal(
            [Protocol.DefaultPort],
            attemptedPorts);
        Assert.False(
            result.UsedAutomaticFallback);
        Assert.Equal(
            Protocol.DefaultPort,
            result.ListeningPort);
    }

    [Fact]
    public async Task HostStartUsesSafePortAfterAccessDenied()
    {
        var attemptedPorts =
            new List<int>();

        HostPortStartResult result =
            await RemotePortPolicy
                .StartHostWithAccessDeniedFallbackAsync(
                    Protocol.DefaultPort,
                    port =>
                    {
                        attemptedPorts.Add(port);
                        if (port ==
                            Protocol.DefaultPort)
                        {
                            throw new SocketException(
                                (int)SocketError
                                    .AccessDenied);
                        }

                        return Task.CompletedTask;
                    });

        Assert.Equal(
            [
                Protocol.DefaultPort,
                RemotePortPolicy
                    .PreferredFallbackHostPort
            ],
            attemptedPorts);
        Assert.True(
            result.UsedAutomaticFallback);
        Assert.Equal(
            RemotePortPolicy
                .PreferredFallbackHostPort,
            result.ListeningPort);
        Assert.True(
            result.ListeningPort < 49152);
    }

    [Fact]
    public async Task HostStartUsesAlternateWhenPreferredSafePortIsOccupied()
    {
        var attemptedPorts =
            new List<int>();

        HostPortStartResult result =
            await RemotePortPolicy
                .StartHostWithAccessDeniedFallbackAsync(
                    Protocol.DefaultPort,
                    port =>
                    {
                        attemptedPorts.Add(port);
                        if (port ==
                            Protocol.DefaultPort)
                        {
                            throw new SocketException(
                                (int)SocketError
                                    .AccessDenied);
                        }

                        if (port ==
                            RemotePortPolicy
                                .PreferredFallbackHostPort)
                        {
                            throw new SocketException(
                                (int)SocketError
                                    .AddressAlreadyInUse);
                        }

                        return Task.CompletedTask;
                    });

        Assert.Equal(
            [
                Protocol.DefaultPort,
                RemotePortPolicy
                    .PreferredFallbackHostPort,
                RemotePortPolicy
                    .AlternateFallbackHostPort
            ],
            attemptedPorts);
        Assert.Equal(
            RemotePortPolicy
                .AlternateFallbackHostPort,
            result.ListeningPort);
    }

    [Fact]
    public async Task HostStartDoesNotHideNonAccessDeniedBindFailure()
    {
        var exception =
            new SocketException(
                (int)SocketError
                    .AddressAlreadyInUse);

        SocketException actual =
            await Assert.ThrowsAsync<
                SocketException>(() =>
                RemotePortPolicy
                    .StartHostWithAccessDeniedFallbackAsync(
                        Protocol.DefaultPort,
                        _ => throw exception));

        Assert.Same(
            exception,
            actual);
    }
}
