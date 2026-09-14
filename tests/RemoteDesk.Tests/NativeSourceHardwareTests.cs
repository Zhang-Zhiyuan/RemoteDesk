using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeSourceHardwareTests
{
    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void ReadbackBoundsBudgetQuotasCancellationAndBorrowedDeviceRemainIntact()
    {
        if (!Enabled) return;
        using var device = CreateDevice();
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        using var texture = CreatePattern(device, 257, 259, out byte[] bgra);
        nint identity = texture.Device.NativePointer;
        using var readback = new D3D11NativeRegionReadback(device);
        Rectangle edge = new(256, 256, 1, 3);
        Assert.Equal(262144, readback.StagingBytes);
        Assert.False(readback.TryEnqueue(texture, 1, edge, default));
        Assert.False(readback.TryEnqueue(texture, 1, edge, Budget() with { InputPending = true }));
        Assert.False(readback.TryEnqueue(texture, 1, edge, Budget() with { BaseFrameBacklogged = true }));
        Assert.False(readback.TryEnqueue(texture, 1, edge, Budget() with { TransportCongested = true }));
        Assert.Equal(0, readback.PendingCount);
        Assert.True(readback.TryEnqueue(texture, 1, edge, Budget()));
        Assert.True(readback.TryEnqueue(texture, 1, new(0, 0, 128, 128), Budget()));
        Assert.False(readback.TryEnqueue(texture, 1, edge, Budget()));
        Assert.True(readback.TryEnqueue(texture, 2, edge, Budget()));
        Assert.True(readback.TryEnqueue(texture, 2, new(128, 128, 128, 128), Budget()));
        Assert.False(readback.TryEnqueue(texture, 3, edge, Budget()));
        Assert.Null(readback.TryRead(default));
        Assert.Equal(4, readback.PendingCount);
        Assert.Equal(identity, texture.Device.NativePointer);
        // Test-only flush makes the fixture's stand-alone GPU copies eligible
        // to complete. Production readback never flushes/waits for completion.
        context.Flush();
        var pixels = new List<NativeRegionPixels>();
        WaitUntil(() =>
        {
            if (readback.TryRead(Budget()) is { } ready) pixels.Add(ready);
            Assert.False(readback.IsFailed, readback.Failure);
            return pixels.Count == 4;
        });
        foreach (var block in pixels) AssertPattern(block, 257, bgra);
        Assert.Equal(new long[] { 1, 1, 2, 2 }, pixels.Select(value => value.SourceTime100Nanoseconds));
        Assert.Equal(0, readback.PendingCount);
        Assert.True(readback.TryEnqueue(texture, 3, edge, Budget()));
        Assert.True(readback.TryEnqueue(texture, 3, edge, Budget()));
        readback.Clear();
        Assert.Equal(0, readback.PendingCount);
        Assert.False(readback.TryEnqueue(texture, 3, edge, Budget())); // Clear cannot reset quota.
        Assert.False(readback.TryEnqueue(texture, 2, edge, Budget())); // No source rollback.
        Assert.True(readback.TryEnqueue(texture, 4, edge, Budget()));
        readback.Dispose(); readback.Dispose();
        Assert.Equal(0, readback.StagingBytes);
        Assert.Null(readback.TryRead(Budget()));
        Assert.False(readback.TryEnqueue(texture, 5, edge, Budget()));
        Assert.Equal(identity, texture.Device.NativePointer);
    }

    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void ReadbackRejectsOtherDeviceAndOverflowWithoutPoisoningCapture()
    {
        if (!Enabled) return;
        using var device = CreateDevice();
        using var other = CreateDevice();
        using var localTexture = CreatePattern(device, 128, 128, out _);
        using var foreignTexture = CreatePattern(other, 128, 128, out _);
        using var readback = new D3D11NativeRegionReadback(device);
        Assert.Throws<ArgumentException>(() => readback.TryEnqueue(foreignTexture, 1, new(0, 0, 128, 128), Budget()));
        Assert.Throws<ArgumentException>(() => readback.TryEnqueue(localTexture, 1, new(int.MaxValue, 0, 128, 128), Budget()));
        Assert.False(readback.IsFailed);
        Assert.Equal(0, readback.PendingCount);
        Assert.True(readback.TryEnqueue(localTexture, 1, new(0, 0, 128, 128), Budget()));
    }

    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void HardwareBaseAndNativeFanoutPreserveSourceIdentityWithoutASecondInput()
    {
        if (!Enabled) return;
        using var device = CreateDevice();
        using var first = CreatePattern(device, 640, 360, out byte[] firstBgra);
        using var second = CreatePattern(device, 640, 360, out byte[] secondBgra, inverted: true);
        Assert.True(MediaFoundationD3D11H264Encoder.TryCreate(device,
            new(new(640, 360), new(320, 180), 60, 4_000_000), out var created, out string? failure), failure);
        using var encoder = created!;
        using var readback = new D3D11NativeRegionReadback(device);
        Assert.True(MediaFoundationD3D11H264Decoder.TryCreate(new(320, 180, 60, AllSamplesIndependent: true),
            out var createdDecoder, out var capability), capability.Detail);
        using var decoder = createdDecoder!;
        for (int i = 0; i < 12; i++)
        {
            long pts = 3_000_000L + i * 233_333;
            var texture = (i & 1) == 0 ? first : second;
            byte[] expected = (i & 1) == 0 ? firstBgra : secondBgra;
            WaitUntil(() =>
            {
                Assert.False(encoder.IsFailed, encoder.Failure);
                return encoder.TrySubmit(texture, pts);
            });
            Assert.True(encoder.HasInFlightFrame);
            Assert.False(encoder.TrySubmit(texture, pts + 1)); // Never queue a second input.
            Assert.Throws<ArgumentOutOfRangeException>(() => encoder.TrySubmit(texture, pts));
            Assert.True(readback.TryEnqueue(texture, pts, new(128, 128, 128, 128), Budget()));
            HardwareEncodedSourceFrame? encoded = null;
            NativeRegionPixels? pixels = null;
            WaitUntil(() =>
            {
                encoded ??= encoder.TryRead(); pixels ??= readback.TryRead(Budget());
                Assert.False(encoder.IsFailed, encoder.Failure);
                Assert.False(readback.IsFailed, readback.Failure);
                return encoded is not null && pixels is not null;
            });
            Assert.Equal(pts, encoded!.SourceTime100Nanoseconds);
            Assert.Equal(pts, pixels!.SourceTime100Nanoseconds);
            AssertPattern(pixels, 640, expected);
            var result = decoder.DecodeAccessUnit(encoded.AnnexBBytes, pts);
            using var frame = result.Frame;
            Assert.NotNull(frame);
            Assert.True(frame.HasExplicitSampleTime);
            Assert.Equal(pts, frame.SampleTime100Nanoseconds);
            Assert.False(encoder.HasInFlightFrame);
            Assert.Null(encoder.TryRead());
            Assert.NotEqual(nint.Zero, texture.Device.NativePointer);
        }
        encoder.Dispose(); encoder.Dispose();
        Assert.False(encoder.TrySubmit(first, 99_000_000));
        Assert.Null(encoder.TryRead());
        Assert.NotEqual(nint.Zero, first.Device.NativePointer);
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("REMOTEDESK_RUN_HARDWARE_SMOKE") == "1";

    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void HardwareBasePreservesSdrColorBarsAfterGpuConversion()
    {
        if (!Enabled) return;
        Color[] colors = [Color.Red, Color.Lime, Color.Blue, Color.White, Color.Black,
            Color.FromArgb(16, 30, 200), Color.FromArgb(30, 75, 180), Color.Cyan];
        using var device = CreateDevice();
        using var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
            640, 360, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget));
        byte[] bgra = new byte[640 * 360 * 4];
        for (int y = 0; y < 360; y++) for (int x = 0; x < 640; x++)
        {
            Color color = colors[y / 180 * 4 + x / 160];
            int offset = (y * 640 + x) * 4;
            bgra[offset] = color.B; bgra[offset + 1] = color.G;
            bgra[offset + 2] = color.R; bgra[offset + 3] = 255;
        }
        device.ImmediateContext.UpdateSubresource(bgra, texture, 0, 640 * 4);
        Assert.True(MediaFoundationD3D11H264Encoder.TryCreate(device,
            new(new(640, 360), new(320, 180), 60, 4_000_000), out var created, out string? failure), failure);
        using var encoder = created!;
        WaitUntil(() => encoder.TrySubmit(texture, 1_000_000));
        HardwareEncodedSourceFrame? encoded = null;
        WaitUntil(() => { encoded = encoder.TryRead(); Assert.False(encoder.IsFailed, encoder.Failure); return encoded is not null; });
        Assert.True(MediaFoundationD3D11H264Decoder.TryCreate(new(320, 180, 60, AllSamplesIndependent: true),
            out var createdDecoder, out var capability), capability.Detail);
        using var decoder = createdDecoder!;
        using var frame = decoder.DecodeAccessUnit(encoded!.AnnexBBytes, encoded.SourceTime100Nanoseconds).Frame;
        Assert.NotNull(frame);
        using var decoderDevice = decoder.AcquireDeviceLease();
        using var window = new Form { ClientSize = new(320, 180), ShowInTaskbar = false };
        Assert.True(D3D11HwndVideoPresenter.TryCreate(decoderDevice,
            new(window.Handle, 320, 180, 60, EnableEdgeEnhancement: false), out var candidate, out var presentationCapability), presentationCapability.Detail);
        using var presenter = candidate!;
        Assert.True(presenter.Resize(320, 180).IsSuccess);
        Assert.True(presenter.Present(frame, new(0, 0, 320, 180), captureValidation: true).IsSuccess);
        var validation = (ID3D11Texture2D)typeof(D3D11HwndVideoPresenter)
            .GetField("_validationSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter)!;
        var description = validation.Description;
        description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = decoderDevice.CreateTexture2D(description);
        using var context = decoderDevice.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        context.CopyResource(staging, validation);
        // Deliberate blocking readback belongs only to this color correctness
        // test, not the source/encoder/opportunistic readback implementations.
        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            for (int i = 0; i < colors.Length; i++)
            {
                int x = i % 4 * 80 + 40, y = i / 4 * 90 + 45;
                nint pixel = mapped.DataPointer + y * (int)mapped.RowPitch + x * 4;
                var actual = Color.FromArgb(Marshal.ReadByte(pixel + 2), Marshal.ReadByte(pixel + 1), Marshal.ReadByte(pixel));
                Assert.True(Math.Abs(actual.R - colors[i].R) <= 8 && Math.Abs(actual.G - colors[i].G) <= 8 &&
                    Math.Abs(actual.B - colors[i].B) <= 8, $"SDR color mismatch: {colors[i]} -> {actual}");
            }
        }
        finally { context.Unmap(staging, 0); }
    }

    private static ID3D11Device CreateDevice() => D3D11.D3D11CreateDevice(DriverType.Hardware,
        DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
    private static NativeDetailRenderBudget Budget() => new(Stopwatch.GetTimestamp() + Stopwatch.Frequency);

    private static ID3D11Texture2D CreatePattern(ID3D11Device device, int width, int height, out byte[] bgra, bool inverted = false)
    {
        var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
            (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget));
        bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 4;
            bgra[index] = (byte)(x ^ (inverted ? 255 : 0));
            bgra[index + 1] = (byte)y; bgra[index + 2] = (byte)(x + y); bgra[index + 3] = 255;
        }
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        context.UpdateSubresource(bgra, texture, 0, (uint)(width * 4));
        return texture;
    }

    private static void AssertPattern(NativeRegionPixels pixels, int width, byte[] expected)
    {
        for (int y = 0; y < pixels.Bounds.Height; y++) for (int x = 0; x < pixels.Bounds.Width; x++)
        {
            int source = ((pixels.Bounds.Y + y) * width + pixels.Bounds.X + x) * 4;
            int actual = (y * pixels.Bounds.Width + x) * 4;
            Assert.Equal(expected[source + 2], pixels.Rgba[actual]);
            Assert.Equal(expected[source + 1], pixels.Rgba[actual + 1]);
            Assert.Equal(expected[source], pixels.Rgba[actual + 2]);
            Assert.Equal(255, pixels.Rgba[actual + 3]);
        }
    }

    private static void WaitUntil(Func<bool> done)
    {
        using var timer = new WindowsHighResolutionPacingWaiter();
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "Bounded GPU fixture did not complete.");
            timer.Wait(TimeSpan.FromMilliseconds(.2), CancellationToken.None);
        }
    }
}
