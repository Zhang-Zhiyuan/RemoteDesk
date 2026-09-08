using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class H264QueuePolicyTests
{
    [Fact]
    public void KeyFrameRequestMinIntervalFavorsFastH264Recovery()
    {
        Assert.Equal(550, RemoteViewerWindow.H264KeyFrameRequestMinIntervalMs);
        Assert.Equal(4, RemoteViewerWindow.MaxQueuedH264Frames);
    }

    [Fact]
    public void FindH264QueueStartIndexPrefersRecoveryFrameBeforeRetainedTail()
    {
        RemoteFrame[] frames = CreateFrames(15);
        frames[7] = CreateFrame(RemoteFrameFlags.KeyFrame);
        frames[8] = CreateFrame(RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig);

        int startIndex = RemoteViewerWindow.FindH264QueueStartIndex(
            frames,
            maxQueuedFrames: 12,
            out bool startsAtRecoveryFrame);

        Assert.Equal(8, startIndex);
        Assert.True(startsAtRecoveryFrame);
    }

    [Fact]
    public void FindH264QueueStartIndexStartsAtLatestRecoveryFrameInsideTail()
    {
        RemoteFrame[] frames = CreateFrames(15);
        frames[10] = CreateFrame(RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig);

        int startIndex = RemoteViewerWindow.FindH264QueueStartIndex(
            frames,
            maxQueuedFrames: 12,
            out bool startsAtRecoveryFrame);

        Assert.Equal(10, startIndex);
        Assert.True(startsAtRecoveryFrame);
    }

    [Fact]
    public void FindH264QueueStartIndexFallsBackToKeyFrameBeforeRetainedTail()
    {
        RemoteFrame[] frames = CreateFrames(15);
        frames[8] = CreateFrame(RemoteFrameFlags.KeyFrame);

        int startIndex = RemoteViewerWindow.FindH264QueueStartIndex(
            frames,
            maxQueuedFrames: 12,
            out bool startsAtRecoveryFrame);

        Assert.Equal(8, startIndex);
        Assert.True(startsAtRecoveryFrame);
    }

    [Fact]
    public void FindH264QueueStartIndexDropsOrphanedTailWhenNoRecoveryFrameExists()
    {
        RemoteFrame[] frames = CreateFrames(15);

        int startIndex = RemoteViewerWindow.FindH264QueueStartIndex(
            frames,
            maxQueuedFrames: 12,
            out bool startsAtRecoveryFrame);

        Assert.Equal(frames.Length, startIndex);
        Assert.False(startsAtRecoveryFrame);
    }

    [Fact]
    public void IncomingRecoveryPreservesHealthyGop2PairAndClearsOnlyFullBacklog()
    {
        RemoteFrame recovery = CreateFrame(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig);

        Assert.False(RemoteViewerWindow.ShouldClearH264QueueForIncomingFrame(
            recovery,
            queuedFrameCount: 1,
            samplesAreAllIndependent: false));

        Assert.True(RemoteViewerWindow.ShouldClearH264QueueForIncomingFrame(
            recovery,
            RemoteViewerWindow.MaxQueuedH264Frames,
            samplesAreAllIndependent: false));

        Assert.False(RemoteViewerWindow.ShouldClearH264QueueForIncomingFrame(
            CreateFrame(RemoteFrameFlags.KeyFrame),
            RemoteViewerWindow.MaxQueuedH264Frames,
            samplesAreAllIndependent: false));

        Assert.False(
            RemoteViewerWindow.ShouldClearH264QueueForIncomingFrame(
                new RemoteFrame(
                    1280,
                    720,
                    RemoteFrameEncoding.Jpeg,
                    RemoteFrameFlags.KeyFrame |
                        RemoteFrameFlags.CodecConfig,
                    [0xFF, 0xD8, 0xFF, 0xD9],
                    0,
                    4,
                    0,
                    0),
                RemoteViewerWindow.MaxQueuedH264Frames,
                samplesAreAllIndependent: false));
    }

    [Fact]
    public void IncomingIndependentRecoveryReplacesAnyQueuedFrame()
    {
        RemoteFrame recovery = CreateFrame(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig);

        Assert.False(
            RemoteViewerWindow.ShouldClearH264QueueForIncomingFrame(
                recovery,
                queuedFrameCount: 0,
                samplesAreAllIndependent: true));
        Assert.True(
            RemoteViewerWindow.ShouldClearH264QueueForIncomingFrame(
                recovery,
                queuedFrameCount: 1,
                samplesAreAllIndependent: true));
    }

    [Fact]
    public void DecoderSizeTracksDecoderInputRatherThanDisplayedUiSize()
    {
        RemoteFrame sameSizeRecovery =
            CreateFrame(
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig);
        RemoteFrame changedSizeRecovery =
            CreateFrame(
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig,
                width: 3840,
                height: 2160);
        RemoteFrame changedSizeDependent =
            CreateFrame(
                RemoteFrameFlags.None,
                width: 3840,
                height: 2160);

        Assert.False(
            RemoteViewerWindow.ShouldResetH264DecoderForFrame(
                Size.Empty,
                sameSizeRecovery));
        Assert.False(
            RemoteViewerWindow.ShouldResetH264DecoderForFrame(
                new Size(1280, 720),
                sameSizeRecovery));
        Assert.True(
            RemoteViewerWindow.ShouldResetH264DecoderForFrame(
                new Size(1280, 720),
                changedSizeRecovery));
        Assert.False(
            RemoteViewerWindow.ShouldResetH264DecoderForFrame(
                new Size(1280, 720),
                changedSizeDependent));
    }

    private static RemoteFrame[] CreateFrames(int count)
    {
        return Enumerable
            .Range(0, count)
            .Select(_ => CreateFrame(RemoteFrameFlags.None))
            .ToArray();
    }

    private static RemoteFrame CreateFrame(
        RemoteFrameFlags flags,
        int width = 1280,
        int height = 720)
    {
        return new RemoteFrame(
            width,
            height,
            RemoteFrameEncoding.H264AnnexB,
            flags,
            [0, 0, 0, 1, 9],
            0,
            5,
            0,
            0);
    }
}
