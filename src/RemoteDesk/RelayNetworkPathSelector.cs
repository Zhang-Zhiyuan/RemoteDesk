using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;

namespace RemoteDesk;

internal sealed record RelayNetworkPath(
    string InterfaceId, string InterfaceName, int InterfaceIndex,
    IPAddress LocalAddress, IPAddress RemoteAddress, string Gateway)
{
    internal string Identity => $"{InterfaceId}/{InterfaceIndex}/{LocalAddress}/{RemoteAddress}/{Gateway}";

    internal void Bind(Socket socket)
    {
        // IP_UNICAST_IF accepts the IPv4 interface index in network byte order.
        // This changes only this socket, never routes, metrics or other apps.
        const SocketOptionName ipUnicastIf = (SocketOptionName)31; // Windows IP_UNICAST_IF; not named in .NET 8.
        socket.SetSocketOption(SocketOptionLevel.IP, ipUnicastIf,
            IPAddress.HostToNetworkOrder(InterfaceIndex));
        socket.Bind(new IPEndPoint(LocalAddress, 0));
    }
}

/// <summary>
/// Race pinned TLS handshakes, not ping or unauthenticated TCP accepts. Only the
/// winner receives relay credentials and a role, so probes cannot replace a host
/// registration or create duplicate remote sessions. Existing sessions stay put.
/// </summary>
internal sealed class RelayNetworkPathSelector
{
    internal static readonly RelayNetworkPathSelector Shared = new(backgroundProbes: true);
    internal const int CachedPathHeadStartMilliseconds = 150;
    internal const int DiscoveryTimeoutMilliseconds = 250;
    private readonly ConcurrentDictionary<string, RelayPathStability> _preferred = new();
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<RelayNetworkPath>>> _paths;
    private readonly Func<long> _now;
    private readonly TimeSpan _discoveryTimeout;
    private readonly bool _backgroundProbes;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    internal RelayNetworkPathSelector(
        Func<string, CancellationToken, Task<IReadOnlyList<RelayNetworkPath>>>? paths = null,
        Func<long>? now = null,
        TimeSpan? discoveryTimeout = null, bool backgroundProbes = false)
    {
        _paths = paths ?? GetPathsAsync;
        _now = now ?? (() => Environment.TickCount64);
        _discoveryTimeout = discoveryTimeout ?? TimeSpan.FromMilliseconds(DiscoveryTimeoutMilliseconds);
        _backgroundProbes = backgroundProbes;
    }

    internal async Task<T> ConnectAsync<T>(RelayConnectionOptions options,
        Func<RelayNetworkPath?, CancellationToken, Task<T>> connect,
        CancellationToken cancellationToken) where T : class, IDisposable
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RelayNetworkPath> paths = await DiscoverPathsAsync(options.ServerAddress, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Count == 0)
            return await connect(null, cancellationToken).ConfigureAwait(false);

        // A changed address, interface index, gateway or DNS answer invalidates
        // the preference. No credentials or device IDs belong in this cache.
        RelayPathStability stability = GetStability(options, paths);
        string? cached = stability.Preferred(_now());
        var candidates = new List<RelayNetworkPath?> { null }; // Preserve normal IPv4/IPv6/DNS routing.
        candidates.AddRange(paths);
        if (cached is not null)
        {
            int index = candidates.FindIndex(path => Identity(path) == cached);
            if (index >= 0)
            {
                RelayNetworkPath? first = candidates[index];
                candidates.RemoveAt(index);
                candidates.Insert(0, first);
            }
            else cached = null;
        }

