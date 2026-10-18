using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SlowFrameHeartbeatTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealViewerWaitsForProgressButDisconnectsAStalledFrame(bool keepSending)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedFrame = new TaskCompletionSource<RemoteFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        byte[] image = Enumerable.Range(0, 16384).Select(x => (byte)x).ToArray();
        async Task Host()
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            using var gate = new SemaphoreSlim(1, 1);
            using var session = await Protocol.AuthenticateServerAsync(stream, "owned-slow-frame", timeout.Token);
            Assert.NotNull(session);
            await Protocol.ReadMessageAsync(stream, session, timeout.Token);
            await Protocol.ReadMessageAsync(stream, session, timeout.Token);
            await Protocol.WriteMessageAsync(stream, MessageType.Control,
                RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor("slow-fixture", RemoteDevicePlatforms.Windows, RemoteDeviceCapabilities.None)),
                session, gate, timeout.Token);
            byte[] payload = RemoteMessageCodec.EncodeFrame(640, 360, image);
            byte[] plain = new byte[payload.Length + 5];
            plain[0] = (byte)MessageType.Frame;
            BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(1), payload.Length);
            payload.CopyTo(plain, 5);
            byte[] cipher = session.Encrypt(plain);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, cipher.Length);
            await stream.WriteAsync(header, timeout.Token);
            await stream.WriteAsync(cipher.AsMemory(0, 32), timeout.Token);
            if (keepSending)
            {
                int offset = 32;
                for (int i = 0; i < 11; i++)
                {
                    await Task.Delay(2000, timeout.Token);
                    await stream.WriteAsync(cipher.AsMemory(offset, 32), timeout.Token);
                    offset += 32;
                }
                await stream.WriteAsync(cipher.AsMemory(offset), timeout.Token);
            }
            // No Pong can jump ahead of an in-progress encrypted frame.
            await release.Task.WaitAsync(timeout.Token);
        }
        Task host = Host();
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        viewer.Log += logs.Enqueue;
        viewer.FrameReceived += frame => receivedFrame.TrySetResult(frame);
        viewer.ConnectedChanged += connected => { if (!connected) disconnected.TrySetResult(); };
        try
        {
            await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                "owned-slow-frame", ViewerVideoMode.StableJpeg, timeout.Token);
            if (keepSending)
            {
                RemoteFrame frame = await receivedFrame.Task.WaitAsync(timeout.Token);
                Assert.True(viewer.IsConnected);
                Assert.Equal(image, frame.EncodedBuffer.AsSpan(frame.EncodedOffset, frame.EncodedLength).ToArray());
                Assert.Contains(logs, line => line.Contains("等待传输完成"));
                Assert.DoesNotContain(logs, line => line.Contains("连接超时"));
            }
            else
            {
                await disconnected.Task.WaitAsync(timeout.Token);
                Assert.False(viewer.IsConnected);
                Assert.False(receivedFrame.Task.IsCompleted);
                Assert.Contains(logs, line => line.Contains("连接超时"));
            }
        }
        finally
        {
            release.TrySetResult();
            await viewer.DisconnectAsync();
            await host.WaitAsync(timeout.Token);
        }
    }
}
