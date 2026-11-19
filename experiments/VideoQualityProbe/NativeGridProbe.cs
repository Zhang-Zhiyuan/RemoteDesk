using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using RemoteDesk;

// Render text at its actual remote size, encode once and give every scaler
// the exact same decoded surface. Run in a fresh process for cold-switch data.
internal static class NativeGridProbe
{
    internal static void Run(string output)
    {
        if (Directory.Exists(output)) throw new IOException("Preserving previous probe output.");
        Directory.CreateDirectory(output);
        WindowsFormsSynchronizationContext.AutoInstall = false;
        Size source = new(1280, 720);
        Rectangle visible = new(Point.Empty, source);
        string png = Path.Combine(output, "native.png"), stream = Path.Combine(output, "native.h264");
        using var original = UpscaleComparisonProbe.Fixture(source);
        original.Save(png);
        TextClarityProbe.RunFfmpegAsync("ffmpeg", ["-v", "error", "-i", png, "-frames:v", "1",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-crf", "20",
            "-pix_fmt", "yuv420p", "-f", "h264", stream], Path.Combine(output, "encoder.log")).GetAwaiter().GetResult();
        if (!MediaFoundationD3D11H264Decoder.TryCreate(new(source.Width, source.Height, 60, AllSamplesIndependent: true),
            out var created, out var capability)) throw new IOException(capability.Detail);
        using var decoder = created!;
        using var device = decoder.AcquireDeviceLease();
        var decoded = decoder.DecodeAccessUnit(File.ReadAllBytes(stream), 1_000_000);
        using var frame = decoded.Frame ?? throw new IOException(decoded.Detail);
        var rows = new List<object>();
        foreach (int scale in new[] { 2, 3 })
        {
            Size size = new(source.Width * scale, source.Height * scale);
            using var window = new Form { ShowInTaskbar = false, ClientSize = size };
            if (!D3D11HwndVideoPresenter.TryCreate(device, new(window.Handle, source.Width, source.Height, 60),
                out var made, out var status)) throw new IOException(status.Detail);
            using var presenter = made!;
            if (!presenter.Resize(size.Width, size.Height).IsSuccess) throw new IOException("Resize failed.");
            for (int i = 0; i < 8; i++) Present(false);
            foreach (var mode in Enum.GetValues<ExperimentalUpscalingAlgorithm>().OrderBy(m => m != ExperimentalUpscalingAlgorithm.Nis))
            {
                var wall = Stopwatch.StartNew();
                presenter.SetExperimentalUpscaling(true, mode);
                Present(false);
                double firstMs = wall.Elapsed.TotalMilliseconds;
                double longestWarmupPresentMs = firstMs;
                int ordinaryFramesDuringPreparation = 0;
                while (!presenter.ExperimentalUpscalingActive)
                {
                    ordinaryFramesDuringPreparation++;
                    if (wall.ElapsedMilliseconds > 5000) throw new TimeoutException("Scaler never became active.");
                    Thread.Sleep(2);
                    long start = Stopwatch.GetTimestamp();
                    Present(false);
                    longestWarmupPresentMs = Math.Max(longestWarmupPresentMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                double readyMs = wall.Elapsed.TotalMilliseconds;
                double[] samples = new double[100];
                for (int i = 0; i < samples.Length; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    Present(false);
                    samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                Present(true);
                using var result = UpscaleComparisonProbe.ReadPixels(device, presenter, size);
                // The reference is the original small raster before encoding. Measure
                // fidelity on ink/edge pixels only, excluding large white regions.
                // Each output sample is compared with its corresponding source texel.
                long error = 0, count = 0;
                for (int y = 17; y < 105; y++)
                    for (int x = 17; x < 500; x++)
                    {
                        int ink = original.GetPixel(x, y).R;
                        if (ink >= 245 && original.GetPixel(x-1, y).R >= 245 && original.GetPixel(x+1, y).R >= 245 &&
                            original.GetPixel(x, y-1).R >= 245 && original.GetPixel(x, y+1).R >= 245) continue;
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                            {
                                error += Math.Abs(result.GetPixel(x * scale + dx, y * scale + dy).R - ink);
                                count++;
                            }
                    }
                using var crop = result.Clone(new Rectangle(16 * scale, 17 * scale, 360 * scale, 90 * scale), PixelFormat.Format32bppArgb);
                crop.Save(Path.Combine(output, $"{scale}x-{mode}.png"));
                Array.Sort(samples);
                var row = new { scale, mode = mode.ToString(), actual = presenter.ExperimentalUpscalingDetail,
                    firstMs, readyMs, longestWarmupPresentMs, ordinaryFramesDuringPreparation,
                    medianPresentMs = samples[49], p95PresentMs = samples[94],
                    nativeTextMeanAbsoluteError = error / (double)count };
                rows.Add(row);
                Console.WriteLine(JsonSerializer.Serialize(row));
            }
            void Present(bool snapshot)
            {
                var result = presenter.Present(frame, visible, captureValidation: snapshot);
                if (!result.IsSuccess) throw new IOException(result.Detail);
            }
        }
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
