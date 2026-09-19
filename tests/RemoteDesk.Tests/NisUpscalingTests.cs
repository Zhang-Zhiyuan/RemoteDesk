using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NisUpscalingTests
{
    [Fact]
    public void PinnedUpstreamComputeShaderCompiles() => Assert.NotEmpty(D3D11NisScaler.Compile());

    [Theory]
    [InlineData(1920, 1080, 2560, 1440, true)]
    [InlineData(1920, 1080, 3840, 2160, true)]
    [InlineData(1920, 1080, 3841, 2160, false)]
    [InlineData(1920, 1080, 1920, 1080, false)]
    [InlineData(1920, 1080, 1280, 1440, false)]
    [InlineData(0, 1080, 1280, 1440, false)]
    [InlineData(1920, 0, 1280, 1440, false)]
    [InlineData(100, 100, -1, 200, false)]
    [InlineData(64, 64, 127, 127, true)]
    public void ScaleGateEnforcesUpstreamLimits(int sw, int sh, int dw, int dh, bool expected) =>
        Assert.Equal(expected, D3D11NisScaler.Supports(new(sw, sh), new(dw, dh)));

    [Theory]
    [InlineData("coef_scale", 1)]
    [InlineData("coef_usm", 0)]
    public void OriginalCoefficientPhasesHaveCorrectSizeAndNormalization(string name, int expectedSum)
    {
        float[] values = D3D11NisScaler.ReadCoefficients(name);
        Assert.Equal(512, values.Length);
        for (int phase = 0; phase < 64; phase++)
        {
            Assert.InRange(values.Skip(phase * 8).Take(8).Sum(), expectedSum - 0.003f, expectedSum + 0.003f);
            Assert.Equal(0, values[phase * 8 + 6]);
            Assert.Equal(0, values[phase * 8 + 7]);
        }
    }

    [Fact]
    public void ConstantsMatchSdrCbufferAndSharpnessClamps()
    {
        float[] c = D3D11NisScaler.CreateConstants(new(1920, 1080), new(3840, 2160));
        Assert.Equal(28, c.Length);
        Assert.Equal(0.5f, c[12]); Assert.Equal(0.5f, c[13]);
        Assert.Equal(1920, BitConverter.SingleToInt32Bits(c[20]));
        Assert.Equal(1080, BitConverter.SingleToInt32Bits(c[21]));
        Assert.Equal(3840, BitConverter.SingleToInt32Bits(c[24]));
        Assert.Equal(2160, BitConverter.SingleToInt32Bits(c[25]));
        Assert.InRange(c[8], 0.03999f, 0.04001f);
        Assert.InRange(c[9], 0.61499f, 0.61501f);
        Assert.Equal(0.1f, c[10]);
        Assert.Equal(D3D11NisScaler.CreateConstants(new(64, 64), new(128, 128), 0),
            D3D11NisScaler.CreateConstants(new(64, 64), new(128, 128), -1));
        Assert.Equal(D3D11NisScaler.CreateConstants(new(64, 64), new(128, 128), 1),
            D3D11NisScaler.CreateConstants(new(64, 64), new(128, 128), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D11NisScaler.CreateConstants(new(64, 64), new(128, 128), float.NaN));
    }

    [Fact]
    public void AlgorithmSelectionDoesNotEnableExperimentOrChangeInputGeometry()
    {
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        using var viewer = new RemoteViewerWindow(client, "algorithm test", false, false, false, false, false, false);
        bool geometry = viewer.AllowDisplayUpscalingForEntityTests;
        viewer.SelectExperimentalUpscalingAlgorithm(ExperimentalUpscalingAlgorithm.CatmullRom);
        Assert.False(viewer.ExperimentalUpscalingForEntityTests);
        viewer.ToggleExperimentalUpscaling();
        viewer.SelectExperimentalUpscalingAlgorithm(ExperimentalUpscalingAlgorithm.Nis);
        Assert.True(viewer.ExperimentalUpscalingForEntityTests);
        Assert.Equal(geometry, viewer.AllowDisplayUpscalingForEntityTests);
        Assert.NotNull(viewer.ExperimentalUpscaleButtonForEntityTests.ContextMenuStrip);
        viewer.ToggleExperimentalUpscaling();
        Assert.False(viewer.ExperimentalUpscalingForEntityTests);
    }

    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void RgbChannelsAndOddEdgesSurviveNisDispatchAndResize()
    {
        if (Environment.GetEnvironmentVariable("REMOTEDESK_RUN_HARDWARE_SMOKE") != "1") return;
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        var context = device.ImmediateContext;
        using var input = device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, 64, 64, 1, 1, BindFlags.ShaderResource));
        using var view = device.CreateShaderResourceView(input);
        byte[] pixels = new byte[64 * 64 * 4];
        byte[][] colors = [[255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 255, 255]];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                Array.Copy(colors[(y / 32) * 2 + x / 32], 0, pixels, (y * 64 + x) * 4, 4);
        context.UpdateSubresource(pixels, input, 0, 64 * 4, (uint)pixels.Length);
        using var nis = new D3D11NisScaler(device, context);
        var deviations = new List<string>();
        foreach (int dimension in new[] { 96, 127, 128, 97 })
        {
            nis.Render(view, new(64, 64), new(dimension, dimension));
            var texture = (ID3D11Texture2D)typeof(D3D11NisScaler).GetField("_output", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nis)!;
            var description = texture.Description; description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None; description.CPUAccessFlags = CpuAccessFlags.Read;
            using var readback = device.CreateTexture2D(description);
            context.CopyResource(readback, texture);
            var mapped = context.Map(readback, 0, MapMode.Read);
            try
            {
                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    int x = quadrant % 2 == 0 ? 0 : dimension - 1, y = quadrant / 2 == 0 ? 0 : dimension - 1;
                    int offset = y * (int)mapped.RowPitch + x * 4;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int actual = Marshal.ReadByte(mapped.DataPointer, offset + channel);
                        int expected = colors[quadrant][channel];
                        if (Math.Abs(actual - expected) > 2)
                            deviations.Add($"size={dimension} quadrant={quadrant} channel={channel} expected={expected} actual={actual}");
                    }
                }
            }
            finally { context.Unmap(readback, 0); }
        }
        Assert.True(deviations.Count == 0, string.Join("; ", deviations));
    }
}
