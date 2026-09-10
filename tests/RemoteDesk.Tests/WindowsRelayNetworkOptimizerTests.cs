using System.Net;
using System.Security.Authentication;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsRelayNetworkOptimizerTests
{
    private static readonly RelayNetworkPath Wifi = new("wifi", "Wi-Fi", 3,
        IPAddress.Parse("10.16.169.189"), IPAddress.Parse("8.138.5.232"), "10.16.171.254");
    private static readonly RelayNetworkPath Wired = new("eth", "Ethernet", 18,
        IPAddress.Parse("10.7.9.79"), Wifi.RemoteAddress, "10.7.11.254");
    private static readonly RelayConnectionOptions Options = new("8.138.5.232", 56567,
        new string('a', 64), new string('b', 64), Guid.NewGuid().ToString());

    private sealed class Lease(RelayNetworkPath path) : IRelayRouteLease
    {
        public RelayNetworkPath Path { get; } = path;
        internal int Disposals, Renewals;
        internal bool Healthy = true;
        public bool Renew() { Renewals++; return Healthy; }
        public void Dispose() => Disposals++;
    }
    private sealed class Backend : IRelayRouteBackend
    {
        public bool Available { get; set; } = true;
        internal bool Allowed = true;
        internal readonly List<Lease> Leases = [];
        public bool CanChange(IPAddress address) => Allowed;
        public IRelayRouteLease? TryCreate(RelayNetworkPath path, uint seconds)
        {
            Assert.Equal(90u, seconds);
            if (!Allowed) return null;
            var lease = new Lease(path);
            Leases.Add(lease);
            return lease;
        }
    }
    private static WindowsRelayNetworkOptimizer Create(Backend backend,
        Func<RelayConnectionOptions, RelayNetworkPath?, CancellationToken, Task<double>>? probe = null,
        Func<long>? now = null, Action<IPAddress>? disconnect = null, Func<IPAddress, bool>? recent = null,
        IReadOnlyList<RelayNetworkPath>? paths = null) => new(backend,
            (_, _) => Task.FromResult(paths ?? (IReadOnlyList<RelayNetworkPath>)[Wifi, Wired]),
            probe ?? ((options, path, _) =>
            {
                Assert.Empty(options.AccessToken);
                Assert.Empty(options.DeviceId);
                return Task.FromResult(path is null ? 650d : 65d);
            }), now, maintain: false, recentTraffic: recent ?? (_ => false), disconnect: disconnect ?? (_ => { }));

    [Theory]
    [InlineData(650, 70, true)]
    [InlineData(650, 490, false)]
    [InlineData(100, 70, false)]
    [InlineData(650, double.PositiveInfinity, false)]
    [InlineData(double.PositiveInfinity, 500, true)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity, false)]
    [InlineData(650, double.NaN, false)]
    [InlineData(650, -1, false)]
    public void SwitchingRequiresMeaningfulStableGain(double baseline, double candidate, bool expected) =>
        Assert.Equal(expected, WindowsRelayNetworkOptimizer.IsWorthSwitching(baseline, candidate));

    [Fact]
    public async Task ImprovementIsRetainedRenewedAndDisposedExactlyOnce()
    {
        var backend = new Backend();
        using var optimizer = Create(backend);
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Equal(Wifi, optimizer.ActivePath);
        Assert.Equal(1, Assert.Single(backend.Leases).Renewals);
        await optimizer.MaintainAsync(default);
        Assert.Equal(2, backend.Leases[0].Renewals);
        optimizer.Dispose();
        optimizer.Dispose();
        Assert.Equal(1, backend.Leases[0].Disposals);
        Assert.Null(optimizer.ActivePath);
    }

    [Fact]
    public async Task ActiveLeaseNeverTriggersAlternativeTrials()
    {
        var backend = new Backend();
        using var optimizer = Create(backend);
        await optimizer.OptimizeAsync(Options, true, default);
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Single(backend.Leases);
        Assert.Equal(0, backend.Leases[0].Disposals);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task DisabledUnprivilegedAndExistingPolicyCannotCreateRoutes(bool enabled, bool available, bool allowed)
    {
        var backend = new Backend { Available = available, Allowed = allowed };
        using var optimizer = Create(backend, (_, _, _) => throw new Exception("No probes allowed"));
        await optimizer.OptimizeAsync(Options, enabled, default);
        Assert.Empty(backend.Leases);
    }

    [Theory]
    [InlineData("10.7.163.74")]
    [InlineData("relay.example.com")]
    [InlineData("::1")]
    public async Task NonPublicLiteralNeverCreatesRoutes(string server)
    {
        var backend = new Backend();
        using var optimizer = Create(backend);
        await optimizer.OptimizeAsync(Options with { ServerAddress = server }, true, default);
        Assert.Empty(backend.Leases);
    }

    [Fact]
    public async Task SingleInterfaceNeverCreatesRoutes()
    {
        var backend = new Backend();
        using var optimizer = Create(backend, paths: [Wifi]);
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Empty(backend.Leases);
    }

    [Fact]
    public async Task FastDefaultNeverNeedsRouteChanges()
    {
        var backend = new Backend();
        using var optimizer = Create(backend, (_, _, _) => Task.FromResult(60d));
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Empty(backend.Leases);
    }

    [Fact]
    public async Task LosingRoutesAreRemovedAndRetriesHaveCooldown()
    {
        var backend = new Backend();
        long clock = 1;
        using var optimizer = Create(backend, (_, _, _) => Task.FromResult(650d), now: () => clock);
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Null(optimizer.ActivePath);
        Assert.Equal(2, backend.Leases.Count);
        Assert.All(backend.Leases, lease => Assert.Equal(1, lease.Disposals));
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Equal(2, backend.Leases.Count);
        clock += 60_001;
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Equal(4, backend.Leases.Count);
    }

    [Fact]
    public async Task DefaultLatencySpikeDoesNotTriggerSwitch()
    {
        var backend = new Backend();
        int baselineCalls = 0;
        using var optimizer = Create(backend, (_, path, _) => Task.FromResult(path is null ?
            ++baselineCalls == 1 ? 650d : 80d : 60d));
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Null(optimizer.ActivePath);
        Assert.All(backend.Leases, lease => Assert.Equal(1, lease.Disposals));
    }

    [Fact]
    public async Task UnstableCandidateDoesNotTriggerSwitch()
    {
        var backend = new Backend();
        int calls = 0;
        using var optimizer = Create(backend, (_, path, _) => Task.FromResult(path is null ? 650d : ++calls % 2 == 1 ? 60d : 630d));
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Null(optimizer.ActivePath);
        Assert.All(backend.Leases, lease => Assert.Equal(1, lease.Disposals));
    }

    [Fact]
    public async Task CertificateFailureCannotRetainTemporaryRoute()
    {
        var backend = new Backend();
        using var optimizer = Create(backend, (_, path, _) => path is null ? Task.FromResult(650d) : throw new AuthenticationException());
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Null(optimizer.ActivePath);
        Assert.Equal(1, Assert.Single(backend.Leases).Disposals);
        Assert.Contains("证书", optimizer.Status);
    }

    [Fact]
    public async Task CandidateFailureFallsThroughToAnotherInterface()
    {
        var backend = new Backend();
        using var optimizer = Create(backend, (_, path, _) => path == Wifi ? throw new IOException() : Task.FromResult(path is null ? 650d : 60d));
        await optimizer.OptimizeAsync(Options, true, default);
        Assert.Equal(Wired, optimizer.ActivePath);
        Assert.Equal(1, backend.Leases[0].Disposals);
        Assert.Equal(0, backend.Leases[1].Disposals);
    }

    [Fact]
    public async Task CancellingTrialReleasesCandidateBeforeReturning()
    {
        var backend = new Backend();
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var optimizer = Create(backend, async (_, path, token) =>
        {
            if (path is null) return 650;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 60;
        });
        Task task = optimizer.OptimizeAsync(Options, true, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, Assert.Single(backend.Leases).Disposals);
        Assert.Null(optimizer.ActivePath);
    }

    [Fact]
    public async Task DisposeDuringTrialCannotPublishLateLease()
    {
        var backend = new Backend();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var optimizer = Create(backend, async (_, path, token) =>
        {
            if (path is null) return 650;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 60;
        });
        Task task = optimizer.OptimizeAsync(Options, true, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        optimizer.Dispose();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Assert.Single(backend.Leases).Disposals);
        Assert.Null(optimizer.ActivePath);
    }

    [Fact]
    public async Task GatewayOrOwnershipLossClosesOnlyAffectedRelayConnections()
    {
        var backend = new Backend();
        var disconnected = new List<IPAddress>();
        using var optimizer = Create(backend, disconnect: disconnected.Add);
        await optimizer.OptimizeAsync(Options, true, default);
        backend.Leases[0].Healthy = false;
        await optimizer.MaintainAsync(default);
        Assert.Null(optimizer.ActivePath);
        Assert.Equal(Wifi.RemoteAddress, Assert.Single(disconnected));
        Assert.Equal(1, backend.Leases[0].Disposals);
    }

    [Fact]
    public async Task RecentTrafficPreventsRedundantProbesAndFalseDisconnects()
    {
        var backend = new Backend();
        bool fail = false;
        using var optimizer = Create(backend, (_, path, _) => fail ? throw new IOException() : Task.FromResult(path is null ? 650d : 60d),
            recent: _ => true, disconnect: _ => Assert.Fail("Active traffic must stay connected"));
        await optimizer.OptimizeAsync(Options, true, default);
        fail = true;
        await optimizer.MaintainAsync(default);
        await optimizer.MaintainAsync(default);
        Assert.NotNull(optimizer.ActivePath);
    }

    [Fact]
    public async Task TwoHealthFailuresWithoutTrafficReleaseRoute()
    {
        var backend = new Backend();
        bool fail = false;
        var disconnected = new List<IPAddress>();
        using var optimizer = Create(backend, (_, path, _) => fail ? throw new IOException() : Task.FromResult(path is null ? 650d : 60d),
            disconnect: disconnected.Add);
        await optimizer.OptimizeAsync(Options, true, default);
        fail = true;
        await optimizer.MaintainAsync(default);
        Assert.NotNull(optimizer.ActivePath);
        await optimizer.MaintainAsync(default);
        Assert.Null(optimizer.ActivePath);
        Assert.Single(disconnected);
    }

    [Fact]
    public async Task DisablingFeatureRemovesItsRouteAndReconnects()
    {
        var backend = new Backend();
        var disconnected = new List<IPAddress>();
        using var optimizer = Create(backend, disconnect: disconnected.Add);
        await optimizer.OptimizeAsync(Options, true, default);
        await optimizer.OptimizeAsync(Options, false, default);
        Assert.Null(optimizer.ActivePath);
        Assert.Equal(1, Assert.Single(backend.Leases).Disposals);
        Assert.Single(disconnected);
    }

    [Fact]
    public async Task ChangedServerReleasesPreviousLeaseWithoutAdoptingIt()
    {
        var backend = new Backend();
        using var optimizer = Create(backend);
        await optimizer.OptimizeAsync(Options, true, default);
        await optimizer.OptimizeAsync(Options with { ServerAddress = "10.0.0.1" }, true, default);
        Assert.Null(optimizer.ActivePath);
        Assert.Equal(1, Assert.Single(backend.Leases).Disposals);
    }
}
