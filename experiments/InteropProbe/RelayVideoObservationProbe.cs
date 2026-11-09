using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RemoteDesk;

// Bounded authenticated transport observation. No GUI, input, clipboard,
// file transfer, screenshot export, video-mode renegotiation or takeover.
internal static class RelayVideoObservationProbe
{
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        var settings = new AppSettingsService().Load();
        var saved = settings.Relay;
        if (string.IsNullOrWhiteSpace(saved.ServerAddress) ||
            saved.ServerAddress != config.GetProperty("expectedServer").GetString() || string.IsNullOrEmpty(saved.SshHostKeySha256))
            throw new InvalidOperationException("Unexpected relay identity");
        string id = Guid.Parse(config.GetProperty("deviceId").GetString()!).ToString("D");
        RemoteDeviceIdentity.LocalId = saved.DeviceId;
        bool local = RemoteDeviceIdentity.Same(id, RemoteDeviceIdentity.LocalId);
        var options = new RelayConnectionOptions(saved.ServerAddress, saved.RelayPort,
            AppSettingsService.UnprotectSecret(saved.ProtectedAccessToken), saved.TlsCertificateSha256!, id).Validate();
        int seconds = config.TryGetProperty("seconds", out var duration) ? duration.GetInt32() : 35;
        if (seconds is < 10 or > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 35));
        var device = (await RelayTunnelClient.ListDevicesAsync(options, deadline.Token)).Single(x => x.DeviceId == id);
        if (device.Busy) throw new InvalidOperationException("Device is in use; no takeover");
        string? key = config.TryGetProperty("deviceKey", out var explicitKey)
            ? explicitKey.GetString()
            : RelayWindowsUpdateProbe.Credentials(settings, options, device).FirstOrDefault().Secret;
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("No saved or explicitly supplied device key.");
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: local);
        var identity = new TaskCompletionSource<RemoteDeviceDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new ConcurrentQueue<object>();
        var logs = new ConcurrentQueue<string>();
        var firstFrames = new ConcurrentQueue<object>();
        client.Log += value => { logs.Enqueue(value); while (logs.Count > 30) logs.TryDequeue(out _); };
        var samples = new List<object>();
        var clock = Stopwatch.StartNew();
        long frames = 0, h264 = 0, jpeg = 0, bytes = 0, rttMicroseconds = 0;
        client.DeviceInfoReceived += value => { if (value.DeviceId is not null) identity.TrySetResult(value); };
        client.FrameReceived += frame =>
        {
            Interlocked.Increment(ref frames);
            if (frame.Encoding == RemoteFrameEncoding.H264AnnexB) Interlocked.Increment(ref h264);
            else Interlocked.Increment(ref jpeg);
            Interlocked.Add(ref bytes, frame.EncodedLength);
            if (firstFrames.Count < 6) firstFrames.Enqueue(new { atSeconds = clock.Elapsed.TotalSeconds,
                encoding = frame.Encoding.ToString(), frame.Width, frame.Height, frame.EncodedLength });
        };
        client.RoundTripUpdated += value => Interlocked.Exchange(ref rttMicroseconds, (long)(value.TotalMilliseconds * 1000));
        client.HostVideoDiagnosticsReceived += value => diagnostics.Enqueue(new { atSeconds = clock.Elapsed.TotalSeconds, message = value });
        try
        {
            await client.ConnectViaRelayAsync(options, key, ViewerVideoMode.Automatic, deadline.Token);
            var info = await identity.Task.WaitAsync(deadline.Token);
            if (!RemoteDeviceIdentity.Same(info.DeviceId, id)) throw new InvalidOperationException("Authenticated device identity mismatch");
            long previousFrames = 0, previousBytes = 0;
            for (int i = 0; i < seconds / 5; i++)
            {
                await Task.Delay(5000, deadline.Token);
                if (!client.IsConnected) throw new IOException("Observation disconnected");
                long count = Interlocked.Read(ref frames), size = Interlocked.Read(ref bytes);
                var sample = new { atSeconds = clock.Elapsed.TotalSeconds, receivedFps = (count - previousFrames) / 5d,
                    receivedMbps = (size - previousBytes) * 8d / 5_000_000,
                    heartbeatIncludingQueuesMs = Interlocked.Read(ref rttMicroseconds) / 1000d,
                    h264Frames = Interlocked.Read(ref h264), jpegFrames = Interlocked.Read(ref jpeg) };
                samples.Add(sample);
                Console.WriteLine(JsonSerializer.Serialize(sample));
                previousFrames = count; previousBytes = size;
                await client.RequestHostVideoDiagnosticsAsync();
            }
            await Task.Delay(1500, deadline.Token);
            Program.Save(Path.Combine(output, "observation.json"), new { deviceId = id, info.BuildStamp,
                scope = "Authenticated public-relay video receipt and host diagnostics; no hardware rendering or user-input latency measurement",
                frames, h264, jpeg, samples, firstFrames = firstFrames.ToArray(), logs = logs.ToArray(),
                diagnostics = diagnostics.ToArray(), connectedAtEnd = client.IsConnected });
            return frames > 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Program.Save(Path.Combine(output, "failure.json"), new { deviceId = id,
                error = error.GetType().Name, message = error.Message, frames, h264, jpeg, samples,
                firstFrames = firstFrames.ToArray(), logs = logs.ToArray(), diagnostics = diagnostics.ToArray() });
            throw;
        }
        finally { await client.DisconnectAsync(); }
    }
}