        int preferredFailed = 0;
        async Task<T> Attempt(RelayNetworkPath? path, CancellationToken token)
        {
            try { return await connect(path, token).ConfigureAwait(false); }
            catch
            {
                if (!token.IsCancellationRequested && Identity(path) == cached)
                    Interlocked.Exchange(ref preferredFailed, 1);
                throw;
            }
        }
        (RelayNetworkPath? path, T connection) = await RaceAsync(candidates, Attempt,
            cached is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(CachedPathHeadStartMilliseconds),
            cancellationToken).ConfigureAwait(false);
        stability.Connected(Identity(path), _now(), Volatile.Read(ref preferredFailed) != 0 ? cached : null);
        if (_backgroundProbes && _probeGate.Wait(0))
        {
            long scheduledAt = _now();
            if (stability.BeginProbe(scheduledAt))
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Let initial video/authentication start first. This is
                        // detached, bounded TLS-only sampling, never a relay role.
                        await Task.Delay(1000).ConfigureAwait(false);
                        using var discoveryStop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        var current = await DiscoverPathsAsync(options.ServerAddress, discoveryStop.Token).ConfigureAwait(false);
                        if (!current.Select(item => item.Identity).Order(StringComparer.Ordinal)
                            .SequenceEqual(paths.Select(item => item.Identity).Order(StringComparer.Ordinal))) return;
                        await ProbeRoundAsync(stability, candidates, connect, observedAt: scheduledAt).ConfigureAwait(false);
                    }
                    catch { /* Optional measurement cannot break a live connection. */ }
                    finally { _probeGate.Release(); }
                });
            else _probeGate.Release();
        }
        return connection;
    }

    private static string Identity(RelayNetworkPath? path) => path?.Identity ?? RelayPathStability.SystemPath;

    internal RelayPathStability GetStability(RelayConnectionOptions options, IReadOnlyList<RelayNetworkPath> paths)
    {
        string key = $"{options.ServerAddress.ToLowerInvariant()}:{options.Port}/{options.TlsCertificateSha256}/" +
            string.Join('|', paths.Select(path => path.Identity).Order(StringComparer.Ordinal));
        if (_preferred.Count >= 32 && !_preferred.ContainsKey(key)) _preferred.Clear();
        return _preferred.GetOrAdd(key, _ => new());
    }

    internal async Task ProbeRoundAsync<T>(RelayPathStability stability, IReadOnlyList<RelayNetworkPath?> paths,
        Func<RelayNetworkPath?, CancellationToken, Task<T>> connect, TimeSpan? timeout = null, long? observedAt = null)
        where T : class, IDisposable
    {
        long roundAt = observedAt ?? _now();
        using var stop = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        var raw = new List<Task<double>>();
        foreach (RelayNetworkPath? path in paths)
            raw.Add(Measure(path));
        var observations = new Dictionary<string, double>();
        for (int i = 0; i < raw.Count; i++)
        {
            double value;
            try { value = await raw[i].WaitAsync(stop.Token).ConfigureAwait(false); }
            catch { value = double.PositiveInfinity; }
            observations[Identity(paths[i])] = value;
        }
        stability.ObserveRound(roundAt, observations);
        stop.Cancel();
        // Retain the single audit slot until even cancellation-ignoring native
        // work ends. Repeated reconnects cannot accumulate stranded probes.
        await Task.WhenAll(raw).ConfigureAwait(false);

        async Task<double> Measure(RelayNetworkPath? path)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using T result = await connect(path, stop.Token).ConfigureAwait(false);
                return stop.IsCancellationRequested ? double.PositiveInfinity : watch.Elapsed.TotalMilliseconds;
            }
            catch { return double.PositiveInfinity; }
        }
    }

    internal async Task<IReadOnlyList<RelayNetworkPath>> DiscoverPathsAsync(
        string serverAddress, CancellationToken cancellationToken)
    {
        // Optional adapter enumeration/DNS must not consume the entire relay
        // deadline, or block a UI caller before the first asynchronous yield.
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken discoveryToken = stop.Token;
        Task<IReadOnlyList<RelayNetworkPath>> discovery = Task.Run(
            () => _paths(serverAddress, discoveryToken), CancellationToken.None);
        try
        {
            return await discovery.WaitAsync(_discoveryTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Discovery is advisory. The ordinary connection retains its own
            // DNS, pin verification and original cancellation/timeout policy.
            return [];
        }
        finally
        {
            stop.Cancel();
            _ = discovery.ContinueWith(completed =>
            {
                _ = completed.Exception; // Observe even a late DNS/enumeration fault.
                stop.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal static async Task<(RelayNetworkPath? Path, T Connection)> RaceAsync<T>(
        IReadOnlyList<RelayNetworkPath?> paths,
        Func<RelayNetworkPath?, CancellationToken, Task<T>> connect,
        TimeSpan headStart,
        CancellationToken cancellationToken) where T : class, IDisposable
    {
        if (paths.Count == 0) throw new ArgumentException("At least one path is required.", nameof(paths));
        cancellationToken.ThrowIfCancellationRequested();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var primaryFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Dictionary<Task<T>, RelayNetworkPath?>();
        var errors = new List<Exception>();
        try
        {
            for (int index = 0; index < paths.Count; index++)
            {
                RelayNetworkPath? path = paths[index];
                pending.Add(AttemptAsync(path, index == 0), path);
            }
            while (pending.Count > 0)
            {
                Task<T> completed = await Task.WhenAny(pending.Keys).WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                RelayNetworkPath? path = pending[completed];
                pending.Remove(completed);
                try
                {
                    T result = await completed.ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Dispose();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    return (path, result);
                }
                catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
                    OperationCanceledException or ObjectDisposedException)
                {
                    errors.Add(ex);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // A captive portal or intercepted fast path must not beat a valid
            // slower TLS handshake. If every path fails, retain pin failures.
            throw errors.OfType<AuthenticationException>().FirstOrDefault() ??
                errors.FirstOrDefault() ?? new IOException("没有可用的中继网络路径。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A dead route may outlive a completed pin rejection on another
            // route. Carry that observed failure through the shared deadline,
            // while preserving OperationCanceledException for explicit cancel.
            AuthenticationException? rejected = errors.OfType<AuthenticationException>().FirstOrDefault() ??
                pending.Keys.Where(task => task.IsFaulted)
                    .SelectMany(task => task.Exception!.InnerExceptions)
                    .OfType<AuthenticationException>().FirstOrDefault();
            if (rejected is null) throw;
            throw new OperationCanceledException("中继线路选择已取消。", rejected, cancellationToken);
        }
        finally
        {
            stop.Cancel();
            // Never wait for a slow losing handshake before returning the winner.
            // Observe every loser, including a late success, and release its TLS
            // stream/socket; keep its cancellation source alive until then.
            _ = DisposeRemainingAsync(pending.Keys.ToArray(), stop);
        }

        async Task<T> AttemptAsync(RelayNetworkPath? path, bool primary)
        {
            try
            {
                if (!primary && headStart > TimeSpan.Zero)
                    await Task.WhenAny(primaryFailed.Task, Task.Delay(headStart, stop.Token))
                        .ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                return await connect(path, stop.Token).ConfigureAwait(false);
            }
            catch
            {
                if (primary) primaryFailed.TrySetResult();
                throw;
            }
        }
    }

    private static async Task DisposeRemainingAsync<T>(Task<T>[] tasks, CancellationTokenSource stop)
        where T : class, IDisposable
    {
        try
        {
            await Task.WhenAll(tasks.Select(async task =>
            {
                try { (await task.ConfigureAwait(false)).Dispose(); }
                catch { /* Every fault is observed; only the winner is handed to the caller. */ }
            })).ConfigureAwait(false);
        }
        finally { stop.Dispose(); }
    }

    internal static bool IsEligibleRemoteAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    internal static async Task<IReadOnlyList<RelayNetworkPath>> GetPathsAsync(
        string serverAddress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("REMOTEDESK_RELAY_SYSTEM_ROUTE_ONLY") == "1" ||
            string.Equals(serverAddress, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(serverAddress, out IPAddress? literal) && !IsEligibleRemoteAddress(literal))
            return [];
        try
        {
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
            // Respect an active tunnel/PPP policy instead of preferring a
            // physical path around it. The normal system route is always usable.
            if (all.Any(nic => nic.OperationalStatus == OperationalStatus.Up &&
                nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)) return [];
            var local = new List<(NetworkInterface Nic, IPAddress Address, int Index, string Gateway)>();
            foreach (NetworkInterface nic in all)
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
                IPInterfaceProperties properties = nic.GetIPProperties();
                string[] gateways = properties.GatewayAddresses.Select(value => value.Address)
                    .Where(value => value.AddressFamily == AddressFamily.InterNetwork && !value.Equals(IPAddress.Any))
                    .Select(value => value.ToString()).Order(StringComparer.Ordinal).ToArray();
                if (gateways.Length == 0) continue;
                int index = properties.GetIPv4Properties().Index;
                if (index is <= 0 or > 0x00ffffff) continue;
                // TAP/VPN adapters can report themselves as Ethernet. An
                // active non-hardware gateway is policy, not an alternate uplink.
                if (!WindowsRelayRouteBackend.IsPhysicalInterface(checked((uint)index))) return [];
                IPAddress? address = properties.UnicastAddresses.Where(value =>
                    value.Address.AddressFamily == AddressFamily.InterNetwork &&
                    value.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred &&
                    !IPAddress.IsLoopback(value.Address) && !value.Address.Equals(IPAddress.Any) &&
                    !(value.Address.GetAddressBytes()[0] == 169 && value.Address.GetAddressBytes()[1] == 254))
                    .Select(value => value.Address).FirstOrDefault();
                if (address is not null) local.Add((nic, address, index, string.Join(',', gateways)));
            }
            if (local.Count < 2) return [];
            IPAddress[] remote = IPAddress.TryParse(serverAddress, out IPAddress? ip) ? [ip] :
                await Dns.GetHostAddressesAsync(serverAddress, cancellationToken).ConfigureAwait(false);
            // Mixed private/public DNS must follow the system resolver's normal
            // behavior, not trigger probes on a different network.
            if (remote.Any(value => value.AddressFamily == AddressFamily.InterNetwork &&
                !IsEligibleRemoteAddress(value))) return [];
            IPAddress? destination = remote.FirstOrDefault(IsEligibleRemoteAddress);
            if (destination is null) return [];
            return local.OrderBy(value => value.Nic.Id, StringComparer.Ordinal).Take(4)
                .Select(value => new RelayNetworkPath(value.Nic.Id, value.Nic.Name, value.Index,
                    value.Address, destination, value.Gateway)).ToArray();
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or
            InvalidOperationException or NotSupportedException or ObjectDisposedException)
        {
            return []; // Discovery is optional; never prevent an ordinary connection.
        }
    }

    internal static string DescribeLocalEndpoint(EndPoint? endpoint)
    {
        if (endpoint is not IPEndPoint ip) return "系统默认网络";
        IPAddress address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        try
        {
            NetworkInterface? adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(nic =>
                nic.GetIPProperties().UnicastAddresses.Any(value => value.Address.Equals(address)));
            if (adapter is not null) return $"{adapter.Name} ({address})";
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or
            InvalidOperationException or NotSupportedException or ObjectDisposedException) { }
        return address.ToString();
    }
}
