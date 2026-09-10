using System.Net;
using RemoteDesk;

// Explicit opt-in on the authorized configured relay only. Never relax the
// product's existing-connection, route ownership, elevation or gateway checks.
internal static class WindowsRelayRouteProbe
{
    internal sealed class ShortLeaseBackend : IRelayRouteBackend
    {
        private readonly WindowsRelayRouteBackend _backend = new();
        public bool Available => _backend.Available;
        public bool CanChange(IPAddress address) => _backend.CanChange(address);
        public IRelayRouteLease? TryCreate(RelayNetworkPath path, uint seconds) => _backend.TryCreate(path, 8);
    }

    internal static object Snapshot(string server) => new WindowsRelayRouteBackend().ReadRoutes()
        .Where(row => row.Destination.Length == 0 || row.Matches(IPAddress.Parse(server)))
        .Select(row => new {
            prefix = $"{row.Destination.Address.Address}/{row.Destination.Length}",
            interfaceIndex = row.InterfaceIndex, nextHop = row.NextHop.Address.ToString(),
            metric = row.Metric, lifetimeSeconds = row.ValidLifetime,
            immortal = row.Immortal, protocol = row.Protocol, origin = row.Origin
        }).ToArray();

    internal static async Task<int> RunAsync(RelayConnectionOptions options, string output)
    {
        var backend = new WindowsRelayRouteBackend();
        var address = IPAddress.Parse(options.ServerAddress);
        object before = Snapshot(options.ServerAddress);
        bool allowed = backend.CanChange(address);
        if (!allowed)
        {
            Program.Save(Path.Combine(output, "route-lease.json"), new { allowed, before,
                skipped = "Existing connection/policy, or no elevation. No route changes attempted." });
            return 2;
        }
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var paths = await RelayNetworkPathSelector.GetPathsAsync(options.ServerAddress, limit.Token);
        RelayNetworkPath path = paths.FirstOrDefault(item => item.InterfaceName.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Expected authorized Wi-Fi test interface not found");
        var steps = new List<object>();
        bool complete = false;
        string? failure = null;
        try
        {
            using (var lease = backend.TryCreate(path, 8) ?? throw new IOException("Native route creation refused"))
            {
                steps.Add(new { step = "created", routes = Snapshot(options.ServerAddress) });
                double tls = await WindowsRelayNetworkOptimizer.MeasureTlsAsync(options, path, limit.Token);
                steps.Add(new { step = "pinnedTls", milliseconds = tls });
                await Task.Delay(3000, limit.Token);
                if (!lease.Renew()) throw new IOException("Native lease renewal failed");
                steps.Add(new { step = "renewed", routes = Snapshot(options.ServerAddress) });
                await Task.Delay(6000, limit.Token);
                if (!backend.ReadRoutes().Any(row => row.Destination.Length == 32 && row.Matches(address)))
                    throw new IOException("Renewed route expired before the extended lifetime");
            }
            steps.Add(new { step = "disposed", routes = Snapshot(options.ServerAddress) });
            if (backend.ReadRoutes().Any(row => row.Destination.Length == 32 && row.Matches(address)))
                throw new IOException("Disposed route was not removed");
            using (var abandoned = backend.TryCreate(path, 8) ?? throw new IOException("Expiry trial creation refused"))
            {
                // No refresh: emulate the absence of a running lease owner.
                await Task.Delay(11_000, limit.Token);
                steps.Add(new { step = "kernelExpired", routes = Snapshot(options.ServerAddress) });
                if (backend.ReadRoutes().Any(row => row.Destination.Length == 32 && row.Matches(address)))
                    throw new IOException("Kernel failed to expire an unrefreshed route");
            }
            complete = true;
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            Program.Save(Path.Combine(output, "route-lease.json"), new { allowed, complete, failure, before, steps,
                after = Snapshot(options.ServerAddress), scope = "Only newly created finite /32 relay routes; no default route changes" });
        }
        Console.WriteLine($"Native route lease audit complete={complete}; {failure}");
        return complete ? 0 : 1;
    }
}
