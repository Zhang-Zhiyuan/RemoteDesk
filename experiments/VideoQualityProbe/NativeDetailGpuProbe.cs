using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using RemoteDesk;
using RemoteDesk.NativeDetail;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

// Exercises the production presenter entry point; the source/wire are still
// synthetic. No desktop capture, real host, input or installed-app changes.
internal static class NativeDetailGpuProbe
{
    internal static void Run(string fixtures, string priorDetailRun, string output)
    {
        if (Directory.Exists(output)) throw new IOException("Preserving previous GPU detail evidence.");
        Directory.CreateDirectory(output);
        WindowsFormsSynchronizationContext.AutoInstall = false;
        var rows = new List<object>();
        NativeText(fixtures, priorDetailRun, output, rows);
        Geometry(output, rows);
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
        {
            passed = true, gpuAtlasBytes = D3D11NativeDetailCompositor.AtlasBytes,
            maximumTileUploadsPerFrame = D3D11NativeDetailCompositor.MaximumUploadsPerFrame, rows,
            scope = "Actual Windows MF/D3D11 presenter and GPU native tile composition/readback. Synthetic sources; no live capture, production session negotiation, real relay, input application ACK or end-to-end latency. Preparation is awaited only in probe setup, never in Present. An artificial two-second budget admits detail for pixel correctness; it is NOT a real latency-policy qualification. GPU timestamps exclude DXGI wait, Present, warm-up and readback."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: native text GPU composition, sparse interpolation/geometry, sample binding, no-change reuse and same-frame failure rollback.");
    }

    private static void NativeText(string fixtures, string prior, string output, List<object> rows)
    {
        using var native = new Bitmap(Path.Combine(fixtures, "native-reference.png"));
        using var edited = new Bitmap(Path.Combine(fixtures, "native-edited.png"));
        var size = native.Size;
        var viewport = new Rectangle(0, 0, 960, 640);
        var context = new DetailContext(1, 1, size.Width, size.Height);
        var source = new DetailSource(context);
        var cache = new DetailCache(); cache.Reset(context, new(0, 0, 960, 640));
        var first = source.Observe(NativeDetailProbe.Rgba(native), 0); cache.Present(first);
        Fill(source, cache, first, viewport, 250);
        var adapter = new Adapter();
        if (!MediaFoundationD3D11H264Decoder.TryCreate(new(1920, 1080, 30, AllSamplesIndependent: true), out var created, out var capability))
            throw new IOException(capability.Detail);
        using var decoder = created!;
        using var device = decoder.AcquireDeviceLease();
        using var window = new Form { ClientSize = size, ShowInTaskbar = false };
        using var presenter = CreatePresenter(device, window, new(1920, 1080), size, D3D11HwndVideoScaleMode.Fit, nis: true);
        var firstDecode = decoder.DecodeAccessUnit(NativeDetailProbe.LastIndependentAu(Path.Combine(fixtures, "10mbps-static-1080p-gop1.h264")), 1_000_000);
        using var frame = firstDecode.Frame ?? throw new IOException(firstDecode.Detail);
        Check(frame.HasExplicitSampleTime && frame.SampleTime100Nanoseconds == 1_000_000, "No exact decoder PTS.");
        Rectangle baseImage = new(0, 0, 1920, 1080);
        using var baseline = Draw(device, presenter, frame, baseImage, size, null);
        var batch = adapter.Convert(cache, first, viewport, frame.SampleTime100Nanoseconds);
        using (var notReady = Draw(device, presenter, frame, baseImage, size, batch)) ExactImage(baseline, notReady);
        Check(presenter.NativeDetailAtlasBytes == 0, "Present lazily created an atlas.");
        Prepare(presenter);
        var initialUpload = Populate(presenter, () => presenter.Present(frame, baseImage, nativeDetails: batch, nativeDetailBudget: CorrectnessBudget()), 40);
        using var detailed = Draw(device, presenter, frame, baseImage, size, batch);
        Check(presenter.NativeDetailActive, presenter.NativeDetailStatus);
        Check(presenter.NativeDetailUploadedTiles == 0, "Expected reuse after the paced initial uploads.");
        ExactViewport(native, baseline, detailed, viewport);
        detailed.Save(Path.Combine(output, "native-gpu.png"));
        SaveComparison(native, baseline, detailed, output);
        using (var repeat = Draw(device, presenter, frame, baseImage, size, batch))
        {
            Check(presenter.NativeDetailUploadedTiles == 0, "Unchanged tiles were uploaded again.");
            ExactViewport(native, baseline, repeat, viewport);
        }
        var next = source.Observe(NativeDetailProbe.Rgba(edited), 1000); Check(cache.Present(next), "Edited manifest rejected.");
        var changedDecode = decoder.DecodeAccessUnit(NativeDetailProbe.LastIndependentAu(Path.Combine(prior, "edited-base.h264")), 2_000_000);
        using var changedFrame = changedDecode.Frame ?? throw new IOException(changedDecode.Detail);
        Check(changedFrame.HasExplicitSampleTime && changedFrame.SampleTime100Nanoseconds == 2_000_000, "Changed decoder PTS mismatch.");
        var beforeRefill = adapter.Convert(cache, next, viewport, changedFrame.SampleTime100Nanoseconds);
        using (var cleared = Draw(device, presenter, changedFrame, baseImage, size, beforeRefill))
        {
            Check(presenter.NativeDetailUploadedTiles == 0, "Unchanged 36 tiles should stay on the GPU.");
            cleared.Save(Path.Combine(output, "changed-before-refill.png"));
        }
        Fill(source, cache, next, viewport, 1250);
        var afterRefill = adapter.Convert(cache, next, viewport, changedFrame.SampleTime100Nanoseconds);
        var changedUpload = Populate(presenter, () => presenter.Present(changedFrame, baseImage, nativeDetails: afterRefill, nativeDetailBudget: CorrectnessBudget()), 4);
        using var restored = Draw(device, presenter, changedFrame, baseImage, size, afterRefill);
        Check(presenter.NativeDetailUploadedTiles == 0, "Expected reuse after four changed uploads.");
        restored.Save(Path.Combine(output, "changed-native-gpu.png"));
        using var changedBase = Draw(device, presenter, changedFrame, baseImage, size, null);
        ExactViewport(edited, changedBase, restored, viewport);
        using (var mismatched = Draw(device, presenter, changedFrame, baseImage, size, batch))
        {
            Check(!presenter.NativeDetailActive, "Old sample authorized a native overlay.");
            ExactImage(changedBase, mismatched);
        }
        // Simulate a fault AFTER Draw, not merely constructor failure. The
        // same frame must be re-rendered without any native pixels left over.
        presenter.NativeDetailAfterDrawForTests = () => throw new InvalidOperationException("Injected post-draw GPU detail failure");
        using (var failed = Draw(device, presenter, changedFrame, baseImage, size, afterRefill))
        {
            Check(!presenter.NativeDetailActive && presenter.NativeDetailStatus.Contains("回退"), "Detail failure did not fall back.");
            ExactImage(changedBase, failed);
        }
        presenter.NativeDetailAfterDrawForTests = null;
        using (var sticky = Draw(device, presenter, changedFrame, baseImage, size, afterRefill)) ExactImage(changedBase, sticky);
        presenter.DisableNativeDetails();
        using (var off = Draw(device, presenter, changedFrame, baseImage, size, null)) ExactImage(changedBase, off);
        Prepare(presenter);
        Populate(presenter, () => presenter.Present(changedFrame, baseImage, nativeDetails: afterRefill, nativeDetailBudget: CorrectnessBudget()), 40);
        using (var on = Draw(device, presenter, changedFrame, baseImage, size, afterRefill)) ExactViewport(edited, changedBase, on, viewport);
        var baseTiming = Time(device, presenter, changedFrame, baseImage, null);
        var detailTiming = Time(device, presenter, changedFrame, baseImage, afterRefill);
        rows.Add(new { test = "actual-h264-nis-native-text", initialTiles = 40, changedTiles = 4,
            initialUpload, changedUpload,
            exactViewport = true, wrongSampleRejected = true, exactDisable = true, postDrawFailureRollback = true,
            baseTiming, cachedDetailTiming = detailTiming });
    }

    private static void Geometry(string output, List<object> rows)
    {
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        using var dxgi = device.QueryInterface<IDXGIDevice>(); using var hardware = dxgi.GetAdapter();
        rows.Add(new { adapter = hardware.Description.Description });
        var nativeSize = new Size(257, 259);
        var bytes = new byte[nativeSize.Width * nativeSize.Height * 4];
        for (int y = 0; y < nativeSize.Height; y++)
        for (int x = 0; x < nativeSize.Width; x++)
        {
            int i = (y * nativeSize.Width + x) * 4;
            bytes[i] = (byte)(x * 19 + y * 7); bytes[i + 1] = (byte)(x * 3 + y * 29); bytes[i + 2] = (byte)(x ^ y); bytes[i + 3] = 255;
        }
        var tiles = new List<NativeDetailTile>();
        for (int y = 0; y < nativeSize.Height; y += 128)
        for (int x = 0; x < nativeSize.Width; x += 128)
        {
            var rect = new Rectangle(x, y, Math.Min(128, nativeSize.Width - x), Math.Min(128, nativeSize.Height - y));
            var tile = new byte[rect.Width * rect.Height * 4];
            for (int row = 0; row < rect.Height; row++)
                bytes.AsSpan(((y + row) * nativeSize.Width + x) * 4, rect.Width * 4).CopyTo(tile.AsSpan(row * rect.Width * 4));
            tiles.Add(new(rect, 1, tile));
        }
        var baseImage = new Rectangle(2, 4, 128, 130);
        using var texture = device.CreateTexture2D(new Texture2DDescription(Format.NV12, 132, 138, 1, 1, BindFlags.Decoder));
        byte[] nv12 = new byte[132 * 138 * 3 / 2]; Array.Fill(nv12, (byte)96, 0, 132 * 138); Array.Fill(nv12, (byte)128, 132 * 138, 132 * 138 / 2);
        device.ImmediateContext.UpdateSubresource(nv12, texture, 0, 132, (uint)nv12.Length);
        var cases = new[] {
            ("native", new Size(257,259), D3D11HwndVideoScaleMode.Fill),
            ("125-percent", new Size(322,324), D3D11HwndVideoScaleMode.Fill),
            ("150-percent", new Size(386,389), D3D11HwndVideoScaleMode.Fill),
            ("200-percent", new Size(514,518), D3D11HwndVideoScaleMode.Fill),
            ("reduction", new Size(173,177), D3D11HwndVideoScaleMode.Fill),
            ("letterbox", new Size(514,600), D3D11HwndVideoScaleMode.Fit),
            ("crop", new Size(700,400), D3D11HwndVideoScaleMode.Fill),
            ("native-base", new Size(514,518), D3D11HwndVideoScaleMode.FitWithoutUpscaling) };
        foreach (var (name, size, mode) in cases)
        using (var window = new Form { ClientSize = size, ShowInTaskbar = false })
        using (var presenter = CreatePresenter(device, window, baseImage.Size, size, mode, nis: false))
        {
            Rectangle viewport = name == "native" ? new(Point.Empty, nativeSize) : new(19, 23, 211, 221);
            var frameTiles = tiles.Where(tile => tile.Bounds.IntersectsWith(viewport)).ToArray();
            var full = new NativeDetailPresentation(2, 1, 1, 77, nativeSize, viewport, frameTiles);
            Check(presenter.Present(texture, 0, baseImage, true).IsSuccess, "Synthetic base presentation failed.");
            using var baseline = UpscaleComparisonProbe.ReadPixels(device, presenter, size);
            Prepare(presenter);
            var geometry = D3D11HwndVideoPresenter.CalculateGeometry(baseImage, size, mode);
            Populate(presenter, () => presenter.Present(texture, 0, baseImage, false, full, 77, CorrectnessBudget()),
                D3D11NativeDetailCompositor.CanRender(full, baseImage, geometry) ? frameTiles.Length : 0);
            Check(presenter.Present(texture, 0, baseImage, true, full, 77, CorrectnessBudget()).IsSuccess, presenter.NativeDetailStatus);
            using var composed = UpscaleComparisonProbe.ReadPixels(device, presenter, size);
            var error = CheckReference(bytes, baseline, composed, full, baseImage, geometry);
            var sparse = new NativeDetailPresentation(2, 1, 2, 78, nativeSize, viewport,
                frameTiles.Where(tile => tile.Bounds.Location != new Point(128, 128)).ToArray());
            Check(presenter.Present(texture, 0, baseImage, true, sparse, 78, CorrectnessBudget()).IsSuccess, presenter.NativeDetailStatus);
            using var holes = UpscaleComparisonProbe.ReadPixels(device, presenter, size);
            var sparseError = CheckReference(bytes, baseline, holes, sparse, baseImage, geometry);
            composed.Save(Path.Combine(output, name + ".png")); holes.Save(Path.Combine(output, name + "-sparse.png"));
            rows.Add(new { test = name, size, maxRgbError = error, maxSparseRgbError = sparseError, noStaleOrUninitializedPixels = true });
        }
    }

    private static int CheckReference(byte[] native, Bitmap baseline, Bitmap actual, NativeDetailPresentation frame,
        Rectangle baseImage, D3D11HwndVideoPresentationGeometry geometry)
    {
        var c = D3D11NativeDetailCompositor.Constants(frame, baseImage, geometry);
        var present = frame.Tiles.ToArray().Select(tile => tile.Bounds.Location).ToHashSet();
        byte[] low = NativeDetailProbe.Rgba(baseline), got = NativeDetailProbe.Rgba(actual);
        int maxError = 0;
        for (int y = 0; y < actual.Height; y++)
        for (int x = 0; x < actual.Width; x++)
        {
            // Deliberately independent CPU pixel sampling. Constants follow the
            // production mapping; unit tests separately verify that mapping.
            double cx = c[0] + (x + .5 - c[4]) * c[2], cy = c[1] + (y + .5 - c[5]) * c[3];
            bool applies = geometry.Destination.Contains(x,y) && cx >= frame.Viewport.Left && cy >= frame.Viewport.Top &&
                cx < frame.Viewport.Right && cy < frame.Viewport.Bottom && c[2] <= 2 && c[3] <= 2;
            double[] color = new double[3];
            if (applies)
            {
                foreach (var (py, wy) in Weights(cy, c[3]))
                foreach (var (px, wx) in Weights(cx, c[2]))
                {
                    int nx = Math.Clamp(px, 0, frame.NativeSize.Width - 1), ny = Math.Clamp(py, 0, frame.NativeSize.Height - 1);
                    if (!present.Contains(new Point(nx / 128 * 128, ny / 128 * 128))) applies = false;
                    for (int channel = 0; channel < 3; channel++) color[channel] += native[(ny * frame.NativeSize.Width + nx) * 4 + channel] * wx * wy;
                }
            }
            int offset = (y * actual.Width + x) * 4;
            for (int channel = 0; channel < 3; channel++)
            {
                int expected = applies ? (int)Math.Round(color[channel]) : low[offset + channel];
                int error = Math.Abs(got[offset + channel] - expected); maxError = Math.Max(maxError, error);
                if (error > (applies ? 1 : 0)) throw new IOException($"GPU detail pixel mismatch at {x},{y}, channel {channel}: {got[offset+channel]} != {expected}, native={applies}.");
            }
            Check(got[offset + 3] == 255, "Non-opaque GPU output.");
        }
        return maxError;
    }
    private static IEnumerable<(int Pixel, double Weight)> Weights(double center, double footprint)
    {
        if (footprint <= 1)
        {
            int start = (int)Math.Floor(center - .5); double fraction = center - .5 - start;
            if (1 - fraction > 0) yield return (start, 1 - fraction);
            if (fraction > 0) yield return (start + 1, fraction);
        }
        else
        {
            double lo = center - footprint * .5, hi = center + footprint * .5;
            for (int p = (int)Math.Floor(lo); p < Math.Ceiling(hi); p++)
            {
                double weight = Math.Max(0, Math.Min(p + 1, hi) - Math.Max(p, lo)) / footprint;
                if (weight > 0) yield return (p, weight);
            }
        }
    }

    private static object Time(ID3D11Device device, D3D11HwndVideoPresenter presenter, MediaFoundationD3D11DecodedFrame frame,
        Rectangle baseImage, NativeDetailPresentation? batch)
    {
        var context = device.ImmediateContext; var timings = new List<double>();
        for (int i = 0; i < 38; i++)
        {
            using var disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            using var begin = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            using var end = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            context.Begin(disjoint); presenter.RenderTimingQueriesForTests = (begin, end);
            var result = presenter.Present(frame, baseImage, nativeDetails: batch, nativeDetailBudget: CorrectnessBudget());
            context.End(disjoint); presenter.RenderTimingQueriesForTests = null; context.Flush();
            Check(result.IsSuccess, result.Detail);
            var clock = Stopwatch.StartNew(); QueryDataTimestampDisjoint info;
            while (!context.GetData(disjoint, out info)) { if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("GPU query timeout."); Thread.Sleep(1); }
            Check(context.GetData(begin, out ulong first), "Missing beginning GPU timestamp.");
            Check(context.GetData(end, out ulong last), "Missing ending GPU timestamp.");
            if (i >= 8 && !info.Disjoint && info.Frequency > 0) timings.Add((last - first) * 1000d / info.Frequency);
            if (batch is not null) Check(presenter.NativeDetailActive && (i == 0 || presenter.NativeDetailUploadedTiles == 0), "Cached detail reupload/fallback during timing.");
        }
        timings.Sort(); Check(timings.Count > 0, "No valid GPU samples.");
        return new { samples = timings.Count, medianGpuMs = timings[timings.Count / 2], p95GpuMs = timings[(int)(timings.Count * .95)] };
    }

    private static D3D11HwndVideoPresenter CreatePresenter(ID3D11Device device, Form window, Size source, Size output,
        D3D11HwndVideoScaleMode mode, bool nis)
    {
        if (!D3D11HwndVideoPresenter.TryCreate(device, new(window.Handle, source.Width, source.Height, 30, mode,
            EnableEdgeEnhancement: false, EnableExperimentalUpscaling: nis, UpscalingAlgorithm: ExperimentalUpscalingAlgorithm.Nis), out var presenter, out var capability))
            throw new IOException(capability.Detail);
        if (!presenter!.Resize(output.Width, output.Height).IsSuccess) { presenter.Dispose(); throw new IOException("Presenter resize failed."); }
        return presenter;
    }
    private static Bitmap Draw(ID3D11Device device, D3D11HwndVideoPresenter presenter, MediaFoundationD3D11DecodedFrame frame,
        Rectangle baseImage, Size output, NativeDetailPresentation? batch)
    {
        var result = presenter.Present(frame, baseImage, captureValidation: true, nativeDetails: batch, nativeDetailBudget: CorrectnessBudget());
        Check(result.IsSuccess, result.Detail);
        return UpscaleComparisonProbe.ReadPixels(device, presenter, output);
    }

    // Pixel correctness only: long artificial headroom avoids a busy test
    // workstation silently skipping an expected overlay. No production caller
    // may substitute this for a measured local deadline/queue/input policy.
    internal static NativeDetailRenderBudget CorrectnessBudget() => new(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2);
    internal static void Prepare(D3D11HwndVideoPresenter presenter) =>
        Check(presenter.PrepareNativeDetailsAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(), presenter.NativeDetailStatus);

    private static object Populate(D3D11HwndVideoPresenter presenter, Func<D3D11HwndVideoPresenterResult> present, int expectedUploads)
    {
        int uploads = 0;
        for (int i = 0; i <= NativeDetailPresentation.MaximumTiles; i++)
        {
            // Each call presents the base immediately, even with incomplete
            // detail. This loop is a test driver, not a wait inside a frame.
            var result = present(); Check(result.IsSuccess, result.Detail);
            Check(presenter.NativeDetailUploadedTiles is >= 0 and <= D3D11NativeDetailCompositor.MaximumUploadsPerFrame, "Unbounded atlas upload burst.");
            uploads += presenter.NativeDetailUploadedTiles;
            if (presenter.NativeDetailUploadedTiles == 0)
            {
                Check(uploads == expectedUploads, $"Expected {expectedUploads} paced uploads, got {uploads}.");
                return new { presentedFrames = i + 1, uploads };
            }
        }
        throw new IOException("Atlas did not converge within the bounded probe frame count.");
    }
    private static void Fill(DetailSource source, DetailCache cache, DetailManifest manifest, Rectangle viewport, long now)
    {
        foreach (int tile in Enumerable.Range(0, manifest.Context.TileCount))
        {
            var rect = manifest.Context.Tile(tile);
            if (!new Rectangle(rect.X, rect.Y, rect.Width, rect.Height).IntersectsWith(viewport)) continue;
            var transfer = source.Build(manifest, tile, now) ?? throw new IOException("Unstable synthetic tile.");
            for (int offset = 0; offset < transfer.Encoded.Length; offset += DetailWire.ChunkBytes)
            {
                var status = cache.Receive(transfer.Chunk(offset), now);
                Check(status is DetailReceiveResult.Partial or DetailReceiveResult.Applied or DetailReceiveResult.Duplicate, "Tile wire decode failed.");
            }
        }
    }
    internal sealed class Adapter
    {
        private readonly Dictionary<int, (DetailPatch Patch, NativeDetailTile Tile)> _tiles = new();
        internal NativeDetailPresentation Convert(DetailCache cache, DetailManifest manifest, Rectangle viewport, long pts)
        {
            var patches = cache.Patches; var requested = patches.Select(p => p.Tile).ToHashSet();
            foreach (int key in _tiles.Keys.ToArray()) if (!requested.Contains(key)) _tiles.Remove(key);
            foreach (var patch in patches)
                if (!_tiles.TryGetValue(patch.Tile, out var old) || !ReferenceEquals(old.Patch, patch))
                    _tiles[patch.Tile] = (patch, new(new(patch.Rect.X, patch.Rect.Y, patch.Rect.Width, patch.Rect.Height), patch.Version, patch.Rgba.Span));
            return new(manifest.Context.Epoch, manifest.Context.Request, manifest.Sequence, pts,
                new(manifest.Context.Width, manifest.Context.Height), viewport, _tiles.Values.Select(item => item.Tile).ToArray());
        }
    }
    private static void ExactViewport(Bitmap native, Bitmap baseline, Bitmap actual, Rectangle viewport)
    {
        var original = NativeDetailProbe.Rgba(native); var low = NativeDetailProbe.Rgba(baseline); var got = NativeDetailProbe.Rgba(actual);
        for (int y = 0; y < actual.Height; y++) for (int x = 0; x < actual.Width; x++)
        {
            int offset = (y * actual.Width + x) * 4;
            if (!got.AsSpan(offset, 4).SequenceEqual((viewport.Contains(x,y) ? original : low).AsSpan(offset, 4)))
                throw new IOException($"Native viewport mismatch at {x},{y}.");
        }
    }
    private static void ExactImage(Bitmap expected, Bitmap actual) => Check(NativeDetailProbe.Rgba(expected).AsSpan().SequenceEqual(NativeDetailProbe.Rgba(actual)), "Not an exact base rollback.");
    private static void SaveComparison(Bitmap native, Bitmap baseline, Bitmap detailed, string output)
    {
        using var result = new Bitmap(720, 696); using var graphics = Graphics.FromImage(result); graphics.Clear(Color.LightGray);
        using var font = new Font("Segoe UI", 16, GraphicsUnit.Pixel);
        var rows = new[] { ("Native source",native), ("Actual MF/D3D11/NIS base",baseline), ("Same decoded sample + GPU native atlas",detailed) };
        for (int i = 0; i < rows.Length; i++)
        {
            graphics.DrawString(rows[i].Item1, font, Brushes.Black, 12, i * 232 + 4);
            using var crop = rows[i].Item2.Clone(new(24, 24, 660, 184), PixelFormat.Format32bppArgb);
            graphics.DrawImageUnscaled(crop, 12, i * 232 + 34);
        }
        result.Save(Path.Combine(output, "native-detail-gpu-comparison.png"));
    }
    private static void Check(bool valid, string reason) { if (!valid) throw new IOException(reason); }
}
