using System.Net;
using System.Security.Authentication;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayNetworkPathSelectorTests
{
    private static readonly RelayNetworkPath Wifi = new("wifi", "Wi-Fi", 3,
        IPAddress.Parse("10.16.169.189"), IPAddress.Parse("8.138.5.232"), "10.16.171.254");
    private static readonly RelayNetworkPath Wired = new("eth", "Ethernet", 18,
        IPAddress.Parse("10.7.9.79"), IPAddress.Parse("8.138.5.232"), "10.7.11.254");
    private static readonly RelayConnectionOptions Options = new("8.138.5.232", 56567,
        new string('a', 64), new string('b', 64), Guid.NewGuid().ToString());

    private sealed class Connection : IDisposable
    {
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed.TrySetResult();
    }

    [Theory]
    [InlineData("8.138.5.232", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.7.163.74", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.2.1", false)]
    [InlineData("169.254.2.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:8.138.5.232", false)]
    public void LocalAndNonIpv4DestinationsKeepSystemRoute(string address, bool allowed) =>
        Assert.Equal(allowed, RelayNetworkPathSelector.IsEligibleRemoteAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task FastTrustedPathWinsAndLateSuccessfulLoserIsDisposed()
    {
        var slowRelease = new TaskCompletionSource<Connection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = new Connection();
        var loser = new Connection();
        var result = await RelayNetworkPathSelector.RaceAsync<Connection>([null, Wifi],
            (path, token) => path is null ? slowRelease.Task : Task.FromResult(winner),
            TimeSpan.Zero, CancellationToken.None);
        Assert.Same(Wifi, result.Path);
        Assert.Same(winner, result.Connection);
        Assert.False(winner.Disposed.Task.IsCompleted);
        slowRelease.SetResult(loser);
        await loser.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        winner.Dispose();
    }

    [Fact]
    public async Task FastWrongCertificateCannotBeatTrustedFallback()
    {
        using var result = await new RelayNetworkPathSelector((_, _) => Task.FromResult<IReadOnlyList<RelayNetworkPath>>([Wifi]))
            .ConnectAsync(Options, async (path, token) =>
            {
                if (path is not null) throw new AuthenticationException("wrong pin");
                await Task.Yield();
                return new Connection();
            }, CancellationToken.None);
    }

    [Fact]
    public async Task AllFailuresRetainCertificateRejection()
    {
        await Assert.ThrowsAsync<AuthenticationException>(() => RelayNetworkPathSelector.RaceAsync<Connection>(
            [null, Wifi], (path, _) => Task.FromException<Connection>(path is null
                ? new IOException("network") : new AuthenticationException("pin")), TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task DeadlineRetainsObservedPinFailureWithoutHidingCallerCancellation()
    {
        using var stop = new CancellationTokenSource();
        var pinFailure = new AuthenticationException("owned fixture pin rejection");
        Task attempt = RelayNetworkPathSelector.RaceAsync<Connection>([null, Wifi],
            (path, token) => path is null ? Task.FromException<Connection>(pinFailure) : Unresponsive(token),
            TimeSpan.Zero, stop.Token);
        stop.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Equal(stop.Token, error.CancellationToken);
        Assert.Same(pinFailure, error.InnerException);

        static async Task<Connection> Unresponsive(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return new Connection();
        }
    }

    [Fact]
    public void InternalConnectionDeadlineReportsKnownPinRejection()
    {
        var rejected = new AuthenticationException("pin rejected");
        var cancelled = new OperationCanceledException("deadline", rejected);
        var error = Assert.IsType<AuthenticationException>(RelayTls.CreateConnectionDeadlineError(cancelled));
        Assert.Same(rejected, error.InnerException);
        Assert.Contains("证书指纹", error.Message);
    }

    [Fact]
    public void DeadlineWithoutPinEvidenceRemainsANetworkTimeout()
    {
        var error = new OperationCanceledException();
        var timeout = Assert.IsType<TimeoutException>(RelayTls.CreateConnectionDeadlineError(error));
        Assert.Same(error, timeout.InnerException);
    }

    [Fact]
    public void DirectoryDeadlineKeepsItsOwnActionableMessageUnlessPinWasRejected()
    {
        const string message = "读取在线设备超时";
        Assert.Equal(message, RelayTls.CreateConnectionDeadlineError(new OperationCanceledException(), message).Message);
        Assert.IsType<AuthenticationException>(RelayTls.CreateConnectionDeadlineError(
            new OperationCanceledException("deadline", new AuthenticationException("pin rejected")), message));
    }

    [Fact]
    public void InterruptedHandshakeIsNotMistakenForPinRejection()
    {
        var network = new IOException("peer reset TLS", new AuthenticationException("peer fatal alert"));
        Assert.IsType<TimeoutException>(RelayTls.CreateConnectionDeadlineError(new OperationCanceledException("deadline", network)));
    }

    [Fact]
    public async Task PreferredFailureStartsFallbackWithoutWaitingForHeadStart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await RelayNetworkPathSelector.RaceAsync<Connection>([Wifi, null],
            (path, _) => path is not null ? Task.FromException<Connection>(new IOException("offline")) : Task.FromResult(new Connection()),
            TimeSpan.FromMinutes(1), deadline.Token);
        Assert.Null(result.Path);
        result.Connection.Dispose();
    }

    [Fact]
    public async Task CallerCancellationCancelsAllPendingPaths()
    {
        using var stop = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task result = RelayNetworkPathSelector.RaceAsync<Connection>([null, Wifi], async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return new Connection();
        }, TimeSpan.Zero, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task HealthyPreferenceSurvivesOneMinuteAndExpiresAfterTenMinutesIdle()
    {
        long now = 100;
        var selector = new RelayNetworkPathSelector((_, _) => Task.FromResult<IReadOnlyList<RelayNetworkPath>>([Wifi, Wired]), () => now);
        var calls = new List<string?>();
        Task<Connection> Connect(RelayNetworkPath? path, CancellationToken token)
        {
            calls.Add(path?.InterfaceId);
            return path == Wifi ? Task.FromResult(new Connection()) : Slow(token);
        }
        static async Task<Connection> Slow(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return new Connection();
        }
        using (await selector.ConnectAsync(Options, Connect, CancellationToken.None)) { }
        Assert.Equal(3, calls.Count);
        calls.Clear();
        now += 30_000;
        using (await selector.ConnectAsync(Options, Connect, CancellationToken.None)) { }
        Assert.Equal(new[] { "wifi" }, calls);
        calls.Clear();
        now += 30_001;
        using (await selector.ConnectAsync(Options, Connect, CancellationToken.None)) { }
        Assert.Equal(new[] { "wifi" }, calls);
        calls.Clear();
        now += RelayPathStability.HistoryMilliseconds;
        using (await selector.ConnectAsync(Options, Connect, CancellationToken.None)) { }
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task NetworkAddressChangeInvalidatesPreference()
    {
        IReadOnlyList<RelayNetworkPath> paths = [Wifi, Wired];
        var selector = new RelayNetworkPathSelector((_, _) => Task.FromResult(paths));
        int calls = 0;
        async Task<Connection> Connect(RelayNetworkPath? path, CancellationToken token)
        {
            calls++;
            if (path?.InterfaceId != "wifi") await Task.Delay(Timeout.Infinite, token);
            return new Connection();
        }
        using (await selector.ConnectAsync(Options, Connect, CancellationToken.None)) { }
        calls = 0;
        paths = [Wifi with { LocalAddress = IPAddress.Parse("10.16.169.190") }, Wired];
        using (await selector.ConnectAsync(Options, Connect, CancellationToken.None)) { }
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task NoAlternativeUsesExactlyOneOriginalConnection()
    {
        var selector = new RelayNetworkPathSelector((_, _) => Task.FromResult<IReadOnlyList<RelayNetworkPath>>([]));
        int calls = 0;
        using var connection = await selector.ConnectAsync(Options, (path, token) =>
        {
            Assert.Null(path);
            calls++;
            return Task.FromResult(new Connection());
        }, CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedPreferredPathIsReplacedWithoutWaitingForHold()
    {
        var selector = new RelayNetworkPathSelector((_, _) => Task.FromResult<IReadOnlyList<RelayNetworkPath>>([Wifi, Wired]));
        using (await selector.ConnectAsync(Options, (path, token) => path == Wifi ?
            Task.FromResult(new Connection()) : Slow(token), default)) { }
        using (await selector.ConnectAsync(Options, (path, token) => path == Wifi ?
            Task.FromException<Connection>(new IOException("offline")) : path == Wired ?
            Task.FromResult(new Connection()) : Slow(token), default)) { }
        var called = new List<RelayNetworkPath?>();
        using (await selector.ConnectAsync(Options, (path, token) =>
        {
            called.Add(path);
            return Task.FromResult(new Connection());
        }, default)) { }
        Assert.Equal(new[] { Wired }, called);
        static async Task<Connection> Slow(CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); return new Connection(); }
    }

    [Fact]
    public async Task ProbeRoundDisposesOnlyItsOwnConnections()
    {
        using var live = new Connection();
        var measured = new List<Connection>();
        var state = new RelayPathStability();
        state.Connected(Wifi.Identity, 0);
        await new RelayNetworkPathSelector().ProbeRoundAsync(state, [Wifi, Wired], (_, _) =>
        {
            var connection = new Connection(); measured.Add(connection);
            return Task.FromResult(connection);
        });
        Assert.Equal(2, measured.Count);
        Assert.All(measured, connection => Assert.True(connection.Disposed.Task.IsCompleted));
        Assert.False(live.Disposed.Task.IsCompleted);
    }

    [Fact]
    public async Task QualifiedImprovementAffectsNextDialButDoesNotCloseExistingConnection()
    {
        long now = 0;
        var selector = new RelayNetworkPathSelector((_, _) => Task.FromResult<IReadOnlyList<RelayNetworkPath>>([Wifi, Wired]), () => now);
        using Connection live = await selector.ConnectAsync(Options, async (path, token) =>
        {
            if (path != Wifi) await Task.Delay(Timeout.Infinite, token);
            return new Connection();
        }, default);
        var state = selector.GetStability(Options, [Wifi, Wired]);
        for (int round = 0; round <= 6; round++)
        {
            now = round * 30_000;
            state.ObserveRound(now, new Dictionary<string, double> { [Wifi.Identity] = 40, [Wired.Identity] = 12 });
        }
        var called = new List<RelayNetworkPath?>();
        using (await selector.ConnectAsync(Options, (path, token) =>
        {
            called.Add(path); return Task.FromResult(new Connection());
        }, default)) { }
        Assert.Equal(new[] { Wired }, called);
        Assert.False(live.Disposed.Task.IsCompleted);
    }

    [Fact]
    public async Task BackgroundProbesNeverHoldUpForegroundOrAccumulateDuringReconnects()
    {
        long now = 0;
        int calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measured = new System.Collections.Concurrent.ConcurrentBag<Connection>();
        var selector = new RelayNetworkPathSelector((_, _) => Task.FromResult<IReadOnlyList<RelayNetworkPath>>([Wifi]),
            () => now, backgroundProbes: true);
        using Connection live = await selector.ConnectAsync(Options, async (path, token) =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
            {
                if (path is null) await Task.Delay(Timeout.Infinite, token);
                return new Connection();
            }
            started.TrySetResult();
            await release.Task; // Deliberately ignore audit cancellation.
            var connection = new Connection(); measured.Add(connection); return connection;
        }, default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 20; i++)
            {
                now += 31_000;
                using var foreground = await selector.ConnectAsync(Options,
                    (_, _) => Task.FromResult(new Connection()), default).WaitAsync(TimeSpan.FromSeconds(2));
            }
            Assert.InRange(Volatile.Read(ref calls), 3, 4);
            Assert.False(live.Disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (measured.Count != 2 || measured.Any(item => !item.Disposed.Task.IsCompleted))
            await Task.Delay(10, limit.Token);
    }

    [Fact]
    public async Task TimedOutAuditRetainsOwnershipUntilLateNativeWorkEnds()
    {
        var release = new TaskCompletionSource<Connection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new Connection();
        Task audit = new RelayNetworkPathSelector().ProbeRoundAsync(new RelayPathStability(), [Wifi],
            async (_, token) =>
            {
                using var registration = token.Register(() => expired.TrySetResult());
                return await release.Task;
            }, TimeSpan.FromMilliseconds(30));
        try
        {
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(audit.IsCompleted);
        }
        finally { release.TrySetResult(late); }
        await audit.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(late.Disposed.Task.IsCompleted);
    }

    [Fact]
    public async Task SlowOptionalDiscoveryDoesNotConsumeTheConnectionDeadline()
    {
        var discovery = new TaskCompletionSource<IReadOnlyList<RelayNetworkPath>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selector = new RelayNetworkPathSelector((_, _) => discovery.Task,
            discoveryTimeout: TimeSpan.FromMilliseconds(25));
        int calls = 0;
        try
        {
            using var result = await selector.ConnectAsync(Options, (path, token) =>
            {
                Assert.Null(path);
                calls++;
                return Task.FromResult(new Connection());
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, calls);
            Assert.False(discovery.Task.IsCompleted);
        }
        finally { discovery.TrySetException(new IOException("late optional DNS failure")); }
    }

    [Fact]
    public async Task DiscoveryFailureFallsBackButDoesNotHideTlsFailure()
    {
        var selector = new RelayNetworkPathSelector((_, _) =>
            throw new System.Net.NetworkInformation.NetworkInformationException());
        await Assert.ThrowsAsync<AuthenticationException>(() => selector.ConnectAsync<Connection>(Options,
            (path, token) =>
            {
                Assert.Null(path);
                throw new AuthenticationException("pin rejected");
            }, CancellationToken.None));
    }

    [Fact]
    public async Task CancellingDiscoveryDoesNotStartAFallbackConnection()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selector = new RelayNetworkPathSelector(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }, discoveryTimeout: TimeSpan.FromSeconds(10));
        using var stop = new CancellationTokenSource();
        int calls = 0;
        Task result = selector.ConnectAsync(Options, (path, token) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new Connection());
        }, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        Assert.Equal(0, calls);
    }
}
