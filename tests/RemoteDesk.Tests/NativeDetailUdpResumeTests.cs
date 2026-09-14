using System.Net;
using System.Reflection;
using RemoteDesk.NativeDetail;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeDetailUdpResumeTests
{
    [Fact]
    public async Task ResumeNeedsBothPeersAndRejectsLateFramesAndReplayedBoundaries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const LowLatencyVideoFeatures features = LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.AuthenticatedHeartbeat | LowLatencyVideoFeatures.UdpMouseInput;
        int published = 0;
        var publicationGate = new object();
        await using var host = new LowLatencyVideoHostTransport(_ => { }, timeout.Token);
        var offer = Assert.IsType<LowLatencyVideoOffer>(host.TryCreateOffer(IPAddress.Loopback, features));
        var viewerOffer = RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer)).LowLatencyVideoOffer!;
        host.MarkOfferSent(); host.ClearOfferSecrets();
        await using var viewer = new LowLatencyVideoViewerTransport(viewerOffer, IPAddress.Loopback,
            bytes =>
            {
                var control = RemoteMessageCodec.DecodeControl(bytes);
                if (control.Kind == RemoteControlKind.LowLatencyVideoReady)
                    Assert.True(host.TryMarkReady(control.LowLatencyVideoChannelId, control.LowLatencyVideoEpoch));
                return Task.FromResult(true);
            }, _ => { Assert.True(Monitor.IsEntered(publicationGate)); Interlocked.Increment(ref published); },
            _ => { }, timeout.Token, features, framePublicationGate: publicationGate);
        async Task Until(Func<bool> ready)
        { while (!ready()) await Task.Delay(5, timeout.Token); }
        await Until(() => host.IsRouteActive);
        Assert.True(host.TryQueueJpegFrame(1, 1, 0, 0, new byte[] { 1 }));
        await Until(() => Volatile.Read(ref published) == 1);
        await host.StopVideoForCaptureTargetChangeAsync();
        viewer.AcknowledgeStopped(viewerOffer.ChannelId, viewerOffer.Epoch,
            LowLatencyVideoFallbackReasons.PreserveUdpInput, allowAuthenticatedHostInitiatedPreserve: true);
        var context = new DetailContext(1, 2, 128, 128);
        var resume = Assert.IsType<NativeDetailUdpResume>(host.PrepareNativeResume(context));
        Assert.Equal(resume, NativeDetailSessionProtocol.DecodeResume(NativeDetailSessionProtocol.EncodeResume(resume)));
        Assert.False(viewer.ResumeAfterNativeStop(resume with { Epoch = resume.Epoch + 1 }));
        Assert.False(host.CompleteNativeResume(resume with { Context = context with { Request = 3 } }));
        Assert.True(viewer.ResumeAfterNativeStop(resume));
        Assert.False(viewer.ShouldIgnoreTcpFrames); // no black hole before first new UDP frame
        Assert.False(host.TryQueueJpegFrame(1, 1, 0, 0, new byte[] { 1 })); // host still awaits ACK
        byte[] stale = new byte[RemoteMessageCodec.FrameHeaderLength + 1];
        RemoteMessageCodec.WriteFrameHeader(stale, 1, 1, 0, 0);
        typeof(LowLatencyVideoViewerTransport).GetMethod("PublishCompletedFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, [(ulong)resume.MinimumFrameSequence - 1, MessageType.Frame, new ReadOnlyMemory<byte>(stale)]);
        Assert.Equal(1, published);
        Assert.True(host.CompleteNativeResume(resume));
        Assert.True(host.TryQueueJpegFrame(1, 1, 0, 0, new byte[] { 2 }));
        await Until(() => Volatile.Read(ref published) == 2);
        Assert.True(viewer.ShouldIgnoreTcpFrames);
        Assert.True(viewer.TryQueueMouseMove(RemoteInputCommand.MouseMove(1, 2)));
        Assert.False(viewer.ResumeAfterNativeStop(resume));
        Assert.False(host.CompleteNativeResume(resume));
        await host.StopVideoForCaptureTargetChangeAsync();
        viewer.AcknowledgeStopped(viewerOffer.ChannelId, viewerOffer.Epoch,
            LowLatencyVideoFallbackReasons.PreserveUdpInput, allowAuthenticatedHostInitiatedPreserve: true);
        var next = Assert.IsType<NativeDetailUdpResume>(host.PrepareNativeResume(context with { Request = 4 }));
        Assert.True(next.MinimumFrameSequence > resume.MinimumFrameSequence);
        Assert.False(host.CompleteNativeResume(resume));
        Assert.False(viewer.ResumeAfterNativeStop(resume));
        Assert.True(viewer.ResumeAfterNativeStop(next));
        Assert.True(host.CompleteNativeResume(next));
    }

    [Fact]
    public void InputInvalidationDoesNotAuthorizeChangingTheTransport()
    {
        using var viewer = new NativeDetailViewerSession();
        var context = new DetailContext(1, 1, 128, 128);
        viewer.BeginRequest(new(context, new(0, 0, 128, 128), true)); viewer.Invalidate();
        Assert.False(viewer.IsStopRequest(context));
        viewer.BeginRequest(new(context with { Request = 2 }, new(0, 0, 128, 128), false));
        Assert.False(viewer.IsStopRequest(context));
        Assert.True(viewer.IsStopRequest(context with { Request = 2 }));
        viewer.ResetConnection();
        Assert.False(viewer.IsStopRequest(context with { Request = 2 }));
    }
}
