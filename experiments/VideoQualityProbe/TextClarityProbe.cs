using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// Synthetic fixtures and an unshown owned HWND only. Does not connect to a
// host, capture the desktop, inject input, or change release/settings/permissions.
internal static class TextClarityProbe
{
    private const int Width = 3840, Height = 2160, Fps = 30, Count = 180;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Rectangle TextRegion = new(24, 24, 660, 184);

    internal static Bitmap RenderDetailBase(string path)
    {
        using var units = new Units(path);
        return RenderLast(units, new Size(1920, 1080), experimental: true);
    }

    internal static async Task EncodeDetailBaseAsync(string ffmpeg, string input, string output)
    {
        var production = FfmpegDesktopH264Capture.BuildArguments(new(
            FfmpegDesktopCaptureBackend.GdiGrabBounds, new Rectangle(0, 0, Width, Height),
            new Size(1920, 1080), Fps, GopLength: 1), FfmpegH264Encoder.NvidiaNvenc).ToList();
        int start = production.IndexOf("-c:v");
        var arguments = production.GetRange(start, production.IndexOf("-an") - start);
        Set(arguments, "-b:v", "10M"); Set(arguments, "-maxrate", "10M"); Set(arguments, "-bufsize", "1333333");
        await RunFfmpegAsync(ffmpeg, ["-hide_banner", "-nostdin", "-n", "-loglevel", "warning", "-i", input,
            "-vf", "scale=1920:1080:flags=lanczos:in_range=full:out_range=tv:out_color_matrix=bt709,format=nv12",
            "-frames:v", "1", ..arguments, "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
            "-color_range", "tv", "-an", output], output + ".log").ConfigureAwait(false);
    }

    internal static void RefreshComparison(string directory)
    {
        using var reference = new Bitmap(Path.Combine(directory, "native-reference.png"));
        var pictures = new Dictionary<string, Bitmap>();
        try
        {
            foreach (string name in new[] { "1080p-gop1", "1080p-gop1-nis", "native-gop1", "native-gop30" })
                pictures.Add(name, new Bitmap(Path.Combine(directory, "10mbps-static-" + name + ".png")));
            SaveComparison(reference, pictures, directory);
        }
        finally { foreach (var picture in pictures.Values) picture.Dispose(); }
    }

    internal static void RunRecovery(string directory, string output)
    {
        if (File.Exists(output)) throw new IOException("Preserving existing recovery evidence.");
        var rows = new List<object>();
        var fixtures = new[] { "10mbps-static-native-gop1", "10mbps-static-native-gop2", "10mbps-static-native-gop30" }
            .Where(name => File.Exists(Path.Combine(directory, name + ".h264"))).ToArray();
        if (fixtures.Length == 0) throw new IOException("No native 4K fixtures found.");
        foreach (string name in fixtures)
        using (var units = new Units(Path.Combine(directory, name + ".h264")))
        for (int round = 0; round < 3; round++)
        {
            bool independent = units.Items.All(unit => unit.IsIdr);
            if (!MediaFoundationD3D11H264Decoder.TryCreate(new(Width, Height, Fps,
                AllSamplesIndependent: independent), out var created, out var capability))
                throw new IOException(capability.Detail);
            using var decoder = created!;
            int decoded = 0;
            for (int stage = 0; stage < 3; stage++)
            {
                if (stage == 1 && !decoder.NotifyAccessUnitGap(out var gap)) throw new IOException(gap);
                if (stage == 2 && !decoder.ResetForDiscontinuity(out var reset)) throw new IOException(reset);
                if (!independent)
                {
                    var orphan = decoder.DecodeAccessUnit(units.Items[stage * 30 + 1].Bytes, (stage * 30 + 1) * 10_000_000L / Fps);
                    orphan.Frame?.Dispose();
                    if (orphan.Status != MediaFoundationD3D11DecodeStatus.Failed) throw new IOException("Orphan P frame admitted.");
                }
                for (int index = stage * 30; index < stage * 30 + 16; index++)
                {
                    var result = decoder.DecodeAccessUnit(units.Items[index].Bytes, index * 10_000_000L / Fps);
                    using var frame = result.Frame;
                    if (result.Status == MediaFoundationD3D11DecodeStatus.Failed || frame is null)
                        throw new IOException("No immediate recovered frame: " + result.Detail);
                    decoded++;
                }
            }
            rows.Add(new { name, round = round + 1, decoded, expected = 48, passed = decoded == 48,
                scope = "Actual 4K MF decoder cold-start/gap/reset. Explicit gap notification, not network packet loss or production frame queue qualification." });
            Console.WriteLine($"{name} recovery {round + 1}: {decoded}/48");
            Save(output, rows);
        }
    }

