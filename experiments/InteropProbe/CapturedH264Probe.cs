using System.Diagnostics;
using RemoteDesk;

internal static class CapturedH264Probe
{
    // Local capture/encode/decode only. No input, clipboard, settings or saved
    // screenshots. Works on the current desktop, including the secure helper.
    internal static async Task<int> RunAsync(string output)
    {
        using var capture = new ScreenCaptureService(ScreenCaptureService.GetDefaultTarget());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CapturedFrameH264Encoder? encoder = null;
        FfmpegH264Decoder? decoder = null;
        var samples = new List<object>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                int scale = CapturedFrameH264Encoder.ResolveCaptureScale(ScreenCaptureService.GetDefaultTarget().Bounds, 100, true);
                ScreenCaptureResult source = capture.CaptureJpeg(90, scale);
                Size size = CapturedFrameH264Encoder.ResolveOutputSize(source.FrameSize, adaptive: true);
                encoder ??= new CapturedFrameH264Encoder(FfmpegH264Decoder.AvailablePath!, size, 10);
                decoder ??= FfmpegH264Decoder.TryCreate(size) ?? throw new IOException("No decoder.");
                long start = Stopwatch.GetTimestamp();
                using AnnexBH264AccessUnit frame = await encoder.EncodeAsync(source.JpegBytes, timeout.Token);
                double encodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var result = await decoder.DecodeAsync(i + 1, frame.Bytes).WaitAsync(timeout.Token);
                using Bitmap bitmap = result.Bitmap ?? throw new IOException("Encoded screen could not be decoded.");
                int nonBlack = 0;
                for (int y = 0; y < bitmap.Height; y += 16)
                    for (int x = 0; x < bitmap.Width; x += 16)
                    { Color pixel = bitmap.GetPixel(x, y); if (pixel.R + pixel.G + pixel.B > 30) nonBlack++; }
                samples.Add(new { i, source.IsSecureDesktop, source.FrameSize, size, source.CaptureMilliseconds,
                    localJpegBytes = source.JpegBytes.Length, h264Bytes = frame.Bytes.Length, encodeMs,
                    throughDecodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds, nonBlack,
                    frame.IsIdr, frame.HasSps, frame.HasPps });
                Console.WriteLine($"FRAME {i}: secure={source.IsSecureDesktop}, {size.Width}x{size.Height}, H264={frame.Bytes.Length}, encode={encodeMs:F1}ms, nonBlack={nonBlack}");
                await Task.Delay(100, timeout.Token);
            }
            Program.Save(Path.Combine(output, "capture-codec.json"), new { samples,
                scope = "Actual local capture including existing secure helper, H264 encode/decode and pixel statistics; no network or input" });
            return 0;
        }
        finally { decoder?.Dispose(); encoder?.Dispose(); }
    }
}
