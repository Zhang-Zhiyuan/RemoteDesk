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
        var relay = config.GetProperty("relay");
        var options = new RelayConnectionOptions(relay.GetProperty("serverAddress").GetString()!, relay.GetProperty("port").GetInt32(),
            relay.GetProperty("accessToken").GetString()!, relay.GetProperty("tlsCertificateSha256").GetString()!, Guid.NewGuid().ToString()).Validate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var connector = new RelayHostConnector();
        var measurements = new List<object>();
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
                await RelayTunnelClient.ConnectViewerIntoAsync(viewer, options, token);
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
            complete = true;
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            await connector.StopAsync();
            deadline.Cancel(); listener.Stop();
            try { await echo; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { }
            Program.Save(Path.Combine(output, "transport.json"), new { complete, failure, measurements,
                productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RemoteViewerClient).Assembly.Location))),
                scope = "Actual public TLS relay; synthetic loopback echo only; not video FPS or end-to-end presentation latency" });
        }
        return complete ? 0 : 1;
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
