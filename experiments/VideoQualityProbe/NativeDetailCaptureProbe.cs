using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using RemoteDesk;

// Real Desktop Duplication -> native BGRA GPU snapshot -> GPU scaling/NV12 ->
// hardware MFT -> real decoder/presenter. Captures only the fixed client area of
// this probe's visible, topmost synthetic window, never sends data externally.
internal static class NativeDetailCaptureProbe
{
    public static void Run(string fixtures, string outputDirectory, bool forceCoarseDamage = false, bool fullRepaint = false)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output)) throw new IOException("Use a new output directory.");
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunCore(fixtures, output, forceCoarseDamage, fullRepaint); }
            catch (Exception ex)
            {
                failure = ex;
                if (!File.Exists(Path.Combine(output, "summary.json")))
                    File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(
                        new { passed = false, startupFailure = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(50)))
            throw new TimeoutException("Owned GPU capture probe exceeded its 50-second outer deadline.");
        if (failure is not null) throw new IOException("Capture probe failed; see summary.json.", failure);
    }

    private static void RunCore(string fixtures, string output, bool forceCoarseDamage, bool fullRepaint)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Screen monitor = Screen.PrimaryScreen ?? throw new IOException("No primary screen.");
        if (monitor.Bounds.Width < 3500 || monitor.Bounds.Height < 1500)
            throw new IOException("This isolated 2560x1440 fixture requires a 4K-class primary monitor.");
        using var original = new Bitmap(Path.Combine(fixtures, "native-reference.png"));
        using var edited = new Bitmap(Path.Combine(fixtures, "native-edited.png"));
        var nativeSize = new Size(2560, 1440);
        var encodedSize = new Size(1280, 720);
        using var canvas = new SourceWindow(original, edited)
        {
            StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None,
            AutoScaleMode = AutoScaleMode.None, ClientSize = nativeSize, ShowInTaskbar = false,
            TopMost = true, Location = new(monitor.Bounds.Left + 8, monitor.Bounds.Top + 8)
        };
        canvas.ValidatePreparedFixtures(original, edited);
        using var preview = new PreviewWindow
        {
            StartPosition = FormStartPosition.Manual, AutoScaleMode = AutoScaleMode.None,
            ClientSize = new(960, 540), Location = new(monitor.Bounds.Left + 2600, monitor.Bounds.Top + 80),
            Text = "RemoteDesk 同源 GPU 采集测试（自动关闭）", ShowInTaskbar = false
        };
        canvas.Show(); preview.Show(); Application.DoEvents();
        Rectangle captureBounds = canvas.RectangleToScreen(canvas.ClientRectangle);
        if (captureBounds.Size != nativeSize) throw new IOException("Unexpected source DPI scaling.");
        if (!WindowsGraphicsCaptureTargetResolver.TryResolveCandidates(monitor.DeviceName, monitor.Bounds,
            out _, out WindowsDesktopDuplicationTarget? target, out string mappingFailure) || target is null)
            throw new IOException(mappingFailure);
        var crop = captureBounds; crop.Offset(-monitor.Bounds.Left, -monitor.Bounds.Top);
        if (!D3D11DesktopSource.TryCreate(target, crop, out var capture, out string? captureFailure))
            throw new IOException(captureFailure);
        using var source = capture!;
        using var device = source.AcquireDeviceLease();
        if (!MediaFoundationD3D11H264Encoder.TryCreate(device,
            new(nativeSize, encodedSize, 60, 12_000_000), out var encoderResult, out string? encoderFailure))
            throw new IOException(encoderFailure);
        using var encoder = encoderResult!;
        using var refiner = new D3D11NativeDamageRefiner(device);
        using var readback = new D3D11NativeRegionReadback(device);
        if (!MediaFoundationD3D11H264Decoder.TryCreate(new(1280, 720, 60, AllSamplesIndependent: true),
            out var decoderResult, out var decoderCapability)) throw new IOException(decoderCapability.Detail);
        using var decoder = decoderResult!;
        using var decoderDevice = decoder.AcquireDeviceLease();
        if (!D3D11HwndVideoPresenter.TryCreate(decoderDevice, new(preview.Handle, 1280, 720, 60,
            EnableExperimentalUpscaling: true, UpscalingAlgorithm: ExperimentalUpscalingAlgorithm.Nis),
            out var presenterResult, out var presenterCapability)) throw new IOException(presenterCapability.Detail);
        using var presenter = presenterResult!;
        // Render at the native size; the small preview is only a convenient
        // visible window, not a claim of 1:1 physical pixels in that preview.
        if (!presenter.Resize(nativeSize.Width, nativeSize.Height).IsSuccess)
            throw new IOException("Cannot create a native-size presentation buffer.");
        using var timer = new WindowsHighResolutionPacingWaiter();
        var watch = Stopwatch.StartNew();
        var samples = new List<object>();
        var encodedManifests = new Dictionary<long, NativeSurfaceManifest>();
        var nativeTiles = new Dictionary<int, (long Version, NativeDetailTile Tile)>();
        var changedAt = new Dictionary<int, long>();
        var tileVersions = new Dictionary<int, long>();
        var correctnessBlocks = new List<NativeRegionPixels>();
        var sourceDiagnostics = new Dictionary<long, object>();
        int[] requestedTiles = [0, 1, 2, 20, 21, 22];
        Rectangle viewport = new(0, 0, 384, 256);
        NativeSurfaceManifest? pending = null;
        NativeDesktopSurface? pendingSurface = null;
        object? pendingFixtureState = null;
        NativeSurfaceManifest? displayed = null;
        long pendingCapturedAt = 0;
        double refinementCpuMs = 0;
        long nextPaint = 0;
        long nextCapture = 0;
        double maximumFixturePaintMs = 0;
        int painted = 0, frames = 0, exactTiles = 0, activeFrames = 0, busyFrames = 0;
        int editedBlocks = 0;
        int rejectedThirdCopies = 0;
        int contentChanges = 0, unstableSkips = 0, copyBudgetSkips = 0;
        string? failure = null;
        Task<bool>? preparation = null;
        try
        {
            while (frames < 240 && watch.Elapsed < TimeSpan.FromSeconds(25))
            {
                if (canvas.IsDisposed || preview.IsDisposed || canvas.RectangleToScreen(canvas.ClientRectangle) != captureBounds)
                    throw new IOException("Owned fixture moved or closed; stop capturing immediately.");
                long now = Stopwatch.GetTimestamp();
                if (now >= nextPaint)
                {
                    long paintStarted = Stopwatch.GetTimestamp();
                    canvas.Advance(++painted, frames >= 150, fullRepaint);
                    nextPaint = now + Stopwatch.Frequency / 60;
                    Application.DoEvents();
                    maximumFixturePaintMs = Math.Max(maximumFixturePaintMs, Stopwatch.GetElapsedTime(paintStarted).TotalMilliseconds);
                }
                // Poll while the encoder is independently working. Some
                // drivers initiate staging access on the first nonblocking Map;
                // doing that only at output time would discard every result.
                if (pending is not null && frames is >= 60 and < 210)
                {
                    long pollStarted = Stopwatch.GetTimestamp();
                    refiner.TryPoll(new(pendingCapturedAt + Stopwatch.Frequency / 60,
                        InputPending: frames is >= 180 and < 210));
                    refinementCpuMs += Stopwatch.GetElapsedTime(pollStarted).TotalMilliseconds;
                }
                var encoded = encoder.TryRead();
                if (encoder.IsFailed) throw new IOException(encoder.Failure);
                if (encoded is not null)
                {
                    if (pending is null || encoded.SourceTime100Nanoseconds != pending.Sequence * 166_667L)
                        throw new IOException("Base frame lost its exact native source identity.");
                    using var currentSurface = pendingSurface ?? throw new IOException("Lost the pending GPU surface.");
                    pendingSurface = null;
                    bool enabled = frames is >= 60 and < 210;
                    bool busy = frames is >= 180 and < 210;
                    var budget = new NativeDetailRenderBudget(pendingCapturedAt + Stopwatch.Frequency / 60,
                        InputPending: busy);
                    long completionStarted = Stopwatch.GetTimestamp();
                    displayed = refiner.Complete(pending.Sequence, enabled ? budget : default); pending = null;
                    refinementCpuMs += Stopwatch.GetElapsedTime(completionStarted).TotalMilliseconds;
                    if (refiner.IsFailed) throw new IOException(refiner.Failure);
                    long pts = encoded.SourceTime100Nanoseconds;
                    encodedManifests[pts] = displayed;
                    foreach (long old in encodedManifests.Keys.Where(key => key < pts - 16 * 166_667L).ToArray())
                        encodedManifests.Remove(old);
                    if (sourceDiagnostics.Count >= 1024) throw new IOException("Capture diagnostics exceeded their probe bound.");
                    sourceDiagnostics[pts] = new { currentSurface.DesktopPresentedAtTimestamp, currentSurface.CapturedAtTimestamp,
                        currentSurface.Diagnostics, lastCompletedFixturePaintAtAcquire = pendingFixtureState,
                        rawTileVersions = requestedTiles.Select(tile => currentSurface.Manifest[tile]).ToArray(),
                        trackedTileVersions = requestedTiles.Select(tile => displayed[tile]).ToArray(),
                        captureBounds, crop, monitorBounds = monitor.Bounds };
                    foreach (int tile in nativeTiles.Keys.ToArray())
                        if (nativeTiles[tile].Version != displayed[tile]) nativeTiles.Remove(tile);
                    long baseStart = Stopwatch.GetTimestamp();
                    var decoded = decoder.DecodeAccessUnit(encoded.AnnexBBytes, encoded.SourceTime100Nanoseconds);
                    using var frame = decoded.Frame ?? throw new IOException(decoded.Detail);
                    if (!frame.HasExplicitSampleTime || frame.SampleTime100Nanoseconds != encoded.SourceTime100Nanoseconds)
                        throw new IOException("Decoded frame does not match the native source.");
                    var batch = enabled ? new NativeDetailPresentation(1, 1, displayed.Sequence,
                        encoded.SourceTime100Nanoseconds, nativeSize, viewport,
                        nativeTiles.Values.Select(value => value.Tile).ToArray()) : null;
                    var presented = presenter.Present(frame, new(0, 0, 1280, 720), nativeDetails: batch, nativeDetailBudget: budget);
                    if (!presented.IsSuccess) throw new IOException(presented.Detail);
                    if (busy)
                    {
                        busyFrames++;
                        if (presenter.NativeDetailActive || presenter.NativeDetailUploadedTiles != 0)
                            throw new IOException("Busy frame performed native presentation work.");
                    }
                    if (presenter.NativeDetailActive) activeFrames++;
                    samples.Add(new { frame = frames, sourceSequence = displayed.Sequence,
                        captureToEncodeMs = Stopwatch.GetElapsedTime(pendingCapturedAt, encoded.CompletedAtTimestamp).TotalMilliseconds,
                        encodeMs = Stopwatch.GetElapsedTime(encoded.SubmittedAtTimestamp, encoded.CompletedAtTimestamp).TotalMilliseconds,
                        decodeAndPresentMs = Stopwatch.GetElapsedTime(baseStart).TotalMilliseconds,
                        captureToSubmittedMs = Stopwatch.GetElapsedTime(pendingCapturedAt).TotalMilliseconds,
                        nativeActive = presenter.NativeDetailActive, uploaded = presenter.NativeDetailUploadedTiles,
                        patches = nativeTiles.Count, busy, refinementCpuMs });
                    // Base presentation has already been submitted. Optional
                    // copies use this SAME immutable source and its finalized,
                    // precisely verified manifest, never a second screenshot.
                    int enqueued = 0;
                    foreach (int tile in requestedTiles)
                    {
                        if (!tileVersions.TryGetValue(tile, out long version) || version != displayed[tile])
                        { tileVersions[tile] = displayed[tile]; changedAt[tile] = now; contentChanges++; }
                        if (Stopwatch.GetElapsedTime(changedAt[tile]).TotalMilliseconds < 250)
                        { unstableSkips++; continue; }
                        if (!enabled || (nativeTiles.TryGetValue(tile, out var patch) && patch.Version == displayed[tile])) continue;
                        Rectangle bounds = new(tile % 20 * 128, tile / 20 * 128, 128, 128);
                        if (readback.HasPendingRegion(bounds)) continue;
                        if (readback.TryEnqueue(currentSurface.Texture, pts, bounds, budget)) enqueued++;
                        else if (!budget.Allows(Stopwatch.GetTimestamp())) copyBudgetSkips++;
                        if (enqueued == 2)
                        {
                            if (readback.TryEnqueue(currentSurface.Texture, pts, bounds, budget))
                                throw new IOException("Readback exceeded two copies per source frame.");
                            rejectedThirdCopies++; break;
                        }
                    }
                    frames++;
                    if (frames == 60) preparation = presenter.PrepareNativeDetailsAsync();
                    if (frames == 210) { presenter.DisableNativeDetails(); readback.Clear(); nativeTiles.Clear(); }
                    if (frames % 60 == 0) Console.WriteLine($"Capture: {frames} real base frames, {correctnessBlocks.Count} native readbacks pending final validation, {activeFrames} enhanced frames.");
                }
                if (pending is null && now >= nextCapture)
                {
                    var surface = source.TryAcquire();
                    if (source.IsFailed) throw new IOException(source.Failure);
                    if (surface is not null)
                    {
                        try
                        {
                            long pts = surface.Manifest.Sequence * 166_667L;
                            if (encoder.TrySubmit(surface.Texture, pts))
                            {
                                nextCapture = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 60;
                                pending = forceCoarseDamage ? new NativeSurfaceManifest(surface.Manifest.Sequence, nativeSize,
                                    Enumerable.Repeat(surface.Manifest.Sequence, surface.Manifest.Count).ToArray()) : surface.Manifest;
                                pendingCapturedAt = surface.CapturedAtTimestamp;
                                pendingFixtureState = new { canvas.PaintGeneration, canvas.LastPaintFinishedAt, canvas.Edited,
                                    canvas.DeviceDpi, sourceImageDpi = original.HorizontalResolution };
                                var budget = new NativeDetailRenderBudget(pendingCapturedAt + Stopwatch.Frequency / 60,
                                    InputPending: frames is >= 180 and < 210);
                                long refineStarted = Stopwatch.GetTimestamp();
                                refiner.Begin(surface.Texture, pending, requestedTiles, frames is >= 60 and < 210 ? budget : default);
                                refinementCpuMs = Stopwatch.GetElapsedTime(refineStarted).TotalMilliseconds;
                                pendingSurface = surface; surface = null;
                            }
                        }
                        finally { surface?.Dispose(); }
                    }
                }
                var readBudget = new NativeDetailRenderBudget(
                    pending is null ? nextCapture : pendingCapturedAt + Stopwatch.Frequency / 60,
                    InputPending: frames is >= 180 and < 210);
                NativeRegionPixels? pixels = readback.TryRead(readBudget);
                if (readback.IsFailed) throw new IOException(readback.Failure);
                if (pixels is not null && encodedManifests.TryGetValue(pixels.SourceTime100Nanoseconds, out var manifest))
                {
                    int tile = pixels.Bounds.Y / 128 * 20 + pixels.Bounds.X / 128;
                    // Keep the expensive per-pixel oracle OUTSIDE the frame
                    // loop. This bounded list is probe-only validation data.
                    if (correctnessBlocks.Count >= 64) throw new IOException("Unexpected unbounded detail churn.");
                    correctnessBlocks.Add(pixels);
                    if (displayed is not null && displayed[tile] == manifest[tile])
                        nativeTiles[tile] = (manifest[tile], new(pixels.Bounds, manifest[tile], pixels.Rgba));
                }
                timer.Wait(TimeSpan.FromMilliseconds(.2), CancellationToken.None);
            }
            foreach (var pixels in correctnessBlocks)
            {
                bool matchesOriginal = NativePixelsMatch(original, pixels);
                bool matchesEdited = NativePixelsMatch(edited, pixels);
                if (!matchesOriginal && !matchesEdited)
                {
                    using var mismatch = NativeDetailProbe.BitmapFromRgba(pixels.Rgba, pixels.Bounds.Size);
                    mismatch.Save(Path.Combine(output, "native-mismatch.png"));
                    using var expected = original.Clone(pixels.Bounds, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    expected.Save(Path.Combine(output, "native-expected.png"));
                    using var expectedEdited = edited.Clone(pixels.Bounds, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    expectedEdited.Save(Path.Combine(output, "native-expected-edited.png"));
                    sourceDiagnostics.TryGetValue(pixels.SourceTime100Nanoseconds, out object? diagnostic);
                    File.WriteAllText(Path.Combine(output, "native-mismatch.json"), JsonSerializer.Serialize(new
                    {
                        pixels.SourceTime100Nanoseconds, pixels.Bounds, diagnostic,
                        mismatches = FindMismatches(original, edited, pixels)
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    throw new IOException($"Native block differs from both fixtures: source={pixels.SourceTime100Nanoseconds}, bounds={pixels.Bounds}.");
                }
                if (matchesEdited && !matchesOriginal) editedBlocks++;
                exactTiles++;
            }
            if (frames != 240 || activeFrames < 10 || exactTiles < 6 || busyFrames != 30 || rejectedThirdCopies == 0 || editedBlocks == 0)
                throw new IOException($"Incomplete same-surface run: frames={frames}, active={activeFrames}, exact={exactTiles}, busy={busyFrames}.");
            if (preparation is not { IsCompletedSuccessfully: true } || !preparation.Result)
                throw new IOException("Native preparation did not complete.");
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, failure, frames, exactTiles, activeFrames, busyFrames, editedBlocks,
                rejectedThirdCopies, contentChanges, unstableSkips, copyBudgetSkips, maximumFixturePaintMs,
                forceCoarseDamage, fullRepaint, refiner.SubmittedComparisons, refiner.CompletedComparisons, refiner.UnreadyComparisons,
                comparisonStagingBytes = refiner.StagingBytes,
                encoder = encoder.Name, capture = "DXGI Desktop Duplication, owned fixed crop",
                nativeSize, encodedSize, readbackStagingBytes = readback.StagingBytes, samples, sourceDiagnostics,
                scope = "Real same-surface GPU capture/encode/readback/decode/present. No network or production session. Timings end at CPU submission, not DWM/photon. Driver initialization and per-pixel correctness oracle are not an end-to-end latency measurement."
            }, new JsonSerializerOptions { WriteIndented = true }));
            pendingSurface?.Dispose(); preview.Close(); canvas.Close();
        }
    }

    private static bool NativePixelsMatch(Bitmap original, NativeRegionPixels pixels)
    {
        for (int y = 0; y < pixels.Bounds.Height; y++)
            for (int x = 0; x < pixels.Bounds.Width; x++)
            {
                Color expected = original.GetPixel(pixels.Bounds.X + x, pixels.Bounds.Y + y);
                int index = (y * pixels.Bounds.Width + x) * 4;
                if (pixels.Rgba[index] != expected.R || pixels.Rgba[index + 1] != expected.G ||
                    pixels.Rgba[index + 2] != expected.B || pixels.Rgba[index + 3] != 255)
                    return false;
            }
        return true;
    }

    private static List<object> FindMismatches(Bitmap original, Bitmap edited, NativeRegionPixels pixels)
    {
        var result = new List<object>();
        for (int y = 0; y < pixels.Bounds.Height && result.Count < 64; y++)
        for (int x = 0; x < pixels.Bounds.Width && result.Count < 64; x++)
        {
            Color a = original.GetPixel(pixels.Bounds.X + x, pixels.Bounds.Y + y);
            Color b = edited.GetPixel(pixels.Bounds.X + x, pixels.Bounds.Y + y);
            int index = (y * pixels.Bounds.Width + x) * 4;
            byte[] rgb = pixels.Rgba.AsSpan(index, 4).ToArray();
            if ((rgb[0] != a.R || rgb[1] != a.G || rgb[2] != a.B) &&
                (rgb[0] != b.R || rgb[1] != b.G || rgb[2] != b.B))
                result.Add(new { x, y, actual = rgb, original = new[] { a.R, a.G, a.B }, edited = new[] { b.R, b.G, b.B } });
        }
        return result;
    }

    private sealed class PreviewWindow : Form { protected override bool ShowWithoutActivation => true; }
    private sealed class SourceWindow : Form
    {
        private readonly Bitmap _original, _editedImage;
        private int _serial;
        private bool _edited;
        public SourceWindow(Bitmap original, Bitmap edited)
        {
            // Convert the synthetic fixture once, outside measured capture.
            // Re-converting/resampling an ARGB PNG in every full-window GDI+
            // paint can occupy this test's encoder-polling thread for >100 ms.
            // Opaque premultiplied images + explicit pixel-coordinate blits avoid
            // presenting a half-painted expected fixture to Desktop Duplication.
            _original = OpaqueSurface(original);
            try { _editedImage = OpaqueSurface(edited); }
            catch { _original.Dispose(); throw; }
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        }
        private static Bitmap OpaqueSurface(Bitmap source)
        {
            var bitmap = new Bitmap(2560, 1440, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                Rectangle pixels = new(0, 0, bitmap.Width, bitmap.Height);
                graphics.DrawImage(source, pixels, pixels, GraphicsUnit.Pixel);
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        public void ValidatePreparedFixtures(Bitmap original, Bitmap edited)
        {
            Validate(original, _original); Validate(edited, _editedImage);
            static void Validate(Bitmap expected, Bitmap prepared)
            {
                byte[] expectedPixels = NativeDetailProbe.Rgba(expected);
                byte[] preparedPixels = NativeDetailProbe.Rgba(prepared);
                for (int y = 0; y < prepared.Height; y++)
                    if (!expectedPixels.AsSpan(y * expected.Width * 4, prepared.Width * 4)
                        .SequenceEqual(preparedPixels.AsSpan(y * prepared.Width * 4, prepared.Width * 4)))
                        throw new IOException($"Synthetic source preparation changed pixels at row {y}; check fixture DPI before blaming GPU capture.");
            }
        }
        public bool Edited => _edited;
        public long PaintGeneration { get; private set; }
        public long LastPaintFinishedAt { get; private set; }
        protected override bool ShowWithoutActivation => true;
        public void Advance(int serial, bool editedNow, bool fullRepaint)
        {
            bool changed = editedNow != _edited; _serial = serial; _edited = editedNow;
            // These are the only changed pixels in the edited fixture. Do not
            // repaint 3.7 million GDI+ pixels on the thread polling the encoder:
            // that would measure the test's repaint, not hardware latency.
            if (changed && !fullRepaint) { Invalidate(new Rectangle(24, 104, 600, 28)); Update(); }
            Invalidate(fullRepaint ? ClientRectangle : new Rectangle(2200, 1250, 320, 128));
            Update();
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
            Rectangle clip = Rectangle.Intersect(ClientRectangle, e.ClipRectangle);
            // Explicit pixel source AND destination rectangles. DrawImage's
            // physical-size overloads depend on bitmap/window DPI and can turn
            // a nominal "unscaled" fixture into an unexpected 150% rendering.
            e.Graphics.DrawImage(_edited ? _editedImage : _original, clip, clip, GraphicsUnit.Pixel);
            e.Graphics.CompositingMode = CompositingMode.SourceOver;
            e.Graphics.FillRectangle((_serial & 1) == 0 ? Brushes.DarkBlue : Brushes.DarkRed, 2200, 1250, 320, 128);
            e.Graphics.DrawString($"RemoteDesk GPU test {_serial}", SystemFonts.DefaultFont, Brushes.White, 2208, 1260);
            PaintGeneration++;
            LastPaintFinishedAt = Stopwatch.GetTimestamp();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _original.Dispose(); _editedImage.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
