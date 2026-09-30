using System.Net;
using System.Net.Sockets;

namespace RemoteDesk;

// Race TCP establishment only. Authentication starts on exactly one winner,
// so alternate DNS addresses never take over another active desktop session.
internal static class DualStackConnector
{
    internal static async Task<TcpClient> ConnectAsync(string host, int port,
        Action<TcpClient> configure, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(addresses, port, configure, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<IPAddress> Interleave(IEnumerable<IPAddress> values)
    {
        var remaining = values.Where(ip => ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct().Take(32).ToList();
        var result = new List<IPAddress>();
        AddressFamily next = remaining.FirstOrDefault()?.AddressFamily ?? AddressFamily.InterNetwork;
        while (remaining.Count > 0 && result.Count < 8)
        {
            int index = remaining.FindIndex(ip => ip.AddressFamily == next);
            if (index < 0) index = 0;
            IPAddress selected = remaining[index];
            remaining.RemoveAt(index); result.Add(selected);
            next = selected.AddressFamily == AddressFamily.InterNetwork
                ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        }
        return result;
    }

    internal static async Task<TcpClient> ConnectAsync(IEnumerable<IPAddress> addresses, int port,
        Action<TcpClient> configure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<IPAddress> candidates = Interleave(addresses);
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var owned = new List<TcpClient>();
        var pending = new List<Task<TcpClient>>();
        TcpClient? winner = null;
        Exception? failure = null;
        int index = 0;
        async Task<TcpClient> Start(IPAddress address)
        {
            var client = new TcpClient(address.AddressFamily);
            owned.Add(client);
            configure(client);
            await client.ConnectAsync(address, port, attempts.Token).ConfigureAwait(false);
            return client;
        }
        try
        {
            if (candidates.Count > 0) pending.Add(Start(candidates[index++]));
            Task delay = Task.Delay(250, attempts.Token);
            while (pending.Count > 0)
            {
                Task completed = index < candidates.Count
                    ? await Task.WhenAny(pending.Cast<Task>().Append(delay)).ConfigureAwait(false)
                    : await Task.WhenAny(pending).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (completed == delay)
                {
                    pending.Add(Start(candidates[index++]));
                    delay = Task.Delay(250, attempts.Token);
                    continue;
                }
                var attempt = (Task<TcpClient>)completed;
                pending.Remove(attempt);
                try { winner = await attempt.ConfigureAwait(false); return winner; }
                catch (Exception ex) when (ex is SocketException or IOException or NotSupportedException)
                {
                    failure = ex;
                    // A refused/unroutable address should not add the stagger delay.
                    if (index < candidates.Count)
                    {
                        pending.Add(Start(candidates[index++]));
                        delay = Task.Delay(250, attempts.Token);
                    }
                }
            }
            throw failure ?? new SocketException((int)SocketError.HostNotFound);
        }
        finally
        {
            attempts.Cancel();
            foreach (TcpClient client in owned) if (!ReferenceEquals(client, winner)) client.Dispose();
            // Observe failed losers without keeping their socket or background work alive.
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        }
    }
}
