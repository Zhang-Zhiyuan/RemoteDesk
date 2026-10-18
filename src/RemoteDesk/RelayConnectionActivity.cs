using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace RemoteDesk;

/// <summary>Weak ownership: observes only this process's relay sockets, never direct sessions.</summary>
internal sealed class RelayConnectionActivity
{
    internal static readonly RelayConnectionActivity Shared = new();
    private readonly ConditionalWeakTable<Stream, Entry> _streams = new();
    private readonly List<WeakReference<Entry>> _entries = [];
    private readonly object _sync = new();
    private readonly Func<long> _now;
    internal RelayConnectionActivity(Func<long>? now = null) => _now = now ?? (() => Environment.TickCount64);

    internal sealed class Entry(IPAddress address, IPAddress localAddress, TcpClient client, Func<long> now)
    {
        internal IPAddress Address { get; } = address;
        internal IPAddress LocalAddress { get; } = localAddress;
        internal WeakReference<TcpClient> Client { get; } = new(client);
        private long _lastReceived = long.MinValue;
        internal void Touch() => Interlocked.Exchange(ref _lastReceived, now());
        internal bool IsRecent(long tick) => Interlocked.Read(ref _lastReceived) is var last && last != long.MinValue && tick - last < 30_000;
    }

    internal void Track(TcpClient client, Stream stream)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
            client.Client.LocalEndPoint is not IPEndPoint local) return;
        IPAddress address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        IPAddress localAddress = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
        var entry = new Entry(address, localAddress, client, _now);
        _streams.Add(stream, entry);
        lock (_sync)
        {
            _entries.RemoveAll(item => !item.TryGetTarget(out _));
            _entries.Add(new WeakReference<Entry>(entry));
        }
    }

    internal Entry? Find(Stream stream) => _streams.TryGetValue(stream, out Entry? entry) ? entry : null;
    internal bool HasRecentTraffic(RelayNetworkPath path) => HasRecentTraffic(path.RemoteAddress, path.LocalAddress);
    internal bool HasRecentTraffic(IPAddress address, IPAddress? localAddress = null)
    {
        lock (_sync) return _entries.Any(item => item.TryGetTarget(out Entry? entry) &&
            Matches(entry, address, localAddress) && entry.IsRecent(_now()));
    }
    internal void Disconnect(RelayNetworkPath path) => Disconnect(path.RemoteAddress, path.LocalAddress);
    internal void Disconnect(IPAddress address, IPAddress? localAddress = null)
    {
        lock (_sync)
        {
            foreach (var item in _entries)
                if (item.TryGetTarget(out Entry? entry) && Matches(entry, address, localAddress) && entry.Client.TryGetTarget(out TcpClient? client))
                    try { client.Dispose(); } catch { }
            _entries.RemoveAll(item => !item.TryGetTarget(out Entry? entry) || Matches(entry, address, localAddress));
        }
    }

    private static bool Matches(Entry entry, IPAddress address, IPAddress? localAddress) =>
        entry.Address.Equals(address) && (localAddress is null || entry.LocalAddress.Equals(localAddress));
}
