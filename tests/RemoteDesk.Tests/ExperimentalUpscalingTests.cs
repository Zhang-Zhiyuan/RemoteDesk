using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ExperimentalUpscalingTests
{
    [Fact]
    public void ExperimentIsOptInAndDoesNotReplaceLegacySettings()
    {
        var options = new D3D11HwndVideoPresenterOptions((nint)1, 1920, 1080, 30);
        Assert.False(options.EnableExperimentalUpscaling);
        Assert.True(options.EnableEdgeEnhancement);
        Assert.Null((options with { EnableExperimentalUpscaling = true }).Validate());
    }

    [Theory]
    [InlineData(false, 100, 100, 200, 200, false)]
    [InlineData(true, 100, 100, 200, 200, true)]
    [InlineData(true, 100, 100, 133, 133, true)]
    [InlineData(true, 100, 100, 100, 100, false)]
    [InlineData(true, 100, 100, 50, 50, false)]
    [InlineData(true, 0, 100, 200, 200, false)]
    [InlineData(true, 100, 100, 0, 200, false)]
    public void OnlyUpscalingUsesExperimentalShader(bool enabled, int sw, int sh, int dw, int dh, bool expected) =>
        Assert.Equal(expected, D3D11ExperimentalUpscaler.ShouldApply(enabled, new(sw, sh), new(dw, dh)));

    [Theory]
    [InlineData("vs", "vs_4_0")]
    [InlineData("ps", "ps_4_0")]
    [InlineData("psCopy", "ps_4_0")]
    [InlineData("psGrid", "ps_4_0")]
    public void ShaderCompilesWithSystemCompiler(string entry, string profile) =>
        Assert.NotEmpty(D3D11ExperimentalUpscaler.Compile(entry, profile));

    [Fact]
    public void DisplayMenuDefaultsToTextAndCanRestoreSystemScalingWithoutChangingGeometry()
    {
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        using var viewer = new RemoteViewerWindow(client, "upscale test", false, false, false, false, false, false);
        Assert.Equal("画面", viewer.DisplayScaleButtonForEntityTests.Text);
        var filters = viewer.DisplayMenuForEntityTests.Items.OfType<ToolStripMenuItem>()
            .Single(item => item.Text == "放大效果").DropDownItems.OfType<ToolStripMenuItem>().ToArray();
        Assert.Equal("文字优先", Assert.Single(filters, item => item.Checked).Text);
        filters.Single(item => item.Text == "系统缩放").PerformClick();
        Assert.False(viewer.ExperimentalUpscalingForEntityTests);
        Assert.True(viewer.AllowDisplayUpscalingForEntityTests);
        filters.Single(item => item.Text == "文字优先").PerformClick();
        Assert.True(viewer.ExperimentalUpscalingForEntityTests);
        Assert.True(viewer.AllowDisplayUpscalingForEntityTests);
    }

    [Theory]
    [Trait("Category", "HardwareSmoke")]
    [InlineData(ExperimentalUpscalingAlgorithm.CatmullRom)]
    [InlineData(ExperimentalUpscalingAlgorithm.Nis)]
    [InlineData(ExperimentalUpscalingAlgorithm.TextStrokes)]
    [InlineData(ExperimentalUpscalingAlgorithm.PixelGrid)]
    internal async Task HardwareUpscalingPreservesOrientationAndPixelsAcrossToggleResizeAndCrop(ExperimentalUpscalingAlgorithm algorithm)
    {
        if (Environment.GetEnvironmentVariable("REMOTEDESK_RUN_HARDWARE_SMOKE") != "1") return;
        await D3D11ExperimentalUpscaler.PrepareShadersAsync(algorithm);
        using var window = new Form { ClientSize = new Size(128, 128), ShowInTaskbar = false };
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        Assert.True(D3D11HwndVideoPresenter.TryCreate(device,
            new(window.Handle, 64, 64, 30, EnableExperimentalUpscaling: true, UpscalingAlgorithm: algorithm), out var presenter, out var capability), capability.Detail);
        Assert.NotNull(presenter);
        using (presenter)
        using (var texture = device.CreateTexture2D(new Texture2DDescription(
            Format.NV12, 64, 64, 1, 1, BindFlags.Decoder)))
        {
            byte[] nv12 = new byte[64 * 64 * 3 / 2];
            byte[] luma = [16, 64, 128, 235];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++) nv12[y * 64 + x] = luma[(y / 32) * 2 + x / 32];
            Array.Fill(nv12, (byte)128, 64 * 64, 64 * 32);
            var context = device.ImmediateContext;
            context.UpdateSubresource(nv12, texture, 0, 64, (uint)nv12.Length);
            var failures = new List<string>();
            presenter.ExperimentalUpscalingFailed += (_, reason) => failures.Add(reason);
            foreach (int size in new[] { 96, 111, 112, 128, 224, 127 })
            {
                Assert.True(presenter.Resize(size, size).IsSuccess);
                foreach (bool enabled in new[] { true, false, true, false })
                {
                    presenter.SetExperimentalUpscaling(enabled);
                    var result = presenter.Present(texture, 0, new Rectangle(0, 0, 64, 64), captureValidation: true);
                    Assert.True(result.IsSuccess || result.Status == D3D11HwndVideoPresenterStatus.Occluded, result.Detail);
                    Assert.Empty(failures);
                    Assert.Equal(enabled, presenter.ExperimentalUpscalingActive);
                    if (enabled)
                        Assert.Equal(algorithm switch
                        {
                            ExperimentalUpscalingAlgorithm.PixelGrid => "原像素",
                            ExperimentalUpscalingAlgorithm.Nis when size <= 128 => "NIS",
                            ExperimentalUpscalingAlgorithm.Nis => "双三次（NIS 限 1～2 倍）",
                            ExperimentalUpscalingAlgorithm.TextStrokes when size >= 112 => "文字笔画",
                            _ => "双三次"
                        }, presenter.ExperimentalUpscalingDetail);
                    // Inspect the copy made BEFORE flip-model Present, not a
                    // rotated/uninitialized back buffer. Test-only GPU readback.
                    var source = (ID3D11Texture2D?)typeof(D3D11HwndVideoPresenter).GetField(
                        "_validationSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter);
                    Assert.NotNull(source);
                    var description = source.Description;
                    description.Usage = ResourceUsage.Staging;
                    description.BindFlags = BindFlags.None;
                    description.CPUAccessFlags = CpuAccessFlags.Read;
                    using var readback = device.CreateTexture2D(description);
                    context.CopyResource(readback, source);
                    var mapped = context.Map(readback, 0, MapMode.Read);
                    try
                    {
                        int previous = -1;
                        for (int quadrant = 0; quadrant < 4; quadrant++)
                        {
                            int x = quadrant % 2 == 0 ? 0 : size - 1;
                            int y = quadrant / 2 == 0 ? 0 : size - 1;
                            int offset = checked((int)mapped.RowPitch) * y + x * 4;
                            int blue = Marshal.ReadByte(mapped.DataPointer, offset);
                            int green = Marshal.ReadByte(mapped.DataPointer, offset + 1);
                            int red = Marshal.ReadByte(mapped.DataPointer, offset + 2);
                            Assert.InRange(Math.Abs(red - green), 0, 3);
                            Assert.InRange(Math.Abs(red - blue), 0, 3);
                            Assert.Equal(255, Marshal.ReadByte(mapped.DataPointer, offset + 3));
                            Assert.True(red > previous, $"Blank/flipped output: size={size}, enabled={enabled}, quadrant={quadrant}, red={red}, previous={previous}");
                            previous = red;
                        }
                        if (enabled && algorithm == ExperimentalUpscalingAlgorithm.PixelGrid)
                        {
                            // A vertical texel boundary halfway through an odd-sized
                            // viewport covers half of each colour. At even sizes its
                            // two neighbours remain intact, including at 1.5x zoom.
                            int offset = checked((int)mapped.RowPitch) * (size / 4);
                            int Read(int x) => Marshal.ReadByte(mapped.DataPointer, offset + x * 4 + 2);
                            int left = Read(0), right = Read(size - 1), middle = size / 2;
                            Assert.InRange(Math.Abs(Read(middle - 1) - left), 0, 1);
                            Assert.InRange(Math.Abs(Read(middle) - (size % 2 == 0 ? right : (left + right) / 2)), 0, 1);
                            Assert.InRange(Math.Abs(Read(middle + 1) - right), 0, 1);
                        }
                    }
                    finally { context.Unmap(readback, 0); }
                }
            }
            presenter.SetExperimentalUpscaling(true);
            // A new visible crop recreates only the optional native-size target.
            presenter.Present(texture, 0, new Rectangle(16, 16, 32, 32), captureValidation: true);
            Assert.Empty(failures);
            Assert.True(presenter.ExperimentalUpscalingActive);
            Assert.True(presenter.Resize(32, 32).IsSuccess);
            presenter.Present(texture, 0, new Rectangle(0, 0, 64, 64));
            Assert.False(presenter.ExperimentalUpscalingActive); // No sharpening on shrink.

            Assert.True(presenter.Resize(128, 128).IsSuccess);
            presenter.Present(texture, 0, new Rectangle(0, 0, 64, 64));
            Assert.True(presenter.ExperimentalUpscalingActive);
            var scaler = typeof(D3D11HwndVideoPresenter).GetField("_experimentalUpscaler",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter)!;
            if (algorithm == ExperimentalUpscalingAlgorithm.Nis)
            {
                // Simulate a sticky NIS-only failure; bicubic must continue
                // without turning off the experiment or reinitializing decode.
                typeof(D3D11ExperimentalUpscaler).GetField("_nisFailed",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scaler, true);
                presenter.Present(texture, 0, new Rectangle(0, 0, 64, 64));
                Assert.True(presenter.ExperimentalUpscalingActive);
                Assert.Equal("双三次（NIS 回退）", presenter.ExperimentalUpscalingDetail);
                Assert.Empty(failures);
            }
            // Test-only managed fault, before any native call: verify the SAME
            // frame falls back and a later frame does not retry a broken path.
            typeof(D3D11ExperimentalUpscaler).GetField("_sourceSize",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scaler, Size.Empty);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var fallback = presenter.Present(texture, 0, new Rectangle(0, 0, 64, 64), captureValidation: true);
                Assert.True(fallback.IsSuccess || fallback.Status == D3D11HwndVideoPresenterStatus.Occluded, fallback.Detail);
                Assert.False(presenter.ExperimentalUpscalingActive);
            }
            Assert.Single(failures);
            presenter.SetExperimentalUpscaling(true);
            presenter.Present(texture, 0, new Rectangle(0, 0, 64, 64));
            Assert.True(presenter.ExperimentalUpscalingActive);
        }
    }
}
