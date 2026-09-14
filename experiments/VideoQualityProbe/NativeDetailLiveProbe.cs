using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RemoteDesk;
using RemoteDesk.NativeDetail;

// Own synthetic Win32 window only. One immutable PrintWindow snapshot feeds
// BOTH native damage detection and a persistent NVENC encoder. This CPU/GDI
// bridge is an integration control, NOT the zero-copy production WGC backend.
internal static class NativeDetailLiveProbe
{
    private static readonly Size NativeSize = new(1920, 1080), BaseSize = new(960, 540);
    private static readonly Rectangle Viewport = new(0, 0, 960, 640);

    internal static void Run(string ffmpeg, string fixtures, string output)
    {
        if (Directory.Exists(output)) throw new IOException("Preserving previous live detail evidence.");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        WindowsFormsSynchronizationContext.AutoInstall = false;
        using var original = new Bitmap(Path.Combine(fixtures, "native-reference.png"));
        using var edited = new Bitmap(Path.Combine(fixtures, "native-edited.png"));
        // The synchronous owner remains on the HWND thread throughout the run.
        // PrintWindow targets ONLY this owned synthetic window; no desktop or
        // third-party application pixels can enter the fixture.
        using var window = new CaptureWindow(original, edited, output);
        using var encoder = new PairedEncoder(ffmpeg, output);
        using var link = NativeDetailProbe.LocalLink.CreateAsync().GetAwaiter().GetResult();
        if (!MediaFoundationD3D11H264Decoder.TryCreate(new(BaseSize.Width, BaseSize.Height, 30, AllSamplesIndependent: true),
            out var created, out var capability)) throw new IOException(capability.Detail);
        using var decoder = created!; using var device = decoder.AcquireDeviceLease();
        using var viewer = new Form { ClientSize = NativeSize, ShowInTaskbar = false };
        if (!D3D11HwndVideoPresenter.TryCreate(device, new(viewer.Handle, BaseSize.Width, BaseSize.Height, 30,
            EnableEdgeEnhancement: false, EnableExperimentalUpscaling: true, UpscalingAlgorithm: ExperimentalUpscalingAlgorithm.Nis),
            out var createdPresenter, out var presentationCapability)) throw new IOException(presentationCapability.Detail);
        using var presenter = createdPresenter!; Check(presenter.Resize(NativeSize.Width, NativeSize.Height).IsSuccess, "Live presenter resize failed.");
        // Test setup only, before capture/latency sampling. Present itself does
        // not wait for shader compilation or atlas creation.
        NativeDetailGpuProbe.Prepare(presenter);
        var detailContext = new DetailContext(1, 1, NativeSize.Width, NativeSize.Height);
        var source = new DetailSource(detailContext); var cache = new DetailCache(); cache.Reset(detailContext, new(0,0,960,640));
        var queue = new DetailSendQueue(); var adapter = new NativeDetailGpuProbe.Adapter();
        var clock = Stopwatch.StartNew(); var frames = new List<object>();
        int expectedTiles = Enumerable.Range(0, detailContext.TileCount).Count(i => Intersects(detailContext.Tile(i)));
        int phase = 0, phaseFrames = 0, completedPhases = 0, verifiedNativeFrames = 0;
        long phaseStart = 0, nextControl = 0, detailWireBytes = 0; int controls = 0;
        long? firstNativeInPhase = null;
        DetailTransfer[] oldTextCandidates = []; bool rejectedOldText = false;
        var phaseReports = new List<object>();
        for (int serial = 1; serial <= 200 && completedPhases < 2; serial++)
        {
            long iteration = clock.ElapsedMilliseconds;
            if (clock.ElapsedMilliseconds > 25_000) throw new TimeoutException("Live paired capture exceeded its test deadline.");
            var captureClock = Stopwatch.StartNew();
            byte[] rgba = window.CaptureSnapshot(serial, phase != 0); double captureMs = captureClock.Elapsed.TotalMilliseconds;
            var sourceClock = Stopwatch.StartNew(); var manifest = source.Observe(rgba, clock.ElapsedMilliseconds); double damageMs = sourceClock.Elapsed.TotalMilliseconds;
            var encodeClock = Stopwatch.StartNew(); byte[] au = encoder.EncodeAsync(rgba, serial).GetAwaiter().GetResult(); double encodeBridgeMs = encodeClock.Elapsed.TotalMilliseconds;
            byte[] metadata = DetailWire.EncodeManifest(manifest), payload = new byte[8 + metadata.Length + au.Length];
            "NDB1"u8.CopyTo(payload); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), metadata.Length);
            metadata.CopyTo(payload, 8); au.CopyTo(payload, 8 + metadata.Length);
            byte[] received = link.ExchangeAsync(MessageType.VideoFrame, payload).GetAwaiter().GetResult();
            Check(received.Length >= 8 && received.AsSpan(0,4).SequenceEqual("NDB1"u8), "Invalid paired base envelope.");
            int manifestLength = BinaryPrimitives.ReadInt32LittleEndian(received.AsSpan(4));
            Check(manifestLength >= 44 && manifestLength <= 44 + 2048 * 10 && manifestLength < received.Length - 8, "Invalid paired manifest length.");
            var remoteManifest = DetailWire.DecodeManifest(received.AsSpan(8, manifestLength));
            Check(remoteManifest.Sequence == serial && cache.CanPresent(remoteManifest), "Mismatched source sequence.");
            long pts = serial * 10_000_000L / 30;
            var decoded = decoder.DecodeAccessUnit(received.AsMemory(8 + manifestLength), pts);
            using var frame = decoded.Frame ?? throw new IOException(decoded.Detail);
            Check(frame.HasExplicitSampleTime && frame.SampleTime100Nanoseconds == pts, "Decoder returned the wrong captured sample.");
            Check(cache.Present(remoteManifest), "Could not commit live base manifest.");
            if (phase == 1 && !rejectedOldText)
            {
                // Select an ACTUALLY changed captured tile. A fixed tile index
                // can remain unchanged when the edited glyph starts elsewhere.
                var delayedOldText = oldTextCandidates.FirstOrDefault(tile => remoteManifest[tile.Tile] != tile.Version);
                Check(delayedOldText is not null, "Live edit did not change a captured viewport tile.");
                var old = link.ExchangeAsync(MessageType.Control, delayedOldText!.Chunk(0)).GetAwaiter().GetResult();
                var oldResult = cache.Receive(old, clock.ElapsedMilliseconds);
                Check(oldResult == DetailReceiveResult.Stale, $"Changed old tile was not rejected as stale: {oldResult}, tile={delayedOldText.Tile}, versions={delayedOldText.Version}/{remoteManifest[delayedOldText.Tile]}.");
                rejectedOldText = true;
            }
            var current = cache.Patches.Select(p => p.Tile).ToHashSet();
            // Prepare only a small send window. Precompressing a whole viewport
            // here makes late tiles expire while this slow capture/readback
            // owner is still sending early ones, wasting work and fragments.
            for (int tile = 0; tile < detailContext.TileCount && queue.Count < 2; tile++)
            {
                if (current.Contains(tile) || queue.ContainsTile(tile) || !Intersects(detailContext.Tile(tile))) continue;
                var transfer = source.Build(manifest, tile, clock.ElapsedMilliseconds);
                if (transfer is not null) queue.Enqueue(transfer, clock.ElapsedMilliseconds);
            }
            bool controlPending = clock.ElapsedMilliseconds >= nextControl;
            byte[]? chunk = queue.TryTake(clock.ElapsedMilliseconds, 2_000_000, controlPending, false, 0, source.IsCurrent);
            if (controlPending)
            {
                Check(chunk is null, "Control lost priority.");
                link.ExchangeAsync(MessageType.Ping, []).GetAwaiter().GetResult(); controls++; nextControl = clock.ElapsedMilliseconds + 30;
                chunk = queue.TryTake(clock.ElapsedMilliseconds, 2_000_000, false, false, 0, source.IsCurrent);
            }
            // At most one admitted fragment per captured frame in this bounded
            // test owner. It is deliberately not a production scheduler/FPS test.
            if (chunk is not null)
            {
                var data = link.ExchangeAsync(MessageType.Control, chunk).GetAwaiter().GetResult();
                var status = cache.Receive(data, clock.ElapsedMilliseconds);
                Check(status is DetailReceiveResult.Partial or DetailReceiveResult.Applied, "Unexpected live fragment state: " + status);
                detailWireBytes += chunk.Length + DetailWire.OuterRecordBytes;
            }
            var batch = adapter.Convert(cache, remoteManifest, Viewport, pts);
            var shown = presenter.Present(frame, new(Point.Empty, BaseSize), captureValidation: true,
                nativeDetails: batch, nativeDetailBudget: NativeDetailGpuProbe.CorrectnessBudget());
            Check(shown.IsSuccess, shown.Detail);
            using var pixels = UpscaleComparisonProbe.ReadPixels(device, presenter, NativeSize);
            Check(ReadBarcode(pixels) == serial, "Captured/encoded/displayed frame identity differs; refusing FIFO-only correlation.");
            bool nativeReady = cache.Patches.Count == expectedTiles && presenter.NativeDetailActive;
            if (nativeReady)
            {
                ExactNativeViewport(rgba, pixels); verifiedNativeFrames++;
                firstNativeInPhase ??= clock.ElapsedMilliseconds - phaseStart;
            }
            frames.Add(new { serial, phase, captureMs, printWindowMs = window.LastPrintWindowMilliseconds, damageMs, encodeBridgeMs, h264Bytes = au.Length,
                nativeTiles = cache.Patches.Count, uploadedTiles = presenter.NativeDetailUploadedTiles, nativeReady,
                elapsedMs = clock.ElapsedMilliseconds - iteration });
            phaseFrames++;
            if (nativeReady && phaseFrames >= 65)
            {
                if (phase == 0)
                    oldTextCandidates = Enumerable.Range(0, detailContext.TileCount).Where(i => Intersects(detailContext.Tile(i)))
                        .Select(i => source.Build(manifest, i, clock.ElapsedMilliseconds) ?? throw new IOException("Stable captured viewport became unavailable.")).ToArray();
                pixels.Save(Path.Combine(output, $"phase-{phase}-native.png"));
                using var captured = NativeDetailProbe.BitmapFromRgba((byte[])rgba.Clone(), NativeSize);
                captured.Save(Path.Combine(output, $"phase-{phase}-captured.png"));
                phaseReports.Add(new { phase, frames = phaseFrames, milliseconds = clock.ElapsedMilliseconds - phaseStart,
                    firstNativeMilliseconds = firstNativeInPhase, exactNative = true });
                completedPhases++; phase++; phaseFrames = 0; phaseStart = clock.ElapsedMilliseconds; firstNativeInPhase = null;
            }
            int remaining = (int)(33 - (clock.ElapsedMilliseconds - iteration));
            if (remaining > 0) Thread.Sleep(remaining);
        }
        Check(completedPhases == 2 && rejectedOldText && verifiedNativeFrames > 0, "Live native phases incomplete.");
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
        {
            passed = true, frames = frames.Count, expectedTiles, verifiedNativeFrames, controls, detailWireBytes,
            rejectedOldText, expiredOrObsoleteQueuedTiles = queue.ExpiredTiles, configuredDetailBitsPerSecond = 2_000_000,
            nativeSize = NativeSize, videoSize = BaseSize, phaseReports, samples = frames,
            scope = "Owned synthetic Win32 PrintWindow capture; SAME CPU RGBA snapshot to native comparison and persistent hardware NVENC. Strict single-pending input with RTP sequence/timestamp checks; rendered barcode independently verifies every source frame. Real RDK1 encrypted loopback + MF/D3D11/NIS + GPU native atlas. Preparation is awaited in setup, not inside Present; artificial two-second correctness budgets are NOT a latency policy. Includes per-frame validation readback and a one-fragment-per-frame test scheduler; not zero-copy WGC, production session, WAN/FPS/latency qualification or real user input."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {frames.Count} owned-window captures / NVENC frames / encrypted bases / MF decoded barcodes, {verifiedNativeFrames} exact GPU native viewports, live edit stale rejection.");
    }

    private static bool Intersects(DetailRect rect) => new Rectangle(rect.X,rect.Y,rect.Width,rect.Height).IntersectsWith(Viewport);
    private static int ReadBarcode(Bitmap pixels)
    {
        int value = 0;
        for (int i = 0; i < 16; i++) if (pixels.GetPixel(24 + i * 32 + 16, 1020).R > 128) value |= 1 << i;
        return value;
    }
    private static int ReadBarcode(ReadOnlySpan<byte> rgba)
    {
        int value = 0;
        for (int i = 0; i < 16; i++)
            if (rgba[(1020 * NativeSize.Width + 24 + i * 32 + 16) * 4] > 128) value |= 1 << i;
        return value;
    }
    private static void ExactNativeViewport(byte[] source, Bitmap actual)
    {
        var got = NativeDetailProbe.Rgba(actual);
        for (int y = Viewport.Top; y < Viewport.Bottom; y++)
        {
            int offset = (y * NativeSize.Width + Viewport.Left) * 4;
            if (!source.AsSpan(offset, Viewport.Width * 4).SequenceEqual(got.AsSpan(offset, Viewport.Width * 4)))
                throw new IOException($"Live native pixels do not match captured source at row {y}.");
        }
    }
    private static void Check(bool valid, string reason) { if (!valid) throw new IOException(reason); }

    private sealed class CaptureWindow : Form
    {
        private readonly Bitmap _original, _edited;
        private readonly string _output;
        private int _paintCount;
        internal double LastPrintWindowMilliseconds { get; private set; }
        private int _serial; private bool _useEdited;
        internal CaptureWindow(Bitmap original, Bitmap edited, string output)
        {
            _original = original; _edited = edited; _output = output;
            AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Text = "RemoteDesk 原生补清测试（自动关闭）";
            StartPosition = FormStartPosition.Manual; Location = new Point(40,40);
            ClientSize = NativeSize; ShowInTaskbar = false; _ = Handle;
            Check(ClientSize == NativeSize, "Owned capture HWND changed native size.");
            Show(); Update();
        }
        protected override bool ShowWithoutActivation => true;
        protected override void OnPaint(PaintEventArgs e)
        {
            _paintCount++;
            e.Graphics.DrawImageUnscaled(_useEdited ? _edited : _original, 0, 0);
            e.Graphics.FillRectangle(Brushes.Black, 0, 992, 1920, 88);
            for (int i = 0; i < 16; i++)
                e.Graphics.FillRectangle((_serial & (1 << i)) != 0 ? Brushes.White : Brushes.Black, 24 + i * 32, 1000, 32, 40);
        }
        internal byte[] CaptureSnapshot(int serial, bool edited)
        {
            Application.DoEvents();
            Check(!IsDisposed && IsHandleCreated, "Owned capture window was closed.");
            _serial = serial; _useEdited = edited;
            using var bitmap = new Bitmap(NativeSize.Width, NativeSize.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                nint dc = graphics.GetHdc();
                var captureClock = Stopwatch.StartNew();
                try { Check(PrintWindow(Handle, dc, 1 | 2), "Owned PrintWindow capture failed."); }
                finally { graphics.ReleaseHdc(dc); }
                LastPrintWindowMilliseconds = captureClock.Elapsed.TotalMilliseconds;
            }
            byte[] rgba = NativeDetailProbe.Rgba(bitmap);
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255; // Desktop is opaque; identical bytes go to both branches.
            if (ReadBarcode(rgba) != serial)
            {
                using var verify = NativeDetailProbe.BitmapFromRgba((byte[])rgba.Clone(), NativeSize);
                verify.Save(Path.Combine(_output, "capture-failed.png"));
                throw new IOException($"Owned capture barcode {ReadBarcode(rgba)} != {serial}; paints={_paintCount}, DPI={DeviceDpi}, client={ClientSize}.");
            }
            return rgba;
        }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    }

    private sealed class PairedEncoder : IDisposable
    {
        private readonly Process _process;
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly H264RtpAccessUnitAssembler _assembler = new();
        private readonly Task<string> _errors, _stdout;
        private readonly string _output;
        private readonly SafeFileHandle? _job;
        private bool _failed, _disposed;
        private uint? _origin;
        private ushort? _nextSequence;
        private int _serial;
        internal PairedEncoder(string ffmpeg, string output)
        {
            _output = output;
            _socket.ReceiveBufferSize = 4 * 1024 * 1024; _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
            var production = FfmpegDesktopH264Capture.BuildArguments(new(FfmpegDesktopCaptureBackend.GdiGrabBounds,
                new(Point.Empty, NativeSize), BaseSize, 30, GopLength: 1), FfmpegH264Encoder.NvidiaNvenc).ToList();
            int start = production.IndexOf("-c:v"); var settings = production.GetRange(start, production.IndexOf("-an") - start);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-filter_threads", "1",
                "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "1920x1080", "-framerate", "30",
                "-probesize", "32", "-analyzeduration", "0", "-i", "pipe:0",
                "-vf", "scale=960:540:flags=lanczos:in_range=full:out_range=tv:out_color_matrix=bt709,format=nv12" };
            args.AddRange(settings);
            args.AddRange(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
                "-an", "-fps_mode", "passthrough", "-avioflags", "direct", "-flush_packets", "1",
                "-bsf:v", "dump_extra=freq=keyframe,h264_metadata=aud=insert", "-f", "rtp", "-payload_type", "96",
                "-rtpflags", "skip_rtcp", $"rtp://127.0.0.1:{port}?pkt_size=16384&connect=1&localaddr=127.0.0.1"]);
            var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in args) info.ArgumentList.Add(argument);
            _process = new Process { StartInfo = info };
            try { if (!_process.Start()) throw new IOException("Could not start the owned NVENC encoder."); }
            catch { _socket.Dispose(); _process.Dispose(); throw; }
            _job = WindowsKillOnCloseJob.TryCreateAndAssign(_process);
            _errors = _process.StandardError.ReadToEndAsync(); _stdout = _process.StandardOutput.ReadToEndAsync();
        }
        internal async Task<byte[]> EncodeAsync(byte[] rgba, int serial)
        {
            if (_failed || _disposed || serial != _serial + 1 || rgba.Length != NativeSize.Width * NativeSize.Height * 4)
                throw new InvalidOperationException("Paired encoder generation or input is invalid.");
            if (_socket.Available != 0) throw new IOException("Unexpected output without an outstanding native snapshot.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(serial == 1 ? 10 : 2));
            Task<byte[]> receiving = ReadAsync(serial, deadline.Token);
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(rgba, deadline.Token).ConfigureAwait(false);
                await _process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
                byte[] result = await receiving.ConfigureAwait(false); _serial = serial; return result;
            }
            catch
            {
                _failed = true; deadline.Cancel(); Stop();
                try { await receiving.ConfigureAwait(false); } catch { }
                throw;
            }
        }
        private async Task<byte[]> ReadAsync(int serial, CancellationToken token)
        {
            var datagram = new byte[65535];
            while (true)
            {
                int length = await _socket.ReceiveAsync(datagram, SocketFlags.None, token).ConfigureAwait(false);
                Check(length >= 12 && (datagram[0] >> 6) == 2 && (datagram[1] & 127) == 96, "Invalid encoder RTP record.");
                ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(2));
                Check(_nextSequence is null || sequence == _nextSequence, "Encoder RTP gap; native pairing is invalid.");
                _nextSequence = unchecked((ushort)(sequence + 1));
                uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(4)); _origin ??= timestamp;
                Check(unchecked(timestamp - _origin.Value) == (uint)(serial - 1) * 3000u, "Encoder RTP sample order differs from native input.");
                using var au = _assembler.AppendPacket(datagram.AsSpan(0, length));
                if (au is null) continue;
                Check(au.IsIdr && au.HasSps && au.HasPps, "Paired encoder did not return a configured independent frame.");
                return au.Bytes.ToArray();
            }
        }
        private void Stop()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(1500)) Stop();
                if (!_process.WaitForExit(1500)) throw new IOException("Owned encoder did not exit.");
                File.WriteAllText(Path.Combine(_output, "encoder.log"), _errors.GetAwaiter().GetResult());
                _stdout.GetAwaiter().GetResult();
            }
            finally { _socket.Dispose(); _job?.Dispose(); _process.Dispose(); }
        }
    }
}
