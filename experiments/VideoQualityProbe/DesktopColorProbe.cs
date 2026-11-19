using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using RemoteDesk;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static class DesktopColorProbe
{
    private static readonly Size Source = new(1280, 720);
    internal static readonly Color[] Swatches =
    [
        Color.Black, Color.FromArgb(16, 16, 16), Color.FromArgb(64, 64, 64),
        Color.FromArgb(128, 128, 128), Color.FromArgb(235, 235, 235), Color.White,
        Color.FromArgb(204, 38, 45), Color.FromArgb(31, 174, 76), Color.FromArgb(31, 81, 209),
        Color.FromArgb(226, 172, 44)
    ];

    internal static void Run(string output, string? legacyNative)
    {
        if (Directory.Exists(output)) throw new IOException("Choose a new output directory.");
        Directory.CreateDirectory(output);
        WindowsFormsSynchronizationContext.AutoInstall = false;
        string png = Path.Combine(output, "source.png");
        using var original = UpscaleComparisonProbe.Fixture(Source);
        using (var g = Graphics.FromImage(original))
        {
            using var background = new SolidBrush(Color.FromArgb(23, 26, 33));
            g.FillRectangle(background, 16, 443, 1240, 96);
            using var text = new Font("Microsoft YaHei UI", 14, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.FromArgb(120, 205, 246));
            g.DrawString("远程终端   端口已连接   status = READY   0O 1lI   细线对照", text, ink, 30, 461);
            for (int i = 0; i < Swatches.Length; i++)
            {
                using var brush = new SolidBrush(Swatches[i]);
                using var pen = new Pen(Swatches[i]);
                g.FillRectangle(brush, 18 + 120 * i, 622, 108, 65);
                g.DrawLine(pen, 24 + 120 * i, 494, 112 + 120 * i, 525);
            }
        }
        original.Save(png, ImageFormat.Png);
        var rows = new List<object>();
        foreach (var (name, matrix, range, tag) in new[]
        {
            ("auto", "bt601", "tv", ""),
            ("601-limited", "bt601", "tv", "smpte170m"),
            ("709-limited", "bt709", "tv", "bt709"),
            ("601-full", "bt601", "pc", "smpte170m"),
            ("709-full", "bt709", "pc", "bt709"),
            ("unspecified", "bt601", "tv", "")
        })
        {
            string stream = Path.Combine(output, name + ".h264");
            List<string> arguments = ["-v", "error", "-i", png, "-frames:v", "1", "-vf",
                $"scale=in_range=pc:out_range={range}:out_color_matrix={matrix},format=yuv420p",
                "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-crf", "17"];
            if (tag.Length > 0) arguments.AddRange(["-color_range", range, "-colorspace", tag, "-color_primaries", tag, "-color_trc", tag]);
            if (name == "unspecified") arguments.AddRange(["-bsf:v", "h264_metadata=colour_primaries=2:transfer_characteristics=2:matrix_coefficients=2"]);
            arguments.AddRange(["-f", "h264", stream]);
            TextClarityProbe.RunFfmpegAsync("ffmpeg", arguments, stream + ".log").GetAwaiter().GetResult();
        }
        EncodeNativeDesktop(original, Path.Combine(output, "native-encoder.h264"));
        if (legacyNative is not null) File.Copy(legacyNative, Path.Combine(output, "old-native.h264"));
        if (!MediaFoundationD3D11H264Decoder.TryCreate(new(Source.Width, Source.Height, 30, AllSamplesIndependent: true),
            out var created, out var capability)) throw new IOException(capability.Detail);
        using var decoder = created!;
        using var device = decoder.AcquireDeviceLease();
        var signal = new WindowsH264VideoSignal();
        long pts = 1_000_000;
        foreach (int scale in new[] { 1, 2 })
        {
            Size size = new(Source.Width * scale, Source.Height * scale);
            using var window = new Form { ShowInTaskbar = false, ClientSize = size };
            if (!D3D11HwndVideoPresenter.TryCreate(device, new(window.Handle, Source.Width, Source.Height, 30,
                EnableExperimentalUpscaling: scale > 1, UpscalingAlgorithm: ExperimentalUpscalingAlgorithm.PixelGrid),
                out var made, out var status)) throw new IOException(status.Detail);
            using var presenter = made!;
            if (!presenter.Resize(size.Width, size.Height).IsSuccess) throw new IOException("Could not resize the swap chain.");
            foreach (string name in new[] { "auto", "601-limited", "709-limited", "601-full", "709-full", "unspecified", "native-encoder", "old-native" })
            {
                if (name == "old-native" && legacyNative is null) continue;
                byte[] encoded = File.ReadAllBytes(Path.Combine(output, name + ".h264"));
                bool legacyColors = (name is "native-encoder" or "old-native") && signal.UsesDriverDefaults(encoded);
                var decoded = decoder.DecodeAccessUnit(encoded, pts += 333333);
                using var frame = decoded.Frame ?? throw new IOException(decoded.Detail);
                var warm = Stopwatch.StartNew();
                void Present(bool capture)
                {
                    var result = presenter.Present(frame, new(Point.Empty, Source), captureValidation: capture,
                        useDriverVideoColors: legacyColors);
                    if (!result.IsSuccess) throw new IOException(result.Detail);
                }
                do
                {
                    Present(false);
                    if (scale == 1 || presenter.ExperimentalUpscalingActive) break;
                    if (warm.Elapsed.TotalSeconds > 10) throw new IOException("Pixel-grid shader did not become active.");
                    Thread.Sleep(5);
                } while (true);
                Present(true);
                using var pixels = UpscaleComparisonProbe.ReadPixels(device, presenter, size);
                string image = $"{name}-{scale}x.png";
                pixels.Save(Path.Combine(output, image), ImageFormat.Png);
                var actual = Enumerable.Range(0, Swatches.Length).Select(i =>
                    pixels.GetPixel((70 + 120 * i) * scale, 650 * scale)).ToArray();
                var errors = actual.Zip(Swatches, (a, e) => new[] { Math.Abs(a.R - e.R), Math.Abs(a.G - e.G), Math.Abs(a.B - e.B) }).SelectMany(v => v).ToArray();
                var row = new { name, scale, black = actual[0].R, white = actual[5].R, maxChannelError = errors.Max(),
                    meanChannelError = errors.Average(), frame.ColorSpace, rgb = actual.Select(c => $"{c.R:X2}{c.G:X2}{c.B:X2}"), image };
                rows.Add(row);
                Console.WriteLine(JsonSerializer.Serialize(row));
                if (row.maxChannelError > 5)
                    throw new IOException($"{name} at {scale}x differs from the RGB reference by {row.maxChannelError}.");
            }
        }
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void EncodeNativeDesktop(Bitmap original, string path)
    {
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        if (!MediaFoundationD3D11H264Encoder.TryCreate(device, new(Source, Source, 30, 12_000_000),
            out var created, out var failure)) throw new IOException(failure);
        using var encoder = created!;
        using var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
            (uint)Source.Width, (uint)Source.Height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        var data = original.LockBits(new(Point.Empty, Source), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { device.ImmediateContext.UpdateSubresource(texture, 0, null, data.Scan0, (uint)data.Stride, 0); }
        finally { original.UnlockBits(data); }
        var waiting = Stopwatch.StartNew();
        bool submitted = false;
        while (!encoder.IsFailed && waiting.Elapsed.TotalSeconds < 10)
        {
            if (!submitted) submitted = encoder.TrySubmit(texture, 1_000_000);
            if (encoder.TryRead() is { } sample)
            {
                File.WriteAllBytes(path, sample.AnnexBBytes);
                return;
            }
            Thread.Sleep(1);
        }
        throw new IOException(encoder.Failure ?? "Native desktop hardware encoder timed out.");
    }
}
