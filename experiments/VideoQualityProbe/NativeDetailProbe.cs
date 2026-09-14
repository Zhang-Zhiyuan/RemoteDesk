using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDesk;
using RemoteDesk.NativeDetail;

// Isolated NDL1 research receiver. Only listens on ephemeral loopback, with a
// random per-run secret and the actual RDK1/AES-GCM envelope. No real endpoint,
// screen capture, input injection, app settings or production protocol changes.
internal static class NativeDetailProbe
{
    internal static async Task RunAsync(string ffmpeg, string fixtures, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Preserving existing native-detail evidence.");
        Directory.CreateDirectory(output);
        NativeDetailGolden.Write(Path.Combine(output, "interop-vectors.txt"));
        string referenceFile = Path.Combine(fixtures, "native-reference.png");
        using var native = new Bitmap(referenceFile);
        using var edited = new Bitmap(Path.Combine(fixtures, "native-edited.png"));
        if (native.Size != new Size(3840, 2160) || edited.Size != native.Size) throw new IOException("Expected the 4K text fixtures.");
        byte[] originalPixels = Rgba(native), editedPixels = Rgba(edited);
        string editedH264 = Path.Combine(output, "edited-base.h264");
        await TextClarityProbe.EncodeDetailBaseAsync(ffmpeg, Path.Combine(fixtures, "native-edited.png"), editedH264).ConfigureAwait(false);
        byte[] firstAu = LastIndependentAu(Path.Combine(fixtures, "10mbps-static-1080p-gop1.h264"));
        byte[] editedAu = LastIndependentAu(editedH264);
        var context = new DetailContext(1, 1, native.Width, native.Height);
        var viewport = new DetailRect(0, 0, 960, 640);
        var source = new DetailSource(context);
        var cache = new DetailCache(); cache.Reset(context, viewport);
        var clock = Stopwatch.StartNew();
        var first = source.Observe(originalPixels, clock.ElapsedMilliseconds);
        using var link = await LocalLink.CreateAsync().ConfigureAwait(false);
        using var initialBase = await BaseAsync(link, cache, first, firstAu, output, "initial").ConfigureAwait(false);
        await Task.Delay(250).ConfigureAwait(false);
        var initialTransfers = new List<DetailTransfer>();
        var sender = new DetailSendQueue();
        for (int tile = 0; tile < context.TileCount; tile++)
        {
            if (!context.Tile(tile).Intersects(viewport)) continue;
            var transfer = source.Build(first, tile, clock.ElapsedMilliseconds) ?? throw new IOException("Unexpected unstable initial tile.");
            initialTransfers.Add(transfer);
            if (!sender.Enqueue(transfer, clock.ElapsedMilliseconds)) throw new IOException("Viewport exceeds initial bounded queue.");
        }
        object initialDelivery = await DeliverAsync(link, source, cache, sender, clock).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "initial-delivery.json"), JsonSerializer.Serialize(new
            { expectedTiles = initialTransfers.Count, receivedTiles = cache.Patches.Count, sender.ExpiredTiles, initialDelivery })).ConfigureAwait(false);
        Check(cache.Patches.Count == initialTransfers.Count, $"Incomplete initial viewport: {cache.Patches.Count}/{initialTransfers.Count}, expired {sender.ExpiredTiles}.");
        using var enhanced = Composite(initialBase, cache, viewport);
        CheckPixels(originalPixels, initialBase, enhanced, viewport, expectNative: true);
        initialBase.Save(Path.Combine(output, "base-nis.png")); enhanced.Save(Path.Combine(output, "native-detail.png"));
        SaveComparison(native, initialBase, enhanced, output);

        // A new IDR/base with unchanged native content must not erase details.
        var repeat = source.Observe(originalPixels, clock.ElapsedMilliseconds);
        using var repeatedBase = await BaseAsync(link, cache, repeat, firstAu, output, "repeat").ConfigureAwait(false);
        Check(cache.Patches.Count == initialTransfers.Count, "Unchanged base erased native detail.");
        using var repeatedComposite = Composite(repeatedBase, cache, viewport);
        CheckPixels(originalPixels, repeatedBase, repeatedComposite, viewport, expectNative: true);

        // Simulate an autonomous page update, without local input. Source-native
        // comparison must invalidate changed tiles independently of input idle.
        var change = source.Observe(editedPixels, clock.ElapsedMilliseconds);
        var changedTiles = initialTransfers.Where(tile => change[tile.Tile] != tile.Version).Select(tile => tile.Tile).ToHashSet();
        Check(changedTiles.Count > 0, "Fixture edit changed no tiles.");
        using var changedBase = await BaseAsync(link, cache, change, editedAu, output, "changed").ConfigureAwait(false);
        Check(cache.Patches.Count == initialTransfers.Count - changedTiles.Count, "Changed detail was retained or unrelated detail erased.");
        var late = initialTransfers.First(tile => changedTiles.Contains(tile.Tile));
        byte[] latePayload = await link.ExchangeAsync(MessageType.Control, late.Chunk(0)).ConfigureAwait(false);
        Check(cache.Receive(latePayload, clock.ElapsedMilliseconds) == DetailReceiveResult.Stale, "Late old text accepted.");
        using (var cleared = Composite(changedBase, cache, viewport))
        {
            byte[] composed = Rgba(cleared), baseline = Rgba(changedBase);
            foreach (int tile in changedTiles)
            {
                var rect = context.Tile(tile);
                for (int y = rect.Y; y < Math.Min(rect.Y + rect.Height, viewport.Height); y++)
                for (int x = rect.X; x < Math.Min(rect.X + rect.Width, viewport.Width); x++)
                {
                    int offset = (y * context.Width + x) * 4;
                    Check(composed.AsSpan(offset, 4).SequenceEqual(baseline.AsSpan(offset, 4)), "Old text remained above a newer base.");
                }
            }
            cleared.Save(Path.Combine(output, "changed-before-refill.png"));
        }
        await Task.Delay(250).ConfigureAwait(false);
        foreach (int tile in changedTiles)
            Check(sender.Enqueue(source.Build(change, tile, clock.ElapsedMilliseconds)!, clock.ElapsedMilliseconds), "Changed tile queue rejected.");
        object editedDelivery = await DeliverAsync(link, source, cache, sender, clock).ConfigureAwait(false);
        using var restored = Composite(changedBase, cache, viewport);
        CheckPixels(editedPixels, changedBase, restored, viewport, expectNative: true);
        restored.Save(Path.Combine(output, "changed-native-detail.png"));

        // Off is a complete local rollback, without changing video geometry.
        cache.Disable(); using var disabled = Composite(changedBase, cache, viewport);
        Check(Rgba(disabled).AsSpan().SequenceEqual(Rgba(changedBase)), "Disable did not restore the exact base.");
        Check(cache.Receive(latePayload, clock.ElapsedMilliseconds) == DetailReceiveResult.Inactive, "Old patch reactivated a disabled receiver.");
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
        {
            passed = true, initialTiles = initialTransfers.Count, changedTiles = changedTiles.Count,
            initialCompressedBytes = initialTransfers.Sum(tile => tile.Encoded.Length), initialDelivery, editedDelivery,
            exactNativeViewport = true, exactOffRollback = true, rejectedLateOldText = true,
            manifestBytes = new[] { first, repeat, change }.Select(item => DetailWire.EncodeManifest(item).Length).ToArray(),
            scope = "Synthetic same-source 4K RGB + downscaled NVENC video, actual RDK1 encrypted TCP loopback, real MF/D3D11/NIS base readback, CPU RGBA detail composition. 2 Mbps detail shaper is configured, not measured WAN capacity. No live capture, production UI, sustained video traffic, real input or end-to-end display latency."
        }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        Console.WriteLine($"PASS: {initialTransfers.Count} exact native tiles, {changedTiles.Count} changed/refilled, stale rejection and exact off rollback.");
    }

    private static async Task<object> DeliverAsync(LocalLink link, DetailSource source, DetailCache cache,
        DetailSendQueue sender, Stopwatch clock)
    {
        long started = clock.ElapsedMilliseconds, nextControl = started + 30, wireBytes = 0;
        int chunks = 0; var controlMs = new List<double>();
        while (sender.Count != 0)
        {
            long now = clock.ElapsedMilliseconds;
            if (now - started > 5000) throw new TimeoutException("Detail delivery failed its bounded test deadline.");
            bool control = now >= nextControl;
            var packet = sender.TryTake(now, 2_000_000, control, false, 0, source.IsCurrent);
            if (control)
            {
                Check(packet is null, "Detail was admitted ahead of pending control.");
                var timing = Stopwatch.StartNew();
                byte[] ping = []; // Production RDK1 Ping records have no payload.
                Check((await link.ExchangeAsync(MessageType.Ping, ping).ConfigureAwait(false)).AsSpan().SequenceEqual(ping), "Control envelope mismatch.");
                controlMs.Add(timing.Elapsed.TotalMilliseconds); nextControl = clock.ElapsedMilliseconds + 30;
            }
            else if (packet is not null)
            {
                wireBytes += packet.Length + DetailWire.OuterRecordBytes; chunks++;
                var received = await link.ExchangeAsync(MessageType.Control, packet).ConfigureAwait(false);
                var result = cache.Receive(received, clock.ElapsedMilliseconds);
                Check(result is DetailReceiveResult.Partial or DetailReceiveResult.Applied, "Unexpected tile result: " + result);
            }
            else await Task.Delay(1).ConfigureAwait(false);
        }
        return new { milliseconds = clock.ElapsedMilliseconds - started, wireBytes, chunks, controlRecords = controlMs.Count,
            maximumControlExchangeMs = controlMs.Count == 0 ? (double?)null : controlMs.Max(),
            scope = "Real encrypted local record exchanges with synthetic control-priority requests, no remote input application ACK or WAN measurement." };
    }

    private static async Task<Bitmap> BaseAsync(LocalLink link, DetailCache cache, DetailManifest manifest, byte[] au,
        string output, string name)
    {
        byte[] metadata = DetailWire.EncodeManifest(manifest);
        byte[] packet = new byte[8 + metadata.Length + au.Length];
        "NDB1"u8.CopyTo(packet); BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), metadata.Length);
        metadata.CopyTo(packet, 8); au.CopyTo(packet, 8 + metadata.Length);
        byte[] received = await link.ExchangeAsync(MessageType.VideoFrame, packet).ConfigureAwait(false);
        Check(received.Length >= 8 && received.AsSpan(0, 4).SequenceEqual("NDB1"u8), "Invalid prototype base envelope.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(received.AsSpan(4));
        Check(length >= 44 && length <= 44 + 2048 * 10 && length < received.Length - 8, "Invalid prototype base lengths.");
        var decodedManifest = DetailWire.DecodeManifest(received.AsSpan(8, length));
        Check(cache.CanPresent(decodedManifest), "Out-of-order prototype base.");
        string encoded = Path.Combine(output, name + "-received.h264");
        await File.WriteAllBytesAsync(encoded, received.AsMemory(8 + length).ToArray()).ConfigureAwait(false);
        var rendered = TextClarityProbe.RenderDetailBase(encoded);
        // The final composite is only exposed after successful base decode and
        // atomic manifest commit on this single owner; failed decode cannot
        // authorize details over the preceding base.
        Check(cache.Present(decodedManifest), "Base commit rejected after decode.");
        return rendered;
    }

    internal static byte[] LastIndependentAu(string path)
    {
        var parser = new AnnexBH264AccessUnitParser();
        var units = parser.Append(File.ReadAllBytes(path)).Concat(parser.Complete()).ToArray();
        try
        {
            var last = units.Last();
            Check(last.IsIdr && last.HasSps && last.HasPps, "Expected a self-contained configured IDR.");
            return last.Bytes.ToArray();
        }
        finally { foreach (var unit in units) unit.Dispose(); }
    }

    private static Bitmap Composite(Bitmap baseline, DetailCache cache, DetailRect viewport)
    {
        byte[] output = Rgba(baseline);
        foreach (var patch in cache.Patches)
        {
            var rect = patch.Rect;
            for (int y = Math.Max(rect.Y, viewport.Y); y < Math.Min(rect.Y + rect.Height, viewport.Y + viewport.Height); y++)
            {
                int x = Math.Max(rect.X, viewport.X), width = Math.Min(rect.X + rect.Width, viewport.X + viewport.Width) - x;
                if (width > 0) patch.Rgba.Span.Slice(((y - rect.Y) * rect.Width + x - rect.X) * 4, width * 4)
                    .CopyTo(output.AsSpan((y * baseline.Width + x) * 4));
            }
        }
        return BitmapFromRgba(output, baseline.Size);
    }
    private static void CheckPixels(byte[] native, Bitmap baseline, Bitmap actual, DetailRect viewport, bool expectNative)
    {
        byte[] result = Rgba(actual), fallback = Rgba(baseline);
        for (int y = 0; y < actual.Height; y++)
        for (int x = 0; x < actual.Width; x++)
        {
            int offset = (y * actual.Width + x) * 4;
            bool inside = x >= viewport.X && x < viewport.X + viewport.Width && y >= viewport.Y && y < viewport.Y + viewport.Height;
            Check(result.AsSpan(offset, 4).SequenceEqual((inside && expectNative ? native : fallback).AsSpan(offset, 4)), "Composite changed the wrong pixel.");
        }
    }
    internal static byte[] Rgba(Bitmap picture)
    {
        var bytes = new byte[picture.Width * picture.Height * 4];
        var data = picture.LockBits(new(0, 0, picture.Width, picture.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < picture.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * picture.Width * 4, picture.Width * 4); }
        finally { picture.UnlockBits(data); }
        SwapRedBlue(bytes); return bytes;
    }
    internal static Bitmap BitmapFromRgba(byte[] rgba, Size size)
    {
        var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb); SwapRedBlue(rgba);
        var data = result.LockBits(new(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < size.Height; y++) Marshal.Copy(rgba, y * size.Width * 4, data.Scan0 + y * data.Stride, size.Width * 4); }
        finally { result.UnlockBits(data); }
        return result;
    }
    private static void SwapRedBlue(byte[] pixels)
    { for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]); }
    private static void SaveComparison(Bitmap native, Bitmap baseline, Bitmap detailed, string output)
    {
        using var result = new Bitmap(720, 232 * 3); using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.LightGray); using var font = new Font("Segoe UI", 16, GraphicsUnit.Pixel);
        var rows = new[] { ("Native source", native), ("1080p H.264 + actual NIS readback", baseline), ("Same base + received native RGBA tiles (prototype)", detailed) };
        for (int i = 0; i < rows.Length; i++)
        {
            graphics.DrawString(rows[i].Item1, font, Brushes.Black, 12, i * 232 + 4);
            using var crop = rows[i].Item2.Clone(new(24, 24, 660, 184), PixelFormat.Format32bppArgb);
            graphics.DrawImageUnscaled(crop, 12, i * 232 + 34);
        }
        result.Save(Path.Combine(output, "native-detail-comparison.png"));
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new IOException(reason); }

    internal sealed class LocalLink : IDisposable
    {
        private readonly TcpClient _client, _server;
        private readonly SecureSession _clientSession, _serverSession;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(45));
        private LocalLink(TcpClient client, TcpClient server, SecureSession clientSession, SecureSession serverSession)
        { _client = client; _server = server; _clientSession = clientSession; _serverSession = serverSession; }
        internal static async Task<LocalLink> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            var client = new TcpClient { NoDelay = true }; TcpClient? server = null;
            SecureSession? serverSession = null, clientSession = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var accepting = listener.AcceptTcpClientAsync(deadline.Token);
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token).ConfigureAwait(false);
                server = await accepting.ConfigureAwait(false); server.NoDelay = true;
                string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var authentication = Protocol.AuthenticateServerAsync(server.GetStream(), secret, deadline.Token);
                clientSession = await Protocol.AuthenticateClientAsync(client.GetStream(), secret, deadline.Token).ConfigureAwait(false);
                serverSession = await authentication.ConfigureAwait(false) ?? throw new IOException("Local authentication failed.");
                return new(client, server, clientSession, serverSession);
            }
            catch { clientSession?.Dispose(); serverSession?.Dispose(); client.Dispose(); server?.Dispose(); throw; }
            finally { listener.Stop(); }
        }
        internal async Task<byte[]> ExchangeAsync(MessageType type, byte[] payload)
        {
            var read = Protocol.ReadMessageAsync(_client.GetStream(), _clientSession, _deadline.Token);
            await Protocol.WriteMessageAsync(_server.GetStream(), type, payload, _serverSession, _writeLock, _deadline.Token).ConfigureAwait(false);
            var received = await read.ConfigureAwait(false); Check(received.Type == type, "Local message type mismatch.");
            return received.PayloadSpan.ToArray();
        }
        public void Dispose()
        { _deadline.Cancel(); _client.Dispose(); _server.Dispose(); _clientSession.Dispose(); _serverSession.Dispose(); _writeLock.Dispose(); _deadline.Dispose(); }
    }
}
