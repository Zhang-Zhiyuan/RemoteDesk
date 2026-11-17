using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteDesk;

// Owned encrypted TCP streams only. Synthetic immutable frames; no capture,
// input, clipboard or installed-app changes. ACKs have a pipelined 300 ms delay.
internal static class ReliableVideoFeedbackProbe
{
    internal static async Task<int> RunAsync(string output, bool healthy = false)
    {
        var baseline = await MeasureAsync(false, healthy);
        Program.Save(Path.Combine(output, "baseline.json"), baseline);
        var feedback = await MeasureAsync(true, healthy);
        Program.Save(Path.Combine(output, "feedback.json"), feedback);
        var result = new { scope = healthy
            ? "Encrypted local TCP; 48 KiB synthetic independent frames at 30 Hz; no artificial shaping/delay. No capture/decode/display."
            : "Encrypted TCP through a shaped bridge; 48 KiB synthetic independent frames at 30 Hz; bandwidth 850/200/850 KiB/s; pipelined 300 ms receipt delay. Frame age excludes display/decode and is not a Europe measurement.", baseline, feedback };
        Program.Save(Path.Combine(output, "comparison.json"), result);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, Program.Json));
        return 0;
    }

    private static async Task<object> MeasureAsync(bool feedbackEnabled, bool healthy)
    {
        int duration = healthy ? 12 : 30;
        const int frameBytes = 48 * 1024;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        using var first = new TcpListener(IPAddress.Loopback, 0);
        using var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start(); second.Start();
        using var sender = new TcpClient(); using var bridgeOut = new TcpClient();
        await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)first.LocalEndpoint).Port, token);
        using var bridgeIn = await first.AcceptTcpClientAsync(token);
        await bridgeOut.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)second.LocalEndpoint).Port, token);
        using var receiver = await second.AcceptTcpClientAsync(token);
        foreach (var client in new[] { sender, bridgeIn, bridgeOut, receiver }) RelayLoopbackPolicy.Configure(client);
        NetworkStream sendStream = sender.GetStream(), receiveStream = receiver.GetStream();
        NetworkStream bridgeInStream = bridgeIn.GetStream(), bridgeOutStream = bridgeOut.GetStream();
        var watch = Stopwatch.StartNew();
        async Task Pump(NetworkStream source, NetworkStream target, bool shape)
        {
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                int count = await source.ReadAsync(buffer, token);
                if (count == 0) return;
                int rate = watch.Elapsed.TotalSeconds is >= 8 and < 18 ? 200 * 1024 : 850 * 1024;
                if (shape && !healthy) await Task.Delay(TimeSpan.FromSeconds(count / (double)rate), token);
                await target.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
        Task forward = Task.Run(async () => {
            await Pump(bridgeInStream, bridgeOutStream, true);
            bridgeOut.Client.Shutdown(SocketShutdown.Send);
        }, token);
        Task reverse = Pump(bridgeOutStream, bridgeInStream, false);
        Task<SecureSession?> authenticate = Protocol.AuthenticateServerAsync(sendStream, "owned-receipt-fixture", token);
        using SecureSession receiveSession = await Protocol.AuthenticateClientAsync(receiveStream, "owned-receipt-fixture", token);
        using SecureSession sendSession = await authenticate ?? throw new IOException("Authentication failed");
        using var sendGate = new SemaphoreSlim(1, 1); using var ackGate = new SemaphoreSlim(1, 1);
        var window = new ReliableVideoSendWindow();
        var pacer = new ReliableH264FramePacer(30, true);
        var timings = new ProtocolFrameWriteTimingsWindow();
        var ackQueue = new ConcurrentQueue<(ReliableVideoReceipt Receipt, long Due)>();
        var samples = new List<(double At, double Age)>();
        byte[] payload = RandomNumberGenerator.GetBytes(frameBytes);
        byte[] hash = SHA256.HashData(payload.AsSpan(16));
        long receivedBytes = 0, maxPendingBytes = 0;
        int sent = 0, received = 0, count = 0;
        var changes = new List<object>();
        Task consume = Task.Run(async () => {
            try
            {
                while (true)
                {
                    var message = await Protocol.ReadMessageAsync(receiveStream, receiveSession, token);
                    if (message.Type != (feedbackEnabled ? MessageType.TrackedVideoFrame : MessageType.VideoFrame))
                        throw new IOException("Unexpected fixture frame type");
                    long sequence = 0;
                    RemoteFrame frame;
                    if (feedbackEnabled) (sequence, frame) = ReliableVideoFeedbackProtocol.DecodeFrame(message.PayloadMemory);
                    else frame = RemoteMessageCodec.DecodeVideoFrame(message.PayloadMemory);
                    long produced = BinaryPrimitives.ReadInt64LittleEndian(frame.EncodedBuffer.AsSpan(frame.EncodedOffset));
                    if (frame.EncodedLength != frameBytes || !SHA256.HashData(frame.EncodedBuffer.AsSpan(frame.EncodedOffset + 16, frameBytes - 16)).AsSpan().SequenceEqual(hash))
                        throw new IOException("Synthetic image bytes changed");
                    received++;
                    samples.Add((watch.Elapsed.TotalSeconds, Stopwatch.GetElapsedTime(produced).TotalMilliseconds));
                    if (feedbackEnabled)
                    {
                        receivedBytes += message.PayloadLength + 25;
                        ackQueue.Enqueue((new(sequence, receivedBytes), Environment.TickCount64 + (healthy ? 0 : 300)));
                    }
                }
            }
            catch (EndOfStreamException) { }
        }, token);
        Task sendAcks = Task.Run(async () => {
            if (!feedbackEnabled) return;
            while (!token.IsCancellationRequested)
            {
                ReliableVideoReceipt? latest = null;
                while (ackQueue.TryPeek(out var due) && due.Due <= Environment.TickCount64)
                    if (ackQueue.TryDequeue(out var item)) latest = item.Receipt;
                if (latest is not null)
                    await Protocol.WriteMessageAsync(receiveStream, MessageType.ReliableVideoFeedback,
                        ReliableVideoFeedbackProtocol.EncodeReceipt(latest), receiveSession, ackGate, token);
                await Task.Delay(5, token);
            }
        }, token);
        Task readAcks = Task.Run(async () => {
            if (!feedbackEnabled) return;
            while (true)
            {
                var message = await Protocol.ReadMessageAsync(sendStream, sendSession, token);
                if (message.Type != MessageType.ReliableVideoFeedback ||
                    !window.Observe(ReliableVideoFeedbackProtocol.DecodeReceipt(message.PayloadSpan), Environment.TickCount64))
                    throw new IOException("Invalid receipt in fixture");
            }
        }, token);
        long next = Stopwatch.GetTimestamp(), metricsAt = next, interval = Stopwatch.Frequency / 30;
        try
        {
            while (watch.Elapsed.TotalSeconds < duration)
            {
                long now = Stopwatch.GetTimestamp();
                if (now < next) { await Task.Delay(Stopwatch.GetElapsedTime(now, next), token); continue; }
                long produced = next + (now - next) / interval * interval;
                next = produced + interval;
                if (!pacer.ShouldSendFrame(now)) continue;
                long sequence = 0;
                if (feedbackEnabled && !window.TryReserve(frameBytes + ReliableVideoFeedbackProtocol.FrameOverhead, Environment.TickCount64, out sequence)) continue;
                BinaryPrimitives.WriteInt64LittleEndian(payload, produced);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), sent);
                timings.Record(feedbackEnabled
                    ? await Protocol.WriteTrackedVideoFrameAsync(sendStream, sequence, 1920, 1080,
                        RemoteFrameFlags.KeyFrame, 0, 0, payload, sendSession, sendGate, token)
                    : await Protocol.WriteVideoFrameMessageAsync(sendStream, 1920, 1080,
                        RemoteFrameEncoding.H264AnnexB, RemoteFrameFlags.KeyFrame, 0, 0, payload, sendSession, sendGate, token));
                sent++; count++; maxPendingBytes = Math.Max(maxPendingBytes, window.PendingBytes);
                var elapsed = Stopwatch.GetElapsedTime(metricsAt);
                if (elapsed.TotalSeconds >= 1)
                {
                    if (pacer.Observe(elapsed, count / elapsed.TotalSeconds, timings.AverageSocketWriteMilliseconds, true))
                        changes.Add(new { at = watch.Elapsed.TotalSeconds, fps = pacer.FramesPerSecond });
                    count = 0; timings.Reset(); metricsAt = Stopwatch.GetTimestamp();
                }
            }
            sender.Client.Shutdown(SocketShutdown.Send);
            await forward.WaitAsync(token); await consume.WaitAsync(token);
            if (received != sent) throw new IOException("Lost an admitted frame");
            object Phase(double from, double to)
            {
                var ages = samples.Where(s => s.At >= from && s.At < to).Select(s => s.Age).Order().ToArray();
                return new { frames = ages.Length, fps = ages.Length / (to - from),
                    p50Ms = ages.Length == 0 ? 0 : ages[ages.Length / 2],
                    p95Ms = ages.Length == 0 ? 0 : ages[Math.Min(ages.Length - 1, (int)(ages.Length * .95))] };
            }
            return new { feedbackEnabled, sent, received, maxPendingBytes, droppedBeforeSend = window.DroppedFrames,
                initial = Phase(2, healthy ? duration : 8), constrained = healthy ? null : Phase(10, 18),
                recovered = healthy ? null : Phase(22, 30), changes,
                unchangedBytesPerFrame = frameBytes - 16 };
        }
        finally
        {
            deadline.Cancel();
            try { await Task.WhenAll(forward, reverse, consume, sendAcks, readAcks); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
    }
}
