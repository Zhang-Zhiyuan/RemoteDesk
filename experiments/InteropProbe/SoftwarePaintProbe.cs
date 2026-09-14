using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteDesk;

internal static class SoftwarePaintProbe
{
    internal static void Verify(string output)
    {
        using var source = new Bitmap(3840, 2160);
        using (Graphics graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.White);
            for (int y = 0; y < source.Height; y += 7) graphics.DrawLine(y % 2 == 0 ? Pens.Red : Pens.Blue, 0, y, source.Width - 1, y);
            using var font = new Font("Microsoft YaHei UI", 28);
            graphics.DrawString("RemoteDesk 4K 中文文字测试 0123456789", font, Brushes.Black, 20, 25);
        }
        var results = new List<object>();
        MethodInfo paint = typeof(RemoteViewerWindow.BufferedPictureBox).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (Size viewport in new[] { new Size(3530, 1987), new Size(1920, 1080), new Size(1280, 800) })
        {
            using var old = new RemoteViewerWindow.BufferedPictureBox { Size = viewport, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom, Image = source };
            using var optimized = new RemoteViewerWindow.BufferedPictureBox { Size = viewport, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom };
            var watch = Stopwatch.StartNew();
            var preview = Task.Run(() => PreparedSoftwareBitmap.Create(source, viewport, true)).GetAwaiter().GetResult();
            double prepareMs = watch.Elapsed.TotalMilliseconds;
            if (preview is null) throw new InvalidOperationException("No prepared preview.");
            optimized.SetFrameImage(source, preview);
            using var target = new NativePaintTarget(viewport);
            var args = new PaintEventArgs(target.Graphics, new Rectangle(Point.Empty, viewport));
            var originalTimes = new List<double>();
            var optimizedTimes = new List<double>();
            for (int index = 0; index < 12; index++)
            {
                watch.Restart(); paint.Invoke(old, [args]); double original = watch.Elapsed.TotalMilliseconds;
                watch.Restart(); paint.Invoke(optimized, [args]); double fast = watch.Elapsed.TotalMilliseconds;
                if (index >= 2) { originalTimes.Add(original); optimizedTimes.Add(fast); }
            }
            results.Add(new { viewport, source = source.Size, prepareMs, originalUiPaintMs = originalTimes.Average(),
                preparedUiPaintMs = optimizedTimes.Average(), originalMaximumMs = originalTimes.Max(), preparedMaximumMs = optimizedTimes.Max() });
        }
        Program.Save(Path.Combine(output, "software-paint.json"), new { scope = "Synthetic 4K source; actual PictureBox paint methods on isolated Windows desktop; no network or user input", results });
        Program.Save(Path.Combine(output, "software-pipeline.json"), new
        {
            scope = "Actual RemoteViewerWindow, synthetic 4K decoded frames at up to 15 fps, private desktop, input/clipboard/files disabled",
            original = RunPipeline(source, prepared: false),
            optimized = RunPipeline(source, prepared: true)
        });
    }


    private static object RunPipeline(Bitmap source, bool prepared)
    {
        using var client = new RemoteViewerClient();
        using var window = new RemoteViewerWindow(client, "synthetic-software-pipeline", inputEnabled: false,
            clipboardTextEnabled: false, filePasteEnabled: false, fileDropPasteEnabled: false, remoteFilePullEnabled: false, isAndroidRemote: false)
            { ShowInTaskbar = false };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (!prepared) typeof(RemoteViewerWindow).GetField("_softwarePreparationDisabled", flags)!.SetValue(window, 1);
        window.Show();
        window.ClientSize = new Size(3530, 2100);
        Application.DoEvents();
        var picture = (PictureBox)typeof(RemoteViewerWindow).GetField("_pictureBox", flags)!.GetValue(window)!;
        using var paintTarget = new NativePaintTarget(picture.ClientSize);
        nint pictureHandle = picture.Handle;
        var paintTimes = new List<double>();
        var frameLatencies = new List<double>();
        var submittedAt = new ConcurrentDictionary<Image, long>();
        MethodInfo enqueue = typeof(RemoteViewerWindow).GetMethod("QueueRemoteImage", flags)!;
        // Warm both paths, including WM_PAINT interop stubs, before comparing
        // steady playback. Cold initialization is measured separately above.
        for (int warmup = 0; warmup < 3; warmup++)
        {
            Image? oldImage = picture.Image;
            Task warmSubmission = Task.Run(() => SubmitFrame());
            var deadline = Stopwatch.StartNew();
            while ((!warmSubmission.IsCompleted || ReferenceEquals(oldImage, picture.Image)) && deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            warmSubmission.GetAwaiter().GetResult();
            if (ReferenceEquals(oldImage, picture.Image)) throw new InvalidOperationException("Frame warm-up timed out.");
            _ = SendMessage(pictureHandle, 0x0318, paintTarget.DeviceContext, 4);
            picture.Invalidate();
            _ = SendMessage(pictureHandle, 0x000F, 0, 0);
        }
        void SubmitFrame()
        {
            long now = Stopwatch.GetTimestamp();
            var bitmap = new Bitmap(source);
            var metadata = RemoteFrameMetadata.FromFrame(new RemoteFrame(source.Width, source.Height,
                RemoteFrameEncoding.Jpeg, RemoteFrameFlags.None, [1], 0, 1, 0, 0, now));
            submittedAt[bitmap] = Stopwatch.GetTimestamp();
            enqueue.Invoke(window, [metadata, bitmap, null, 0L, 0d, null, false, now, now, 0L]);
        }
        submittedAt.Clear();
        var heartbeatGaps = new List<double>();
        var clock = Stopwatch.StartNew();
        double precedingTick = 0;
        using var heartbeat = new System.Windows.Forms.Timer { Interval = 10 };
        heartbeat.Tick += (_, _) => { double now = clock.Elapsed.TotalMilliseconds; heartbeatGaps.Add(now - precedingTick); precedingTick = now; };
        heartbeat.Start();
        int submitted = 0, observedImages = 0;
        Image? precedingImage = picture.Image;
        int initialCollections = GC.CollectionCount(2);
        Task sender = Task.Run(async () =>
        {
            while (clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                SubmitFrame();
                Interlocked.Increment(ref submitted);
                await Task.Delay(67).ConfigureAwait(false);
            }
        });
        while (clock.Elapsed < TimeSpan.FromSeconds(5.5) || !sender.IsCompleted)
        {
            Application.DoEvents();
            if (picture.Image is not null && !ReferenceEquals(picture.Image, precedingImage))
            {
                observedImages++; precedingImage = picture.Image;
                // A private desktop can be occluded. Explicitly run the real
                // paint path so the baseline cannot silently skip scaling.
                var paintClock = Stopwatch.StartNew();
                _ = SendMessage(pictureHandle, 0x0318, paintTarget.DeviceContext, 4); // WM_PRINTCLIENT / PRF_CLIENT
                paintTimes.Add(paintClock.Elapsed.TotalMilliseconds);
                if (submittedAt.TryRemove(precedingImage, out long enqueuedAt))
                    frameLatencies.Add(Stopwatch.GetElapsedTime(enqueuedAt).TotalMilliseconds);
            }
            Thread.Sleep(1);
        }
        sender.GetAwaiter().GetResult();
        heartbeat.Stop();
        double[] sorted = heartbeatGaps.Order().ToArray();
        double[] sortedLatencies = frameLatencies.Order().ToArray();
        if (sorted.Length == 0 || observedImages == 0) throw new InvalidOperationException("Synthetic pipeline did not present frames or process UI messages.");
        return new { prepared, viewport = picture.ClientSize, submitted, observedImages,
            generation2Collections = GC.CollectionCount(2) - initialCollections,
            averageUiPaintMs = paintTimes.Average(), maximumUiPaintMs = paintTimes.Max(),
            decodedBitmapToPaintAverageMs = sortedLatencies.Average(),
            decodedBitmapToPaintP95Ms = sortedLatencies[(int)Math.Floor((sortedLatencies.Length - 1) * .95)],
            heartbeatCount = sorted.Length, heartbeatMedianMs = sorted[sorted.Length / 2],
            heartbeatP95Ms = sorted[(int)Math.Floor((sorted.Length - 1) * .95)], heartbeatMaxMs = sorted[^1] };
    }

    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    // WinForms paints to a native window/back-buffer DC. A Graphics backed by
    // a managed Bitmap instead makes GetHdc/ReleaseHdc copy a temporary image,
    // which is not the application's presentation path.
    private sealed class NativePaintTarget : IDisposable
    {
        private readonly nint _bitmap, _dc, _previous;
        internal Graphics Graphics { get; }
        internal nint DeviceContext => _dc;
        internal NativePaintTarget(Size size)
        {
            _dc = CreateCompatibleDC(0);
            var info = new BitmapInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
            _bitmap = CreateDIBSection(_dc, ref info, 0, out _, 0, 0);
            if (_dc == 0 || _bitmap == 0) throw new InvalidOperationException("Could not allocate an isolated paint target.");
            _previous = SelectObject(_dc, _bitmap);
            Graphics = Graphics.FromHdc(_dc);
        }
        public void Dispose()
        {
            Graphics.Dispose();
            _ = SelectObject(_dc, _previous);
            _ = DeleteObject(_bitmap);
            _ = DeleteDC(_dc);
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ColorsUsed, ColorsImportant, Color;
        }
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
        [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
        [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    }
}
