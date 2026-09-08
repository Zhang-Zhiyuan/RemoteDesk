using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteDesk;

internal static class RelayProtocolProbe
{
    public static async Task<object> RunAsync(RelayConnectionOptions options, CancellationToken stop)
    {
        const int width = 1920, height = 1080;
        byte[] jpeg;
        using (var bitmap = new Bitmap(width, height))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var encoded = new MemoryStream())
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.RoyalBlue, 0, 0, width / 2, height);
            graphics.DrawRectangle(Pens.Black, 20, 20, width - 40, height - 40);
            bitmap.Save(encoded, ImageFormat.Jpeg);
            jpeg = encoded.ToArray();
        }
        RemoteInputCommand[] expectedInputs =
        [
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 20000, 30000),
            RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 20000, 30000),
            RemoteInputCommand.KeyDown(0x41),
            RemoteInputCommand.KeyUp(0x41)
        ];
        // A separate random RemoteDesk password, never the server administrator's password.
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        CancellationToken token = deadline.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var connector = new RelayHostConnector();
        using var viewer = new RemoteViewerClient(null, null, connectTimeout: TimeSpan.FromSeconds(20));
        var frameReceived = new TaskCompletionSource<RemoteFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FrameReceived += frame => frameReceived.TrySetResult(frame);
        Task<RemoteDeviceCapabilities> host = RunHostAsync();
        try
        {
            await connector.StartAsync(options, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            while (!(await RelayTunnelClient.ListDevicesAsync(options, token))
                .Any(device => device.DeviceId == options.DeviceId))
                await Task.Delay(300, token);
            await viewer.ConnectViaRelayAsync(options, password, ViewerVideoMode.StableJpeg, token);
            if (!await viewer.WaitForCurrentDeviceInfoAsync(TimeSpan.FromSeconds(10), token))
                throw new IOException("Encrypted device-info handshake did not complete.");
            RemoteFrame frame = await frameReceived.Task.WaitAsync(token);
            if (frame.Encoding != RemoteFrameEncoding.Jpeg || frame.Width != width || frame.Height != height ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(jpeg),
                    SHA256.HashData(frame.EncodedBuffer.AsSpan(frame.EncodedOffset, frame.EncodedLength))))
                throw new IOException("The encrypted 1080p frame did not arrive intact.");
            using (var encoded = new MemoryStream(frame.EncodedBuffer, frame.EncodedOffset, frame.EncodedLength))
            using (var decoded = new Bitmap(encoded))
                if (decoded.Width != width || decoded.Height != height)
                    throw new IOException("The received JPEG could not be decoded at its original resolution.");

            await viewer.SendInputsAsync(expectedInputs);
            await inputsReceived.Task.WaitAsync(token);
            releaseHost.TrySetResult();
            RemoteDeviceCapabilities capabilities = await host.WaitAsync(token);
            if ((capabilities & (RemoteDeviceCapabilities.LowLatencyUdpVideo |
                RemoteDeviceCapabilities.LowLatencyUdpMouseInput)) != 0)
                throw new IOException("Relay mode incorrectly advertised LAN-only UDP transport.");
            return new { encryptedAuthentication = true, frameWidth = width, frameHeight = height,
                jpegBytes = jpeg.Length, jpegHashVerified = true, jpegDecoded = true,
                mouseAndKeyboardMessages = expectedInputs.Length, inputInjectedIntoDesktop = false,
                lanUdpDisabled = true };
        }
        finally
        {
            releaseHost.TrySetResult();
            deadline.Cancel();
            listener.Stop();
            await viewer.DisconnectAsync();
            await connector.StopAsync();
            try { await host; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }

        async Task<RemoteDeviceCapabilities> RunHostAsync()
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(token);
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            using var writeLock = new SemaphoreSlim(1, 1);
            ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(stream, password, token);
            using SecureSession session = authentication.Session ?? throw new IOException("RemoteDesk password authentication failed.");
            RemoteDeviceCapabilities capabilities = RemoteDeviceCapabilities.None;
            bool viewerInfo = false;
            while (!viewerInfo || capabilities == RemoteDeviceCapabilities.None)
            {
                ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, token);
                if (message.Type != MessageType.Control) continue;
                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                if (control.Kind == RemoteControlKind.ViewerInfo) viewerInfo = true;
                if (control.Kind == RemoteControlKind.ViewerCapabilities) capabilities = control.Capabilities;
            }
            await Protocol.WriteMessageAsync(stream, MessageType.Control,
                RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor("synthetic-public-relay-probe",
                    RemoteDevicePlatforms.Windows, RemoteDeviceCapabilities.RemoteDesktop | RemoteDeviceCapabilities.InputControl)),
                session, writeLock, token);
            await Protocol.WriteMessageAsync(stream, MessageType.Frame,
                RemoteMessageCodec.EncodeFrame(width, height, jpeg), session, writeLock, token);
            foreach (RemoteInputCommand expected in expectedInputs)
            {
                ProtocolMessage message;
                do
                {
                    message = await Protocol.ReadMessageAsync(stream, session, token);
                    if (message.Type == MessageType.Ping)
                        await Protocol.WriteMessageAsync(stream, MessageType.Pong, ReadOnlyMemory<byte>.Empty, session, writeLock, token);
                } while (message.Type != MessageType.Input);
                // Decode and compare only; this host never calls SendInput or touches the desktop.
                if (RemoteMessageCodec.DecodeInput(message.PayloadSpan) != expected)
                    throw new IOException("Encrypted mouse/keyboard commands were lost, reordered or changed.");
            }
            inputsReceived.TrySetResult();
            await releaseHost.Task.WaitAsync(token);
            return capabilities;
        }
    }
}
