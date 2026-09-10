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
    public async Task CacheAvoidsExtraHandshakesAndExpiresWithoutSliding()
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