    internal static void RunTemporal(string directory, string output)
    {
        if (Directory.Exists(output)) throw new IOException("Preserving existing temporal evidence.");
        Directory.CreateDirectory(output);
        using var reference = new Bitmap(Path.Combine(directory, "native-reference.png"));
        var rows = new List<object>();
        int[] selected = [0, 1, 2, 3, 4, 5, 15, 29, 30, 31, 32, 33, 59, 60, 61, 90, 91, 120, 121, 179];
        var fixtures = new[] { "1080p-gop1", "native-gop1", "native-gop2", "native-gop30" }
            .Where(name => File.Exists(Path.Combine(directory, "10mbps-static-" + name + ".h264"))).ToArray();
        if (fixtures.Length == 0) throw new IOException("No 10 Mbps static fixtures found.");
        foreach (string name in fixtures)
        {
            using var units = new Units(Path.Combine(directory, "10mbps-static-" + name + ".h264"));
            var size = name.StartsWith("1080p", StringComparison.Ordinal) ? new Size(1920, 1080) : new Size(Width, Height);
            RenderSelected(units, size, experimental: false, selected.ToHashSet(), (index, pixels) =>
            {
                rows.Add(new { name, frame = index, idr = units.Items[index].IsIdr,
                    bytes = units.Items[index].Bytes.Length, textMetric = TextMetric(reference, pixels) });
                using var crop = pixels.Clone(TextRegion, PixelFormat.Format32bppArgb);
                crop.Save(Path.Combine(output, $"{name}-{index:D3}.png"));
            });
            Console.WriteLine($"{name}: sampled {selected.Length} frames including IDR boundaries.");
            Save(Path.Combine(output, "temporal.json"), rows);
        }
    }

