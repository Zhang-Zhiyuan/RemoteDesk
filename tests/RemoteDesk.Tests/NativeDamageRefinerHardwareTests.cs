using System.Diagnostics;
using System.Drawing;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeDamageRefinerHardwareTests
{
    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void ExactGpuComparisonDetectsOneBitChangesEdgesAndRestoresNoOldVersions()
    {
        if (!Enabled) return;
        using var device = Device();
        using var refiner = new D3D11NativeDamageRefiner(device);
        byte[] pixels = Enumerable.Repeat((byte)127, 257 * 259 * 4).ToArray();
        using var first = Texture(device, pixels);
        int[] tiles = Enumerable.Range(0, 9).ToArray();
        Assert.False(refiner.Begin(first, Coarse(1), tiles, Budget()));
        Assert.All(refiner.Complete(1, Budget()).CopyVersions(), version => Assert.Equal(1, version));
        using var identical = Texture(device, pixels);
        Assert.True(refiner.Begin(identical, Coarse(2), tiles, Budget()));
        FinishGpu(device, refiner);
        Assert.All(refiner.Complete(2, Budget()).CopyVersions(), version => Assert.Equal(1, version));
        pixels[(258 * 257 + 256) * 4] ^= 1; // Last pixel in a 1x3 edge tile.
        pixels[(127 * 257 + 127) * 4 + 1] ^= 1; // Tile boundary, one green bit.
        pixels[(128 * 257 + 128) * 4 + 2] ^= 1; // Next tile, one red bit.
        pixels[(0 * 257 + 128) * 4 + 3] ^= 1; // Only alpha: opaque native RGB unchanged.
        using var changed = Texture(device, pixels);
        Assert.True(refiner.Begin(changed, Coarse(3), tiles, Budget()));
        FinishGpu(device, refiner);
        Assert.Equal(new long[] { 3, 1, 1, 1, 3, 1, 1, 1, 3 }, refiner.Complete(3, Budget()).CopyVersions());
        Assert.True(refiner.Begin(first, Coarse(4), tiles, Budget()));
        FinishGpu(device, refiner);
        Assert.Equal(new long[] { 4, 1, 1, 1, 4, 1, 1, 1, 4 }, refiner.Complete(4, Budget()).CopyVersions());
        Assert.Equal(256, refiner.StagingBytes);
        Assert.Equal(3, refiner.CompletedComparisons);
        Assert.NotEqual(nint.Zero, first.Device.NativePointer);
    }

    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void SkippedExpiredBusyAndReusedResultsNeverHoldTheBaseOrKeepStaleTiles()
    {
        if (!Enabled) return;
        using var device = Device();
        using var refiner = new D3D11NativeDamageRefiner(device);
        using var texture = Texture(device, new byte[257 * 259 * 4]);
        Assert.False(refiner.Begin(texture, Coarse(1), [0], Budget()));
        refiner.Complete(1, Budget());
        long sequence = 1;
        foreach (var denied in new[] { default, Budget() with { InputPending = true },
            Budget() with { BaseFrameBacklogged = true }, Budget() with { TransportCongested = true } })
        {
            Assert.False(refiner.Begin(texture, Coarse(++sequence), [0], denied));
            Assert.All(refiner.Complete(sequence, Budget()).CopyVersions(), version => Assert.Equal(sequence, version));
        }
        Assert.Equal(0, refiner.SubmittedComparisons);
        Assert.True(refiner.Begin(texture, Coarse(6), [0], Budget()));
        FinishGpu(device, refiner);
        // Already-computed evidence remains valid after the render deadline;
        // the renderer independently skips its optional draw in that case.
        Assert.Equal(new long[] { 5, 6, 6, 6, 6, 6, 6, 6, 6 }, refiner.Complete(6, default).CopyVersions());
        // The prior proof must not be re-used for a new surface/request.
        Assert.False(refiner.Begin(texture, Coarse(7), [1], default));
        Assert.All(refiner.Complete(7, Budget()).CopyVersions(), version => Assert.Equal(7, version));
        Assert.True(refiner.Begin(texture, Coarse(8), [1], Budget()));
        Assert.Throws<InvalidOperationException>(() => refiner.Begin(texture, Coarse(9), [0], Budget()));
        Assert.Throws<ArgumentException>(() => refiner.Complete(9, Budget()));
        FinishGpu(device, refiner);
        Assert.Equal(new long[] { 8, 7, 8, 8, 8, 8, 8, 8, 8 }, refiner.Complete(8, Budget()).CopyVersions());
        Assert.Equal(0, refiner.PendingCount);
        refiner.Dispose(); refiner.Dispose();
        Assert.Equal(0, refiner.StagingBytes);
        Assert.Throws<ObjectDisposedException>(() => refiner.Begin(texture, Coarse(9), [0], Budget()));
    }

    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void InvalidRegionsDeviceAndSequenceDoNotPoisonValidComparisons()
    {
        if (!Enabled) return;
        using var device = Device();
        using var other = Device();
        using var refiner = new D3D11NativeDamageRefiner(device);
        using var texture = Texture(device, new byte[257 * 259 * 4]);
        using var foreign = Texture(other, new byte[257 * 259 * 4]);
        Assert.Throws<ArgumentException>(() => refiner.Begin(foreign, Coarse(1), [0], Budget()));
        Assert.Throws<ArgumentException>(() => refiner.Begin(texture, Coarse(1), [-1], Budget()));
        Assert.Throws<ArgumentException>(() => refiner.Begin(texture, Coarse(1), [9], Budget()));
        Assert.Throws<ArgumentException>(() => refiner.Begin(texture, Coarse(1), [0, 0], Budget()));
        Assert.Throws<ArgumentOutOfRangeException>(() => refiner.Begin(texture, Coarse(1), new int[65], Budget()));
        Assert.Equal(0, refiner.PendingCount);
        refiner.Begin(texture, Coarse(1), [0], Budget()); refiner.Complete(1, Budget());
        Assert.Throws<ArgumentException>(() => refiner.Begin(texture, Coarse(1), [0], Budget()));
        Assert.True(refiner.Begin(texture, Coarse(2), [0], Budget()));
        FinishGpu(device, refiner);
        Assert.Equal(1, refiner.Complete(2, Budget())[0]);
        Assert.False(refiner.IsFailed, refiner.Failure);
        Assert.NotEqual(nint.Zero, texture.Device.NativePointer);
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("REMOTEDESK_RUN_HARDWARE_SMOKE") == "1";
    private static NativeDetailRenderBudget Budget() => new(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
    private static ID3D11Device Device() => D3D11.D3D11CreateDevice(DriverType.Hardware,
        DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
    private static NativeSurfaceManifest Coarse(long sequence) => new(sequence, new Size(257, 259), Enumerable.Repeat(sequence, 9).ToArray());
    private static ID3D11Texture2D Texture(ID3D11Device device, byte[] pixels)
    {
        var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 257, 259, 1, 1, BindFlags.ShaderResource));
        device.ImmediateContext.UpdateSubresource(pixels, texture, 0, 257 * 4);
        return texture;
    }
    private static void FinishGpu(ID3D11Device device, D3D11NativeDamageRefiner refiner)
    {
        // Test-only standalone wait. The implementation submits asynchronously;
        // Complete immediately discards an unavailable comparison.
        device.ImmediateContext.Flush();
        var watch = Stopwatch.StartNew();
        while (!refiner.TryPoll(Budget()) && watch.Elapsed < TimeSpan.FromSeconds(3))
        { Assert.False(refiner.IsFailed, refiner.Failure); Thread.Sleep(1); }
        Assert.True(refiner.TryPoll(Budget()), refiner.Failure ?? "GPU fixture timed out.");
    }
}
