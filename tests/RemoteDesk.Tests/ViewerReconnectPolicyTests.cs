using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ViewerReconnectPolicyTests
{
    [Fact]
    public void SnapshotRetainsRelayRouteForAutomaticReconnect()
    {
        var route = new RelayConnectionOptions(
            "relay.example.com",
            56567,
            new string('a', 64),
            new string('B', 64),
            Guid.NewGuid().ToString("D"));
        var snapshot = new ViewerConnectionSnapshot(
            route.ServerAddress,
            route.Port,
            "remote-password",
            ViewerVideoMode.Automatic,
            route);

        Assert.Same(route, snapshot.RelayRoute);
        Assert.Equal(route.ServerAddress, snapshot.Host);
        Assert.Equal(route.Port, snapshot.Port);
    }

    [Theory]
    [InlineData(-1, 500)]
    [InlineData(0, 500)]
    [InlineData(1, 1_000)]
    [InlineData(2, 2_000)]
    [InlineData(3, 4_000)]
    [InlineData(4, 8_000)]
    [InlineData(5, 10_000)]
    [InlineData(25, 10_000)]
    public void RetryDelayFollowsSequenceAndCapsAtTenSeconds(
        int failedAttemptCount,
        int expectedMilliseconds)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            ViewerReconnectPolicy.GetRetryDelay(
                failedAttemptCount));
    }

    [Fact]
    public void DeviceInfoAloneDoesNotResetBackoffUntilStableWindow()
    {
        int failedAttemptCount = 0;
        int[] expectedDelays =
            [500, 1_000, 2_000, 4_000, 8_000, 10_000, 10_000];

        foreach (int expectedDelay in expectedDelays)
        {
            Assert.Equal(
                TimeSpan.FromMilliseconds(expectedDelay),
                ViewerReconnectPolicy.GetRetryDelay(
                    failedAttemptCount));
            failedAttemptCount =
                ViewerReconnectPolicy.RecordAttemptResult(
                    failedAttemptCount,
                    stableConnectionWindowCompleted: false);
        }

        Assert.Equal(
            0,
            ViewerReconnectPolicy.RecordAttemptResult(
                failedAttemptCount,
                stableConnectionWindowCompleted: true));
        Assert.Equal(
            TimeSpan.FromMilliseconds(500),
            ViewerReconnectPolicy.GetRetryDelay(
                ViewerReconnectPolicy.RecordAttemptResult(
                    failedAttemptCount,
                    stableConnectionWindowCompleted: true)));
        Assert.Equal(
            int.MaxValue,
            ViewerReconnectPolicy.RecordAttemptResult(
                int.MaxValue,
                stableConnectionWindowCompleted: false));
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            ViewerReconnectPolicy.StableConnectionWindow);
    }

    [Theory]
    [InlineData(7, 7, 31, 31, true, true)]
    [InlineData(7, 8, 31, 31, true, false)]
    [InlineData(7, 7, 31, 32, true, false)]
    [InlineData(7, 7, 31, 31, false, false)]
    public void StableWindowRequiresIntentOwnerAndLiveGeneration(
        long expectedIntent,
        long currentIntent,
        long expectedConnection,
        long currentConnection,
        bool connected,
        bool expected)
    {
        Assert.Equal(
            expected,
            ViewerReconnectPolicy
                .ShouldCompleteStableConnectionWindow(
                    expectedIntent,
                    currentIntent,
                    expectedConnection,
                    currentConnection,
                    connected));
    }

    [Fact]
    public void OnlyTransientConnectionFailuresAreRetried()
    {
        Assert.True(
            ViewerReconnectPolicy.IsRetryableConnectionFailure(
                new TimeoutException()));
        Assert.True(
            ViewerReconnectPolicy.IsRetryableConnectionFailure(
                new IOException()));
        Assert.True(
            ViewerReconnectPolicy.IsRetryableConnectionFailure(
                new SocketException()));

        Assert.False(
            ViewerReconnectPolicy.IsRetryableConnectionFailure(
                new UnauthorizedAccessException()));
        Assert.False(
            ViewerReconnectPolicy.IsRetryableConnectionFailure(
                new InvalidDataException()));
        Assert.False(
            ViewerReconnectPolicy.IsRetryableConnectionFailure(
                new RelayAccessDeniedException(
                    "中继访问密钥错误。")));
        Assert.False(
            ViewerReconnectPolicy.IsRetryableConnectionFailure(
                new RemoteSessionRejectedException(
                    "session replaced")));
        Assert.False(
            ViewerReconnectPolicy.IsRetryableConnectionFailure(
                new InvalidOperationException()));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void QueuedConnectionEventAppliesOnlyToMatchingLiveState(
        bool eventConnected,
        bool currentConnected,
        bool expected)
    {
        Assert.Equal(
            expected,
            ViewerReconnectPolicy.ShouldApplyConnectionEvent(
                eventConnected,
                currentConnected));
    }

    [Fact]
    public void StaleDisconnectCannotCloseAReplacementConnection()
    {
        Assert.False(
            ViewerReconnectPolicy.ShouldApplyConnectionEvent(
                eventConnected: false,
                currentConnected: true));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    public void ReconnectRequiresDisconnectAndQualifiedSession(
        bool connectedEvent,
        bool currentConnected,
        bool hasSuccessfulIntent,
        bool expected)
    {
        Assert.Equal(
            expected,
            ViewerReconnectPolicy.ShouldStartReconnect(
                connectedEvent,
                    currentConnected,
                    hasSuccessfulIntent));
    }

    [Fact]
    public void ReplacedSessionCannotAutomaticallyStealControlBack()
    {
        Assert.False(
            ViewerReconnectPolicy.ShouldStartReconnect(
                connectedEvent: false,
                currentConnected: false,
                hasSuccessfulIntent: true,
                sessionRejected: true));
    }
}