    internal static async Task RunAsync(string ffmpeg, string output, bool shortGop = false, string? fixtureDirectory = null)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new output directory; existing evidence is preserved.");
        Directory.CreateDirectory(output);
        using var reference = fixtureDirectory is null ? DrawFixture() :
            new Bitmap(Path.Combine(fixtureDirectory, "native-reference.png"));
        if (reference.Width != Width || reference.Height != Height) throw new IOException("Expected a native 3840x2160 fixture.");
        string input = Path.Combine(output, "native-reference.png");
        reference.Save(input, ImageFormat.Png);
        var rows = new List<object>();
        var paced = new List<object>();
        var pictures = new Dictionary<string, Bitmap>();
        try
        {
            var candidates = shortGop
                ? new[] { (Name: "native-gop2", W: Width, H: Height, Gop: 2) }
                : new[] { (Name: "1080p-gop1", W: 1920, H: 1080, Gop: 1),
                          (Name: "native-gop1", W: Width, H: Height, Gop: 1),
                          (Name: "native-gop30", W: Width, H: Height, Gop: 30) };
            foreach (int mbps in new[] { 10, 20 })
            foreach (bool scroll in new[] { false, true })
            foreach (var candidate in candidates)
            {
                string label = $"{mbps}mbps-{(scroll ? "scroll" : "static")}-{candidate.Name}";
                string encoded = Path.Combine(output, label + ".h264");
                var actual = FfmpegDesktopH264Capture.BuildArguments(new(
                    FfmpegDesktopCaptureBackend.GdiGrabBounds, new Rectangle(0, 0, Width, Height),
                    new Size(candidate.W, candidate.H), Fps, GopLength: 1), FfmpegH264Encoder.NvidiaNvenc).ToList();
                int start = actual.IndexOf("-c:v");
                var arguments = actual.GetRange(start, actual.IndexOf("-an") - start);
                Set(arguments, "-g", candidate.Gop.ToString(CultureInfo.InvariantCulture));
                Set(arguments, "-b:v", $"{mbps}M"); Set(arguments, "-maxrate", $"{mbps}M");
                Set(arguments, "-bufsize", (mbps * 1_000_000 * 4 / Fps).ToString(CultureInfo.InvariantCulture));
                // Identical BT.709 conversion, requested maxrate and 4-frame VBV.
                // NVENC may overshoot; compare observed bitrate, not only the request.
                // This isolates codec/spatial loss; it is not desktop-capture qualification.
                string filter = (scroll ? "scroll=vertical=0.001," : "") +
                    $"scale={candidate.W}:{candidate.H}:flags=lanczos:in_range=full:out_range=tv:out_color_matrix=bt709,format=nv12";
                var elapsed = Stopwatch.StartNew();
                await RunFfmpegAsync(ffmpeg, ["-hide_banner", "-nostdin", "-n", "-loglevel", "warning",
                    "-loop", "1", "-framerate", "30", "-i", input, "-vf", filter,
                    "-frames:v", Count.ToString(CultureInfo.InvariantCulture), ..arguments,
                    "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
                    "-an", "-fps_mode", "passthrough", "-flush_packets", "1", encoded],
                    Path.Combine(output, label + ".log")).ConfigureAwait(false);
                double processingSeconds = elapsed.Elapsed.TotalSeconds;
                using var units = new Units(encoded);
                if (units.Items.Count != Count) throw new IOException($"{label}: missing access units.");
                double actualMbps = new FileInfo(encoded).Length * 8d * Fps / Count / 1_000_000;
                int largestAu = units.Items.Max(unit => unit.Bytes.Length);
                // A serialization floor on a perfect link, not measured WAN latency.
                double largestSerializationMs = largestAu * 8d / (mbps * 1000);
                object? textMetric = null;
                if (!scroll)
                {
                    using var pixels = RenderLast(units, new(candidate.W, candidate.H), experimental: false);
                    pixels.Save(Path.Combine(output, label + ".png"));
                    textMetric = TextMetric(reference, pixels);
                    if (mbps == 10) pictures.Add(candidate.Name, (Bitmap)pixels.Clone());
                    if (candidate.W == 1920)
                    {
                        using var nis = RenderLast(units, new(candidate.W, candidate.H), experimental: true);
                        nis.Save(Path.Combine(output, label + "-nis.png"));
                        if (mbps == 10) pictures.Add(candidate.Name + "-nis", (Bitmap)nis.Clone());
                        rows.Add(new { label = label + "-nis", actualMbps, textMetric = TextMetric(reference, nis),
                            scope = "Same encoded 1080p stream, production NIS at 2x; no extra transmission" });
                    }
                    for (int round = 0; round < 2; round++)
                    {
                        var result = PacedDecode(units, new(candidate.W, candidate.H));
                        paced.Add(new { label, round = round + 1, result });
                        Save(Path.Combine(output, "paced.json"), paced);
                    }
                }
                rows.Add(new { label, actualMbps, budgetMbps = mbps,
                    observedWithinRequestedRatePlus5Percent = actualMbps <= mbps * 1.05,
                    largestAu, largestSerializationMs,
                    processingSeconds, idrs = units.Items.Count(unit => unit.IsIdr), textMetric,
                    encoderArguments = arguments });
                Console.WriteLine($"{label}: {actualMbps:F2} Mbps, largest AU {largestAu / 1024d:F1} KiB, serialized floor {largestSerializationMs:F1} ms");
                Save(Path.Combine(output, "quality.json"), rows);
            }
            var tiles = MeasureTiles(reference, output);
            Save(Path.Combine(output, "tiles.json"), tiles);
            if (!shortGop) SaveComparison(reference, pictures, output);
            Save(Path.Combine(output, "summary.json"), new {
                complete = true, capturedUserDesktop = false, remoteNetworkTest = false,
                timestampUtc = DateTimeOffset.UtcNow, quality = rows, paced, tiles,
                scope = "Synthetic NVENC encoding and actual MF/D3D11 decode/render. Hidden HWND readback validates pixels, NOT DWM/photon latency. PNG tile cost/identity only: not integrated capture, protocol or compositor. Serialization times are lower-bound calculations, not actual relay throughput."
            });
        }
        finally { foreach (var bitmap in pictures.Values) bitmap.Dispose(); }
    }

    private static object PacedDecode(Units units, Size size)
    {
        var setup = Stopwatch.StartNew();
        if (!MediaFoundationD3D11H264Decoder.TryCreate(new(size.Width, size.Height, Fps,
            AllSamplesIndependent: units.Items.All(unit => unit.IsIdr)), out var created, out var capability))
            return new { available = false, capability.Detail };
        using var decoder = created!;
        double setupMs = setup.Elapsed.TotalMilliseconds;
        var submissions = new Dictionary<long, long>();
        var rows = new List<object>();
        var latencies = new List<double>();
        int outputs = 0, explicitOutputs = 0, frameLag = 0;
        for (int i = 0; i < 60; i++)
        {
            // 30-Hz motion -> 2-Hz quiet screen -> 30-Hz motion.
            // A decoder withholding a sample until the next input is visible here.
            if (i != 0) Thread.Sleep(i is >= 30 and < 36 ? 500 : 33);
            long pts = i * 10_000_000L / Fps;
            long started = Stopwatch.GetTimestamp();
            submissions.Add(pts, started);
            var result = decoder.DecodeAccessUnit(units.Items[i].Bytes, pts);
            using var frame = result.Frame;
            double callMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            double? ageMs = null;
            if (frame is not null)
            {
                outputs++;
                if (frame.HasExplicitSampleTime && submissions.TryGetValue(frame.SampleTime100Nanoseconds, out long sent))
                {
                    explicitOutputs++;
                    ageMs = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                    latencies.Add(ageMs.Value);
                    int source = (int)Math.Round(frame.SampleTime100Nanoseconds * Fps / 10_000_000d);
                    frameLag = Math.Max(frameLag, i - source);
                }
            }
            rows.Add(new { input = i, quiet = i is >= 30 and < 36, callMs, ageMs,
                outputPts = frame?.SampleTime100Nanoseconds, result.Status, result.Detail });
            if (result.Status == MediaFoundationD3D11DecodeStatus.Failed) break;
        }
        latencies.Sort();
        Console.WriteLine($"  paced MF: {outputs}/60 outputs; explicit timestamps {explicitOutputs}; max lag {frameLag} frames; max age {(latencies.Count == 0 ? double.NaN : latencies[^1]):F1} ms");
        return new { available = true, setupMs, outputs, explicitOutputs, frameLag,
            p95Ms = latencies.Count == 0 ? (double?)null : latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1],
            maximumMs = latencies.Count == 0 ? (double?)null : latencies[^1], rows,
            scope = "Wall time from AU submission to timestamp-matched decoded GPU texture. No extra drain, no fabricated future samples, no desktop capture or network; scheduling intervals are after preceding decode."
        };
    }

    private static Bitmap RenderLast(Units units, Size size, bool experimental)
    {
        Bitmap? last = null;
        RenderSelected(units, size, experimental, new HashSet<int> { units.Items.Count - 1 },
            (_, pixels) => last = (Bitmap)pixels.Clone());
        return last ?? throw new IOException("No final decoded frame to compare; refusing a stale image.");
    }

    private static void RenderSelected(Units units, Size size, bool experimental,
        ISet<int> selected, Action<int, Bitmap> capture)
    {
        // This is an unshown HWND with no Application.Run message loop. Never
        // install a WinForms synchronization context on the probe's async worker.
        WindowsFormsSynchronizationContext.AutoInstall = false;
        if (!MediaFoundationD3D11H264Decoder.TryCreate(new(size.Width, size.Height, Fps,
            AllSamplesIndependent: units.Items.All(unit => unit.IsIdr)), out var created, out var capability))
            throw new InvalidOperationException(capability.Detail);
        using var decoder = created!;
        using var device = decoder.AcquireDeviceLease();
        using var window = new Form { ClientSize = new(Width, Height), ShowInTaskbar = false };
        if (!D3D11HwndVideoPresenter.TryCreate(device,
            new(window.Handle, size.Width, size.Height, Fps, EnableEdgeEnhancement: false,
                EnableExperimentalUpscaling: experimental, UpscalingAlgorithm: ExperimentalUpscalingAlgorithm.Nis),
            out var createdPresenter, out var presentationCapability)) throw new InvalidOperationException(presentationCapability.Detail);
        using var presenter = createdPresenter!;
        presenter.Resize(Width, Height);
        int captured = 0;
        for (int i = 0; i < units.Items.Count; i++)
        {
            var result = decoder.DecodeAccessUnit(units.Items[i].Bytes, i * 10_000_000L / Fps);
            using var frame = result.Frame;
            if (result.Status == MediaFoundationD3D11DecodeStatus.Failed) throw new IOException(result.Detail);
            if (!selected.Contains(i)) continue;
            long expectedPts = i * 10_000_000L / Fps;
            if (frame is null || !frame.HasExplicitSampleTime || frame.SampleTime100Nanoseconds != expectedPts)
                throw new IOException("Missing/timestamp-mismatched selected frame; refusing a stale image.");
            var present = presenter.Present(frame, new Rectangle(Point.Empty, size), captureValidation: true);
            if (present.Status is not (D3D11HwndVideoPresenterStatus.Presented or D3D11HwndVideoPresenterStatus.Occluded))
                throw new IOException(present.Detail);
            if (experimental && presenter.ExperimentalUpscalingDetail != "NIS") throw new IOException("NIS fallback is not an NIS comparison.");
            using var pixels = UpscaleComparisonProbe.ReadPixels(device, presenter, new(Width, Height));
            capture(i, pixels);
            captured++;
        }
        if (captured != selected.Count) throw new IOException("Incomplete selected-frame readback.");
    }

    private static object MeasureTiles(Bitmap reference, string output)
    {
        using var edit = (Bitmap)reference.Clone();
        using (var g = Graphics.FromImage(edit))
        using (var font = new Font("Microsoft YaHei UI", 14, GraphicsUnit.Pixel))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.FillRectangle(Brushes.White, 24, 104, 600, 28);
            g.DrawString("测试字段：连接成功 123456 中文修改", font, Brushes.Black, 24, 104);
        }
        edit.Save(Path.Combine(output, "native-edited.png"));
        var before = Pixels(reference); var after = Pixels(edit);
        var rows = new List<object>();
        foreach (int edge in new[] { 128, 256 })
        {
            long allBytes = 0, viewportBytes = 0, changedBytes = 0;
            double encodeMs = 0, decodeMs = 0;
            int changed = 0, count = 0;
            var viewport = new Rectangle(0, 0, 960, 640);
            for (int y = 0; y < Height; y += edge)
            for (int x = 0; x < Width; x += edge)
            {
                var rect = new Rectangle(x, y, Math.Min(edge, Width - x), Math.Min(edge, Height - y));
                var clock = Stopwatch.StartNew();
                using var tile = edit.Clone(rect, PixelFormat.Format32bppArgb);
                using var buffer = new MemoryStream();
                tile.Save(buffer, ImageFormat.Png);
                encodeMs += clock.Elapsed.TotalMilliseconds;
                int bytes = checked((int)buffer.Length);
                allBytes += bytes; count++;
                if (rect.IntersectsWith(viewport)) viewportBytes += bytes;
                bool different = false;
                for (int row = rect.Top; row < rect.Bottom && !different; row++)
                    different = !before.AsSpan((row * Width + rect.Left) * 4, rect.Width * 4)
                        .SequenceEqual(after.AsSpan((row * Width + rect.Left) * 4, rect.Width * 4));
                if (different) { changed++; changedBytes += bytes; }
                clock.Restart(); buffer.Position = 0;
                using var decoded = new Bitmap(buffer);
                byte[] restored = Pixels(decoded);
                decodeMs += clock.Elapsed.TotalMilliseconds;
                for (int row = 0; row < rect.Height; row++)
                    if (!restored.AsSpan(row * rect.Width * 4, rect.Width * 4)
                        .SequenceEqual(after.AsSpan(((row + rect.Y) * Width + rect.X) * 4, rect.Width * 4)))
                        throw new IOException("PNG tile did not preserve exact source RGBA pixels.");
            }
            rows.Add(new { edge, count, changed, allBytes, viewportBytes, changedBytes, encodeMs, decodeMs,
                exactDecodedPixels = true,
                floorAt2Mbps = new { fullMs = allBytes * 8d / 2000, viewportMs = viewportBytes * 8d / 2000, editMs = changedBytes * 8d / 2000 },
                scope = "Sequential CPU PNG clone/encode and decode/copy, synthetic source. No capture/damage detection/GPU composition/network/crypto overhead; 2 Mbps is an assumed available detail budget, not a measurement."
            });
        }
        return rows;
    }

    private static object TextMetric(Bitmap reference, Bitmap actual)
    {
        byte[] a = Pixels(reference), b = Pixels(actual);
        long intersection = 0, union = 0, absoluteError = 0, pixels = 0;
        for (int y = TextRegion.Top; y < TextRegion.Bottom; y++)
        for (int x = TextRegion.Left; x < TextRegion.Right; x++)
        {
            int offset = (y * Width + x) * 4;
            int first = (a[offset] * 29 + a[offset + 1] * 150 + a[offset + 2] * 77) >> 8;
            int second = (b[offset] * 29 + b[offset + 1] * 150 + b[offset + 2] * 77) >> 8;
            bool af = first < 150, bf = second < 150;
            if (af && bf) intersection++;
            if (af || bf) { union++; absoluteError += Math.Abs(first - second); pixels++; }
        }
        return new { inkMaskIou = union == 0 ? 1d : intersection / (double)union,
            inkMeanAbsoluteLumaError = pixels == 0 ? 0d : absoluteError / (double)pixels,
            scope = "Fixed native-pixel text crop, luma threshold 150. Includes scaling/chroma/render conversion. NOT OCR character accuracy."
        };
    }

    private static void SaveComparison(Bitmap reference, Dictionary<string, Bitmap> pictures, string output)
    {
        string[] names = ["native source", "1080p-gop1", "1080p-gop1-nis", "native-gop1", "native-gop30"];
        using var result = new Bitmap(720, names.Length * 232, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        using var font = new Font("Segoe UI", 16, GraphicsUnit.Pixel);
        g.Clear(Color.FromArgb(230, 230, 230));
        for (int i = 0; i < names.Length; i++)
        {
            double? rate = null;
            if (i > 0)
            {
                string stream = names[i].Replace("-nis", "", StringComparison.Ordinal);
                rate = new FileInfo(Path.Combine(output, "10mbps-static-" + stream + ".h264")).Length * 8d * Fps / Count / 1_000_000;
            }
            g.DrawString(names[i] + (rate is null ? "" : $" | actual {rate:F2} Mbps"), font, Brushes.Black, 12, i * 232 + 4);
            using var crop = (i == 0 ? reference : pictures[names[i]]).Clone(TextRegion, PixelFormat.Format32bppArgb);
            g.DrawImageUnscaled(crop, 12, i * 232 + 34);
        }
        result.Save(Path.Combine(output, "small-text-comparison.png"));
    }

    private static Bitmap DrawFixture()
    {
        var result = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.Clear(Color.White); g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        for (int column = 0; column < 4; column++)
        {
            bool dark = column % 2 != 0;
            if (dark) g.FillRectangle(Brushes.Black, column * 960, 0, 960, Height);
            for (int y = 24, line = 0; y < Height - 30; y += 28, line++)
            {
                using var font = new Font("Microsoft YaHei UI", line % 4 == 0 ? 12 : 14, GraphicsUnit.Pixel);
                string text = line % 3 == 0 ? "远程桌面 清晰度测试：请输入中文，复制粘贴 0123456789 ABC xyz" :
                    line % 3 == 1 ? "微小笔画：清 晴 睛 情，请核对每个汉字和标点。" : "if (frame.Ready) { Present(texture); } // input latency < 50 ms";
                g.DrawString(text + $"  {line:D3}", font, dark ? Brushes.White : Brushes.Black, column * 960 + 24, y);
                if (line % 7 == 6) g.DrawString("彩色细字 红绿蓝", font, dark ? Brushes.Cyan : Brushes.Red, column * 960 + 700, y);
            }
        }
        return result;
    }

    private static byte[] Pixels(Bitmap source)
    {
        var result = new byte[checked(source.Width * source.Height * 4)];
        var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < source.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, result, y * source.Width * 4, source.Width * 4); }
        finally { source.UnlockBits(data); }
        return result;
    }

    private static void Set(List<string> arguments, string key, string value)
    {
        int i = arguments.IndexOf(key);
        if (i < 0) arguments.AddRange([key, value]); else arguments[i + 1] = value;
    }

    private static async Task RunFfmpegAsync(string ffmpeg, List<string> arguments, string log)
    {
        using var child = new Process { StartInfo = new(ffmpeg) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        foreach (string item in arguments) child.StartInfo.ArgumentList.Add(item);
        child.Start();
        Task<string> stderr = child.StandardError.ReadToEndAsync(), stdout = child.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync().ConfigureAwait(false); throw; }
        string messages = await stderr.ConfigureAwait(false) + await stdout.ConfigureAwait(false);
        await File.WriteAllTextAsync(log, messages).ConfigureAwait(false);
        if (child.ExitCode != 0) throw new IOException("Fixture encoding failed; inspect " + log);
    }

    private static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private sealed class Units : IDisposable
    {
        internal List<AnnexBH264AccessUnit> Items { get; }
        internal Units(string path)
        {
            var parser = new AnnexBH264AccessUnitParser();
            Items = [..parser.Append(File.ReadAllBytes(path)), ..parser.Complete()];
        }
        public void Dispose() { foreach (var item in Items) item.Dispose(); }
    }
}
