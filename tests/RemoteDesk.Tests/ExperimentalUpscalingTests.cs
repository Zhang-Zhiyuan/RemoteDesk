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
    public void ShaderCompilesWithSystemCompiler(string entry, string profile) =>
        Assert.NotEmpty(D3D11ExperimentalUpscaler.Compile(entry, profile));

    [Fact]
    public void ToggleIsAdjacentReversibleAndDoesNotChangeGeometry()
    {
        using var client = new RemoteViewerClient();
        using var viewer = new RemoteViewerWindow(client, "upscale test", false, false, false, false, false, false);
        var button = viewer.ExperimentalUpscaleButtonForEntityTests;
        var scale = viewer.DisplayScaleButtonForEntityTests;
        Assert.False(viewer.ExperimentalUpscalingForEntityTests);
        Assert.Contains("关", button.Text);
        Assert.Same(scale.Parent, button.Parent);
        Assert.Equal(scale.Parent!.Controls.GetChildIndex(scale) + 1,
            button.Parent!.Controls.GetChildIndex(button));
        bool originalGeometry = viewer.AllowDisplayUpscalingForEntityTests;
        viewer.ToggleExperimentalUpscaling();
        Assert.True(viewer.ExperimentalUpscalingForEntityTests);
        Assert.Contains("开", button.Text);
        Assert.Equal(originalGeometry, viewer.AllowDisplayUpscalingForEntityTests);
        viewer.ToggleExperimentalUpscaling();
        Assert.False(viewer.ExperimentalUpscalingForEntityTests);
        Assert.Contains("关", button.Text);
        Assert.Equal(originalGeometry, viewer.AllowDisplayUpscalingForEntityTests);
    }

    [Theory]
    [Trait("Category", "HardwareSmoke")]
    [InlineData(ExperimentalUpscalingAlgorithm.CatmullRom)]
    [InlineData(ExperimentalUpscalingAlgorithm.Nis)]
    internal void HardwareUpscalingPreservesOrientationAndPixelsAcrossToggleResizeAndCrop(ExperimentalUpscalingAlgorithm algorithm)
    {
        if (Environment.GetEnvironmentVariable("REMOTEDESK_RUN_HARDWARE_SMOKE") != "1") return;
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
            foreach (int size in new[] { 96, 128, 224, 127 })
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
                        Assert.Equal(algorithm == ExperimentalUpscalingAlgorithm.Nis && size <= 128 ? "NIS" :
                            algorithm == ExperimentalUpscalingAlgorithm.Nis ? "双三次（NIS 限 1～2 倍）" : "双三次",
                            presenter.ExperimentalUpscalingDetail);
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
