using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace RemoteDesk.Tests;

// Keep HWND creation, presentation and destruction on one owner thread. These
// bounded setup/cleanup waits are outside Present; the worker uses Task.Run and
// never dispatches back to xUnit or WinForms. The suspended-worker assertions
// specifically verify that presentation itself does not await preparation.
#pragma warning disable xUnit1031
public sealed class NativeDetailCompositorHardwareTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "HardwareSmoke")]
    public void NativeTilesRemainBoundToTheBaseAndRollbackAfterDrawFailure(bool nis)
    {
        if (Environment.GetEnvironmentVariable("REMOTEDESK_RUN_HARDWARE_SMOKE") != "1") return;
        using var window = new Form { ClientSize = new(128,128), ShowInTaskbar = false };
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        using var texture = device.CreateTexture2D(new Texture2DDescription(Format.NV12, 64, 64, 1, 1, BindFlags.Decoder));
        var nv12 = new byte[64 * 64 * 3 / 2]; Array.Fill(nv12, (byte)90, 0, 64 * 64); Array.Fill(nv12, (byte)128, 64 * 64, 64 * 64 / 2);
        device.ImmediateContext.UpdateSubresource(nv12, texture, 0, 64, (uint)nv12.Length);
        Assert.True(D3D11HwndVideoPresenter.TryCreate(device, new(window.Handle, 64, 64, 30,
            EnableEdgeEnhancement: false, EnableExperimentalUpscaling: nis, UpscalingAlgorithm: ExperimentalUpscalingAlgorithm.Nis),
            out var created, out var capability), capability.Detail);
        using var presenter = created!;
        Assert.True(presenter.Resize(128,128).IsSuccess);
        Rectangle baseImage = new(0,0,64,64), viewport = new(17,19,95,91);
        byte[] rgba = new byte[128 * 128 * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 201; rgba[i+1] = 87; rgba[i+2] = 17; rgba[i+3] = 255; }
        var tile = new NativeDetailTile(new(0,0,128,128), 1, rgba);
        var batch = new NativeDetailPresentation(1,1,1,11,new(128,128),viewport,[tile]);

        byte[] Render(NativeDetailPresentation? details, long? pts, NativeDetailRenderBudget? budget = null)
        {
            var result = presenter.Present(texture,0,baseImage,true,details,pts,budget ?? SpareBudget());
            Assert.True(result.IsSuccess, result.Detail);
            return Pixels(device, presenter);
        }
        byte[] baseline = Render(null,null);
        var empty = new NativeDetailPresentation(1,1,2,12,new(128,128),viewport,[]);
        Assert.Equal(baseline, Render(empty,12));
        Assert.Equal(0, presenter.NativeDetailAtlasBytes);
        Assert.Equal(baseline, Render(batch,11)); // Never lazily prepare on Present.
        Assert.False(presenter.NativeDetailActive);
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            presenter.NativeDetailPreparationForTests = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Test preparation was not released.");
            };
            Task<bool> preparation = presenter.PrepareNativeDetailsAsync();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.Same(preparation, presenter.PrepareNativeDetailsAsync());
                Assert.Equal(baseline, Render(batch,11));
                Assert.False(preparation.IsCompleted); // Render returned while preparation is suspended.
                presenter.DisableNativeDetails();
                release.Set();
                Assert.False(preparation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
                Assert.Equal(baseline, Render(batch,11)); // Late completion must not re-enable.
                Assert.Equal(0, presenter.NativeDetailAtlasBytes);
            }
            finally
            {
                release.Set(); preparation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                presenter.NativeDetailPreparationForTests = null;
            }
        }
        Prepare(presenter);
        Assert.Equal(baseline, Render(batch,11,default(NativeDetailRenderBudget)));
        Assert.False(presenter.NativeDetailActive);
        Assert.Equal(0, presenter.NativeDetailUploadedTiles);
        byte[] native = Render(batch,11);
        Assert.True(presenter.NativeDetailActive, presenter.NativeDetailStatus);
        Assert.Equal(1, presenter.NativeDetailUploadedTiles);
        for (int y=0; y<128; y++) for(int x=0; x<128; x++)
        {
            int i = (y*128+x)*4;
            Assert.Equal(viewport.Contains(x,y) ? new byte[] {17,87,201,255} : baseline.AsSpan(i,4).ToArray(), native.AsSpan(i,4).ToArray());
        }
        Assert.Equal(native, Render(batch,11));
        Assert.Equal(0, presenter.NativeDetailUploadedTiles);
        foreach (var budget in new[] { default(NativeDetailRenderBudget), new NativeDetailRenderBudget(1),
            SpareBudget() with { InputPending = true }, SpareBudget() with { BaseFrameBacklogged = true },
            SpareBudget() with { TransportCongested = true } })
        {
            Assert.Equal(baseline, Render(batch,11,budget));
            Assert.False(presenter.NativeDetailActive);
            Assert.Equal(0, presenter.NativeDetailUploadedTiles);
        }
        Assert.Equal(native, Render(batch,11));
        Assert.Equal(0, presenter.NativeDetailUploadedTiles); // Skipping a busy frame keeps useful cache.
        Assert.Equal(baseline, Render(batch,12)); Assert.False(presenter.NativeDetailActive);
        Assert.Equal(baseline, Render(batch,null)); Assert.False(presenter.NativeDetailActive);
        Assert.Equal(baseline, Render(empty,12));
        Assert.Equal(native, Render(batch,11));
        presenter.NativeDetailAfterDrawForTests = () => throw new InvalidOperationException("Injected after native Draw");
        Assert.Equal(baseline, Render(batch,11)); Assert.False(presenter.NativeDetailActive);
        Assert.Contains("回退", presenter.NativeDetailStatus);
        presenter.NativeDetailAfterDrawForTests = null;
        Assert.Equal(baseline, Render(batch,11)); // No repeated failed setup each frame.
        presenter.DisableNativeDetails();
        Assert.Equal(baseline, Render(null,null));
        Assert.Equal(0, presenter.NativeDetailAtlasBytes);
        Prepare(presenter);
        Assert.Equal(native, Render(batch,11)); // Explicit off permits a new opt-in attempt.

        NativeDetailPresentation FourTiles(long sequence, byte red, byte green, byte blue)
        {
            byte[] pixels = new byte[128 * 128 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            { pixels[i] = red; pixels[i+1] = green; pixels[i+2] = blue; pixels[i+3] = 255; }
            var tiles = new List<NativeDetailTile>();
            for (int y = 0; y < 256; y += 128) for (int x = 0; x < 256; x += 128)
                tiles.Add(new(new(x,y,128,128),sequence,pixels));
            return new(2,1,sequence,20+sequence,new(256,256),new(0,0,256,256),tiles);
        }
        var oldFour = FourTiles(1,31,201,77);
        Render(oldFour,21); Assert.Equal(2, presenter.NativeDetailUploadedTiles);
        byte[] allOld = Render(oldFour,21); Assert.Equal(2, presenter.NativeDetailUploadedTiles);
        for (int i = 0; i < allOld.Length; i += 4) Assert.Equal(new byte[] {77,201,31,255}, allOld.AsSpan(i,4).ToArray());
        var newFour = FourTiles(2,201,17,221);
        byte[] halfNew = Render(newFour,22); Assert.Equal(2, presenter.NativeDetailUploadedTiles);
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
        {
            int i = (y*128+x)*4;
            // The two deferred tiles must show the base NOW, not the old green
            // pixels. Invalidating only uploaded tiles would leave stale text.
            Assert.Equal(y < 64 ? new byte[] {221,17,201,255} : baseline.AsSpan(i,4).ToArray(), halfNew.AsSpan(i,4).ToArray());
        }
        byte[] allNew = Render(newFour,22); Assert.Equal(2, presenter.NativeDetailUploadedTiles);
        for (int i = 0; i < allNew.Length; i += 4) Assert.Equal(new byte[] {221,17,201,255}, allNew.AsSpan(i,4).ToArray());
        Assert.Equal(allNew, Render(newFour,22)); Assert.Equal(0, presenter.NativeDetailUploadedTiles);

        Assert.True(presenter.Resize(256,192).IsSuccess);
        Prepare(presenter);
        Assert.True(presenter.Present(texture,0,baseImage,false,batch,11,SpareBudget()).IsSuccess);
        Assert.True(presenter.NativeDetailActive);
        Assert.Equal(1, presenter.NativeDetailUploadedTiles); // Resize rebuilds renderer resources.
        Assert.True(presenter.SetScaleMode(D3D11HwndVideoScaleMode.Fill).IsSuccess);
        Assert.True(presenter.Present(texture,0,baseImage,false,batch,11,SpareBudget()).IsSuccess);
        Assert.Equal(0, presenter.NativeDetailUploadedTiles); // Geometry alone does not re-upload.
        Assert.True(presenter.Resize(128,128).IsSuccess);
        Prepare(presenter);
        Assert.True(presenter.SetScaleMode(D3D11HwndVideoScaleMode.Fit).IsSuccess);
        Assert.Equal(native, Render(batch,11));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "HardwareSmoke")]
    public void SlowPreparationDoesNotHoldSwapChainBuffersOrResurrectDisposedPresenter(bool dispose)
    {
        if (Environment.GetEnvironmentVariable("REMOTEDESK_RUN_HARDWARE_SMOKE") != "1") return;
        using var window = new Form { ClientSize = new(128,128), ShowInTaskbar = false };
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        Assert.True(D3D11HwndVideoPresenter.TryCreate(device, new(window.Handle,64,64,30), out var created, out var capability), capability.Detail);
        using var presenter = created!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        presenter.NativeDetailPreparationForTests = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Test preparation was not released.");
        };
        Task<bool> preparation = presenter.PrepareNativeDetailsAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (dispose) presenter.Dispose();
            else Assert.True(presenter.Resize(256,192).IsSuccess);
            Assert.False(preparation.IsCompleted);
            release.Set();
            Assert.False(preparation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Assert.Equal(0, presenter.NativeDetailAtlasBytes);
            presenter.NativeDetailPreparationForTests = null;
            if (dispose) Assert.False(presenter.PrepareNativeDetailsAsync().GetAwaiter().GetResult());
            else Prepare(presenter);
        }
        finally { release.Set(); preparation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "HardwareSmoke")]
    public void RapidOffOnAndResizeCoalesceIntoOnePreparation(bool finishDisabled)
    {
        if (Environment.GetEnvironmentVariable("REMOTEDESK_RUN_HARDWARE_SMOKE") != "1") return;
        using var window = new Form { ClientSize = new(128,128), ShowInTaskbar = false };
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        Assert.True(D3D11HwndVideoPresenter.TryCreate(device,new(window.Handle,64,64,30),out var created,out var capability),capability.Detail);
        using var presenter = created!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int workers = 0;
        presenter.NativeDetailPreparationForTests = () =>
        {
            Interlocked.Increment(ref workers); entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Test preparation was not released.");
        };
        var tasks = new List<Task<bool>> { presenter.PrepareNativeDetailsAsync() };
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 20; i++)
            {
                presenter.DisableNativeDetails();
                Assert.True(presenter.Resize(i % 2 == 0 ? 256 : 128,128).IsSuccess);
                tasks.Add(presenter.PrepareNativeDetailsAsync());
                Assert.Same(tasks[0],tasks[^1]);
            }
            if (finishDisabled) presenter.DisableNativeDetails();
            Assert.Equal(1,Volatile.Read(ref workers));
            release.Set();
            Assert.Equal(!finishDisabled,tasks[0].WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Assert.Equal(finishDisabled ? 0 : D3D11NativeDetailCompositor.AtlasBytes,presenter.NativeDetailAtlasBytes);
            presenter.NativeDetailPreparationForTests = null;
            if (!finishDisabled)
            {
                // The reused size-independent atlas must target the LAST
                // explicitly requested swap chain, not a disposed old view.
                using var texture = device.CreateTexture2D(new Texture2DDescription(Format.NV12,64,64,1,1,BindFlags.Decoder));
                byte[] nv12 = new byte[64*64*3/2]; Array.Fill(nv12,(byte)128);
                device.ImmediateContext.UpdateSubresource(nv12,texture,0,64,(uint)nv12.Length);
                var batch = new NativeDetailPresentation(1,1,1,1,new(128,128),new(0,0,128,128),
                    [new NativeDetailTile(new(0,0,128,128),1,new byte[128*128*4])]);
                Assert.True(presenter.Present(texture,0,new(0,0,64,64),false,batch,1,SpareBudget()).IsSuccess);
                Assert.True(presenter.NativeDetailActive,presenter.NativeDetailStatus);
            }
            presenter.DisableNativeDetails();
            Prepare(presenter); // A completed/cancelled worker cannot swallow a subsequent request.
        }
        finally
        {
            release.Set();
            foreach (var task in tasks) task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
    }

    private static NativeDetailRenderBudget SpareBudget() => new(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2);
    private static void Prepare(D3D11HwndVideoPresenter presenter) =>
        Assert.True(presenter.PrepareNativeDetailsAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(), presenter.NativeDetailStatus);

    private static byte[] Pixels(ID3D11Device device, D3D11HwndVideoPresenter presenter)
    {
        var source = (ID3D11Texture2D)typeof(D3D11HwndVideoPresenter).GetField("_validationSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter)!;
        var description = source.Description; description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None; description.CPUAccessFlags = CpuAccessFlags.Read;
        using var readback = device.CreateTexture2D(description);
        var context = device.ImmediateContext; context.CopyResource(readback, source);
        var mapped = context.Map(readback,0,MapMode.Read); byte[] bytes = new byte[128*128*4];
        try { for(int y=0; y<128; y++) Marshal.Copy(mapped.DataPointer+y*(int)mapped.RowPitch,bytes,y*128*4,128*4); }
        finally { context.Unmap(readback,0); }
        return bytes;
    }
}
#pragma warning restore xUnit1031
