using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using RemoteDesk;
using RemoteDesk.NativeDetail;
using Vortice.Direct3D11;

// Real encrypted TCP -> RemoteViewerClient -> pooled frame/decode worker ->
// RemoteViewerWindow -> MF/D3D11 presentation. Synthetic prerecorded source,
// no installed host, OS input injection, clipboard or external network.
internal static class NativeDetailSessionProbe
{
    internal static void Run(string fixtures, string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output)) throw new IOException("Preserving previous session evidence.");
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunCore(fixtures, output); }
            catch (Exception ex)
            {
                failure = ex;
                if (!File.Exists(Path.Combine(output, "summary.json")))
                    File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { passed = false, failure = ex.ToString() }));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) throw new TimeoutException("Owned session probe did not close.");
        if (failure is not null) throw new IOException("Session probe failed; see summary.json.", failure);
    }

    private static void RunCore(string fixtures, string output)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        WindowsFormsSynchronizationContext.AutoInstall = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = cancellation.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string password = Guid.NewGuid().ToString("N");
        using var original = new Bitmap(Path.Combine(fixtures, "native-reference.png"));
        byte[] h264 = NativeDetailProbe.LastIndependentAu(Path.Combine(fixtures, "10mbps-static-1080p-gop1.h264"));
        var baseFrame = new RemoteFrame(1920, 1080, RemoteFrameEncoding.H264AnnexB,
            RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig, h264, 0, h264.Length, 0, 0);
        byte[] legacy = new byte[RemoteMessageCodec.VideoFrameHeaderLength + h264.Length];
        RemoteMessageCodec.WriteVideoFrameHeader(legacy, 1920, 1080, baseFrame.Encoding, baseFrame.Flags, 0, 0);
        h264.CopyTo(legacy, RemoteMessageCodec.VideoFrameHeaderLength);
        var tileBytes = new List<byte[]>();
        for (int tile = 0; tile < 2; tile++)
        {
            using var crop = original.Clone(new Rectangle(tile * 128, 0, 128, 128), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            tileBytes.Add(NativeDetailProbe.Rgba(crop));
        }
        NativeDetailRequest? request = null;
        int paused = 0, inputs = 0;
        long sentFrames = 0, nativeDraws = 0, nativeReceived = 0;
        async Task Host()
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(token);
            NetworkUtils.ConfigureLowLatencyTcpClient(peer, 128 * 1024, 128 * 1024);
            using var stream = peer.GetStream();
            using var write = new SemaphoreSlim(1, 1);
            using var session = await Protocol.AuthenticateServerAsync(stream, password, token)
                ?? throw new IOException("Owned authentication failed.");
            await Protocol.ReadMessageAsync(stream, session, token);
            var caps = RemoteMessageCodec.DecodeControl((await Protocol.ReadMessageAsync(stream, session, token)).PayloadMemory);
            Check(caps.Capabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1), "Native receiver was not negotiated.");
            await Protocol.WriteMessageAsync(stream, MessageType.Control, RemoteMessageCodec.EncodeDeviceInfo(
                new("Native detail test fixture", "Windows", RemoteDeviceCapabilities.RemoteDesktop | RemoteDeviceCapabilities.NativeDetailV1)),
                session, write, token);
            async Task Read()
            {
                while (true)
                {
                    var message = await Protocol.ReadMessageAsync(stream, session, token);
                    if (message.Type == MessageType.NativeDetailRequest)
                        Volatile.Write(ref request, NativeDetailSessionProtocol.DecodeRequest(message.PayloadSpan));
                    else if (message.Type == MessageType.Input) Interlocked.Increment(ref inputs);
                    else if (message.Type == MessageType.Ping)
                        await Protocol.WriteMessageAsync(stream, MessageType.Pong, ReadOnlyMemory<byte>.Empty, session, write, token);
                }
            }
            Task reader = Read();
            try
            {
                long sequence = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref paused) == 0)
                    {
                        NativeDetailRequest? current = Volatile.Read(ref request);
                        if (current is null)
                            await Protocol.WriteMessageAsync(stream, MessageType.VideoFrame, legacy, session, write, token);
                        else
                        {
                            var manifest = new DetailManifest(current.Context, ++sequence,
                                Enumerable.Repeat(1L, current.Context.TileCount).ToArray());
                            await Protocol.WriteMessageAsync(stream, MessageType.NativeVideoFrame,
                                NativeDetailSessionProtocol.EncodeBase(manifest, baseFrame), session, write, token);
                            // A correctness sender, NOT the production WAN
                            // scheduler: repeat only two small synthetic tiles.
                            if (current.Enabled && sequence % 10 == 0)
                                for (int tile = 0; tile < 2; tile++)
                                {
                                    var transfer = DetailTransfer.Encode(manifest, tile, tileBytes[tile]);
                                    for (int offset = 0; offset < transfer.Encoded.Length; offset += DetailWire.ChunkBytes)
                                        await Protocol.WriteMessageAsync(stream, MessageType.NativeDetailChunk,
                                            transfer.Chunk(offset), session, write, token);
                                }
                        }
                        Interlocked.Increment(ref sentFrames);
                    }
                    await Task.Delay(25, token);
                }
            }
            finally
            {
                cancellation.Cancel();
                try { await reader; } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
        }
        Task host = Task.Run(Host);
        using var client = new RemoteViewerClient { EnableNativeDetailReception = true };
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        client.Log += logs.Enqueue;
        client.FrameReceived += frame => { if (frame.NativeDetails is not null) Interlocked.Increment(ref nativeReceived); };
        using var window = new RemoteViewerWindow(client, "RemoteDesk 原生补清会话测试（自动关闭）",
            inputEnabled: false, clipboardTextEnabled: false, filePasteEnabled: false,
            fileDropPasteEnabled: false, remoteFilePullEnabled: false, isAndroidRemote: false)
        { ClientSize = new(2200, 1360), StartPosition = FormStartPosition.Manual, Location = new(25, 25), ShowInTaskbar = false };
        string? failure = null;
        object? pixelCheck = null;
        long nextProgress = Environment.TickCount64 + 2000;
        string phase = "baseline";
        var checks = new List<string>();
        using var waiter = new WindowsHighResolutionPacingWaiter();
        void PumpUntil(Func<bool> condition)
        {
            while (!condition())
            {
                token.ThrowIfCancellationRequested();
                if (host.IsFaulted) host.GetAwaiter().GetResult();
                if (Environment.TickCount64 >= nextProgress)
                {
                    nextProgress = Environment.TickCount64 + 2000;
                    Console.WriteLine($"Session {phase}: sent={sentFrames}, receivedNative={nativeReceived}, enabled={client.NativeDetails.IsEnabled}, applied={client.NativeDetails.AppliedChunks}, presentedSequence={client.NativeDetails.LastPresentedSequence}, nativeDraws={nativeDraws}");
                    if (typeof(RemoteViewerWindow).GetField("_d3d11VideoPresenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is D3D11HwndVideoPresenter present)
                        Console.WriteLine($"Presenter: {present.OutputSize}, atlas={present.NativeDetailAtlasBytes}, status={present.NativeDetailStatus}");
                }
                Application.DoEvents(); waiter.Wait(TimeSpan.FromMilliseconds(1), token);
            }
            Application.DoEvents();
        }
        void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        object Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner) ?? throw new IOException("Missing active " + name);
        D3D11HwndVideoPresenter Presenter() => (D3D11HwndVideoPresenter)Field(window, "_d3d11VideoPresenter");
        void Save(string name)
        {
            Volatile.Write(ref paused, 1);
            long stableAt = Stopwatch.GetTimestamp();
            PumpUntil(() => Stopwatch.GetElapsedTime(stableAt).TotalMilliseconds > 180);
            var gate = (CapturePresentationTransitionGate)Field(window, "_capturePresentationTransitionGate");
            gate.Run(() =>
            {
                var presenter = Presenter();
                using var device = ((ID3D11Device)Field(presenter, "_device")).QueryInterface<ID3D11Device>();
                using var bitmap = UpscaleComparisonProbe.ReadPixels(device, presenter, presenter.OutputSize);
                bitmap.Save(Path.Combine(output, name));
            });
            Volatile.Write(ref paused, 0);
        }
        void Enable(bool enabled = true)
        {
            var task = client.RequestNativeDetailsAsync(original.Size, new(0, 0, 256, 128), enabled);
            Await(task); Check(task.Result, "Native request was not accepted locally.");
        }
        try
        {
            window.Show(); Application.DoEvents();
            // The real viewer applies its responsive preferred size on Load.
            // Resize AFTER that, as a user would, so 4K native -> displayed
            // FHD is within the existing <=2x reduction safety limit.
            window.ClientSize = new(2200, 1360); Application.DoEvents();
            Await(client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                password, ViewerVideoMode.Automatic, token));
            PumpUntil(() => window.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames >= 12);
            Check(!window.CollectRenderTelemetrySnapshot().JpegFallbackRequested, "Unexpected decoder fallback.");
            Save("base.png");
            Presenter().NativeDetailAfterDrawForTests = () => Interlocked.Increment(ref nativeDraws);
            phase = "native";
            Enable();
            PumpUntil(() => Interlocked.Read(ref nativeDraws) >= 15);
            Check(client.NativeDetails.AppliedChunks >= 2, "No patches passed the real receive/cache path.");
            Check(client.NativeDetails.LastPresentedSequence > 0, "Lost native identity in pooled decoder metadata.");
            Save("native.png"); checks.Add("authenticated client / pooled frame / exact decoder identity / actual viewer GPU overlay");
            using (var before = new Bitmap(Path.Combine(output, "base.png")))
            using (var after = new Bitmap(Path.Combine(output, "native.png")))
                pixelCheck = ValidateNativePixels(original, before, after,
                    (D3D11HwndVideoScaleMode)Field(Presenter(), "_scaleMode"));
            client.SendTextInput("中文测试");
            phase = "input cutoff";
            Check(!client.NativeDetails.IsEnabled, "Input failed to invalidate the native request.");
            long settle = window.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames + 4;
            PumpUntil(() => window.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames >= settle);
            long drawCutoff = Interlocked.Read(ref nativeDraws);
            long baseCutoff = window.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames + 12;
            PumpUntil(() => window.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames >= baseCutoff);
            Check(nativeDraws == drawCutoff && inputs == 4, "Input/off leaked a native overlay or blocked its base.");
            checks.Add("Chinese input reaches synthetic peer; base continues with no old overlays");
            Enable();
            phase = "resumed";
            PumpUntil(() => Interlocked.Read(ref nativeDraws) >= drawCutoff + 10);
            window.ClientSize = new(2060, 1240); Application.DoEvents();
            Check(!client.NativeDetails.IsEnabled, "Viewport resize retained an old request.");
            checks.Add("new local request resumes; resize invalidates old viewport");
            Enable(false);
            phase = "off";
            settle = window.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames + 4;
            PumpUntil(() => window.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames >= settle);
            drawCutoff = Interlocked.Read(ref nativeDraws);
            baseCutoff = window.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames + 12;
            PumpUntil(() => window.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames >= baseCutoff);
            Check(nativeDraws == drawCutoff, "Explicit off did not stop GPU overlays.");
            checks.Add("explicit off leaves the base running");
            Console.WriteLine($"PASS: production viewer over owned encrypted TCP, {sentFrames} frames, {nativeDraws} native GPU draws.");
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            cancellation.Cancel();
            window.Close();
            client.DisconnectAsync().GetAwaiter().GetResult();
            try { host.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, failure, sentFrames, nativeDraws, inputs, checks, nativeReceived, pixelCheck,
                telemetry = window.CollectRenderTelemetrySnapshot(includeLatencyDistributions: true), logs = logs.ToArray(),
                scope = "Real production viewer/client/decoder over encrypted loopback. Synthetic prerecorded sender, no production capture/backend selection, WAN scheduling, real input application or glass-to-glass latency. GPU readback only during paused correctness screenshots."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }

    private static object ValidateNativePixels(Bitmap original, Bitmap before, Bitmap after, D3D11HwndVideoScaleMode scaleMode)
    {
        var geometry = D3D11HwndVideoPresenter.CalculateGeometry(new(0, 0, 1920, 1080), after.Size, scaleMode);
        Check(geometry.Destination.Size == new Size(1920, 1080), "Pixel oracle requires exact 2:1 native reduction.");
        var region = new Rectangle(geometry.Destination.Location, new(128, 64));
        int changed = 0, maximumError = 0;
        double beforeError = 0, afterError = 0;
        for (int y = 0; y < after.Height; y++)
        for (int x = 0; x < after.Width; x++)
        {
            Color a = before.GetPixel(x, y), b = after.GetPixel(x, y);
            if (!region.Contains(x, y))
            {
                Check(a.ToArgb() == b.ToArgb(), "Native overlay changed a pixel outside its requested region.");
                continue;
            }
            int nx = (x - region.X) * 2, ny = (y - region.Y) * 2;
            Color[] source = [original.GetPixel(nx, ny), original.GetPixel(nx + 1, ny),
                original.GetPixel(nx, ny + 1), original.GetPixel(nx + 1, ny + 1)];
            double[] expected = [source.Average(p => p.R), source.Average(p => p.G), source.Average(p => p.B)];
            int[] rgbBefore = [a.R, a.G, a.B], rgbAfter = [b.R, b.G, b.B];
            if (a.ToArgb() != b.ToArgb()) changed++;
            for (int c = 0; c < 3; c++)
            {
                maximumError = Math.Max(maximumError, (int)Math.Ceiling(Math.Abs(rgbAfter[c] - expected[c])));
                beforeError += Math.Pow(rgbBefore[c] - expected[c], 2);
                afterError += Math.Pow(rgbAfter[c] - expected[c], 2);
            }
        }
        Check(changed > 100 && maximumError <= 1 && afterError < beforeError, "Real viewer pixels did not reproduce the native reference.");
        return new { outputPixels = 128 * 64, changedPixels = changed, maximumRgbError = maximumError,
            baseRmse = Math.Sqrt(beforeError / (128 * 64 * 3)), nativeRmse = Math.Sqrt(afterError / (128 * 64 * 3)),
            outsideViewportUnchanged = true };
    }
}
