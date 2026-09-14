using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class HardwareH264EncoderOptionsTests
{
    private static MediaFoundationD3D11H264EncoderOptions Valid => new(new(3840, 2160), new(1920, 1080), 60, 12_000_000);

    [Fact]
    public void NativeMayBeOddButNv12OutputMustBeEven()
    {
        Assert.Null(Valid.Validate());
        Assert.Null((Valid with { NativeSize = new(3839, 2159) }).Validate());
        Assert.NotNull((Valid with { OutputSize = new(1919, 1080) }).Validate());
        Assert.NotNull((Valid with { OutputSize = new(1920, 1079) }).Validate());
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(-1, 1080)]
    [InlineData(47, 1080)]
    [InlineData(8193, 1080)]
    [InlineData(8192, 8192)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(1920, 0)]
    public void InvalidNativeSurfaceIsRejected(int width, int height) =>
        Assert.NotNull((Valid with { NativeSize = new(width, height) }).Validate());

    [Theory]
    [InlineData(4098, 2160)]
    [InlineData(1920, 2306)]
    [InlineData(0, 1080)]
    [InlineData(1920, -1)]
    [InlineData(46, 48)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void InvalidOutputIsRejected(int width, int height) =>
        Assert.NotNull((Valid with { OutputSize = new(width, height) }).Validate());

    [Fact]
    public void EncodingDoesNotPretendUpscaledPixelsAreNativeDetail() =>
        Assert.NotNull((Valid with { NativeSize = new(1280, 720) }).Validate());

    [Theory]
    [InlineData(0, 12_000_000)]
    [InlineData(121, 12_000_000)]
    [InlineData(60, 99_999)]
    [InlineData(60, 200_000_001)]
    [InlineData(-1, -1)]
    public void InvalidRateIsRejected(int fps, int bitrate) =>
        Assert.NotNull((Valid with { FramesPerSecond = fps, BitrateBitsPerSecond = bitrate }).Validate());
}
