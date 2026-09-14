using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDesk;

// Bounded synthetic bytes through the actual product relay adapters. No input,
// desktop capture, app settings, server provisioning or persistent credentials.
internal static class RelayThroughputProbe
{
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        RelayConnectionOptions options = ReadOptions(config);
        if (config.TryGetProperty("linuxFeatureAuditTarget", out var featureTarget))
            return await LinuxFeatureRelayProbe.RunAsync(options, featureTarget.GetString()!, output);
        if (config.TryGetProperty("routeLeaseAudit", out var routeAudit) && routeAudit.GetBoolean())
            return await WindowsRelayRouteProbe.RunAsync(options, output);
        if (config.TryGetProperty("linuxProbePath", out var linuxProbePath))
        {
            string stage = linuxProbePath.GetString()!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(stage, "^/tmp/remotedesk-relay-path-[A-Za-z0-9]{8}$"))
                throw new InvalidOperationException("Unexpected Linux test path");
            var info = new ProcessStartInfo("ssh") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=6",
                "zzy@10.7.163.74", $"timeout 70s env PYTHONDONTWRITEBYTECODE=1 python3 {stage}/relay_path_linux_probe.py --stdin" })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {
                    serverAddress = options.ServerAddress, port = options.Port, accessToken = options.AccessToken,
                    tlsCertificateSha256 = options.TlsCertificateSha256, deviceId = options.DeviceId,
                    addressReportTest = config.TryGetProperty("addressReportTest", out var addressTest) && addressTest.GetBoolean()
                }));
                process.StandardInput.Close();
                await process.WaitForExitAsync(limit.Token);
                using var result = JsonDocument.Parse(await stdout);
                _ = await stderr; // Never print arbitrary subprocess stderr alongside credentials.
                Program.Save(Path.Combine(output, "linux-transport.json"), result.RootElement);
                Console.WriteLine(result.RootElement.GetRawText());
                return process.ExitCode == 0 && result.RootElement.GetProperty("complete").GetBoolean() ? 0 : 1;
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        if (config.TryGetProperty("androidForwardPort", out var androidPort))
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var control = new TcpClient();
            await control.ConnectAsync(IPAddress.Loopback, androidPort.GetInt32(), limit.Token);
            await RelayTls.WriteJsonAsync(control.GetStream(), new {
                serverAddress = options.ServerAddress, port = options.Port, accessToken = options.AccessToken,
                tlsCertificateSha256 = options.TlsCertificateSha256, deviceId = options.DeviceId,
                addressReportTest = config.TryGetProperty("addressReportTest", out var addressTest) && addressTest.GetBoolean()
            }, limit.Token);
            using var result = await RelayTls.ReadJsonAsync(control.GetStream(), limit.Token);
            Program.Save(Path.Combine(output, "android-transport.json"), result.RootElement);
            Console.WriteLine(result.RootElement.GetRawText());
            return result.RootElement.GetProperty("complete").GetBoolean() ? 0 : 1;
        }
        if (config.TryGetProperty("addressReportTest", out var localAddressTest) && localAddressTest.GetBoolean())
            return await RelayAddressProbe.RunAsync(options, output);
        if (config.TryGetProperty("pathsOnly", out var pathsOnly) && pathsOnly.GetBoolean())
        {
            var candidates = new List<RelayNetworkPath?> { null };
            candidates.AddRange(await RelayNetworkPathSelector.GetPathsAsync(options.ServerAddress, CancellationToken.None));
            var checks = await Task.WhenAll(candidates.Select(async path =>
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var watch = Stopwatch.StartNew();
                try
                {
                    using var connection = await RelayTls.ConnectPathAsync(options, path, limit.Token, false);
                    return new { path = path?.InterfaceName ?? "default", success = true,
                        ms = watch.Elapsed.TotalMilliseconds, detail = RelayNetworkPathSelector.DescribeLocalEndpoint(connection.Client.Client.LocalEndPoint) };
                }
                catch (Exception ex)
                {
                    return new { path = path?.InterfaceName ?? "default", success = false,
                        ms = watch.Elapsed.TotalMilliseconds, detail = ex.GetType().Name + ": " + ex.Message };
                }
            }));
            Console.WriteLine(JsonSerializer.Serialize(checks));
            Program.Save(Path.Combine(output, "paths.json"), checks);
            return checks.Any(check => check.success) ? 0 : 1;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        bool routeRecovery = config.TryGetProperty("routeRecovery", out var recovery) && recovery.GetBoolean();
        using var optimizer = routeRecovery ? new WindowsRelayNetworkOptimizer(new WindowsRelayRouteProbe.ShortLeaseBackend(), maintain: false) :
            config.TryGetProperty("optimizeRoute", out var optimizeRoute) && optimizeRoute.GetBoolean()
                ? new WindowsRelayNetworkOptimizer() : null;
        var routeDecisions = new List<string>();
        object? routesBefore = optimizer is null ? null : WindowsRelayRouteProbe.Snapshot(options.ServerAddress);
        if (optimizer is not null)
        {
            optimizer.StatusChanged += status => { lock (routeDecisions) routeDecisions.Add(status); Console.WriteLine(status); };
            await optimizer.OptimizeAsync(options, true, token);
        }
        object? routesDuring = optimizer is null ? null : WindowsRelayRouteProbe.Snapshot(options.ServerAddress);
        string? optimizedInterface = optimizer?.ActivePath?.InterfaceName;
        var paths = await RelayNetworkPathSelector.GetPathsAsync(options.ServerAddress, token);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var connector = new RelayHostConnector();
        var measurements = new List<object>();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Diagnostic(string message) { diagnostics.Enqueue(message); Console.WriteLine(message); }
        connector.StatusChanged += Diagnostic;
        Diagnostic("Relay candidates: " + string.Join(", ", paths.Select(path => $"{path.InterfaceName}/{path.LocalAddress}/{path.InterfaceIndex}")));
        bool complete = false;
        string? failure = null;
        Task echo = EchoAsync(listener, token);
        try
        {
            await connector.StartAsync(options, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            while (!(await RelayTunnelClient.ListDevicesAsync(options, token)).Any(device => device.DeviceId == options.DeviceId))
                await Task.Delay(200, token);
            for (int round = 0; round < 3; round++)
            {
                using var viewer = new TcpClient();
                NetworkUtils.ConfigureLowLatencyTcpClient(viewer, RemoteViewerClient.FrameReceiveBufferBytes, 32 * 1024);
                await RelayTunnelClient.ConnectViewerIntoAsync(viewer, options, token, Diagnostic);
                var stream = viewer.GetStream();
                foreach (int length in new[] { 256, 256, 512 * 1024 })
                {
                    byte[] sent = RandomNumberGenerator.GetBytes(length), received = new byte[length];
                    var watch = Stopwatch.StartNew();
                    var receive = stream.ReadExactlyAsync(received, token).AsTask();
                    await stream.WriteAsync(sent, token);
                    await receive;
                    watch.Stop();
                    if (!sent.AsSpan().SequenceEqual(received)) throw new IOException("Synthetic payload integrity failed");
                    var row = new { round, length, milliseconds = watch.Elapsed.TotalMilliseconds,
                        aggregateMbps = 2d * length * 8 / (watch.Elapsed.TotalSeconds * 1_000_000), sha256 = Convert.ToHexString(SHA256.HashData(sent)) };
                    measurements.Add(row);
                    Console.WriteLine(JsonSerializer.Serialize(row));
                }
            }
            if (routeRecovery)
            {
                if (optimizer?.ActivePath is null) throw new IOException("No optimized route was available for the expiry/recovery trial");
                await Task.Delay(11_000, token); // Short test-only lease expires in the kernel; no NIC is disabled.
                await optimizer.MaintainAsync(token);
                if (optimizer.ActivePath is not null) throw new IOException("Expired route remained active in optimizer");
                Diagnostic("Expired native lease detected; awaiting real host reconnect on the system path");
                while (!(await RelayTunnelClient.ListDevicesAsync(options, token)).Any(device => device.DeviceId == options.DeviceId))
                    await Task.Delay(200, token);
                using var recovered = new TcpClient();
                await RelayTunnelClient.ConnectViewerIntoAsync(recovered, options, token, Diagnostic);
                for (int index = 0; index < 3; index++)
                {
                    byte[] sent = RandomNumberGenerator.GetBytes(256), received = new byte[256];
                    var watch = Stopwatch.StartNew();
                    Task receive = recovered.GetStream().ReadExactlyAsync(received, token).AsTask();
                    await recovered.GetStream().WriteAsync(sent, token);
                    await receive;
                    if (!sent.AsSpan().SequenceEqual(received)) throw new IOException("Recovered payload integrity failed");
                    var row = new { phase = "after-kernel-route-expiry", length = 256, milliseconds = watch.Elapsed.TotalMilliseconds };
                    measurements.Add(row);
                    Console.WriteLine(JsonSerializer.Serialize(row));
                }
            }
            complete = true;
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            await connector.StopAsync();
            deadline.Cancel(); listener.Stop();
            try { await echo; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { }
            optimizer?.Dispose();
            if (optimizer is not null) Program.Save(Path.Combine(output, "route-optimization.json"), new {
                optimizedInterface, routeDecisions, routesBefore, routesDuring,
                routesAfter = WindowsRelayRouteProbe.Snapshot(options.ServerAddress)
            });
            Program.Save(Path.Combine(output, "transport.json"), new { complete, failure, measurements, diagnostics,
                productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RemoteViewerClient).Assembly.Location))),
                scope = "Actual public TLS relay; synthetic loopback echo only; not video FPS or end-to-end presentation latency" });
        }
        return complete ? 0 : 1;
    }

    private static RelayConnectionOptions ReadOptions(JsonElement config)
    {
        if (config.TryGetProperty("useInstalledRelay", out JsonElement installed) &&
            installed.ValueKind == JsonValueKind.True)
        {
            // Explicitly opted-in, read-only reuse of the current user's relay
            // configuration. Never read a remote-control password or save/migrate
            // settings, and never print the decrypted access token.
            string path = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "RemoteDesk", "settings.json");
            RelaySettings relay = JsonSerializer.Deserialize<RemoteDeskSettings>(File.ReadAllText(path))?.Relay
                ?? throw new InvalidOperationException("No installed relay configuration");
            if (!string.Equals(relay.ServerAddress, config.GetProperty("expectedServer").GetString(),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Installed relay does not match the explicitly selected server");
            return new RelayConnectionOptions(relay.ServerAddress!, relay.RelayPort,
                AppSettingsService.UnprotectSecret(relay.ProtectedAccessToken),
                relay.TlsCertificateSha256 ?? "", Guid.NewGuid().ToString()).Validate();
        }
        JsonElement supplied = config.GetProperty("relay");
        return new RelayConnectionOptions(supplied.GetProperty("serverAddress").GetString()!,
            supplied.GetProperty("port").GetInt32(), supplied.GetProperty("accessToken").GetString()!,
            supplied.GetProperty("tlsCertificateSha256").GetString()!, Guid.NewGuid().ToString()).Validate();
    }

    private static async Task EchoAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            NetworkUtils.ConfigureLowLatencyTcpClient(client, 32 * 1024, 128 * 1024);
            var stream = client.GetStream();
            byte[] buffer = new byte[64 * 1024];
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer, token)) > 0)
                    await stream.WriteAsync(buffer.AsMemory(0, count), token);
            }
            catch (IOException) when (!token.IsCancellationRequested) { }
        }
    }
}
