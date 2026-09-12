using System.Diagnostics;
using System.Net;
using System.Security.Authentication;

namespace RemoteDesk;

/// <summary>
/// An opt-out, elevated desktop feature for a configured public IPv4 relay.
/// Trials happen before connections exist; a retained route is renewed, never
/// raced against another route mid-session. The kernel lease bounds crash cleanup.
/// </summary>
internal sealed class WindowsRelayNetworkOptimizer : IDisposable
{
    internal const uint LeaseSeconds = 90;
    private readonly IRelayRouteBackend _backend;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<RelayNetworkPath>>> _paths;
    private readonly Func<RelayConnectionOptions, RelayNetworkPath?, CancellationToken, Task<double>> _probe;
    private readonly Func<long> _now;
    private readonly Func<IPAddress, bool> _recentTraffic;
    private readonly Action<IPAddress> _disconnect;
    private readonly TimeSpan _probeTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private IRelayRouteLease? _lease;
    private RelayConnectionOptions? _options;
    private string? _key;
    private string? _topologyKey;
    private long _retryAfter;
    private int _healthFailures;
    private bool _disposed;
    private string _status = "中继使用系统路由";

    internal event Action<string>? StatusChanged;
    internal string Status { get { lock (_sync) return _status; } }
    internal RelayNetworkPath? ActivePath { get { lock (_sync) return _lease?.Path; } }

    internal WindowsRelayNetworkOptimizer(IRelayRouteBackend? backend = null,
        Func<string, CancellationToken, Task<IReadOnlyList<RelayNetworkPath>>>? paths = null,
        Func<RelayConnectionOptions, RelayNetworkPath?, CancellationToken, Task<double>>? probe = null,
        Func<long>? now = null, bool maintain = true, TimeSpan? probeTimeout = null,
        Func<IPAddress, bool>? recentTraffic = null, Action<IPAddress>? disconnect = null)
    {
        _backend = backend ?? new WindowsRelayRouteBackend();
        _paths = paths ?? RelayNetworkPathSelector.Shared.DiscoverPathsAsync;
        _probe = probe ?? MeasureTlsAsync;
        _now = now ?? (() => Environment.TickCount64);
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(2);
        _recentTraffic = recentTraffic ?? RelayConnectionActivity.Shared.HasRecentTraffic;
        _disconnect = disconnect ?? RelayConnectionActivity.Shared.Disconnect;
        if (maintain) _ = MaintainLoopAsync();
    }

    internal Task OptimizeAsync(RelayConnectionOptions options, bool enabled, CancellationToken token) =>
        // Native adapter/route enumeration must not block the WinForms thread.
        Task.Run(() => OptimizeCoreAsync(options, enabled, token), token);

