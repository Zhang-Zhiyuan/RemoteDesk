using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteDesk;

internal static class DelayedVideoNegotiationProbe
{
    internal static async Task<int> RunAsync(string output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        using var host = new RemoteHostServer();
        var logs = new ConcurrentQueue<string>();
        host.Log += logs.Enqueue;
        string key = Guid.NewGuid().ToString("N");
        await host.StartAsync(0, key, 30, 85, 100, ScreenCaptureService.GetDefaultTarget(), true);
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        using var viewer = new TcpClient();
        await viewer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)proxy.LocalEndpoint).Port, deadline.Token);
        using TcpClient fromViewer = await proxy.AcceptTcpClientAsync(deadline.Token);
        using var toHost = new TcpClient();
        await toHost.ConnectAsync(IPAddress.Loopback, host.ListeningPort, deadline.Token);
        foreach (TcpClient socket in new[] { viewer, fromViewer, toHost }) RelayLoopbackPolicy.Configure(socket);
        async Task Pump(NetworkStream source, NetworkStream destination, bool limit)
        {
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                int count = await source.ReadAsync(buffer, deadline.Token);
                if (count == 0) return;
                if (limit) await Task.Delay(TimeSpan.FromSeconds(count / (150d * 1024)), deadline.Token);
                await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
            }
        }
        Task upload = Pump(fromViewer.GetStream(), toHost.GetStream(), false);
        Task download = Pump(toHost.GetStream(), fromViewer.GetStream(), true);
        using var writeLock = new SemaphoreSlim(1, 1);
        var received = new List<object>();
        int jpeg = 0, h264 = 0, pongs = 0, decoded = 0;
        double? firstVideoSeconds = null;
        var clock = Stopwatch.StartNew();
        try
        {
            using SecureSession session = await Protocol.AuthenticateClientAsync(viewer.GetStream(), key, deadline.Token);
            // Simulate ViewerInfo delayed beyond the previous 50 ms grace.
            await Task.Delay(1200, deadline.Token);
            await Protocol.WriteMessageAsync(viewer.GetStream(), MessageType.Control,
                RemoteMessageCodec.EncodeViewerInfo(RemoteVideoCodecs.H264AnnexB), session, writeLock, deadline.Token);
            await Protocol.WriteMessageAsync(viewer.GetStream(), MessageType.Control,
                RemoteMessageCodec.EncodeViewerCapabilities(RemoteViewerClient.RelayTransportViewerCapabilities),
                session, writeLock, deadline.Token);
            Task heartbeat = Task.Run(async () =>
            {
                while (!deadline.IsCancellationRequested)
                {
                    await Protocol.WriteMessageAsync(viewer.GetStream(), MessageType.Ping, ReadOnlyMemory<byte>.Empty,
                        session, writeLock, deadline.Token);
                    await Task.Delay(2000, deadline.Token);
                }
            });
            try
            {
                while (h264 < 3 || pongs < 2)
                {
                    ProtocolMessage message = await Protocol.ReadMessageAsync(viewer.GetStream(), session, deadline.Token);
                    if (message.Type == MessageType.Pong) { pongs++; continue; }
                    if (message.Type is not (MessageType.Frame or MessageType.VideoFrame)) continue;
                    if (message.Type == MessageType.Frame) { jpeg++; continue; }
                    RemoteFrame frame = RemoteMessageCodec.DecodeVideoFrame(message.PayloadMemory);
                    if (frame.Encoding == RemoteFrameEncoding.Jpeg) { jpeg++; continue; }
                    h264++;
                    firstVideoSeconds ??= clock.Elapsed.TotalSeconds;
                    received.Add(new { atSeconds = clock.Elapsed.TotalSeconds, frame.Width, frame.Height, frame.EncodedLength });
                    if (decoded == 0)
                    {
                        using FfmpegH264Decoder decoder = FfmpegH264Decoder.TryCreate(new Size(frame.Width, frame.Height))
                            ?? throw new IOException("No H264 decoder.");
                        var result = await decoder.DecodeAsync(1, frame.EncodedBuffer.AsMemory(frame.EncodedOffset, frame.EncodedLength))
                            .WaitAsync(deadline.Token);
                        using Bitmap bitmap = result.Bitmap ?? throw new IOException("First network H264 frame did not decode.");
                        decoded++;
                    }
                }
            }
            finally
            {
                deadline.Cancel();
                try { await heartbeat; } catch (OperationCanceledException) { }
            }
            Program.Save(Path.Combine(output, "delayed-negotiation.json"), new { firstVideoSeconds, jpeg, h264, pongs, decoded, received,
                logs = logs.ToArray(), scope = "Actual current desktop + new host over encrypted local TCP limited to 150 KiB/s; 1.2s delayed video negotiation; no input, not a Europe WAN measurement" });
            return jpeg == 0 && decoded > 0 ? 0 : 1;
        }
        finally
        {
            deadline.Cancel();
            viewer.Close(); fromViewer.Close(); toHost.Close(); proxy.Stop();
            try { await Task.WhenAll(upload, download); }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            await host.StopAsync();
        }
    }
}
