using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteDesk;

// Two isolated, encrypted TCP runs through a rate-limited loopback bridge.
// Synthetic fixed-size access units only: no screen capture, input, clipboard,
// relay traffic, installed-app interaction or claims about hardware decoding.
internal static class ReliableVideoPacingProbe
{
    internal static async Task<int> RunAsync(string output)
    {
        object baseline = await MeasureAsync(false);
        object paced = await MeasureAsync(true);
        var report = new { measuredAtUtc = DateTimeOffset.UtcNow,
            scope = "Real encrypted loopback TCP, synthetic 48 KiB frames at 30 Hz, 850 KiB/s shaped bridge; not WAN or decode latency",
            baseline, paced };
        Program.Save(Path.Combine(output, "pacing.json"), report);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, Program.Json));
        return 0;
    }

    private static async Task<object> MeasureAsync(bool enabled)
    {
        const int durationSeconds = 20, frameBytes = 48 * 1024, bytesPerSecond = 850 * 1024;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var first = new TcpListener(IPAddress.Loopback, 0);
        using var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start(); second.Start();
        using var sender = new TcpClient();
        using var bridgeOut = new TcpClient();
        await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)first.LocalEndpoint).Port, token);
        using var bridgeIn = await first.AcceptTcpClientAsync(token);
        await bridgeOut.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)second.LocalEndpoint).Port, token);
        using var receiver = await second.AcceptTcpClientAsync(token);
        foreach (var socket in new[] { sender, bridgeIn, bridgeOut, receiver }) RelayLoopbackPolicy.Configure(socket);

        async Task Pump(NetworkStream source, NetworkStream target, bool shaped)
        {
            byte[] buffer = new byte[RelayStreamBridge.CopyBufferBytes];
            while (true)
            {
                int count = await source.ReadAsync(buffer, token);
                if (count == 0) return;
                if (shaped) await Task.Delay(TimeSpan.FromSeconds(count / (double)bytesPerSecond), token);
                await target.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
        Task forward = Task.Run(async () =>
        {
            await Pump(bridgeIn.GetStream(), bridgeOut.GetStream(), true);
            bridgeOut.Client.Shutdown(SocketShutdown.Send);
        }, token);
        Task reverse = Pump(bridgeOut.GetStream(), bridgeIn.GetStream(), false);
        Task<SecureSession?> auth = Protocol.AuthenticateServerAsync(sender.GetStream(), "owned-pacing-fixture", token);
        using SecureSession readSession = await Protocol.AuthenticateClientAsync(receiver.GetStream(), "owned-pacing-fixture", token);
        using SecureSession writeSession = await auth ?? throw new InvalidOperationException("Fixture authentication failed");
        using var gate = new SemaphoreSlim(1, 1);
        byte[] payload = RandomNumberGenerator.GetBytes(frameBytes);
        byte[] immutableHash = SHA256.HashData(payload.AsSpan(16));
        var watch = Stopwatch.StartNew();
        var ages = new List<double>();
        var adjustments = new List<object>();
        int received = 0, lateReceived = 0, sent = 0;
        Task consume = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    ProtocolMessage message = await Protocol.ReadMessageAsync(receiver.GetStream(), readSession, token);
                    if (message.Type != MessageType.VideoFrame) throw new IOException("Unexpected fixture message");
                    RemoteFrame frame = RemoteMessageCodec.DecodeVideoFrame(message.PayloadMemory);
                    long producedAt = BinaryPrimitives.ReadInt64LittleEndian(frame.EncodedBuffer.AsSpan(frame.EncodedOffset));
                    if (frame.EncodedLength != frameBytes || !SHA256.HashData(frame.EncodedBuffer.AsSpan(frame.EncodedOffset + 16, frameBytes - 16)).AsSpan().SequenceEqual(immutableHash))
                        throw new IOException("Frame payload changed");
                    received++;
                    if (watch.Elapsed.TotalSeconds is >= 10 and <= durationSeconds)
                    {
                        ages.Add(Stopwatch.GetElapsedTime(producedAt).TotalMilliseconds);
                        lateReceived++;
                    }
                }
            }
            catch (EndOfStreamException) { }
        }, token);
        var pacer = new ReliableH264FramePacer(30, enabled);
        var timings = new ProtocolFrameWriteTimingsWindow();
        long windowAt = Stopwatch.GetTimestamp();
        long nextSourceAt = Stopwatch.GetTimestamp();
        long sourceInterval = Stopwatch.Frequency / 30;
        try
        {
            while (watch.Elapsed.TotalSeconds < durationSeconds)
            {
                long now = Stopwatch.GetTimestamp();
                if (now < nextSourceAt)
                {
                    await Task.Delay(Stopwatch.GetElapsedTime(now, nextSourceAt), token);
                    continue;
                }
                long producedAt = nextSourceAt + (now - nextSourceAt) / sourceInterval * sourceInterval;
                nextSourceAt = producedAt + sourceInterval;
                if (!pacer.ShouldSendFrame(now)) continue;
                BinaryPrimitives.WriteInt64LittleEndian(payload, producedAt);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), sent);
                timings.Record(await Protocol.WriteVideoFrameMessageAsync(sender.GetStream(), 1920, 1080,
                    RemoteFrameEncoding.H264AnnexB, RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig,
                    0, 0, payload, writeSession, gate, token));
                sent++;
                TimeSpan elapsed = Stopwatch.GetElapsedTime(windowAt);
                if (elapsed.TotalSeconds >= 1)
                {
                    if (pacer.Observe(elapsed, timings.Count / elapsed.TotalSeconds, timings.AverageSocketWriteMilliseconds, true))
                        adjustments.Add(new { atSeconds = watch.Elapsed.TotalSeconds, fps = pacer.FramesPerSecond });
                    timings.Reset();
                    windowAt = Stopwatch.GetTimestamp();
                }
            }
            sender.Client.Shutdown(SocketShutdown.Send);
            await forward.WaitAsync(token);
            await consume.WaitAsync(token);
            if (received != sent) throw new IOException($"Lost frame: {received}/{sent}");
            ages.Sort();
            return new { enabled, sent, received, unchangedPayloadBytes = frameBytes - 16,
                steadyReceivedFps = lateReceived / 10d, finalTargetFps = pacer.FramesPerSecond,
                steadyFrameAgeP50Ms = ages[ages.Count / 2],
                steadyFrameAgeP95Ms = ages[Math.Min(ages.Count - 1, (int)(ages.Count * 0.95))], adjustments };
        }
        finally
        {
            deadline.Cancel();
            try { await Task.WhenAll(forward, reverse, consume); }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
        }
    }
}
