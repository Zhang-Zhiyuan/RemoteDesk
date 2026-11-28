using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteDesk;

internal static class ReliableVideoReceiptProbe
{
    // Measure when a real viewer releases the host's send credit after idle gaps.
    // The loopback host counts wire receipts. Rendering is measured separately.
    internal static async Task<int> RunAsync(string output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        const string key = "owned-receipt-latency-fixture";
        Task connecting = viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            key, ViewerVideoMode.Automatic, token);
        using TcpClient peer = await listener.AcceptTcpClientAsync(token);
        peer.NoDelay = true;
        using var stream = peer.GetStream();
        using var session = await Protocol.AuthenticateServerAsync(stream, key, token)
            ?? throw new IOException("Fixture authentication failed");
        using var gate = new SemaphoreSlim(1, 1);
        await Protocol.WriteMessageAsync(stream, MessageType.Control, RemoteMessageCodec.EncodeDeviceInfo(
            new("fixture", "Linux", RemoteDeviceCapabilities.RemoteDesktop | RemoteDeviceCapabilities.ReliableVideoFeedback)),
            session, gate, token);
        await connecting;
        var times = new List<double>();
        byte[] body = new byte[1000];
        body[3] = 1;
        body[4] = 0x65;
        const int sampleCount = 30;
        for (int sequence = 1; sequence <= sampleCount + 1; sequence++)
        {
            await Task.Delay(65 + sequence * 11 % 41, token);
            var watch = Stopwatch.StartNew();
            await Protocol.WriteTrackedVideoFrameAsync(stream, sequence, 640, 480, RemoteFrameFlags.KeyFrame,
                0, 0, body, session, gate, token);
            ProtocolMessage message;
            do
            {
                message = await Protocol.ReadMessageAsync(stream, session, token);
            }
            while (message.Type != MessageType.ReliableVideoFeedback);
            double elapsed = watch.Elapsed.TotalMilliseconds;
            var receipt = ReliableVideoFeedbackProtocol.DecodeReceipt(message.PayloadSpan);
            if (receipt.Sequence != sequence || receipt.WireBytes != sequence * (body.Length + ReliableVideoFeedbackProtocol.FrameOverhead))
                throw new IOException("Unexpected cumulative receipt");
            if (sequence > 1) times.Add(elapsed);
        }
        times.Sort();
        Program.Save(Path.Combine(output, "receipt.json"), new
        {
            samples = times.Count,
            medianMs = (times[(times.Count - 1) / 2] + times[times.Count / 2]) / 2,
            p95Ms = times[(int)Math.Ceiling(times.Count * 0.95) - 1],
            maxMs = times[^1],
            sortedMilliseconds = times
        });
        return 0;
    }
}
