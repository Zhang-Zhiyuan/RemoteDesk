using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RemoteDesk;

// Read-only, pinned TLS handshakes. Never sends login/registration, switches an
// installed session, changes network settings, or activates another network.
internal static class RelayPathQualityProbe
{
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        RelaySettings saved = new AppSettingsService().Load().Relay;
        if (string.IsNullOrWhiteSpace(saved.ServerAddress) || saved.ServerAddress != config.GetProperty("expectedServer").GetString() ||
            string.IsNullOrWhiteSpace(saved.TlsCertificateSha256) || string.IsNullOrWhiteSpace(saved.SshHostKeySha256))
            throw new InvalidOperationException("Unexpected or unpinned relay; no probes sent.");
        var options = new RelayConnectionOptions(saved.ServerAddress, saved.RelayPort,
            string.Empty, saved.TlsCertificateSha256, string.Empty);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var paths = await RelayNetworkPathSelector.GetPathsAsync(options.ServerAddress, deadline.Token);
        if (paths.Select(path => path.InterfaceId).Distinct().Count() < 2)
            throw new IOException("Two eligible, already-connected physical uplinks are required.");
        var selector = new RelayNetworkPathSelector((_, _) => Task.FromResult(paths));
        var state = selector.GetStability(options, paths);
        var candidates = new List<RelayNetworkPath?> { null };
        candidates.AddRange(paths);
        var rounds = new List<object>();
        var watch = Stopwatch.StartNew();
        using (var seed = await selector.ConnectAsync(options, Dial, deadline.Token))
            Console.WriteLine($"INITIAL {RelayNetworkPathSelector.DescribeLocalEndpoint(seed.Client.Client.LocalEndPoint)}");
        for (int round = 0; round < 8; round++)
        {
            var remaining = TimeSpan.FromSeconds(round * 31) - watch.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, deadline.Token);
            var measured = new ConcurrentBag<object>();
            await selector.ProbeRoundAsync(state, candidates, async (path, token) =>
            {
                var sample = Stopwatch.StartNew();
                try
                {
                    var connection = await Dial(path, token);
                    measured.Add(new { path = path?.InterfaceName ?? "system", milliseconds = sample.Elapsed.TotalMilliseconds,
                        localAddress = connection.Client.Client.LocalEndPoint?.ToString(), success = true });
                    return connection;
                }
                catch (Exception error)
                {
                    measured.Add(new { path = path?.InterfaceName ?? "system", milliseconds = sample.Elapsed.TotalMilliseconds,
                        failureType = error.GetType().Name, success = false });
                    throw;
                }
            });
            string? preferred = state.Preferred(Environment.TickCount64);
            using var next = await selector.ConnectAsync(options, Dial, deadline.Token);
            var record = new {
                round, elapsedSeconds = watch.Elapsed.TotalSeconds,
                preferred = preferred == RelayPathStability.SystemPath ? "system" : paths.FirstOrDefault(path => path.Identity == preferred)?.InterfaceName,
                nextConnection = RelayNetworkPathSelector.DescribeLocalEndpoint(next.Client.Client.LocalEndPoint),
                measurements = measured.ToArray()
            };
            rounds.Add(record);
            Program.Save(Path.Combine(output, "path-quality.json"), new {
                complete = round == 7, server = options.ServerAddress, rounds,
                scope = "Isolated production selector with real TLS samples; no relay role, credentials, system route or installed session changes",
                minimumHoldSeconds = 180, samplingSeconds = 31
            });
            Console.WriteLine(JsonSerializer.Serialize(record, Program.Json));
        }
        return 0;

        Task<RelayTls.RelayTlsConnection> Dial(RelayNetworkPath? path, CancellationToken token) =>
            RelayTls.ConnectPathAsync(options, path, token, dataTunnel: false);
    }
}
