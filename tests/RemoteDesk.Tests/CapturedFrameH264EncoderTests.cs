using System.Diagnostics;
using System.Drawing.Imaging;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class CapturedFrameH264EncoderTests
{
    [Theory]
    [InlineData(3840, 2160, 100, 50)]
    [InlineData(2160, 3840, 100, 50)]
    [InlineData(3840, 2160, 67, 50)]
    [InlineData(2560, 1440, 100, 75)]
    [InlineData(1920, 1080, 100, 100)]
    public void CaptureScalesBeforePrivilegedIpcToAvoidFullSizeImageCopies(int width, int height, int requested, int expected)
    {
        Assert.Equal(expected, CapturedFrameH264Encoder.ResolveCaptureScale(new Rectangle(0, 0, width, height), requested, true));
        Assert.Equal(requested, CapturedFrameH264Encoder.ResolveCaptureScale(new Rectangle(0, 0, width, height), requested, false));
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(2160, 3840, 1080, 1920)]
    [InlineData(1365, 767, 1364, 766)]
    [InlineData(1280, 720, 1280, 720)]
    public void FallbackFitsBothOrientationsWithoutUpscaling(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new Size(expectedWidth, expectedHeight),
            CapturedFrameH264Encoder.ResolveOutputSize(new Size(width, height), adaptive: true));
        Assert.Equal(new Size(width & ~1, height & ~1),
            CapturedFrameH264Encoder.ResolveOutputSize(new Size(width, height), adaptive: false));
    }

    [CapturedEncoderFact]
    public async Task RealEncoderReturnsFirstAndChangedIndependentFramesWithoutFollowingInput()
    {
        // CI can opt in when its hash-pinned companion is installed. The normal
        // unit suite still verifies dimensions and codec migration without it.
        string? path = Environment.GetEnvironmentVariable("REMOTEDESK_TEST_FFMPEG");
        Assert.False(string.IsNullOrEmpty(path));
        var size = new Size(1280, 720);
        using var encoder = new CapturedFrameH264Encoder(path!, size, 15);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var bitmap = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(bitmap);
        byte[]? previous = null;
        foreach (Color color in new[] { Color.White, Color.Blue, Color.Red })
        {
            graphics.Clear(color);
            graphics.DrawString("RemoteDesk H.264 123 中文", SystemFonts.DefaultFont, Brushes.Black, 20, 20);
            using var image = new MemoryStream();
            bitmap.Save(image, ImageFormat.Jpeg);
            long started = Stopwatch.GetTimestamp();
            using AnnexBH264AccessUnit encoded = await encoder.EncodeAsync(image.ToArray(), deadline.Token);
            Assert.True(encoded.IsIdr && encoded.HasSps && encoded.HasPps);
            Assert.True(encoded.Bytes.Length > 100);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3));
            Assert.False(previous?.AsSpan().SequenceEqual(encoded.Bytes.Span) ?? false);
            previous = encoded.Bytes.ToArray();
        }
    }

    [Fact]
    public void ArgumentsDisableFrameReorderingAndBindOnlyLoopback()
    {
        string command = string.Join(' ', CapturedFrameH264Encoder.BuildArguments(new Size(1920, 1080), 10, 12345));
        Assert.Contains("-tune zerolatency", command);
        Assert.Contains("-g 1 -bf 0", command);
        Assert.Contains("rtp://127.0.0.1:12345", command);
        Assert.DoesNotContain("jpeg", command);
    }
}

internal sealed class CapturedEncoderFactAttribute : FactAttribute
{
    public CapturedEncoderFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("REMOTEDESK_TEST_FFMPEG")))
            Skip = "Set REMOTEDESK_TEST_FFMPEG to the installed companion to run real encoding.";
    }
}
