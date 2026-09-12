using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class UnchangedJpegFrameGateTests
{
    private static readonly Size FrameSize = new(2560, 1440);

    [Theory]
    [InlineData((int)RemoteVideoCodecs.Jpeg, (int)RemoteDeviceCapabilities.None, true)]
    [InlineData((int)(RemoteVideoCodecs.H264AnnexB | RemoteVideoCodecs.Jpeg), (int)RemoteDeviceCapabilities.None, true)]
    [InlineData((int)RemoteVideoCodecs.H264AnnexB, (int)RemoteDeviceCapabilities.HighQualityJpeg, true)]
    [InlineData((int)RemoteVideoCodecs.H264AnnexB, (int)RemoteDeviceCapabilities.None, false)]
    [InlineData((int)RemoteVideoCodecs.None, (int)RemoteDeviceCapabilities.HighQualityJpeg, false)]
    [InlineData((int)RemoteVideoCodecs.H264AnnexB, (int)RemoteDeviceCapabilities.DeviceIdentity, false)]
    public void LegacyJpegCapabilityEnablesSecureRecoveryButNeverInventsDecoderSupport(
        int codecs, int capabilities, bool expected)
    {
        Assert.Equal(expected, RemoteHostServer.SupportsSecureDesktopJpegRecovery(
            (RemoteVideoCodecs)codecs, (RemoteDeviceCapabilities)capabilities));
    }

    [Fact]
    public void FirstFrameAndEveryContentChangeAreImmediate()
    {
        var gate = new UnchangedJpegFrameGate();
        Assert.True(gate.ShouldSend([1, 2, 3], FrameSize, 0, 100));
        gate.MarkSent(100);
        Assert.False(gate.ShouldSend([1, 2, 3], FrameSize, 0, 101));
        Assert.True(gate.ShouldSend([1, 2, 4], FrameSize, 0, 102));
        gate.MarkSent(102);
        Assert.True(gate.ShouldSend([1, 2, 3], FrameSize, 0, 103));
    }

    [Fact]
    public void IdenticalCaptureDoesNotExtendTheRefreshDeadline()
    {
        var gate = new UnchangedJpegFrameGate();
        Assert.True(gate.ShouldSend([1], FrameSize, 0, 0));
        gate.MarkSent(0);
        for (long now = 100; now < UnchangedJpegFrameGate.RefreshIntervalMilliseconds; now += 100)
            Assert.False(gate.ShouldSend([1], FrameSize, 0, now));
        Assert.True(gate.ShouldSend([1], FrameSize, 0, UnchangedJpegFrameGate.RefreshIntervalMilliseconds));
    }

    [Fact]
    public void RejectedPublicationDoesNotSuppressTheNextAttempt()
    {
        var gate = new UnchangedJpegFrameGate();
        Assert.True(gate.ShouldSend([1], FrameSize, 0, 0));
        Assert.True(gate.ShouldSend([1], FrameSize, 0, 1));
        gate.MarkSent(1);
        Assert.True(gate.ShouldSend([2], FrameSize, 0, 2));
        Assert.True(gate.ShouldSend([2], FrameSize, 0, 3));
    }

    [Fact]
    public void TargetGenerationAndSizeChangesRequireFreshFrames()
    {
        var gate = new UnchangedJpegFrameGate();
        Assert.True(gate.ShouldSend([1], FrameSize, 0, 0));
        gate.MarkSent(0);
        Assert.True(gate.ShouldSend([1], FrameSize, 1, 1));
        gate.MarkSent(1);
        Assert.True(gate.ShouldSend([1], new Size(1920, 1080), 1, 2));
    }

    [Fact]
    public void SourceBufferReuseCannotChangeLastPublishedFingerprint()
    {
        var gate = new UnchangedJpegFrameGate();
        byte[] capture = [1, 2, 3];
        Assert.True(gate.ShouldSend(capture, FrameSize, 0, 0));
        gate.MarkSent(0);
        capture[0] = 4;
        Assert.True(gate.ShouldSend(capture, FrameSize, 0, 1));
        Assert.False(gate.ShouldSend([1, 2, 3], FrameSize, 0, 2));
    }

    [Fact]
    public void ResetAndClockDiscontinuityNeverHideAFrame()
    {
        var gate = new UnchangedJpegFrameGate();
        Assert.True(gate.ShouldSend([1], FrameSize, 0, 100));
        gate.MarkSent(100);
        Assert.True(gate.ShouldSend([1], FrameSize, 0, 99));
        gate.Reset();
        Assert.True(gate.ShouldSend([1], FrameSize, 0, 101));
    }

    [Fact]
    public void CaptureRecoveryResendsUnchangedPictureBeforeRefreshDeadline()
    {
        var gate = new UnchangedJpegFrameGate();
        Assert.True(gate.ShouldSend([1, 2, 3], FrameSize, 0, 0));
        gate.MarkSent(0);
        Assert.False(gate.ShouldSend([1, 2, 3], FrameSize, 0, 100));
        gate.Reset(); // Capture unavailable / viewer surface invalidated.
        Assert.True(gate.ShouldSend([1, 2, 3], FrameSize, 0, 200));
        gate.MarkSent(200);
        Assert.False(gate.ShouldSend([1, 2, 3], FrameSize, 0, 300));
    }

    [Fact]
    public void QuietSourceDoesNotLowerQualityResolutionOrTargetFps()
    {
        var adaptive = new RemoteHostServer.AdaptiveCaptureController(30, 90, 100, true);
        for (int i = 0; i < 12; i++)
            Assert.Null(adaptive.Update(0.2, 1000, 900, sourceWasIdle: true));
        Assert.Equal(30, adaptive.CurrentFps);
        Assert.Equal(90, adaptive.CurrentQuality);
        Assert.Equal(100, adaptive.CurrentScalePercent);
        Assert.NotNull(adaptive.Update(1, 1000, 900));
        Assert.True(adaptive.CurrentFps < 30);
    }
}