    private async Task OptimizeCoreAsync(RelayConnectionOptions options, bool enabled, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        bool entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            string key = $"{options.ServerAddress}:{options.Port}/{options.TlsCertificateSha256}";
            if (_key != key || !enabled)
            {
                ReleaseLease(disconnect: true);
                _retryAfter = 0;
                _key = key;
                _topologyKey = null;
            }
            if (!enabled || Environment.GetEnvironmentVariable("REMOTEDESK_RELAY_SYSTEM_ROUTE_ONLY") == "1")
            {
                ReleaseLease(disconnect: true);
                SetStatus("中继使用系统路由（自动出口优化已关闭）");
                return;
            }
            if (ActivePath is not null) return; // Never re-route an established session to chase latency.
            if (!_backend.Available)
            {
                SetStatus("中继使用系统路由（临时路由优化需要管理员权限）");
                return;
            }
            if (!IPAddress.TryParse(options.ServerAddress, out IPAddress? address) ||
                !RelayNetworkPathSelector.IsEligibleRemoteAddress(address))
            {
                SetStatus("中继使用系统路由（临时路由仅用于公网 IPv4 地址）");
                return;
            }
            IReadOnlyList<RelayNetworkPath> paths = await _paths(options.ServerAddress, deadline.Token).ConfigureAwait(false);
            string topologyKey = string.Join('|', paths.Select(path => path.Identity).Order(StringComparer.Ordinal));
            if (_topologyKey != topologyKey)
            {
                // A failed trial on the previous Wi-Fi must not suppress the
                // first reconnect on a newly available address/gateway.
                _topologyKey = topologyKey;
                _retryAfter = 0;
            }
            if (_now() < _retryAfter) return;
            _retryAfter = _now() + 60_000;
            if (paths.Select(path => path.InterfaceId).Distinct().Count() < 2) return;
            if (!_backend.CanChange(address))
            {
                SetStatus("中继使用系统路由（已有连接或专用路由，暂不切换）");
                return;
            }
            SetStatus("正在比较中继线路；首次连接最多增加 8 秒...");
            // Handshake probes never send an access token or register a device.
            var probeOptions = options with { AccessToken = string.Empty, DeviceId = string.Empty };
            double baseline = await MeasureAsync(probeOptions, null, deadline.Token).ConfigureAwait(false);
            if (baseline < 200)
            {
                SetStatus($"中继系统线路正常（TLS {baseline:F0} ms），无需临时路由");
                return;
            }
            // Use the better baseline and the worse candidate sample, so a
            // transient slow handshake cannot trigger a route change.
            baseline = Math.Min(baseline, await MeasureAsync(probeOptions, null, deadline.Token).ConfigureAwait(false));
            foreach (RelayNetworkPath path in paths.Take(4))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!path.RemoteAddress.Equals(address)) continue;
                IRelayRouteLease? candidate = _backend.TryCreate(path, LeaseSeconds);
                if (candidate is null) continue;
                try
                {
                    double measured = await MeasureAsync(probeOptions, path, deadline.Token).ConfigureAwait(false);
                    if (!IsWorthSwitching(baseline, measured)) continue;
                    measured = Math.Max(measured, await MeasureAsync(probeOptions, path, deadline.Token).ConfigureAwait(false));
                    if (!IsWorthSwitching(baseline, measured) || !candidate.Renew()) continue;
                    lock (_sync)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (_disposed) return;
                        _lease = candidate;
                        _options = probeOptions;
                        _healthFailures = 0;
                        candidate = null;
                    }
                    SetStatus($"中继临时线路：{path.InterfaceName}，TLS {measured:F0} ms；90 秒自动过期、运行中续期");
                    return; // Prefer stability over repeatedly probing for marginal gains.
                }
                finally { candidate?.Dispose(); }
            }
            SetStatus("中继使用系统路由（未找到稳定且明显更快的线路）");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            SetStatus("中继线路检测已结束，继续使用系统连接");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Optimization is optional; the normal connection will report its
            // own socket or certificate error with the original pin intact.
            SetStatus(ex is AuthenticationException ? "线路检测未通过中继证书校验，保留系统路由" :
                "中继线路检测不可用，保留系统路由");
        }
        finally { if (entered) _gate.Release(); }
    }

    internal static bool IsWorthSwitching(double baseline, double candidate) =>
        double.IsFinite(candidate) && candidate >= 0 &&
        (double.IsPositiveInfinity(baseline) || baseline - candidate >= 50 && candidate <= baseline * 0.7);

    private async Task<double> MeasureAsync(RelayConnectionOptions options, RelayNetworkPath? path, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_probeTimeout);
        try { return await _probe(options, path, deadline.Token).ConfigureAwait(false); }
        catch (AuthenticationException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return double.PositiveInfinity; }
    }

    internal static async Task<double> MeasureTlsAsync(RelayConnectionOptions options, RelayNetworkPath? path, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var connection = await RelayTls.ConnectPathAsync(options, path, token, dataTunnel: false).ConfigureAwait(false);
        return watch.Elapsed.TotalMilliseconds;
    }

    private async Task MaintainLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await MaintainAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    internal async Task MaintainAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RelayNetworkPath? path;
            RelayConnectionOptions? options;
            bool renewed;
            lock (_sync)
            {
                if (_disposed || _lease is null) return;
                path = _lease.Path;
                options = _options;
                renewed = _lease.Renew();
            }
            if (!renewed || Environment.GetEnvironmentVariable("REMOTEDESK_RELAY_SYSTEM_ROUTE_ONLY") == "1")
            {
                ReleaseLease(disconnect: true);
                SetStatus("中继临时线路失效，已回退系统线路并重连");
                return;
            }
            if (_recentTraffic(path.RemoteAddress)) { _healthFailures = 0; return; }
            double health;
            try { health = await MeasureAsync(options!, path, token).ConfigureAwait(false); }
            catch (AuthenticationException) { health = double.PositiveInfinity; }
            if (double.IsFinite(health)) _healthFailures = 0;
            else if (++_healthFailures >= 2 && !_recentTraffic(path.RemoteAddress))
            {
                ReleaseLease(disconnect: true);
                _retryAfter = _now() + 60_000;
                SetStatus("中继临时线路连续无响应，已回退系统线路并重连");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            ReleaseLease(disconnect: true);
            SetStatus("中继临时路由无法续期，已回退系统线路");
        }
        finally { _gate.Release(); }
    }

    private void ReleaseLease(bool disconnect)
    {
        IRelayRouteLease? lease;
        lock (_sync) { lease = _lease; _lease = null; _options = null; }
        if (lease is null) return;
        lease.Dispose();
        if (disconnect) _disconnect(lease.Path.RemoteAddress);
    }

    private void SetStatus(string value)
    {
        lock (_sync) { if (_disposed || _status == value) return; _status = value; }
        try { StatusChanged?.Invoke(value); } catch { /* A closing UI must not break lease maintenance. */ }
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        _stop.Cancel();
        ReleaseLease(disconnect: false);
        // An in-flight trial owns its candidate in finally and has a short
        // cancellation deadline; never synchronously wait on the UI here.
    }
}
