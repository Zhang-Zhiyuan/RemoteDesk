using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static class UpscaleComparisonProbe
{
    internal static void Run(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Preserving previous probe output.");
        Directory.CreateDirectory(output);
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        var context = device.ImmediateContext;
        var results = new List<object>();
        foreach (var sizes in new[] { (new Size(1280,720), new Size(1920,1080)),
            (new Size(1280,720), new Size(2560,1440)), (new Size(1920,1080), new Size(3840,2160)) })
        {
            var (sourceSize, targetSize) = sizes;
            string prefix = $"{sourceSize.Width}x{sourceSize.Height}-to-{targetSize.Width}x{targetSize.Height}";
            using var native = Fixture(targetSize);
            native.Save(Path.Combine(output, prefix + "-reference.png"));
            using var small = new Bitmap(sourceSize.Width, sourceSize.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(native, new Rectangle(Point.Empty, sourceSize));
            }
            byte[] nv12 = ToNv12(small);
            using var texture = device.CreateTexture2D(new Texture2DDescription(Format.NV12,
                (uint)sourceSize.Width, (uint)sourceSize.Height, 1, 1, BindFlags.Decoder));
            context.UpdateSubresource(nv12, texture, 0, (uint)sourceSize.Width, (uint)nv12.Length);
            using var window = new System.Windows.Forms.Form { ClientSize = targetSize, ShowInTaskbar = false };
            // Compare against a native-resolution reference through the SAME
            // NV12/color-conversion path, not an unrelated RGB range/gamma.
            using var referenceTexture = device.CreateTexture2D(new Texture2DDescription(Format.NV12,
                (uint)targetSize.Width, (uint)targetSize.Height, 1, 1, BindFlags.Decoder));
            byte[] referenceNv12 = ToNv12(native);
            context.UpdateSubresource(referenceNv12, referenceTexture, 0, (uint)targetSize.Width, (uint)referenceNv12.Length);
            if (!D3D11HwndVideoPresenter.TryCreate(device,
                new(window.Handle, targetSize.Width, targetSize.Height, 30, EnableEdgeEnhancement: false),
                out var nativePresenter, out var nativeCapability)) throw new InvalidOperationException(nativeCapability.Detail);
            Bitmap reference;
            using (nativePresenter)
            {
                nativePresenter!.Resize(targetSize.Width, targetSize.Height);
                nativePresenter.Present(referenceTexture, 0, new Rectangle(Point.Empty, targetSize), captureValidation: true);
                reference = ReadPixels(device, nativePresenter, targetSize);
            }
            using var convertedReference = reference;
            reference.Save(Path.Combine(output, prefix + "-converted-reference.png"));
            foreach (string mode in new[] { "legacy", "bicubic", "nis" })
            {
                var options = new D3D11HwndVideoPresenterOptions(window.Handle, sourceSize.Width, sourceSize.Height, 30,
                    EnableExperimentalUpscaling: mode != "legacy",
                    UpscalingAlgorithm: mode == "nis" ? ExperimentalUpscalingAlgorithm.Nis : ExperimentalUpscalingAlgorithm.CatmullRom);
                if (!D3D11HwndVideoPresenter.TryCreate(device, options, out var presenter, out var capability))
                    throw new InvalidOperationException(capability.Detail);
                using (presenter)
                {
                    presenter!.Resize(targetSize.Width, targetSize.Height);
                    var timings = new List<double>();
                    for (int i = 0; i < 38; i++)
                    {
                        using var disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
                        using var begin = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
                        using var end = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
                        context.Begin(disjoint);
                        presenter.RenderTimingQueriesForTests = (begin, end);
                        var status = presenter.Present(texture, 0, new Rectangle(Point.Empty, sourceSize), captureValidation: i == 37);
                        context.End(disjoint);
                        context.Flush();
                        var clock = Stopwatch.StartNew();
                        QueryDataTimestampDisjoint info;
                        while (!context.GetData(disjoint, out info))
                        {
                            if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("GPU timestamp query timed out.");
                            Thread.Sleep(1);
                        }
                        context.GetData(begin, out ulong first);
                        context.GetData(end, out ulong last);
                        presenter.RenderTimingQueriesForTests = null;
                        if (status.Status is not (D3D11HwndVideoPresenterStatus.Presented or D3D11HwndVideoPresenterStatus.Occluded))
                            throw new InvalidOperationException(status.Detail);
                        if (i >= 8 && i != 37 && !info.Disjoint && info.Frequency > 0)
                            timings.Add((last - first) * 1000d / info.Frequency);
                    }
                    if (mode == "nis" && presenter.ExperimentalUpscalingDetail != "NIS")
                        throw new InvalidOperationException("NIS unexpectedly fell back: " + presenter.ExperimentalUpscalingDetail);
                    using var rendered = ReadPixels(device, presenter, targetSize);
                    rendered.Save(Path.Combine(output, prefix + "-" + mode + ".png"));
                    timings.Sort();
                    double mse = 0;
                    // Only the text/line chart region; blank screen area must
                    // not inflate a quality score. PSNR is NOT readability.
                    int areaWidth = Math.Min(targetSize.Width, 1250), areaHeight = 620;
                    for (int y = 12; y < areaHeight; y++)
                        for (int x = 12; x < areaWidth; x++)
                        {
                            double d = reference.GetPixel(x, y).R - rendered.GetPixel(x, y).R;
                            mse += d * d;
                        }
                    mse /= (areaWidth - 12) * (areaHeight - 12);
                    var result = new
                    {
                        sourceSize,
                        targetSize,
                        mode,
                        actual = presenter.ExperimentalUpscalingDetail,
                        medianGpuMs = timings[timings.Count / 2],
                        p95GpuMs = timings[(int)(timings.Count * 0.95)],
                        chartPsnr = 10 * Math.Log10(255 * 255 / mse),
                        samples = timings.Count
                    };
                    results.Add(result);
                    Console.WriteLine(JsonSerializer.Serialize(result));
                }
            }
        }
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            adapter = adapter.Description.Description,
            results,
            scope = "Synthetic grayscale desktop only; no real capture/network/input. GPU timestamps bracket conversion/scaling commands, excluding DXGI frame wait, Present, first 8 frames and validation readback. Background GPU work/clock changes can affect results. Not end-to-end network latency or proof of text readability."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Bitmap Fixture(Size size)
    {
        var b = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.White); g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        int y = 16;
        foreach (int pixels in new[] { 12, 14, 16, 20, 28, 36 })
        {
            using var font = new Font("Microsoft YaHei UI", pixels, GraphicsUnit.Pixel);
            g.DrawString("远程桌面 清晰度测试：请输入中文，复制粘贴 0123456789 ABC xyz", font, Brushes.Black, 16, y);
            y += pixels + 18;
        }
        g.FillRectangle(Brushes.Black, 16, 350, 1200, 110);
        using var inverse = new Font("Microsoft YaHei UI", 16, GraphicsUnit.Pixel);
        g.DrawString("深色背景 白色文字：登录 / 设置 / return value != null;", inverse, Brushes.White, 28, 370);
        for (int i = 0; i < 9; i++) g.DrawLine(Pens.Black, 20 + i * 130, 500, 110 + i * 130, 600 - i * 8);
        return b;
    }

    private static byte[] ToNv12(Bitmap b)
    {
        int width = b.Width, height = b.Height;
        var bytes = new byte[width * height * 3 / 2];
        var data = b.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < width; x++) bytes[y * width + x] = (byte)(16 + (row[x * 4] * 219 + 127) / 255);
            }
        }
        finally { b.UnlockBits(data); }
        Array.Fill(bytes, (byte)128, width * height, width * height / 2);
        return bytes;
    }

    internal static Bitmap ReadPixels(ID3D11Device device, D3D11HwndVideoPresenter presenter, Size size)
    {
        var source = (ID3D11Texture2D)typeof(D3D11HwndVideoPresenter).GetField("_validationSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter)!;
        var description = source.Description; description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None; description.CPUAccessFlags = CpuAccessFlags.Read;
        using var readback = device.CreateTexture2D(description);
        var context = device.ImmediateContext;
        context.CopyResource(readback, source);
        var mapped = context.Map(readback, 0, MapMode.Read);
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[size.Width * 4];
            for (int y = 0; y < size.Height; y++)
            {
                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, row, 0, row.Length);
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); context.Unmap(readback, 0); }
        return bitmap;
    }
}
