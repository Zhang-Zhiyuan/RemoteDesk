using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class LowLatencyVideoTransportTests
{
    [Fact]
    public void MouseAppliedAckWatchdogTimesOutOnlyWithOutstandingInput()
    {
        var tracker = new LowLatencyMouseInputLatencyTracker();
        long startedAt = Stopwatch.Frequency;

        Assert.False(tracker.HasAcknowledgementTimedOut(
            AddMilliseconds(startedAt, 500),
            TimeSpan.FromSeconds(1)));

        tracker.RecordSent(1, startedAt);

        Assert.False(tracker.HasAcknowledgementTimedOut(
            AddMilliseconds(startedAt, 900),
            TimeSpan.FromSeconds(1)));
        Assert.True(tracker.HasAcknowledgementTimedOut(
            AddMilliseconds(startedAt, 1100),
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void MouseAppliedAckWatchdogRestartsAfterQueueBecomesCurrent()
    {
        var tracker = new LowLatencyMouseInputLatencyTracker();
        long startedAt = Stopwatch.Frequency;
        tracker.RecordSent(1, startedAt);
        Assert.True(tracker.TryRecordAcknowledged(
            1,
            AddMilliseconds(startedAt, 100)));
        Assert.False(tracker.HasAcknowledgementTimedOut(
            AddMilliseconds(startedAt, 5000),
            TimeSpan.FromSeconds(1)));

        long secondSentAt =
            AddMilliseconds(startedAt, 5000);
        tracker.RecordSent(2, secondSentAt);

        Assert.False(tracker.HasAcknowledgementTimedOut(
            AddMilliseconds(secondSentAt, 900),
            TimeSpan.FromSeconds(1)));
        Assert.True(tracker.HasAcknowledgementTimedOut(
            AddMilliseconds(secondSentAt, 1100),
            TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public void PreservedInputStateRejectsLateUdpVideo(
        int state,
        bool expected)
    {
        Assert.Equal(
            expected,
            LowLatencyVideoViewerTransport
                .CanAcceptUdpVideoPacket(state));
    }

    private static long AddMilliseconds(
        long timestamp,
        double milliseconds) =>
        timestamp + checked((long)Math.Round(
            milliseconds * Stopwatch.Frequency / 1000d));

    [Fact]
    public async Task CaptureTargetStopPreservesHealthyAuthenticatedUdpInput()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(8));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.AuthenticatedHeartbeat |
            LowLatencyVideoFeatures.UdpMouseInput;
        var barrier = new TaskCompletionSource<
            (ulong ChannelId, uint Epoch, byte Reason)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        int barrierCount = 0;
        int publishedFrames = 0;
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token,
            (channelId, epoch, reason) =>
            {
                Interlocked.Increment(ref barrierCount);
                barrier.TrySetResult((channelId, epoch, reason));
                return Task.CompletedTask;
            });
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    features));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();
        await using var viewer =
            new LowLatencyVideoViewerTransport(
                viewerOffer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(payload);
                    if (control.Kind ==
                        RemoteControlKind.LowLatencyVideoReady)
                    {
                        Assert.True(host.TryMarkReady(
                            control.LowLatencyVideoChannelId,
                            control.LowLatencyVideoEpoch));
                    }

                    return Task.FromResult(true);
                },
                _ => Interlocked.Increment(
                    ref publishedFrames),
                _ => { },
                timeout.Token,
                features);

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        await host.StopVideoForCaptureTargetChangeAsync();
        (ulong channelId, uint epoch, byte reason) =
            await barrier.Task.WaitAsync(timeout.Token);
        Assert.Equal(
            LowLatencyVideoFallbackReasons.PreserveUdpInput,
            reason);

        // The acknowledgement arrived through the authenticated TCP control
        // loop, so a host-initiated preserve may atomically move 2 -> 5.
        viewer.AcknowledgeStopped(
            channelId,
            epoch,
            reason,
            allowAuthenticatedHostInitiatedPreserve: true);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        Assert.True(viewer.TryQueueMouseMove(
            RemoteInputCommand.MouseMove(7, 9)));
        Assert.False(host.TryQueueJpegFrame(
            1,
            1,
            0,
            0,
            new byte[] { 1 }));
        await Task.Delay(50, timeout.Token);
        Assert.Equal(0, Volatile.Read(ref publishedFrames));

        // A later switch is already protected by state 5. It must not send a
        // second reason 4 that would destroy the preserved input route.
        await host.StopVideoForCaptureTargetChangeAsync();
        await Task.Delay(25, timeout.Token);
        Assert.Equal(1, Volatile.Read(ref barrierCount));
        Assert.True(viewer.TryQueueMouseMove(
            RemoteInputCommand.MouseMove(8, 10)));
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task PreservedUdpInputFallsBackToTcpWhenHeartbeatExpires()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(8));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.AuthenticatedHeartbeat |
            LowLatencyVideoFeatures.UdpMouseInput;
        var barrier = new TaskCompletionSource<
            (ulong ChannelId, uint Epoch, byte Reason)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token,
            (channelId, epoch, reason) =>
            {
                barrier.TrySetResult((channelId, epoch, reason));
                return Task.CompletedTask;
            });
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    features));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();
        await using var viewer =
            new LowLatencyVideoViewerTransport(
                viewerOffer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(payload);
                    if (control.Kind ==
                        RemoteControlKind.LowLatencyVideoReady)
                    {
                        Assert.True(host.TryMarkReady(
                            control.LowLatencyVideoChannelId,
                            control.LowLatencyVideoEpoch));
                    }

                    return Task.FromResult(true);
                },
                _ => { },
                _ => { },
                timeout.Token,
                features);

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        await host.StopVideoForCaptureTargetChangeAsync();
        (ulong channelId, uint epoch, byte reason) =
            await barrier.Task.WaitAsync(timeout.Token);
        Assert.Equal(
            LowLatencyVideoFallbackReasons.PreserveUdpInput,
            reason);
        viewer.AcknowledgeStopped(
            channelId,
            epoch,
            reason,
            allowAuthenticatedHostInitiatedPreserve: true);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        Assert.True(viewer.TryQueueMouseMove(
            RemoteInputCommand.MouseMove(1, 2)));

        // Simulate the host or network disappearing after the TCP-video /
        // UDP-input preserve barrier.  Without a fresh authenticated heartbeat,
        // the viewer must stop accepting UDP mouse moves so the caller falls
        // back to the reliable TCP input queue instead of silently dropping
        // interaction while TCP video still renders.
        await host.DisposeAsync();
        await Task.Delay(
            LowLatencyVideoViewerTransport.SteadyFrameTimeout +
                TimeSpan.FromMilliseconds(250),
            timeout.Token);

        Assert.False(viewer.TryQueueMouseMove(
            RemoteInputCommand.MouseMove(3, 4)));
        await WaitUntilAsync(
            () => viewer.IsShutdownCompleted,
            timeout.Token);
        Assert.False(viewer.TryQueueMouseMove(
            RemoteInputCommand.MouseMove(5, 6)));
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task CaptureTargetStopFallsBackCompletelyWithoutPreserveFeatures()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var barrier = new TaskCompletionSource<
            (ulong ChannelId, uint Epoch, byte Reason)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token,
            (channelId, epoch, reason) =>
            {
                barrier.TrySetResult((channelId, epoch, reason));
                return Task.CompletedTask;
            });
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();
        await using var viewer =
            new LowLatencyVideoViewerTransport(
                viewerOffer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(payload);
                    if (control.Kind ==
                        RemoteControlKind.LowLatencyVideoReady)
                    {
                        Assert.True(host.TryMarkReady(
                            control.LowLatencyVideoChannelId,
                            control.LowLatencyVideoEpoch));
                    }

                    return Task.FromResult(true);
                },
                _ => { },
                _ => { },
                timeout.Token);

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        await host.StopVideoForCaptureTargetChangeAsync();
        (ulong channelId, uint epoch, byte reason) =
            await barrier.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, reason);
        viewer.AcknowledgeStopped(
            channelId,
            epoch,
            reason,
            allowAuthenticatedHostInitiatedPreserve: true);
        await WaitUntilAsync(
            () => viewer.IsShutdownCompleted,
            timeout.Token);
        Assert.False(viewer.TryQueueMouseMove(
            RemoteInputCommand.MouseMove(1, 2)));
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task HostInitiatedPreserveIsRejectedAfterFatalViewerRouteFailure()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(8));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.AuthenticatedHeartbeat |
            LowLatencyVideoFeatures.UdpMouseInput;
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    features));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();
        await using var viewer =
            new LowLatencyVideoViewerTransport(
                viewerOffer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(payload);
                    if (control.Kind ==
                        RemoteControlKind.LowLatencyVideoReady)
                    {
                        Assert.True(host.TryMarkReady(
                            control.LowLatencyVideoChannelId,
                            control.LowLatencyVideoEpoch));
                    }

                    return Task.FromResult(true);
                },
                _ => { },
                _ => { },
                timeout.Token,
                features);

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        viewer.MarkFatalRouteFailureForTests();
        viewer.AcknowledgeStopped(
            offer.ChannelId,
            offer.Epoch,
            LowLatencyVideoFallbackReasons.PreserveUdpInput,
            allowAuthenticatedHostInitiatedPreserve: true);
        await WaitUntilAsync(
            () => viewer.IsShutdownCompleted,
            timeout.Token);
        Assert.False(viewer.TryQueueMouseMove(
            RemoteInputCommand.MouseMove(4, 5)));
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public void DatagramQueueExhaustionIsTransientButRouteErrorsAreFatal()
    {
        Assert.True(
            LowLatencyVideoSocketSupport
                .IsTransientDatagramSendError(
                    new SocketException(
                        (int)SocketError
                            .NoBufferSpaceAvailable)));
        Assert.True(
            LowLatencyVideoSocketSupport
                .IsTransientDatagramSendError(
                    new SocketException(
                        (int)SocketError.WouldBlock)));
        Assert.False(
            LowLatencyVideoSocketSupport
                .IsTransientDatagramSendError(
                    new SocketException(
                        (int)SocketError
                            .NetworkUnreachable)));
        Assert.False(
            LowLatencyVideoSocketSupport
                .IsTransientDatagramSendError(
                    new SocketException(
                        (int)SocketError
                            .ConnectionReset)));
    }

    [Fact]
    public void BindRetryPolicyProvidesTwoStageHostDeadlinesAndProbeBudget()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            LowLatencyVideoViewerTransport.BindTimeout);
        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            LowLatencyVideoViewerTransport.ProbeInterval);
        Assert.Equal(
            TimeSpan.FromMilliseconds(3500),
            LowLatencyVideoHostTransport.ProbeTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            LowLatencyVideoHostTransport.ReadyTimeout);
        Assert.Equal(
            LowLatencyVideoBindPolicy.ViewerTimeoutMilliseconds /
                LowLatencyVideoBindPolicy.ProbeIntervalMilliseconds,
            LowLatencyVideoBindPolicy.NominalProbeAttemptBudget);
        Assert.True(
            LowLatencyVideoBindPolicy.NominalProbeAttemptBudget >= 12);
        Assert.Equal(
            2,
            LowLatencyVideoViewerTransport
                .BindProbeAttemptsPerSocket);
        Assert.Equal(
            ThreadPriority.Highest,
            LowLatencyVideoViewerTransport
                .ReceiveThreadPriority);
        Assert.Equal(
            ThreadPriority.Highest,
            LowLatencyVideoViewerTransport
                .MouseInputThreadPriority);
        Assert.Equal(
            ThreadPriority.Highest,
            LowLatencyVideoHostTransport
                .ReceiveThreadPriority);
        Assert.Equal(
            ThreadPriority.Highest,
            LowLatencyVideoHostTransport
                .MouseInputAckThreadPriority);
        Assert.True(
            LowLatencyVideoHostTransport.ProbeTimeout >=
                LowLatencyVideoViewerTransport.BindTimeout +
                TimeSpan.FromMilliseconds(500));

        // Bind resilience must not weaken the established-frame stall fallback.
        Assert.Equal(
            TimeSpan.FromMilliseconds(2500),
            LowLatencyVideoViewerTransport.SteadyFrameTimeout);
        Assert.Equal(
            LowLatencyVideoViewerTransport.SteadyFrameTimeout,
            LowLatencyVideoHostTransport.FeedbackTimeout);
        Assert.Equal(
            LowLatencyVideoFeedbackPolicy.HardTimeoutMilliseconds,
            LowLatencyVideoHostTransport.FeedbackTimeout.TotalMilliseconds);
        Assert.Equal(
            TimeSpan.FromMilliseconds(4),
            LowLatencyVideoHostTransport
                .MouseInputAckMinimumInterval);
        Assert.Equal(
            256,
            LowLatencyVideoHostTransport
                .ReceiveBacklogBatchDatagramLimit);
    }

    [Fact]
    public void AuthenticatedHeartbeatSeparatesRouteLossFromSourceStall()
    {
        TimeSpan routeDeadline =
            LowLatencyVideoViewerTransport.SteadyFrameTimeout;

        Assert.Equal(
            LowLatencyVideoFallbackReasons.PreserveUdpInput,
            LowLatencyVideoViewerTransport
                .SelectFrameStallFallbackReason(
                    TimeSpan.FromMilliseconds(100),
                    authenticatedHeartbeatNegotiated: true,
                    udpMouseInputNegotiated: true));
        Assert.Equal(
            2,
            LowLatencyVideoViewerTransport
                .SelectFrameStallFallbackReason(
                    routeDeadline,
                    authenticatedHeartbeatNegotiated: true,
                    udpMouseInputNegotiated: true));
        Assert.Equal(
            2,
            LowLatencyVideoViewerTransport
                .SelectFrameStallFallbackReason(
                    TimeSpan.Zero,
                    authenticatedHeartbeatNegotiated: false,
                    udpMouseInputNegotiated: true));
        Assert.Equal(
            2,
            LowLatencyVideoViewerTransport
                .SelectFrameStallFallbackReason(
                    TimeSpan.Zero,
                    authenticatedHeartbeatNegotiated: true,
                    udpMouseInputNegotiated: false));
    }

    [Fact]
    public void AuthenticatedStaticSourceSilenceAllowsLateSameFrameParityButNotNextFrameFragments()
    {
        TimeSpan freshHeartbeat = TimeSpan.FromMilliseconds(100);

        Assert.True(
            LowLatencyVideoViewerTransport
                .IsAuthenticatedStaticSourceSilence(
                    highestAuthenticatedFramePacketSequence: 41,
                    highestCompleteFrameSequence: 42,
                    freshHeartbeat,
                    authenticatedHeartbeatNegotiated: true));
        // A parity or duplicate data packet can arrive after the data packet
        // that completed frame 42. Its timestamp is later, but its frame
        // sequence still identifies harmless same-frame traffic.
        Assert.True(
            LowLatencyVideoViewerTransport
                .IsAuthenticatedStaticSourceSilence(
                    highestAuthenticatedFramePacketSequence: 42,
                    highestCompleteFrameSequence: 42,
                    freshHeartbeat,
                    authenticatedHeartbeatNegotiated: true));
        // One authenticated fragment for frame 43 without a completed frame
        // is not static-source silence and must retain the stall fallback.
        Assert.False(
            LowLatencyVideoViewerTransport
                .IsAuthenticatedStaticSourceSilence(
                    highestAuthenticatedFramePacketSequence: 43,
                    highestCompleteFrameSequence: 42,
                    freshHeartbeat,
                    authenticatedHeartbeatNegotiated: true));
        Assert.False(
            LowLatencyVideoViewerTransport
                .IsAuthenticatedStaticSourceSilence(
                    highestAuthenticatedFramePacketSequence: 42,
                    highestCompleteFrameSequence: 42,
                    LowLatencyVideoViewerTransport
                        .SteadyFrameTimeout,
                    authenticatedHeartbeatNegotiated: true));
        Assert.False(
            LowLatencyVideoViewerTransport
                .IsAuthenticatedStaticSourceSilence(
                    highestAuthenticatedFramePacketSequence: 42,
                    highestCompleteFrameSequence: 42,
                    freshHeartbeat,
                    authenticatedHeartbeatNegotiated: false));
        Assert.False(
            LowLatencyVideoViewerTransport
                .IsAuthenticatedStaticSourceSilence(
                    highestAuthenticatedFramePacketSequence: 0,
                    highestCompleteFrameSequence: 42,
                    freshHeartbeat,
                    authenticatedHeartbeatNegotiated: true));
    }

    [Fact]
    public async Task AuthenticatedHeartbeatKeepsUdpVideoAcrossStaticSourceAndDetectsLaterRouteLoss()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.AuthenticatedHeartbeat |
            LowLatencyVideoFeatures.UdpMouseInput;
        var stopReceived =
            new TaskCompletionSource<RemoteControlMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var routeLossStopReceived =
            new TaskCompletionSource<RemoteControlMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        int receivedFrames = 0;
        LowLatencyVideoViewerTransport? viewerReference = null;
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(
                IPAddress.Loopback,
                features));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            async payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }
                else if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoStop)
                {
                    bool preservingUdpInput =
                        control.LowLatencyVideoStopReason ==
                        LowLatencyVideoFallbackReasons
                            .PreserveUdpInput;
                    if (preservingUdpInput)
                    {
                        Assert.True(host
                            .TryDisableVideoRouteKeepingUdpInput());
                        // Keep the reliable acknowledgement in flight for
                        // longer than a feedback cycle.  The viewer's sole
                        // feedback/watchdog loop must survive state 3 and
                        // continue once the route is promoted to state 5.
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(350),
                            timeout.Token);
                    }
                    else
                    {
                        Assert.Equal(
                            1,
                            control.LowLatencyVideoStopReason);
                    }

                    viewerReference!.AcknowledgeStopped(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch,
                        control.LowLatencyVideoStopReason);
                    (preservingUdpInput
                            ? stopReceived
                            : routeLossStopReceived)
                        .TrySetResult(control);
                }

                return true;
            },
            _ => Interlocked.Increment(ref receivedFrames),
            _ => { },
            timeout.Token,
            features);
        viewerReference = viewer;

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        Assert.True(host.TryQueueJpegFrame(
            1,
            1,
            0,
            0,
            new byte[] { 1 }));
        await WaitUntilAsync(
            () => Volatile.Read(ref receivedFrames) == 1,
            timeout.Token);

        await Task.Delay(
            LowLatencyVideoViewerTransport.SteadyFrameTimeout +
                TimeSpan.FromMilliseconds(500),
            timeout.Token);

        Assert.False(stopReceived.Task.IsCompleted);
        Assert.True(host.IsRouteActive);
        Assert.True(viewer.ShouldIgnoreTcpFrames);

        // Exercise the video-only fallback barrier independently after proving
        // that a healthy, silent WGC source no longer triggers it.
        await viewer.RequestFallbackForTestsAsync(
            LowLatencyVideoFallbackReasons.PreserveUdpInput);
        await stopReceived.Task.WaitAsync(timeout.Token);
        Assert.True(host.IsRouteActive);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        Assert.True(viewer.TryQueueMouseMove(
            RemoteInputCommand.MouseMove(0, 0)));

        host.DisableRoute(notifyViewer: false);
        await routeLossStopReceived.Task.WaitAsync(timeout.Token);
        await WaitUntilAsync(
            () => viewer.IsShutdownCompleted,
            timeout.Token);
        Assert.False(host.IsRouteActive);
        Assert.False(viewer.TryQueueMouseMove(
            RemoteInputCommand.MouseMove(1, 1)));
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task FeedbackDeadlineToleratesTwoSecondGapAndFallsBackOnceAfterHardTimeout()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var feedbackTime = new ManualTimeProvider();
        var logs = new ConcurrentQueue<string>();
        var fallbackBarrier =
            new TaskCompletionSource<(ulong ChannelId, uint Epoch, byte Reason)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        int fallbackBarrierCount = 0;
        await using var host = new LowLatencyVideoHostTransport(
            logs.Enqueue,
            timeout.Token,
            (channelId, epoch, reason) =>
            {
                Interlocked.Increment(ref fallbackBarrierCount);
                fallbackBarrier.TrySetResult(
                    (channelId, epoch, reason));
                return Task.CompletedTask;
            },
            feedbackTimeProvider: feedbackTime);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        using var viewerSendCipher = new LowLatencyVideoSendCipher(
            offer.ViewerToHostKey,
            offer.ViewerNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        using var viewerReceiveCipher = new LowLatencyVideoReceiveCipher(
            offer.HostToViewerKey,
            offer.HostNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        using var viewerSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        viewerSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        host.MarkOfferSent();

        byte[] probe = viewerSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindProbe,
            0,
            offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            offer.Challenge);
        try
        {
            await viewerSocket.SendToAsync(
                probe,
                SocketFlags.None,
                new IPEndPoint(IPAddress.Loopback, offer.Port),
                timeout.Token);
            LowLatencyVideoDatagram bindAck =
                await ReceiveAuthenticatedDatagramAsync(
                    viewerSocket,
                    viewerReceiveCipher,
                    timeout.Token);
            Assert.Equal(
                LowLatencyVideoDatagramKind.BindAck,
                bindAck.Kind);
            CryptographicOperations.ZeroMemory(
                bindAck.Plaintext.Span);
            Assert.True(host.TryMarkReady(
                offer.ChannelId,
                offer.Epoch));
            host.ClearOfferSecrets();
            Assert.True(host.IsRouteActive);

            feedbackTime.Advance(TimeSpan.FromMilliseconds(1200));
            Assert.True(host.IsRouteActive);
            feedbackTime.Advance(TimeSpan.FromMilliseconds(800));
            Assert.True(host.IsRouteActive);
            feedbackTime.Advance(TimeSpan.FromMilliseconds(500));
            Assert.True(host.IsRouteActive);
            Assert.False(fallbackBarrier.Task.IsCompleted);

            feedbackTime.Advance(TimeSpan.FromMilliseconds(1));
            Assert.False(host.IsRouteActive);
            Assert.False(host.IsRouteActive);

            (ulong channelId, uint epoch, byte reason) =
                await fallbackBarrier.Task.WaitAsync(timeout.Token);
            Assert.Equal(offer.ChannelId, channelId);
            Assert.Equal(offer.Epoch, epoch);
            Assert.Equal(1, reason);
            Assert.Equal(
                1,
                Volatile.Read(ref fallbackBarrierCount));
            Assert.Single(
                logs,
                message => message.Contains(
                    "反馈超时",
                    StringComparison.Ordinal));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(probe);
        }
    }

    [Fact]
    public async Task HeartbeatWorkerExpiresMissingViewerFeedbackDuringStaticSource()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.AuthenticatedHeartbeat |
            LowLatencyVideoFeatures.UdpMouseInput;
        var feedbackTime = new ManualTimeProvider();
        var fallbackBarrier =
            new TaskCompletionSource<(ulong ChannelId, uint Epoch, byte Reason)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        int fallbackBarrierCount = 0;
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token,
            (channelId, epoch, reason) =>
            {
                Interlocked.Increment(ref fallbackBarrierCount);
                fallbackBarrier.TrySetResult(
                    (channelId, epoch, reason));
                return Task.CompletedTask;
            },
            feedbackTimeProvider: feedbackTime);
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    features));
        using var viewerSendCipher = new LowLatencyVideoSendCipher(
            offer.ViewerToHostKey,
            offer.ViewerNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        using var viewerReceiveCipher = new LowLatencyVideoReceiveCipher(
            offer.HostToViewerKey,
            offer.HostNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        using var viewerSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        viewerSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        host.MarkOfferSent();

        byte[] probe = viewerSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindProbe,
            0,
            offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            offer.Challenge);
        try
        {
            await viewerSocket.SendToAsync(
                probe,
                SocketFlags.None,
                new IPEndPoint(IPAddress.Loopback, offer.Port),
                timeout.Token);
            LowLatencyVideoDatagram bindAck =
                await ReceiveAuthenticatedDatagramAsync(
                    viewerSocket,
                    viewerReceiveCipher,
                    timeout.Token);
            Assert.Equal(
                LowLatencyVideoDatagramKind.BindAck,
                bindAck.Kind);
            CryptographicOperations.ZeroMemory(
                bindAck.Plaintext.Span);
            Assert.True(host.TryMarkReady(
                offer.ChannelId,
                offer.Epoch));
            host.ClearOfferSecrets();
            Assert.True(host.IsRouteActive);

            // Prove the host-to-viewer direction remains healthy. The manual
            // peer intentionally sends no Feedback/FeedbackV2 datagrams, as
            // happens when an unchanged WGC source hides an upstream failure.
            LowLatencyVideoDatagram heartbeat =
                await ReceiveAuthenticatedDatagramAsync(
                    viewerSocket,
                    viewerReceiveCipher,
                    timeout.Token);
            Assert.Equal(
                LowLatencyVideoDatagramKind.Heartbeat,
                heartbeat.Kind);
            CryptographicOperations.ZeroMemory(
                heartbeat.Plaintext.Span);

            feedbackTime.Advance(
                LowLatencyVideoHostTransport.FeedbackTimeout +
                TimeSpan.FromMilliseconds(1));

            // Do not read IsRouteActive here. The independent heartbeat
            // worker must own the deadline while capture is blocked waiting
            // for a changed desktop frame.
            (ulong channelId, uint epoch, byte reason) =
                await fallbackBarrier.Task.WaitAsync(timeout.Token);
            Assert.Equal(offer.ChannelId, channelId);
            Assert.Equal(offer.Epoch, epoch);
            Assert.Equal(1, reason);
            await WaitUntilAsync(
                () => host.IsIoShutdownCompleted,
                timeout.Token);
            Assert.Equal(
                1,
                Volatile.Read(ref fallbackBarrierCount));
            Assert.False(host.IsRouteActive);
            Assert.False(
                host.TryDisableVideoRouteKeepingUdpInput());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(probe);
        }
    }

    [Fact]
    public async Task Ipv4MappedTcpEndpointsNegotiateAnIpv4UdpRoute()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logs = new ConcurrentQueue<string>();
        IPAddress mappedLoopback = IPAddress.Loopback.MapToIPv6();
        await using var host = new LowLatencyVideoHostTransport(
            logs.Enqueue,
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(mappedLoopback));
        byte[] offerPayload =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(offerPayload)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var frameReceived = new TaskCompletionSource<RemoteFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            mappedLoopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }

                return Task.FromResult(true);
            },
            frame => frameReceived.TrySetResult(
                CloneBorrowedFrame(frame)),
            logs.Enqueue,
            timeout.Token,
            localAddress: mappedLoopback);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        Assert.Equal(
            new IPEndPoint(IPAddress.Loopback, viewer.LocalEndpoint.Port),
            viewer.LocalEndpoint);
        Assert.True(host.TryQueueJpegFrame(
            320,
            200,
            1,
            1,
            new byte[] { 0x11, 0x22, 0x33 }));

        RemoteFrame frame =
            await frameReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal(RemoteFrameEncoding.Jpeg, frame.Encoding);
        Assert.Equal(
            new byte[] { 0x11, 0x22, 0x33 },
            frame.EncodedBuffer
                .AsSpan(frame.EncodedOffset, frame.EncodedLength)
                .ToArray());
        Assert.DoesNotContain(
            logs,
            message => message.Contains(
                "SocketError=",
                StringComparison.Ordinal));

        CryptographicOperations.ZeroMemory(offerPayload);
    }

    [Fact]
    public async Task PreferredLocalEndpointUsesTcpPortForUdpOffer()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var tcpListener =
            new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        int tcpPort =
            Assert.IsType<IPEndPoint>(tcpListener.LocalEndpoint)
                .Port;
        await using var host =
            new LowLatencyVideoHostTransport(
                _ => { },
                timeout.Token);

        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    preferredLocalEndpoint:
                        new IPEndPoint(
                            IPAddress.Loopback,
                            tcpPort)));

        Assert.Equal(tcpPort, offer.Port);
    }

    [Fact]
    public async Task ConflictingPreferredUdpPortFallsBackToEphemeralPort()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logs = new ConcurrentQueue<string>();
        using var occupiedSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        occupiedSocket.Bind(
            new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint occupiedEndpoint =
            Assert.IsType<IPEndPoint>(
                occupiedSocket.LocalEndPoint);
        await using var host =
            new LowLatencyVideoHostTransport(
                logs.Enqueue,
                timeout.Token);

        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    preferredLocalEndpoint:
                        occupiedEndpoint));

        Assert.NotEqual(
            occupiedEndpoint.Port,
            offer.Port);
        Assert.Contains(
            logs,
            message => message.Contains(
                "无法复用 TCP 本地端口",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ViewerRejectsMismatchedExplicitLocalAddressFamily()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(1));
        LowLatencyVideoOffer offer = CreateUnusedLoopbackOffer();
        try
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new LowLatencyVideoViewerTransport(
                    offer,
                    IPAddress.Loopback,
                    _ => Task.FromResult(true),
                    _ => { },
                    _ => { },
                    timeout.Token,
                    localAddress: IPAddress.IPv6Loopback));

            Assert.Equal("localAddress", error.ParamName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(offer.HostToViewerKey);
            CryptographicOperations.ZeroMemory(offer.ViewerToHostKey);
            CryptographicOperations.ZeroMemory(offer.HostNoncePrefix);
            CryptographicOperations.ZeroMemory(offer.ViewerNoncePrefix);
            CryptographicOperations.ZeroMemory(offer.Challenge);
        }
    }

    [Fact]
    public async Task ViewerRotatesSourcePortAfterTwoUnansweredProbesAndReadiesOnSecondSocket()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var logs = new ConcurrentQueue<string>();
        using var hostSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        hostSocket.Bind(
            new IPEndPoint(IPAddress.Loopback, 0));
        int hostPort =
            Assert.IsType<IPEndPoint>(
                hostSocket.LocalEndPoint)
                .Port;
        LowLatencyVideoOffer offer =
            CreateUnusedLoopbackOffer(hostPort);
        using var hostReceiveCipher =
            new LowLatencyVideoReceiveCipher(
                offer.ViewerToHostKey,
                offer.ViewerNoncePrefix,
                offer.ChannelId,
                offer.Epoch);
        using var hostSendCipher =
            new LowLatencyVideoSendCipher(
                offer.HostToViewerKey,
                offer.HostNoncePrefix,
                offer.ChannelId,
                offer.Epoch);
        var readyReceived =
            new TaskCompletionSource<RemoteControlMessage>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var qwaveAttachments = new ConcurrentQueue<
            RecordingQwaveFlowLease>();
        await using var viewer =
            new LowLatencyVideoViewerTransport(
                offer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(
                            payload);
                    if (control.Kind ==
                        RemoteControlKind
                            .LowLatencyVideoReady)
                    {
                        readyReceived.TrySetResult(
                            control);
                    }

                    return Task.FromResult(true);
                },
                _ => { },
                logs.Enqueue,
                timeout.Token,
                localAddress: IPAddress.Loopback,
                attachQwaveFlow:
                    (socket, destination, trafficType) =>
                    {
                        var lease =
                            new RecordingQwaveFlowLease(
                                socket,
                                destination,
                                trafficType);
                        qwaveAttachments.Enqueue(lease);
                        return lease;
                    });

        byte[] receiveBuffer =
            new byte[ushort.MaxValue];
        EndPoint receiveFrom =
            new IPEndPoint(IPAddress.Any, 0);
        IPEndPoint? firstViewerEndpoint = null;
        IPEndPoint? secondViewerEndpoint = null;
        int firstEndpointProbeCount = 0;
        while (secondViewerEndpoint is null)
        {
            SocketReceiveFromResult result =
                await hostSocket.ReceiveFromAsync(
                    receiveBuffer,
                    SocketFlags.None,
                    receiveFrom,
                    timeout.Token);
            if (!hostReceiveCipher.TryDecrypt(
                    receiveBuffer.AsSpan(
                        0,
                        result.ReceivedBytes),
                    out LowLatencyVideoDatagram packet))
            {
                continue;
            }

            try
            {
                if (packet.Kind !=
                        LowLatencyVideoDatagramKind
                            .BindProbe ||
                    !CryptographicOperations
                        .FixedTimeEquals(
                            packet.Plaintext.Span,
                            offer.Challenge))
                {
                    continue;
                }

                IPEndPoint viewerEndpoint =
                    Assert.IsType<IPEndPoint>(
                        result.RemoteEndPoint);
                firstViewerEndpoint ??=
                    viewerEndpoint;
                if (viewerEndpoint.Equals(
                        firstViewerEndpoint))
                {
                    firstEndpointProbeCount++;
                    continue;
                }

                secondViewerEndpoint =
                    viewerEndpoint;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(
                    packet.Plaintext.Span);
            }
        }

        byte[] ack = hostSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindAck,
            0,
            offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            offer.Challenge);
        try
        {
            int sent = await hostSocket.SendToAsync(
                ack,
                SocketFlags.None,
                secondViewerEndpoint,
                timeout.Token);
            Assert.Equal(ack.Length, sent);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ack);
        }

        RemoteControlMessage ready =
            await readyReceived.Task.WaitAsync(
                timeout.Token);
        await WaitUntilAsync(
            () => logs.Any(
                message => message.Contains(
                    "已轮换查看端握手端口",
                    StringComparison.Ordinal)),
            timeout.Token);

        Assert.Equal(
            LowLatencyVideoViewerTransport
                .BindProbeAttemptsPerSocket,
            firstEndpointProbeCount);
        Assert.NotNull(firstViewerEndpoint);
        Assert.NotEqual(
            firstViewerEndpoint,
            secondViewerEndpoint);
        Assert.Equal(
            secondViewerEndpoint,
            viewer.LocalEndpoint);
        Assert.Equal(
            RemoteControlKind.LowLatencyVideoReady,
            ready.Kind);
        Assert.Equal(
            offer.ChannelId,
            ready.LowLatencyVideoChannelId);
        Assert.Equal(
            offer.Epoch,
            ready.LowLatencyVideoEpoch);
        Assert.True(viewer.ShouldIgnoreTcpFrames);
        LowLatencyVideoViewerHandshakeSnapshot snapshot =
            viewer.CollectHandshakeSnapshot();
        Assert.Equal(1, snapshot.ValidAckCount);
        Assert.Equal(0, snapshot.AddressRejectedCount);
        Assert.Equal(0, snapshot.DecryptFailureCount);

        RecordingQwaveFlowLease[] qwaveFlows =
            qwaveAttachments.ToArray();
        Assert.Equal(2, qwaveFlows.Length);
        Assert.All(
            qwaveFlows,
            flow =>
            {
                Assert.Equal(
                    new IPEndPoint(
                        IPAddress.Loopback,
                        hostPort),
                    flow.Destination);
                Assert.Equal(
                    WindowsQwaveTrafficType.Control,
                    flow.TrafficType);
            });
        Assert.Equal(
            firstViewerEndpoint,
            qwaveFlows[0].LocalEndpoint);
        Assert.Equal(
            secondViewerEndpoint,
            qwaveFlows[1].LocalEndpoint);
        Assert.Equal(1, qwaveFlows[0].DisposeCount);
        Assert.True(
            qwaveFlows[0].SocketWasOpenWhenDisposed);
        Assert.Equal(0, qwaveFlows[1].DisposeCount);

        await viewer.DisposeAsync();
        Assert.Equal(1, qwaveFlows[1].DisposeCount);
        Assert.True(
            qwaveFlows[1].SocketWasOpenWhenDisposed);
    }

    [Fact]
    public async Task ValidProbeStartsIndependentReadyDeadline()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var logs = new ConcurrentQueue<string>();
        await using var host = new LowLatencyVideoHostTransport(
            logs.Enqueue,
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        using var viewerSendCipher = new LowLatencyVideoSendCipher(
            offer.ViewerToHostKey,
            offer.ViewerNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        using var viewerReceiveCipher = new LowLatencyVideoReceiveCipher(
            offer.HostToViewerKey,
            offer.HostNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        using var viewerSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        viewerSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var elapsed = Stopwatch.StartNew();
        host.MarkOfferSent();

        await Task.Delay(TimeSpan.FromMilliseconds(2200), timeout.Token);
        byte[] probe = viewerSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindProbe,
            0,
            offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            offer.Challenge);
        try
        {
            await viewerSocket.SendToAsync(
                probe,
                SocketFlags.None,
                new IPEndPoint(IPAddress.Loopback, offer.Port),
                timeout.Token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(probe);
        }

        LowLatencyVideoDatagram ack =
            await ReceiveAuthenticatedDatagramAsync(
                viewerSocket,
                viewerReceiveCipher,
                timeout.Token);
        Assert.Equal(LowLatencyVideoDatagramKind.BindAck, ack.Kind);
        CryptographicOperations.ZeroMemory(ack.Plaintext.Span);

        await Task.Delay(
            LowLatencyVideoHostTransport.ReadyTimeout -
                TimeSpan.FromMilliseconds(500),
            timeout.Token);
        Assert.True(elapsed.Elapsed >
            LowLatencyVideoHostTransport.ProbeTimeout);
        Assert.True(host.TryMarkReady(offer.ChannelId, offer.Epoch));
        Assert.True(host.IsRouteActive);

        LowLatencyVideoHostHandshakeSnapshot snapshot =
            host.CollectHandshakeSnapshot();
        Assert.True(snapshot.RawPacketCount >= 1);
        Assert.Equal(1, snapshot.ValidProbeCount);
        Assert.Equal(1, snapshot.AckSentCount);
        Assert.Contains(
            logs,
            message => message.Contains(
                "阶段=已就绪",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostRepinsToHigherSequenceAuthenticatedProbeBeforeReadyAndRejectsMigrationAfterReady()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logs = new ConcurrentQueue<string>();
        await using var host = new LowLatencyVideoHostTransport(
            logs.Enqueue,
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        using var viewerSendCipher = new LowLatencyVideoSendCipher(
            offer.ViewerToHostKey,
            offer.ViewerNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        using var viewerReceiveCipher = new LowLatencyVideoReceiveCipher(
            offer.HostToViewerKey,
            offer.HostNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        using var firstSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        using var secondSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        using var thirdSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        firstSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        secondSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        thirdSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var hostEndpoint =
            new IPEndPoint(IPAddress.Loopback, offer.Port);
        host.MarkOfferSent();

        byte[] unauthenticated = new byte[32];
        RandomNumberGenerator.Fill(unauthenticated);
        await firstSocket.SendToAsync(
            unauthenticated,
            SocketFlags.None,
            hostEndpoint,
            timeout.Token);
        CryptographicOperations.ZeroMemory(unauthenticated);
        await WaitUntilAsync(
            () => host.CollectHandshakeSnapshot()
                .DecryptFailureCount >= 1,
            timeout.Token);

        byte[] firstProbe = viewerSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindProbe,
            0,
            offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            offer.Challenge);
        await firstSocket.SendToAsync(
            firstProbe,
            SocketFlags.None,
            hostEndpoint,
            timeout.Token);
        CryptographicOperations.ZeroMemory(firstProbe);
        LowLatencyVideoDatagram ack =
            await ReceiveAuthenticatedDatagramAsync(
                firstSocket,
                viewerReceiveCipher,
                timeout.Token);
        CryptographicOperations.ZeroMemory(ack.Plaintext.Span);

        byte[] secondProbe = viewerSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindProbe,
            0,
            offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            offer.Challenge);
        await secondSocket.SendToAsync(
            secondProbe,
            SocketFlags.None,
            hostEndpoint,
            timeout.Token);
        CryptographicOperations.ZeroMemory(secondProbe);
        LowLatencyVideoDatagram secondAck =
            await ReceiveAuthenticatedDatagramAsync(
                secondSocket,
                viewerReceiveCipher,
                timeout.Token);
        Assert.Equal(
            LowLatencyVideoDatagramKind.BindAck,
            secondAck.Kind);
        CryptographicOperations.ZeroMemory(
            secondAck.Plaintext.Span);

        Assert.True(
            host.TryMarkReady(
                offer.ChannelId,
                offer.Epoch));
        Assert.True(host.IsRouteActive);
        Assert.True(host.IsPeerSocketConnected);

        byte[] thirdProbe = viewerSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindProbe,
            0,
            offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            offer.Challenge);
        await thirdSocket.SendToAsync(
            thirdProbe,
            SocketFlags.None,
            hostEndpoint,
            timeout.Token);
        CryptographicOperations.ZeroMemory(thirdProbe);
        using var noAckTimeout =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    timeout.Token);
        noAckTimeout.CancelAfter(
            TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            async () =>
                await ReceiveAuthenticatedDatagramAsync(
                    thirdSocket,
                    viewerReceiveCipher,
                    noAckTimeout.Token));

        await WaitUntilAsync(
            () => logs.Any(
                message => message.Contains(
                    "已通过认证并重新绑定",
                    StringComparison.Ordinal)),
            timeout.Token);

        LowLatencyVideoHostHandshakeSnapshot snapshot =
            host.CollectHandshakeSnapshot();
        Assert.True(snapshot.RawPacketCount >= 3);
        Assert.Equal(1, snapshot.DecryptFailureCount);
        Assert.Equal(0, snapshot.AddressRejectedCount);
        Assert.Equal(2, snapshot.ValidProbeCount);
        Assert.Equal(2, snapshot.AckSentCount);
    }

    [Fact]
    public async Task HostConnectFailureKeepsSendToCompatibilityPathActive()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logs = new ConcurrentQueue<string>();
        int connectAttempts = 0;
        await using var host =
            new LowLatencyVideoHostTransport(
                logs.Enqueue,
                timeout.Token,
                connectPeerSocket: (_, _) =>
                {
                    Interlocked.Increment(ref connectAttempts);
                    throw new SocketException(
                        (int)SocketError.NetworkUnreachable);
                });
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(
                offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(
                    encodedOffer).LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var readyAccepted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var frameReceived =
            new TaskCompletionSource<RemoteFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer =
            new LowLatencyVideoViewerTransport(
                viewerOffer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(payload);
                    if (control.Kind !=
                        RemoteControlKind.LowLatencyVideoReady)
                    {
                        return Task.FromResult(true);
                    }

                    bool accepted =
                        host.TryMarkReady(
                            control.LowLatencyVideoChannelId,
                            control.LowLatencyVideoEpoch);
                    readyAccepted.TrySetResult(accepted);
                    return Task.FromResult(accepted);
                },
                frame => frameReceived.TrySetResult(
                    CloneBorrowedFrame(frame)),
                logs.Enqueue,
                timeout.Token);

        Assert.True(
            await readyAccepted.Task.WaitAsync(timeout.Token));
        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        Assert.Equal(1, Volatile.Read(ref connectAttempts));
        Assert.False(host.IsPeerSocketConnected);
        Assert.Contains(
            logs,
            message => message.Contains(
                "继续使用兼容发送路径",
                StringComparison.Ordinal));

        byte[] jpegBytes = [0xA5, 0x5A, 0xC3, 0x3C];
        Assert.True(host.TryQueueJpegFrame(
            320,
            200,
            1,
            1,
            jpegBytes));
        RemoteFrame frame =
            await frameReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal(RemoteFrameEncoding.Jpeg, frame.Encoding);
        Assert.Equal(
            jpegBytes,
            frame.EncodedBuffer
                .AsSpan(
                    frame.EncodedOffset,
                    frame.EncodedLength)
                .ToArray());
        Assert.True(host.IsRouteActive);

        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task RepinRefreshesReadyDeadlineAndSetupMonitorStillCleansUp()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var logs = new ConcurrentQueue<string>();
        await using var host =
            new LowLatencyVideoHostTransport(
                logs.Enqueue,
                timeout.Token);
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback));
        using var viewerSendCipher =
            new LowLatencyVideoSendCipher(
                offer.ViewerToHostKey,
                offer.ViewerNoncePrefix,
                offer.ChannelId,
                offer.Epoch);
        using var viewerReceiveCipher =
            new LowLatencyVideoReceiveCipher(
                offer.HostToViewerKey,
                offer.HostNoncePrefix,
                offer.ChannelId,
                offer.Epoch);
        using var firstSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        using var secondSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        firstSocket.Bind(
            new IPEndPoint(IPAddress.Loopback, 0));
        secondSocket.Bind(
            new IPEndPoint(IPAddress.Loopback, 0));
        var hostEndpoint =
            new IPEndPoint(
                IPAddress.Loopback,
                offer.Port);
        host.MarkOfferSent();

        async Task SendProbeAndReceiveAckAsync(
            Socket socket)
        {
            byte[] probe = viewerSendCipher.Encrypt(
                LowLatencyVideoDatagramKind.BindProbe,
                0,
                offer.Challenge.Length,
                0,
                0,
                1,
                0,
                0,
                offer.Challenge);
            try
            {
                await socket.SendToAsync(
                    probe,
                    SocketFlags.None,
                    hostEndpoint,
                    timeout.Token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(
                    probe);
            }

            LowLatencyVideoDatagram ack =
                await ReceiveAuthenticatedDatagramAsync(
                    socket,
                    viewerReceiveCipher,
                    timeout.Token);
            Assert.Equal(
                LowLatencyVideoDatagramKind.BindAck,
                ack.Kind);
            CryptographicOperations.ZeroMemory(
                ack.Plaintext.Span);
        }

        await SendProbeAndReceiveAckAsync(
            firstSocket);
        await Task.Delay(
            LowLatencyVideoHostTransport.ReadyTimeout -
                TimeSpan.FromMilliseconds(500),
            timeout.Token);
        await SendProbeAndReceiveAckAsync(
            secondSocket);

        // The original Ready deadline has elapsed, but the authenticated
        // replacement candidate owns a fresh bounded deadline.
        await Task.Delay(
            TimeSpan.FromMilliseconds(700),
            timeout.Token);
        Assert.False(host.IsIoShutdownCompleted);

        await WaitUntilAsync(
            () => logs.Any(
                message => message.Contains(
                    "阶段=等待 TCP Ready",
                    StringComparison.Ordinal)),
            timeout.Token);
        await WaitUntilAsync(
            () => host.IsIoShutdownCompleted,
            timeout.Token);

        Assert.False(
            host.TryMarkReady(
                offer.ChannelId,
                offer.Epoch));
        LowLatencyVideoHostHandshakeSnapshot snapshot =
            host.CollectHandshakeSnapshot();
        Assert.Equal(2, snapshot.ValidProbeCount);
        Assert.Equal(2, snapshot.AckSentCount);
    }

    [Fact]
    public async Task ViewerBindProbeSocketFailureLogsCodesAndEndpoints()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logs = new ConcurrentQueue<string>();
        LowLatencyVideoOffer offer = CreateUnusedLoopbackOffer();
        await using var viewer = new LowLatencyVideoViewerTransport(
            offer,
            IPAddress.Broadcast,
            _ => Task.FromResult(true),
            _ => { },
            logs.Enqueue,
            timeout.Token);

        await WaitUntilAsync(
            () => viewer.IsShutdownCompleted,
            timeout.Token);

        string failure = Assert.Single(
            logs,
            message => message.Contains(
                "BindProbe发送失败",
                StringComparison.Ordinal));
        Assert.Contains("SocketError=AccessDenied", failure);
        Assert.Contains("NativeError=10013", failure);
        Assert.Contains("ErrorCode=10013", failure);
        // A connected UDP socket resolves its concrete routed local address
        // instead of reporting the wildcard bind address.
        Assert.Contains("本地=", failure);
        Assert.DoesNotContain("本地=unavailable", failure);
        Assert.Contains("目标=255.255.255.255:9", failure);
        Assert.Contains("已回退 TCP", failure);
    }

    [Fact]
    public async Task LoopbackHandshakePublishesIndependentLatestFrameAndFallsBack()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        byte[] offerPayload = RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer = Assert.IsType<LowLatencyVideoOffer>(
            RemoteMessageCodec.DecodeControl(offerPayload).LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var frameReceived = new TaskCompletionSource<RemoteFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind == RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }
                return Task.FromResult(true);
            },
            frame => frameReceived.TrySetResult(
                CloneBorrowedFrame(frame)),
            _ => { },
            timeout.Token);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        Assert.True(viewer.ShouldIgnoreTcpFrames);
        Assert.True(host.IsPeerSocketConnected);

        byte[] reusedCaptureBuffer = [0x11, 0x22, 0x33, 0x44];
        Assert.True(host.TryQueueJpegFrame(
            320,
            200,
            1.25,
            2.5,
            reusedCaptureBuffer));
        reusedCaptureBuffer.AsSpan().Fill(0xEE);

        RemoteFrame frame = await frameReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal(320, frame.Width);
        Assert.Equal(200, frame.Height);
        Assert.Equal(RemoteFrameEncoding.Jpeg, frame.Encoding);
        Assert.Equal(RemoteFrameFlags.KeyFrame, frame.Flags);
        Assert.Equal(1.25, frame.CaptureMilliseconds);
        Assert.Equal(2.5, frame.EncodeMilliseconds);
        Assert.Equal(
            new byte[] { 0x11, 0x22, 0x33, 0x44 },
            frame.EncodedBuffer.AsSpan(frame.EncodedOffset, frame.EncodedLength).ToArray());

        viewer.AcknowledgeStopped(offer.ChannelId, offer.Epoch);
        Assert.False(viewer.ShouldIgnoreTcpFrames);

        CryptographicOperations.ZeroMemory(offerPayload);
    }

    [Fact]
    public async Task FiftyConsecutiveLoopbackHandshakesDisposeCleanly()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(90));
        const int iterationCount = 50;
        for (int iteration = 0;
            iteration < iterationCount;
            iteration++)
        {
            var logs = new ConcurrentQueue<string>();
            var readyAccepted =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
            await using var host =
                new LowLatencyVideoHostTransport(
                    logs.Enqueue,
                    timeout.Token);
            LowLatencyVideoOffer offer =
                Assert.IsType<LowLatencyVideoOffer>(
                    host.TryCreateOffer(IPAddress.Loopback));
            byte[] encodedOffer =
                RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
            LowLatencyVideoOffer viewerOffer =
                Assert.IsType<LowLatencyVideoOffer>(
                    RemoteMessageCodec.DecodeControl(encodedOffer)
                        .LowLatencyVideoOffer);
            host.MarkOfferSent();

            await using var viewer =
                new LowLatencyVideoViewerTransport(
                    viewerOffer,
                    IPAddress.Loopback,
                    payload =>
                    {
                        RemoteControlMessage control =
                            RemoteMessageCodec.DecodeControl(payload);
                        if (control.Kind !=
                            RemoteControlKind.LowLatencyVideoReady)
                        {
                            return Task.FromResult(true);
                        }

                        bool accepted =
                            host.TryMarkReady(
                                control.LowLatencyVideoChannelId,
                                control.LowLatencyVideoEpoch);
                        readyAccepted.TrySetResult(accepted);
                        return Task.FromResult(accepted);
                    },
                    _ => { },
                    _ => { },
                    timeout.Token,
                    localAddress: IPAddress.Loopback);
            host.ClearOfferSecrets();
            Task routeActiveTask =
                WaitUntilAsync(
                    () => host.IsRouteActive,
                    timeout.Token);

            bool accepted;
            try
            {
                accepted = await readyAccepted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    timeout.Token);
            }
            catch (TimeoutException)
            {
                LowLatencyVideoHostHandshakeSnapshot failedSnapshot =
                    host.CollectHandshakeSnapshot();
                Assert.Fail(
                    $"UDP handshake iteration {iteration + 1} timed out; " +
                    $"viewer probes={viewer.BindProbeSendSuccessCount}, " +
                    $"host={failedSnapshot}, logs={string.Join(" | ", logs)}");
                return;
            }

            Assert.True(
                accepted,
                $"UDP handshake iteration {iteration + 1} rejected Ready.");
            await routeActiveTask.WaitAsync(
                TimeSpan.FromSeconds(5),
                timeout.Token);
            Assert.True(host.IsRouteActive);
            Assert.True(host.IsPeerSocketConnected);
            Assert.Equal(
                IPAddress.Loopback,
                viewer.LocalEndpoint.Address);
            Assert.True(viewer.BindProbeSendSuccessCount >= 1);
            LowLatencyVideoHostHandshakeSnapshot snapshot =
                host.CollectHandshakeSnapshot();
            Assert.True(snapshot.RawPacketCount >= 1);
            Assert.True(snapshot.ValidProbeCount >= 1);
            // UDP delivery can let the viewer publish Ready before the host
            // receive thread returns from SendTo and increments its success
            // counter. Wait for that diagnostic write rather than treating
            // scheduler order as transport failure.
            await WaitUntilAsync(
                () => host.CollectHandshakeSnapshot().AckSentCount >= 1,
                timeout.Token);
            snapshot = host.CollectHandshakeSnapshot();
            Assert.True(snapshot.AckSentCount >= 1);
            Assert.Equal(0, snapshot.AddressRejectedCount);
            Assert.Equal(0, snapshot.DecryptFailureCount);
            CryptographicOperations.ZeroMemory(encodedOffer);
        }
    }

    [Fact]
    public async Task NegotiatedUdpMouseMoveUsesAuthenticatedLatestOnlyPath()
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.UdpMouseInput;
        var received = new ConcurrentQueue<
            RemoteInputCommand>();
        var latestReceived =
            new TaskCompletionSource<RemoteInputCommand>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        await using var host =
            new LowLatencyVideoHostTransport(
                _ => { },
                timeout.Token,
                applyUdpMouseMove: command =>
                {
                    received.Enqueue(command);
                    if (command.X == 99 &&
                        command.Y == 199)
                    {
                        latestReceived.TrySetResult(
                            command);
                    }
                });
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    features));
        byte[] encodedOffer =
            RemoteMessageCodec
                .EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec
                    .DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        await using var viewer =
            new LowLatencyVideoViewerTransport(
                viewerOffer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(
                            payload);
                    if (control.Kind ==
                        RemoteControlKind
                            .LowLatencyVideoReady)
                    {
                        Assert.True(
                            host.TryMarkReady(
                                control
                                    .LowLatencyVideoChannelId,
                                control
                                    .LowLatencyVideoEpoch));
                    }

                    return Task.FromResult(true);
                },
                _ => { },
                _ => { },
                timeout.Token,
                features);

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        Assert.False(
            viewer.TryQueueMouseMove(
                RemoteInputCommand.KeyDown(65)));
        for (int index = 0;
            index < 100;
            index++)
        {
            Assert.True(
                viewer.TryQueueMouseMove(
                    RemoteInputCommand.MouseMove(
                        index,
                        index + 100)));
        }

        RemoteInputCommand latest =
            await latestReceived.Task.WaitAsync(
                timeout.Token);
        Assert.Equal(
            RemoteInputKind.MouseMove,
            latest.Kind);
        Assert.Equal(99, latest.X);
        Assert.Equal(199, latest.Y);
        Assert.Equal(
            latest,
            received.Last());

        viewer.AcknowledgeStopped(
            offer.ChannelId,
            offer.Epoch);
        await WaitUntilAsync(
            () => viewer.IsShutdownCompleted,
            timeout.Token);
        Assert.False(
            viewer.TryQueueMouseMove(
                RemoteInputCommand.MouseMove(
                    100,
                    200)));

        CryptographicOperations.ZeroMemory(
            encodedOffer);
    }

    [Fact]
    public async Task MouseMoveAppliedAckIsSentOnlyAfterApplyReturns()
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.UdpMouseInput |
            LowLatencyVideoFeatures
                .UdpMouseInputAppliedAck;
        using var releaseApply =
            new ManualResetEventSlim(false);
        var applyEntered =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        await using var host =
            new LowLatencyVideoHostTransport(
                _ => { },
                timeout.Token,
                applyUdpMouseMove: _ =>
                {
                    applyEntered.TrySetResult();
                    releaseApply.Wait(timeout.Token);
                });
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    features));
        byte[] encodedOffer =
            RemoteMessageCodec
                .EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec
                    .DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        await using var viewer =
            new LowLatencyVideoViewerTransport(
                viewerOffer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(
                            payload);
                    if (control.Kind ==
                        RemoteControlKind
                            .LowLatencyVideoReady)
                    {
                        Assert.True(
                            host.TryMarkReady(
                                control
                                    .LowLatencyVideoChannelId,
                                control
                                    .LowLatencyVideoEpoch));
                    }

                    return Task.FromResult(true);
                },
                _ => { },
                _ => { },
                timeout.Token,
                features);

        try
        {
            await WaitUntilAsync(
                () => host.IsRouteActive,
                timeout.Token);
            Assert.True(
                viewer.TryQueueMouseMove(
                    RemoteInputCommand.MouseMove(
                        25,
                        35)));
            await applyEntered.Task.WaitAsync(
                timeout.Token);
            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                timeout.Token);

            LowLatencyMouseInputLatencySnapshot
                beforeApplyReturned =
                    viewer
                        .CollectMouseInputLatencySnapshot();
            Assert.Equal(
                1,
                beforeApplyReturned
                    .SentMouseMoveCount);
            Assert.Equal(
                0,
                beforeApplyReturned
                    .AcknowledgedMouseMoveCount);

            releaseApply.Set();
            await WaitUntilAsync(
                () => viewer
                    .CollectMouseInputLatencySnapshot()
                    .MatchedLatencySampleCount == 1,
                timeout.Token);

            LowLatencyMouseInputLatencySnapshot
                completed =
                    viewer
                        .CollectMouseInputLatencySnapshot();
            Assert.Equal(
                completed.LatestSentSequence,
                completed
                    .LatestAcknowledgedSequence);
            Assert.Equal(
                1,
                completed
                    .AcknowledgedMouseMoveCount);
            Assert.True(
                completed
                    .LatestRoundTripMilliseconds >=
                10);
            Assert.Equal(
                completed
                    .LatestRoundTripMilliseconds,
                completed
                    .SmoothedRoundTripMilliseconds);
            Assert.Equal(
                completed
                    .LatestRoundTripMilliseconds,
                completed
                    .MaximumRoundTripMilliseconds);
        }
        finally
        {
            releaseApply.Set();
            CryptographicOperations.ZeroMemory(
                encodedOffer);
        }
    }

    [Fact]
    public async Task MouseMoveAppliedAckIncludesMailboxSchedulingDelay()
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.UdpMouseInput |
            LowLatencyVideoFeatures
                .UdpMouseInputAppliedAck;
        var sendGateEntered =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var releaseSendGate =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        await using var host =
            new LowLatencyVideoHostTransport(
                _ => { },
                timeout.Token,
                applyUdpMouseMove: _ => { });
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    features));
        byte[] encodedOffer =
            RemoteMessageCodec
                .EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec
                    .DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        await using var viewer =
            new LowLatencyVideoViewerTransport(
                viewerOffer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(
                            payload);
                    if (control.Kind ==
                        RemoteControlKind
                            .LowLatencyVideoReady)
                    {
                        Assert.True(
                            host.TryMarkReady(
                                control
                                    .LowLatencyVideoChannelId,
                                control
                                    .LowLatencyVideoEpoch));
                    }

                    return Task.FromResult(true);
                },
                _ => { },
                _ => { },
                timeout.Token,
                features,
                async cancellationToken =>
                {
                    sendGateEntered.TrySetResult();
                    await releaseSendGate.Task.WaitAsync(
                        cancellationToken);
                });

        try
        {
            await WaitUntilAsync(
                () => host.IsRouteActive,
                timeout.Token);
            var mailboxDelay =
                Stopwatch.StartNew();
            Assert.True(
                viewer.TryQueueMouseMove(
                    RemoteInputCommand.MouseMove(
                        25,
                        35)));
            await sendGateEntered.Task.WaitAsync(
                timeout.Token);
            await Task.Delay(
                TimeSpan.FromMilliseconds(75),
                timeout.Token);
            double minimumExpectedMilliseconds =
                mailboxDelay.Elapsed.TotalMilliseconds;

            LowLatencyMouseInputLatencySnapshot
                beforeSend =
                    viewer
                        .CollectMouseInputLatencySnapshot();
            Assert.Equal(
                0,
                beforeSend.SentMouseMoveCount);

            releaseSendGate.TrySetResult();
            await WaitUntilAsync(
                () => viewer
                    .CollectMouseInputLatencySnapshot()
                    .MatchedLatencySampleCount == 1,
                timeout.Token);

            LowLatencyMouseInputLatencySnapshot
                completed =
                    viewer
                        .CollectMouseInputLatencySnapshot();
            Assert.True(
                completed.LatestRoundTripMilliseconds >=
                    minimumExpectedMilliseconds - 10,
                $"ACK latency {completed.LatestRoundTripMilliseconds:F2}ms " +
                $"did not include the {minimumExpectedMilliseconds:F2}ms " +
                "mailbox/sender scheduling delay.");
        }
        finally
        {
            releaseSendGate.TrySetResult();
            CryptographicOperations.ZeroMemory(
                encodedOffer);
        }
    }

    [Fact]
    public async Task HostBacklogAppliesAndAcknowledgesOnlyNewestMouseMove()
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.UdpMouseInput |
            LowLatencyVideoFeatures
                .UdpMouseInputAppliedAck;
        const int moveCount = 64;
        using var releaseFirstApply =
            new ManualResetEventSlim(false);
        var appliedSequences =
            new ConcurrentQueue<int>();
        var firstApplyEntered =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var latestApplied =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host =
            new LowLatencyVideoHostTransport(
                _ => { },
                timeout.Token,
                applyUdpMouseMove: command =>
                {
                    appliedSequences.Enqueue(command.X);
                    if (command.X == 1)
                    {
                        firstApplyEntered.TrySetResult();
                        releaseFirstApply.Wait(timeout.Token);
                    }

                    if (command.X == moveCount)
                    {
                        latestApplied.TrySetResult();
                    }
                });
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    features));
        byte[] encodedOffer =
            RemoteMessageCodec
                .EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec
                    .DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();

        using var viewerSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        viewerSocket.Bind(
            new IPEndPoint(
                IPAddress.Loopback,
                0));
        using var viewerSendCipher =
            new LowLatencyVideoSendCipher(
                viewerOffer.ViewerToHostKey,
                viewerOffer.ViewerNoncePrefix,
                viewerOffer.ChannelId,
                viewerOffer.Epoch);
        using var viewerReceiveCipher =
            new LowLatencyVideoReceiveCipher(
                viewerOffer.HostToViewerKey,
                viewerOffer.HostNoncePrefix,
                viewerOffer.ChannelId,
                viewerOffer.Epoch);
        var hostEndpoint = new IPEndPoint(
            IPAddress.Loopback,
            viewerOffer.Port);

        byte[] probe = viewerSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindProbe,
            0,
            viewerOffer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            viewerOffer.Challenge);
        await viewerSocket.SendToAsync(
            probe,
            SocketFlags.None,
            hostEndpoint,
            timeout.Token);
        CryptographicOperations.ZeroMemory(probe);

        LowLatencyVideoDatagram bindAck =
            await ReceiveAuthenticatedDatagramAsync(
                viewerSocket,
                viewerReceiveCipher,
                timeout.Token);
        Assert.Equal(
            LowLatencyVideoDatagramKind.BindAck,
            bindAck.Kind);
        CryptographicOperations.ZeroMemory(
            bindAck.Plaintext.Span);
        Assert.True(
            host.TryMarkReady(
                viewerOffer.ChannelId,
                viewerOffer.Epoch));
        host.ClearOfferSecrets();

        byte[] inputPayload =
            new byte[
                RemoteMessageCodec
                    .InputPayloadLength];
        try
        {
            for (int index = 1;
                index <= moveCount;
                index++)
            {
                RemoteMessageCodec.WriteInputPayload(
                    RemoteInputCommand.MouseMove(
                        index,
                        index + 100),
                    inputPayload);
                byte[] datagram =
                    viewerSendCipher.Encrypt(
                        LowLatencyVideoDatagramKind
                            .MouseMove,
                        checked((ulong)index),
                        inputPayload.Length,
                        0,
                        0,
                        1,
                        MessageType.Input,
                        0,
                        inputPayload);
                await viewerSocket.SendToAsync(
                    datagram,
                    SocketFlags.None,
                    hostEndpoint,
                    timeout.Token);
                CryptographicOperations.ZeroMemory(
                    datagram);

                if (index == 1)
                {
                    await firstApplyEntered.Task.WaitAsync(
                        timeout.Token);
                }
            }

            // Keep the first synchronous injection blocked until every later
            // authenticated position is already waiting in the host socket.
            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                timeout.Token);
            releaseFirstApply.Set();
            await latestApplied.Task.WaitAsync(
                timeout.Token);

            Assert.Equal(
                new[] { 1, moveCount },
                appliedSequences.ToArray());

            var acknowledgedSequences =
                new List<ulong>();
            while (acknowledgedSequences.Count == 0 ||
                acknowledgedSequences[^1] !=
                    moveCount)
            {
                LowLatencyVideoDatagram ack =
                    await ReceiveAuthenticatedDatagramAsync(
                        viewerSocket,
                        viewerReceiveCipher,
                        timeout.Token);
                try
                {
                    Assert.Equal(
                        LowLatencyVideoDatagramKind
                            .MouseMoveAppliedAck,
                        ack.Kind);
                    Assert.Equal(0, ack.FrameLength);
                    Assert.Equal(0, ack.FragmentOffset);
                    Assert.Equal(0, ack.FragmentIndex);
                    Assert.Equal(1, ack.FragmentCount);
                    Assert.Equal(
                        MessageType.Input,
                        ack.FrameKind);
                    Assert.Equal(0, ack.Flags);
                    Assert.Equal(
                        0,
                        ack.Plaintext.Length);
                    acknowledgedSequences.Add(
                        ack.FrameSequence);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(
                        ack.Plaintext.Span);
                }
            }

            Assert.Equal(
                acknowledgedSequences
                    .Order(),
                acknowledgedSequences);
            Assert.Equal(
                checked((ulong)moveCount),
                acknowledgedSequences[^1]);
            Assert.All(
                acknowledgedSequences,
                sequence =>
                    Assert.Contains(
                        sequence,
                        new ulong[]
                        {
                            1,
                            moveCount
                        }));
            Assert.InRange(
                acknowledgedSequences.Count,
                1,
                2);
        }
        finally
        {
            releaseFirstApply.Set();
            CryptographicOperations.ZeroMemory(
                inputPayload);
            CryptographicOperations.ZeroMemory(
                encodedOffer);
        }
    }

    [Fact]
    public async Task UdpMouseMoveRequiresNegotiatedCapability()
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
        await using var host =
            new LowLatencyVideoHostTransport(
                _ => { },
                timeout.Token,
                applyUdpMouseMove: _ =>
                    throw new InvalidOperationException(
                        "UDP mouse input was not negotiated."));
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(
                    IPAddress.Loopback,
                    LowLatencyVideoFeatures
                        .CongestionFeedback));
        byte[] encodedOffer =
            RemoteMessageCodec
                .EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec
                    .DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        await using var viewer =
            new LowLatencyVideoViewerTransport(
                viewerOffer,
                IPAddress.Loopback,
                payload =>
                {
                    RemoteControlMessage control =
                        RemoteMessageCodec.DecodeControl(
                            payload);
                    if (control.Kind ==
                        RemoteControlKind
                            .LowLatencyVideoReady)
                    {
                        Assert.True(
                            host.TryMarkReady(
                                control
                                    .LowLatencyVideoChannelId,
                                control
                                    .LowLatencyVideoEpoch));
                    }

                    return Task.FromResult(true);
                },
                _ => { },
                _ => { },
                timeout.Token,
                LowLatencyVideoFeatures
                    .CongestionFeedback);

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        Assert.False(
            viewer.TryQueueMouseMove(
                RemoteInputCommand.MouseMove(
                    10,
                    20)));

        CryptographicOperations.ZeroMemory(
            encodedOffer);
    }

    [Fact]
    public void IndependentRecoveryPolicyRequiresCompleteVideoRecoveryPoint()
    {
        Assert.True(
            LowLatencyVideoHostTransport.IsIndependentlyRecoverableFrame(
                MessageType.Frame,
                RemoteFrameFlags.None));
        Assert.True(
            LowLatencyVideoHostTransport.IsIndependentlyRecoverableFrame(
                MessageType.VideoFrame,
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig));
        Assert.False(
            LowLatencyVideoHostTransport.IsIndependentlyRecoverableFrame(
                MessageType.VideoFrame,
                RemoteFrameFlags.None));
        Assert.False(
            LowLatencyVideoHostTransport.IsIndependentlyRecoverableFrame(
                MessageType.VideoFrame,
                RemoteFrameFlags.KeyFrame));
        Assert.False(
            LowLatencyVideoHostTransport.IsIndependentlyRecoverableFrame(
                MessageType.VideoFrame,
                RemoteFrameFlags.CodecConfig));
        Assert.False(
            LowLatencyVideoHostTransport.IsIndependentlyRecoverableFrame(
                MessageType.VideoFrame,
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig |
                    (RemoteFrameFlags)(1 << 8)));
        Assert.False(
            LowLatencyVideoHostTransport.IsIndependentlyRecoverableFrame(
                MessageType.Control,
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig));
    }

    [Fact]
    public void ShortGopViewerDropsOrphanDependentFramesUntilRecovery()
    {
        static RemoteFrame Frame(RemoteFrameFlags flags) =>
            new(
                1920,
                1080,
                RemoteFrameEncoding.H264AnnexB,
                flags,
                [0, 0, 1, 0x65],
                0,
                4,
                0,
                0);

        long lastPublishedSequence = 0;
        bool waitingForRecovery = true;
        RemoteFrame recovery = Frame(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig);
        RemoteFrame dependent = Frame(
            RemoteFrameFlags.None);

        Assert.False(
            LowLatencyVideoViewerTransport
                .ShouldPublishShortGopFrame(
                    1,
                    dependent,
                    ref lastPublishedSequence,
                    ref waitingForRecovery));
        Assert.True(
            LowLatencyVideoViewerTransport
                .ShouldPublishShortGopFrame(
                    2,
                    recovery,
                    ref lastPublishedSequence,
                    ref waitingForRecovery));
        Assert.True(
            LowLatencyVideoViewerTransport
                .ShouldPublishShortGopFrame(
                    3,
                    dependent,
                    ref lastPublishedSequence,
                    ref waitingForRecovery));
        Assert.False(
            LowLatencyVideoViewerTransport
                .ShouldPublishShortGopFrame(
                    5,
                    dependent,
                    ref lastPublishedSequence,
                    ref waitingForRecovery));
        Assert.False(
            LowLatencyVideoViewerTransport
                .ShouldPublishShortGopFrame(
                    6,
                    dependent,
                    ref lastPublishedSequence,
                    ref waitingForRecovery));
        Assert.True(
            LowLatencyVideoViewerTransport
                .ShouldPublishShortGopFrame(
                    7,
                    recovery,
                    ref lastPublishedSequence,
                    ref waitingForRecovery));
        Assert.False(
            LowLatencyVideoViewerTransport
                .ShouldPublishShortGopFrame(
                    6,
                    recovery,
                    ref lastPublishedSequence,
                    ref waitingForRecovery));
    }

    [Fact]
    public async Task VideoFrameUsesTrueKindForDataAndFecAndRoundTrips()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.XorFec;
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback, features));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();

        using var viewerSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        viewerSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var viewerSendCipher = new LowLatencyVideoSendCipher(
            viewerOffer.ViewerToHostKey,
            viewerOffer.ViewerNoncePrefix,
            viewerOffer.ChannelId,
            viewerOffer.Epoch);
        using var viewerReceiveCipher = new LowLatencyVideoReceiveCipher(
            viewerOffer.HostToViewerKey,
            viewerOffer.HostNoncePrefix,
            viewerOffer.ChannelId,
            viewerOffer.Epoch);
        var hostEndpoint = new IPEndPoint(
            IPAddress.Loopback,
            viewerOffer.Port);

        byte[] probe = viewerSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindProbe,
            0,
            viewerOffer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            viewerOffer.Challenge);
        await viewerSocket.SendToAsync(
            probe,
            SocketFlags.None,
            hostEndpoint,
            timeout.Token);

        LowLatencyVideoDatagram bindAck =
            await ReceiveAuthenticatedDatagramAsync(
                viewerSocket,
                viewerReceiveCipher,
                timeout.Token);
        Assert.Equal(
            LowLatencyVideoDatagramKind.BindAck,
            bindAck.Kind);
        Assert.True(CryptographicOperations.FixedTimeEquals(
            viewerOffer.Challenge,
            bindAck.Plaintext.Span));
        CryptographicOperations.ZeroMemory(bindAck.Plaintext.Span);
        Assert.True(host.TryMarkReady(
            viewerOffer.ChannelId,
            viewerOffer.Epoch));
        host.ClearOfferSecrets();
        host.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);

        byte[] encodedBytes = Enumerable.Range(0, 24 * 1024 + 37)
            .Select(index => (byte)(index % 251))
            .ToArray();
        byte[] expectedEncodedBytes = encodedBytes.ToArray();
        int frameLength =
            RemoteMessageCodec.VideoFrameHeaderLength +
            encodedBytes.Length;
        int dataFragmentCount =
            LowLatencyVideoProtocol.GetFrameFragmentCount(
                frameLength,
                viewerOffer.MaxDatagramBytes);
        Assert.True(
            LowLatencyVideoProtocol.ShouldSendXorFec(
                dataFragmentCount));
        int parityFragmentCount =
            LowLatencyVideoProtocol.GetXorFecGroupCount(
                dataFragmentCount);

        Assert.True(host.TryQueueVideoFrame(
            1920,
            1080,
            RemoteFrameEncoding.H264AnnexB,
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            1.75,
            2.25,
            encodedBytes,
            allowLatencyBudgetDrop: true));
        encodedBytes.AsSpan().Fill(0xEE);

        var packets = new List<LowLatencyVideoDatagram>(
            dataFragmentCount + parityFragmentCount);
        int receivedDataFragments = 0;
        int receivedParityFragments = 0;
        while (receivedDataFragments < dataFragmentCount ||
            receivedParityFragments < parityFragmentCount)
        {
            LowLatencyVideoDatagram packet =
                await ReceiveAuthenticatedDatagramAsync(
                    viewerSocket,
                    viewerReceiveCipher,
                    timeout.Token);
            if (packet.Kind is not (
                LowLatencyVideoDatagramKind.FrameFragment or
                LowLatencyVideoDatagramKind.FrameXorParity))
            {
                CryptographicOperations.ZeroMemory(
                    packet.Plaintext.Span);
                continue;
            }

            Assert.Equal(MessageType.VideoFrame, packet.FrameKind);
            packets.Add(packet);
            if (packet.Kind ==
                LowLatencyVideoDatagramKind.FrameFragment)
            {
                receivedDataFragments++;
            }
            else
            {
                receivedParityFragments++;
            }
        }

        var reassembler = new LowLatencyVideoFrameReassembler(
            viewerOffer.MaxFrameBytes,
            viewerOffer.MaxDatagramBytes,
            enableXorFec: true);
        byte[]? completedPayload = null;
        MessageType completedKind = default;
        bool skippedDataFragment = false;
        foreach (LowLatencyVideoDatagram packet in packets)
        {
            if (!skippedDataFragment &&
                packet.Kind ==
                    LowLatencyVideoDatagramKind.FrameFragment &&
                packet.FragmentIndex == 0)
            {
                skippedDataFragment = true;
                continue;
            }

            if (reassembler.TryAdd(
                    packet,
                    out MessageType frameKind,
                    out byte[]? payload))
            {
                completedKind = frameKind;
                completedPayload = payload;
            }
        }

        Assert.True(skippedDataFragment);
        Assert.Equal(MessageType.VideoFrame, completedKind);
        Assert.NotNull(completedPayload);
        RemoteFrame frame =
            RemoteMessageCodec.DecodeVideoFrame(completedPayload);
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(RemoteFrameEncoding.H264AnnexB, frame.Encoding);
        Assert.Equal(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            frame.Flags);
        Assert.Equal(1.75, frame.CaptureMilliseconds);
        Assert.Equal(2.25, frame.EncodeMilliseconds);
        Assert.Equal(
            expectedEncodedBytes,
            frame.EncodedBuffer.AsSpan(
                frame.EncodedOffset,
                frame.EncodedLength).ToArray());

        foreach (LowLatencyVideoDatagram packet in packets)
        {
            CryptographicOperations.ZeroMemory(
                packet.Plaintext.Span);
        }
        CryptographicOperations.ZeroMemory(probe);
        CryptographicOperations.ZeroMemory(encodedOffer);
        CryptographicOperations.ZeroMemory(expectedEncodedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VideoFrameUdpAdmissionAndInFlightCompletionRequireRecoveryPoint(
        bool pendingIsRecoveryPoint)
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback;
        var logs = new ConcurrentQueue<string>();
        var fallbackBarrier =
            new TaskCompletionSource<(ulong ChannelId, uint Epoch, byte Reason)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new LowLatencyVideoHostTransport(
            logs.Enqueue,
            timeout.Token,
            (channelId, epoch, reason) =>
            {
                fallbackBarrier.TrySetResult(
                    (channelId, epoch, reason));
                return Task.CompletedTask;
            });
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback, features));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var publishedEncodings =
            new ConcurrentQueue<RemoteFrameEncoding>();
        var pendingFrameReceived =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }

                return Task.FromResult(true);
            },
            frame =>
            {
                publishedEncodings.Enqueue(frame.Encoding);
                if (frame.Encoding ==
                    RemoteFrameEncoding.H264AnnexB)
                {
                    pendingFrameReceived.TrySetResult();
                }
            },
            _ => { },
            timeout.Token,
            features);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        byte[] currentJpeg = new byte[1024 * 1024];
        currentJpeg.AsSpan().Fill(0x11);
        Assert.True(host.TryQueueJpegFrame(
            1920,
            1080,
            1,
            2,
            currentJpeg,
            allowLatencyBudgetDrop: true));
        await WaitUntilAsync(
            () => !host.HasPendingFrame,
            timeout.Token);

        RemoteFrameFlags pendingFlags = pendingIsRecoveryPoint
            ? RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig
            : RemoteFrameFlags.None;
        bool queuedForUdp = host.TryQueueVideoFrame(
            1920,
            1080,
            RemoteFrameEncoding.H264AnnexB,
            pendingFlags,
            1,
            2,
            new byte[] { 0x00, 0x00, 0x01, 0x65 },
            allowLatencyBudgetDrop: true);
        if (pendingIsRecoveryPoint)
        {
            Assert.True(queuedForUdp);
            await pendingFrameReceived.Task.WaitAsync(timeout.Token);
            LowLatencyVideoNetworkSnapshot snapshot =
                host.CollectNetworkSnapshot();
            Assert.Equal(0, snapshot.AbortedFrameSends);
            Assert.True(host.IsRouteActive);
            Assert.False(fallbackBarrier.Task.IsCompleted);
            Assert.Contains(
                RemoteFrameEncoding.Jpeg,
                publishedEncodings);
        }
        else
        {
            Assert.False(queuedForUdp);
            Assert.False(host.IsRouteActive);
            (ulong channelId, uint epoch, byte reason) =
                await fallbackBarrier.Task.WaitAsync(timeout.Token);
            Assert.Equal(offer.ChannelId, channelId);
            Assert.Equal(offer.Epoch, epoch);
            Assert.Equal(1, reason);
            Assert.True(viewer.ShouldIgnoreTcpFrames);
            viewer.AcknowledgeStopped(channelId, epoch);
            Assert.False(viewer.ShouldIgnoreTcpFrames);
            Assert.Contains(
                logs,
                message => message.Contains(
                    "未协商的依赖帧",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                RemoteFrameEncoding.H264AnnexB,
                publishedEncodings);
        }

        CryptographicOperations.ZeroMemory(encodedOffer);
        CryptographicOperations.ZeroMemory(currentJpeg);
    }

    [Fact]
    public async Task SustainedLargeRecoveryFramesCompleteWithoutSerializationStarvation()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(8));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.XorFec;
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback, features));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var periodicRecovery = new TaskCompletionSource<RemoteFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int completedFrames = 0;
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }

                return Task.FromResult(true);
            },
            frame =>
            {
                if (Interlocked.Increment(ref completedFrames) == 2)
                {
                    periodicRecovery.TrySetResult(
                        CloneBorrowedFrame(frame));
                }
            },
            _ => { },
            timeout.Token,
            features);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        host.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);
        SetHostTargetBitsPerSecondForTest(
            host,
            LowLatencyVideoCongestionController
                .MinimumTargetBitsPerSecond);

        byte[] accessUnit = new byte[85_352];
        accessUnit.AsSpan().Fill(0x65);
        Assert.Equal(
            76,
            LowLatencyVideoProtocol.GetFrameFragmentCount(
                RemoteMessageCodec.VideoFrameHeaderLength +
                    accessUnit.Length,
                viewerOffer.MaxDatagramBytes));
        using var producerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task producer = Task.Run(
            async () =>
            {
                while (!producerCancellation.IsCancellationRequested)
                {
                    Assert.True(host.TryQueueVideoFrame(
                        2880,
                        1620,
                        RemoteFrameEncoding.H264AnnexB,
                        RemoteFrameFlags.KeyFrame |
                            RemoteFrameFlags.CodecConfig,
                        0,
                        0,
                        accessUnit,
                        allowLatencyBudgetDrop: true));
                    await Task.Delay(
                        1,
                        producerCancellation.Token);
                }
            },
            producerCancellation.Token);

        try
        {
            RemoteFrame frame =
                await periodicRecovery.Task.WaitAsync(
                    TimeSpan.FromSeconds(1.5),
                    timeout.Token);
            Assert.False(producer.IsCompleted);
            Assert.True(Volatile.Read(ref completedFrames) >= 2);
            Assert.Equal(2880, frame.Width);
            Assert.Equal(1620, frame.Height);
            Assert.Equal(
                RemoteFrameEncoding.H264AnnexB,
                frame.Encoding);
            Assert.Equal(
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig,
                frame.Flags);
            Assert.Equal(accessUnit.Length, frame.EncodedLength);
            Assert.True(host.IsRouteActive);
            Assert.True(
                host.CollectNetworkSnapshot().AbortedFrameSends > 0);
        }
        finally
        {
            producerCancellation.Cancel();
            try
            {
                await producer;
            }
            catch (OperationCanceledException)
            {
            }
            CryptographicOperations.ZeroMemory(encodedOffer);
            CryptographicOperations.ZeroMemory(accessUnit);
        }
    }

    [Fact]
    public async Task ShortGopDependentQueuesBehindMatchingRecoveryInFlight()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.XorFec |
            LowLatencyVideoFeatures.ShortGopH264;
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback, features));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var receivedFlags = new ConcurrentQueue<RemoteFrameFlags>();
        var receivedPair = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }

                return Task.FromResult(true);
            },
            frame =>
            {
                receivedFlags.Enqueue(frame.Flags);
                if (receivedFlags.Count >= 2)
                {
                    receivedPair.TrySetResult();
                }
            },
            _ => { },
            timeout.Token,
            features);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        host.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);
        SetHostTargetBitsPerSecondForTest(
            host,
            LowLatencyVideoCongestionController
                .MinimumTargetBitsPerSecond);

        byte[] recovery = new byte[85_352];
        recovery.AsSpan().Fill(0x65);
        byte[] dependent = [0x00, 0x00, 0x01, 0x41, 0x01];
        try
        {
            Assert.True(host.TryQueueVideoFrame(
                1920,
                1080,
                RemoteFrameEncoding.H264AnnexB,
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig,
                0,
                0,
                recovery,
                allowLatencyBudgetDrop: true));
            await WaitUntilAsync(
                () => !host.HasPendingFrame,
                timeout.Token);

            Assert.True(host.TryQueueVideoFrame(
                1920,
                1080,
                RemoteFrameEncoding.H264AnnexB,
                RemoteFrameFlags.None,
                0,
                0,
                dependent,
                allowLatencyBudgetDrop: true));
            await receivedPair.Task.WaitAsync(timeout.Token);

            RemoteFrameFlags[] flags = receivedFlags.ToArray();
            Assert.True(flags.Length >= 2);
            Assert.True(flags[0].HasFlag(RemoteFrameFlags.KeyFrame));
            Assert.False(flags[1].HasFlag(RemoteFrameFlags.KeyFrame));
            Assert.Equal(
                0,
                host.CollectNetworkSnapshot()
                    .SenderQueueDropRatio);
            LowLatencyVideoSendSnapshot sendSnapshot =
                host.CollectSendSnapshot();
            Assert.Equal(
                0,
                sendSnapshot.SuppressedShortGopDependentFrames);
            Assert.True(
                sendSnapshot.SerializedShortGopRecoveryFrames >= 1);
            Assert.True(
                sendSnapshot.SerializedShortGopDependentFrames >= 1);
            Assert.Equal(0, sendSnapshot.ReplacedPendingFrames);
            Assert.True(host.IsRouteActive);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedOffer);
            CryptographicOperations.ZeroMemory(recovery);
            CryptographicOperations.ZeroMemory(dependent);
        }
    }

    [Fact]
    public async Task LegacyTransportWithoutCongestionFeedbackStillEnforcesSerializationBudget()
    {
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            CancellationToken.None);

        Assert.False(host.ShouldAbandonCurrentFrame(
            Stopwatch.GetTimestamp(),
            currentFrameIsIndependentlyRecoverable: true,
            congestionController: null));

        long expiredFrameStartedAt =
            Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        Assert.True(host.ShouldAbandonCurrentFrame(
            expiredFrameStartedAt,
            currentFrameIsIndependentlyRecoverable: true,
            congestionController: null));
    }

    [Fact]
    public async Task HighFrameRateTransportUsesConfiguredSerializationBudget()
    {
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            CancellationToken.None);
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);

        long withinBudgetStartedAt =
            Stopwatch.GetTimestamp() -
            Stopwatch.Frequency / 100;
        Assert.False(host.ShouldAbandonCurrentFrame(
            withinBudgetStartedAt,
            currentFrameIsIndependentlyRecoverable: false,
            congestionController: controller));

        long expiredFrameStartedAt =
            Stopwatch.GetTimestamp() -
            Stopwatch.Frequency / 10;
        Assert.True(host.ShouldAbandonCurrentFrame(
            expiredFrameStartedAt,
            currentFrameIsIndependentlyRecoverable: false,
            congestionController: controller));
    }

    [Fact]
    public async Task FirstGop1RecoveryKeepsLegacyHardCeiling()
    {
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            CancellationToken.None);
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);

        long beyondHighFrameRateBudget =
            Stopwatch.GetTimestamp() -
            Stopwatch.Frequency / 10;
        Assert.False(host.ShouldAbandonCurrentFrame(
            beyondHighFrameRateBudget,
            currentFrameIsIndependentlyRecoverable: true,
            congestionController: controller,
            currentFrameIsGop1Video: true));

        long beyondLegacyHardCeiling =
            Stopwatch.GetTimestamp() -
            Stopwatch.Frequency;
        Assert.True(host.ShouldAbandonCurrentFrame(
            beyondLegacyHardCeiling,
            currentFrameIsIndependentlyRecoverable: true,
            congestionController: controller,
            currentFrameIsGop1Video: true));
    }

    [Fact]
    public void Gop1PreemptionRequiresRecoveryAndOneFrameAge()
    {
        Assert.False(
            LowLatencyVideoHostTransport
                .ShouldPreemptForNewerRecovery(
                    currentFrameIsIndependentlyRecoverable: true,
                    currentFrameIsGop1Video: true,
                    pendingFrameIsIndependentlyRecoverable: true,
                    pendingFrameIsGop1Video: true,
                    hasRecentSerializedGop1Recovery: false,
                    currentFrameReachedPreemptionAge: true));
        Assert.False(
            LowLatencyVideoHostTransport
                .ShouldPreemptForNewerRecovery(
                    currentFrameIsIndependentlyRecoverable: true,
                    currentFrameIsGop1Video: true,
                    pendingFrameIsIndependentlyRecoverable: true,
                    pendingFrameIsGop1Video: true,
                    hasRecentSerializedGop1Recovery: true,
                    currentFrameReachedPreemptionAge: false));
        Assert.True(
            LowLatencyVideoHostTransport
                .ShouldPreemptForNewerRecovery(
                    currentFrameIsIndependentlyRecoverable: true,
                    currentFrameIsGop1Video: true,
                    pendingFrameIsIndependentlyRecoverable: true,
                    pendingFrameIsGop1Video: true,
                    hasRecentSerializedGop1Recovery: true,
                    currentFrameReachedPreemptionAge: true));
        Assert.False(
            LowLatencyVideoHostTransport
                .ShouldPreemptForNewerRecovery(
                    currentFrameIsIndependentlyRecoverable: true,
                    currentFrameIsGop1Video: false,
                    pendingFrameIsIndependentlyRecoverable: true,
                    pendingFrameIsGop1Video: true,
                    hasRecentSerializedGop1Recovery: true,
                    currentFrameReachedPreemptionAge: true));
    }

    [Fact]
    public void Gop1PreemptionAgeUsesOneConfiguredFramePeriod()
    {
        TimeSpan serializationBudget =
            LowLatencyVideoCongestionController
                .HighFrameRateFrameSerializationBudget;

        Assert.Equal(
            TimeSpan.FromSeconds(1d / 60),
            LowLatencyVideoHostTransport
                .CalculateGop1PreemptionMinimumAge(
                    configuredFramesPerSecond: 60,
                    serializationBudget));
        Assert.Equal(
            serializationBudget,
            LowLatencyVideoHostTransport
                .CalculateGop1PreemptionMinimumAge(
                    configuredFramesPerSecond: 0,
                    serializationBudget));
    }

    [Fact]
    public void Gop1RecoveryFreshnessExpiresAndIsScopedToResolution()
    {
        long currentStream =
            LowLatencyVideoHostTransport
                .CreateGop1StreamSignature(
                    3840,
                    2160);
        long otherResolution =
            LowLatencyVideoHostTransport
                .CreateGop1StreamSignature(
                    2560,
                    1440);
        long now = Stopwatch.GetTimestamp();
        long recent =
            now -
            Stopwatch.Frequency / 10;
        long expired =
            now -
            Stopwatch.Frequency;

        Assert.True(
            LowLatencyVideoHostTransport
                .HasRecentSerializedGop1Recovery(
                    currentStream,
                    currentStream,
                    recent,
                    now));
        Assert.False(
            LowLatencyVideoHostTransport
                .HasRecentSerializedGop1Recovery(
                    currentStream,
                    currentStream,
                    expired,
                    now));
        Assert.False(
            LowLatencyVideoHostTransport
                .HasRecentSerializedGop1Recovery(
                    currentStream,
                    otherResolution,
                    recent,
                    now));
    }

    [Fact]
    public void SustainedGop1ReplacementEventuallyProtectsAnotherRecovery()
    {
        long streamSignature =
            LowLatencyVideoHostTransport
                .CreateGop1StreamSignature(
                    3840,
                    2160);
        long serializedAt = Stopwatch.GetTimestamp();
        TimeSpan framePeriod =
            LowLatencyVideoHostTransport
                .CalculateGop1PreemptionMinimumAge(
                    configuredFramesPerSecond: 60,
                    LowLatencyVideoCongestionController
                        .HighFrameRateFrameSerializationBudget);
        bool observedSafePreemption = false;
        bool observedPeriodicProtection = false;

        for (int frameNumber = 1;
            frameNumber <= 20;
            frameNumber++)
        {
            TimeSpan elapsed =
                TimeSpan.FromTicks(
                    checked(framePeriod.Ticks * frameNumber));
            long now = serializedAt +
                checked((long)Math.Ceiling(
                    elapsed.TotalSeconds *
                    Stopwatch.Frequency));
            bool hasRecentRecovery =
                LowLatencyVideoHostTransport
                    .HasRecentSerializedGop1Recovery(
                        streamSignature,
                        streamSignature,
                        serializedAt,
                        now);
            bool shouldPreempt =
                LowLatencyVideoHostTransport
                    .ShouldPreemptForNewerRecovery(
                        currentFrameIsIndependentlyRecoverable: true,
                        currentFrameIsGop1Video: true,
                        pendingFrameIsIndependentlyRecoverable: true,
                        pendingFrameIsGop1Video: true,
                        hasRecentSerializedGop1Recovery:
                            hasRecentRecovery,
                        currentFrameReachedPreemptionAge: true);

            if (hasRecentRecovery)
            {
                Assert.True(shouldPreempt);
                observedSafePreemption = true;
            }
            else
            {
                Assert.False(shouldPreempt);
                observedPeriodicProtection = true;
                break;
            }
        }

        Assert.True(observedSafePreemption);
        Assert.True(observedPeriodicProtection);
    }

    [Fact]
    public async Task ExpiredGop1RecoveryPeriodicallyProtectsACompleteFrame()
    {
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            CancellationToken.None);
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);
        long streamSignature =
            LowLatencyVideoHostTransport
                .CreateGop1StreamSignature(
                    3840,
                    2160);
        long currentFrameStartedAt =
            Stopwatch.GetTimestamp() -
            Stopwatch.Frequency / 10;

        SetHostGop1RecoveryStateForTest(
            host,
            streamSignature,
            Stopwatch.GetTimestamp() -
                Stopwatch.Frequency / 100);
        Assert.True(host.ShouldAbandonCurrentFrame(
            currentFrameStartedAt,
            currentFrameIsIndependentlyRecoverable: true,
            congestionController: controller,
            currentFrameIsGop1Video: true,
            currentGop1StreamSignature: streamSignature));

        SetHostGop1RecoveryStateForTest(
            host,
            streamSignature,
            Stopwatch.GetTimestamp() -
                Stopwatch.Frequency);
        Assert.False(host.ShouldAbandonCurrentFrame(
            currentFrameStartedAt,
            currentFrameIsIndependentlyRecoverable: true,
            congestionController: controller,
            currentFrameIsGop1Video: true,
            currentGop1StreamSignature: streamSignature));

        long otherResolution =
            LowLatencyVideoHostTransport
                .CreateGop1StreamSignature(
                    2560,
                    1440);
        SetHostGop1RecoveryStateForTest(
            host,
            otherResolution,
            Stopwatch.GetTimestamp());
        Assert.False(host.ShouldAbandonCurrentFrame(
            currentFrameStartedAt,
            currentFrameIsIndependentlyRecoverable: true,
            congestionController: controller,
            currentFrameIsGop1Video: true,
            currentGop1StreamSignature: streamSignature));
    }

    [Fact]
    public async Task VideoFrameOfferLimitIncludesVideoHeader()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }

                return Task.FromResult(true);
            },
            _ => { },
            _ => { },
            timeout.Token);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        byte[] encodedBytes = new byte[
            offer.MaxFrameBytes -
            RemoteMessageCodec.VideoFrameHeaderLength +
            1];

        Assert.False(host.TryQueueVideoFrame(
            1920,
            1080,
            RemoteFrameEncoding.H264AnnexB,
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            1,
            2,
            encodedBytes,
            allowLatencyBudgetDrop: true));
        Assert.False(host.IsRouteActive);

        CryptographicOperations.ZeroMemory(encodedOffer);
        CryptographicOperations.ZeroMemory(encodedBytes);
    }

    [Fact]
    public async Task ViewerWaitsPastHardwareProbeBeforeInitialFrameFallback()
    {
        using var timeout = new CancellationTokenSource(
            LowLatencyVideoViewerTransport.InitialFrameTimeout +
                TimeSpan.FromSeconds(2));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer = RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer = Assert.IsType<LowLatencyVideoOffer>(
            RemoteMessageCodec.DecodeControl(encodedOffer).LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var stopReceived = new TaskCompletionSource<RemoteControlMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind == RemoteControlKind.LowLatencyVideoReady)
                {
                    host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch);
                }
                else if (control.Kind == RemoteControlKind.LowLatencyVideoStop)
                {
                    host.DisableRoute();
                    stopReceived.TrySetResult(control);
                }

                return Task.FromResult(true);
            },
            _ => { },
            _ => { },
            timeout.Token);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        await Task.Delay(
            RemoteHostServer.HardwareH264ProbeDeadline +
                TimeSpan.FromMilliseconds(250),
            timeout.Token);
        Assert.False(stopReceived.Task.IsCompleted);
        Assert.True(viewer.ShouldIgnoreTcpFrames);

        RemoteControlMessage stop = await stopReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal(RemoteControlKind.LowLatencyVideoStop, stop.Kind);
        Assert.Equal(2, stop.LowLatencyVideoStopReason);
        Assert.True(viewer.ShouldIgnoreTcpFrames);

        viewer.AcknowledgeStopped(
            stop.LowLatencyVideoChannelId,
            stop.LowLatencyVideoEpoch);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task ViewerUsesSteadyTimeoutAfterFirstCompleteFrame()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var frameReceived = new TaskCompletionSource<RemoteFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReceived = new TaskCompletionSource<RemoteControlMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }
                else if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoStop)
                {
                    host.DisableRoute(notifyViewer: false);
                    stopReceived.TrySetResult(control);
                }

                return Task.FromResult(true);
            },
            frame => frameReceived.TrySetResult(
                CloneBorrowedFrame(frame)),
            _ => { },
            timeout.Token);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        Assert.True(host.TryQueueJpegFrame(
            320,
            200,
            1,
            1,
            new byte[] { 0x11, 0x22, 0x33 }));
        await frameReceived.Task.WaitAsync(timeout.Token);
        var stalledFor = Stopwatch.StartNew();

        RemoteControlMessage stop =
            await stopReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal(RemoteControlKind.LowLatencyVideoStop, stop.Kind);
        Assert.Equal(2, stop.LowLatencyVideoStopReason);
        Assert.InRange(
            stalledFor.Elapsed,
            LowLatencyVideoViewerTransport.SteadyFrameTimeout -
                TimeSpan.FromMilliseconds(100),
            LowLatencyVideoViewerTransport.InitialFrameTimeout -
                TimeSpan.FromMilliseconds(250));

        viewer.AcknowledgeStopped(
            stop.LowLatencyVideoChannelId,
            stop.LowLatencyVideoEpoch);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task AuthenticatedFragmentsCannotExtendInitialFrameDeadline()
    {
        using var timeout =
            new CancellationTokenSource(
                LowLatencyVideoViewerTransport
                    .InitialFrameTimeout +
                TimeSpan.FromSeconds(2));
        using var hostSocket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        hostSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int hostPort =
            Assert.IsType<IPEndPoint>(hostSocket.LocalEndPoint).Port;
        var offer = new LowLatencyVideoOffer(
            hostPort,
            LowLatencyVideoProtocol.DefaultMaxDatagramBytes,
            LowLatencyVideoProtocol.DefaultMaxFrameBytes,
            123,
            456,
            RandomNumberGenerator.GetBytes(
                LowLatencyVideoProtocol.KeyLength),
            RandomNumberGenerator.GetBytes(
                LowLatencyVideoProtocol.KeyLength),
            RandomNumberGenerator.GetBytes(
                LowLatencyVideoProtocol.NoncePrefixLength),
            RandomNumberGenerator.GetBytes(
                LowLatencyVideoProtocol.NoncePrefixLength),
            RandomNumberGenerator.GetBytes(
                LowLatencyVideoProtocol.ChallengeLength));
        using var hostSendCipher = new LowLatencyVideoSendCipher(
            offer.HostToViewerKey,
            offer.HostNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        using var hostReceiveCipher = new LowLatencyVideoReceiveCipher(
            offer.ViewerToHostKey,
            offer.ViewerNoncePrefix,
            offer.ChannelId,
            offer.Epoch);
        var readyReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReceived =
            new TaskCompletionSource<RemoteControlMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        await using var viewer = new LowLatencyVideoViewerTransport(
            offer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    readyReceived.TrySetResult();
                }
                else if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoStop)
                {
                    stopReceived.TrySetResult(control);
                }

                return Task.FromResult(true);
            },
            _ => { },
            logs.Enqueue,
            timeout.Token);

        byte[] receiveBuffer = new byte[ushort.MaxValue];
        EndPoint receiveFrom = new IPEndPoint(IPAddress.Any, 0);
        IPEndPoint? viewerEndpoint = null;
        while (viewerEndpoint is null)
        {
            SocketReceiveFromResult result =
                await hostSocket.ReceiveFromAsync(
                    receiveBuffer,
                    SocketFlags.None,
                    receiveFrom,
                    timeout.Token);
            if (result.RemoteEndPoint is IPEndPoint candidate &&
                hostReceiveCipher.TryDecrypt(
                    receiveBuffer.AsSpan(0, result.ReceivedBytes),
                    out LowLatencyVideoDatagram probe) &&
                probe.Kind == LowLatencyVideoDatagramKind.BindProbe)
            {
                CryptographicOperations.ZeroMemory(
                    probe.Plaintext.Span);
                viewerEndpoint = candidate;
            }
        }

        byte[] ack = hostSendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindAck,
            0,
            offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            offer.Challenge);
        try
        {
            await hostSocket.SendToAsync(
                ack,
                SocketFlags.None,
                viewerEndpoint,
                timeout.Token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ack);
        }

        await readyReceived.Task.WaitAsync(timeout.Token);
        var initialWait = Stopwatch.StartNew();
        using var fragmentCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token);
        Task fragmentSender = Task.Run(
            async () =>
            {
                int maxPayload =
                    LowLatencyVideoProtocol.GetMaxFragmentPayloadBytes(
                        offer.MaxDatagramBytes);
                byte[] fragment = new byte[maxPayload];
                fragment.AsSpan().Fill(0x5A);
                ulong frameSequence = 0;
                try
                {
                    while (!fragmentCancellation.IsCancellationRequested)
                    {
                        byte[] datagram = hostSendCipher.Encrypt(
                            LowLatencyVideoDatagramKind.FrameFragment,
                            ++frameSequence,
                            checked(maxPayload * 2),
                            0,
                            0,
                            2,
                            MessageType.VideoFrame,
                            0,
                            fragment);
                        try
                        {
                            await hostSocket.SendToAsync(
                                datagram,
                                SocketFlags.None,
                                viewerEndpoint,
                                fragmentCancellation.Token);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(
                                datagram);
                        }

                        await Task.Delay(
                            TimeSpan.FromMilliseconds(100),
                            fragmentCancellation.Token);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(fragment);
                }
            },
            fragmentCancellation.Token);

        try
        {
            RemoteControlMessage stop =
                await stopReceived.Task.WaitAsync(timeout.Token);
            Assert.Equal(
                RemoteControlKind.LowLatencyVideoStop,
                stop.Kind);
            Assert.Equal(2, stop.LowLatencyVideoStopReason);
            Assert.InRange(
                initialWait.Elapsed,
                LowLatencyVideoViewerTransport.InitialFrameTimeout -
                    TimeSpan.FromMilliseconds(100),
                LowLatencyVideoViewerTransport.InitialFrameTimeout +
                    TimeSpan.FromSeconds(1));
            Assert.Contains(
                logs,
                message =>
                    message.Contains(
                        "首帧等待超时",
                        StringComparison.Ordinal) &&
                    !message.Contains(
                        "认证画面包 0，",
                        StringComparison.Ordinal));
        }
        finally
        {
            fragmentCancellation.Cancel();
            try
            {
                await fragmentSender;
            }
            catch (OperationCanceledException)
            {
            }
            CryptographicOperations.ZeroMemory(receiveBuffer);
        }
    }

    [Fact]
    public async Task ReadyCanCloseFastPeerRaceBeforeOfferWriteContinuation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer = RemoteMessageCodec.EncodeLowLatencyVideoOffer(
            offer);
        LowLatencyVideoOffer viewerOffer = Assert.IsType<LowLatencyVideoOffer>(
            RemoteMessageCodec.DecodeControl(encodedOffer).LowLatencyVideoOffer);

        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind == RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }

                return Task.FromResult(true);
            },
            _ => { },
            _ => { },
            timeout.Token);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        host.MarkOfferSent();
        host.ClearOfferSecrets();
        Assert.True(host.IsRouteActive);
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task NegotiatedFeedbackV2KeepsRouteAliveAcrossContinuousFramesAndAdaptiveDrop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.XorFec;
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback, features));
        byte[] encodedOffer = RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer = Assert.IsType<LowLatencyVideoOffer>(
            RemoteMessageCodec.DecodeControl(encodedOffer).LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        int receivedFrames = 0;
        int lastPayloadMarker = -1;
        int stopRequests = 0;
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind == RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }
                else if (control.Kind == RemoteControlKind.LowLatencyVideoStop)
                {
                    Interlocked.Increment(ref stopRequests);
                    host.DisableRoute(notifyViewer: false);
                }

                return Task.FromResult(true);
            },
            frame =>
            {
                Volatile.Write(
                    ref lastPayloadMarker,
                    frame.EncodedBuffer[frame.EncodedOffset]);
                Interlocked.Increment(ref receivedFrames);
            },
            _ => { },
            timeout.Token,
            features);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        Assert.True(viewer.ShouldIgnoreTcpFrames);

        const int continuousFrameCount = 8;
        for (int index = 1; index <= continuousFrameCount; index++)
        {
            byte[] jpegBytes = new byte[16 * 1024];
            jpegBytes.AsSpan().Fill(checked((byte)index));
            Assert.True(host.TryQueueJpegFrame(
                1280,
                720,
                index,
                index / 2d,
                jpegBytes,
                allowLatencyBudgetDrop: true));
            await WaitUntilAsync(
                () => Volatile.Read(ref receivedFrames) >= index,
                timeout.Token);
            await Task.Delay(160, timeout.Token);
        }

        await WaitUntilAsync(
            () => host.CollectNetworkSnapshot().HasFeedbackSample,
            timeout.Token);
        LowLatencyVideoNetworkSnapshot feedbackSnapshot =
            host.CollectNetworkSnapshot();
        Assert.True(feedbackSnapshot.HasFeedbackSample);
        Assert.True(host.IsRouteActive);
        Assert.True(viewer.ShouldIgnoreTcpFrames);
        Assert.Equal(continuousFrameCount, Volatile.Read(ref receivedFrames));
        Assert.Equal(continuousFrameCount, Volatile.Read(ref lastPayloadMarker));
        Assert.Equal(0, Volatile.Read(ref stopRequests));

        int receivedBeforeDrop = Volatile.Read(ref receivedFrames);
        byte[] oversizedJpeg =
            new byte[LowLatencyVideoCongestionController.MaximumFrameBudgetBytes];
        Assert.True(host.TryQueueJpegFrame(
            3840,
            2160,
            1,
            1,
            oversizedJpeg,
            allowLatencyBudgetDrop: true));
        LowLatencyVideoNetworkSnapshot dropSnapshot =
            host.CollectNetworkSnapshot();
        Assert.Equal(1, dropSnapshot.OversizedFrameDrops);
        Assert.True(host.IsRouteActive);

        byte[] recoveryFrame = [0xA5, 0x5A, 0xC3, 0x3C];
        Assert.True(host.TryQueueJpegFrame(
            640,
            360,
            1,
            1,
            recoveryFrame,
            allowLatencyBudgetDrop: true));
        await WaitUntilAsync(
            () => Volatile.Read(ref receivedFrames) > receivedBeforeDrop,
            timeout.Token);
        Assert.Equal(0xA5, Volatile.Read(ref lastPayloadMarker));
        Assert.True(host.IsRouteActive);
        Assert.Equal(0, Volatile.Read(ref stopRequests));

        CryptographicOperations.ZeroMemory(encodedOffer);
        CryptographicOperations.ZeroMemory(oversizedJpeg);
    }

    [Fact]
    public async Task NoNegotiatedFeaturesUseLegacyFeedbackAndKeepRouteAlive()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(
                IPAddress.Loopback,
                LowLatencyVideoFeatures.None));
        byte[] encodedOffer = RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer = Assert.IsType<LowLatencyVideoOffer>(
            RemoteMessageCodec.DecodeControl(encodedOffer).LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        int receivedFrames = 0;
        int stopRequests = 0;
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind == RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }
                else if (control.Kind == RemoteControlKind.LowLatencyVideoStop)
                {
                    Interlocked.Increment(ref stopRequests);
                    host.DisableRoute(notifyViewer: false);
                }

                return Task.FromResult(true);
            },
            _ => Interlocked.Increment(ref receivedFrames),
            _ => { },
            timeout.Token,
            LowLatencyVideoFeatures.None);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        Assert.True(host.TryQueueJpegFrame(
            640,
            360,
            1,
            1,
            new byte[] { 0x01 }));
        await WaitUntilAsync(
            () => Volatile.Read(ref receivedFrames) == 1,
            timeout.Token);

        await Task.Delay(1150, timeout.Token);

        Assert.True(host.IsRouteActive);
        Assert.Equal(default, host.CollectNetworkSnapshot());
        Assert.True(host.TryQueueJpegFrame(
            640,
            360,
            2,
            2,
            new byte[] { 0x02 }));
        await WaitUntilAsync(
            () => Volatile.Read(ref receivedFrames) == 2,
            timeout.Token);
        Assert.True(viewer.ShouldIgnoreTcpFrames);
        Assert.Equal(0, Volatile.Read(ref stopRequests));

        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task OversizedFrameWithoutLatencyBudgetDropFallsBackToTcp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback |
            LowLatencyVideoFeatures.XorFec;
        var fallbackReceived =
            new TaskCompletionSource<(ulong ChannelId, uint Epoch, byte Reason)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token,
            (channelId, epoch, reason) =>
            {
                fallbackReceived.TrySetResult((channelId, epoch, reason));
                return Task.CompletedTask;
            });
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback, features));
        byte[] encodedOffer = RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer = Assert.IsType<LowLatencyVideoOffer>(
            RemoteMessageCodec.DecodeControl(encodedOffer).LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind == RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }

                return Task.FromResult(true);
            },
            _ => { },
            _ => { },
            timeout.Token,
            features);

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        byte[] oversizedJpeg =
            new byte[LowLatencyVideoCongestionController.MaximumFrameBudgetBytes];
        Assert.False(host.TryQueueJpegFrame(
            3840,
            2160,
            1,
            1,
            oversizedJpeg,
            allowLatencyBudgetDrop: false));
        Assert.False(host.IsRouteActive);

        (ulong channelId, uint epoch, byte reason) =
            await fallbackReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal(offer.ChannelId, channelId);
        Assert.Equal(offer.Epoch, epoch);
        Assert.Equal(1, reason);

        viewer.AcknowledgeStopped(channelId, epoch);
        Assert.False(viewer.ShouldIgnoreTcpFrames);

        CryptographicOperations.ZeroMemory(encodedOffer);
        CryptographicOperations.ZeroMemory(oversizedJpeg);
    }

    [Fact]
    public async Task QueueAfterConcurrentRouteShutdownDoesNotRetainPooledFrame()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer = RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer = Assert.IsType<LowLatencyVideoOffer>(
            RemoteMessageCodec.DecodeControl(encodedOffer).LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind == RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }

                return Task.FromResult(true);
            },
            _ => { },
            _ => { },
            timeout.Token);
        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);

        var copyEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueCopy = new ManualResetEventSlim(false);
        using var source = new CallbackMemoryManager(
            new byte[] { 0x11, 0x22, 0x33, 0x44 },
            () =>
            {
                copyEntered.TrySetResult();
                continueCopy.Wait(timeout.Token);
            });
        ReadOnlyMemory<byte> sourceMemory = source.CreateReadOnlyMemory();
        Task<bool> queueTask = Task.Run(
            () => host.TryQueueJpegFrame(
                320,
                200,
                1,
                1,
                sourceMemory),
            timeout.Token);

        await copyEntered.Task.WaitAsync(timeout.Token);
        host.DisableRoute(notifyViewer: false);
        await WaitUntilAsync(() => host.IsIoShutdownCompleted, timeout.Token);
        continueCopy.Set();

        Assert.False(await queueTask.WaitAsync(timeout.Token));
        Assert.False(host.HasPendingFrame);
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task FailedReadyControlSendRestoresTcpAndShutsViewerDown()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer = RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer = Assert.IsType<LowLatencyVideoOffer>(
            RemoteMessageCodec.DecodeControl(encodedOffer).LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var readyAttempted = new TaskCompletionSource<RemoteControlMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                readyAttempted.TrySetResult(control);
                return Task.FromResult(false);
            },
            _ => { },
            _ => { },
            timeout.Token);

        RemoteControlMessage ready =
            await readyAttempted.Task.WaitAsync(timeout.Token);
        Assert.Equal(RemoteControlKind.LowLatencyVideoReady, ready.Kind);
        Assert.Equal(offer.ChannelId, ready.LowLatencyVideoChannelId);
        Assert.Equal(offer.Epoch, ready.LowLatencyVideoEpoch);
        await WaitUntilAsync(() => viewer.IsShutdownCompleted, timeout.Token);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task SynchronousFallbackDeadlineCannotRaceDisposedToken()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var connectionAborted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                    return Task.FromResult(true);
                }

                return new TaskCompletionSource<bool>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously).Task;
            },
            _ => { },
            _ => { },
            timeout.Token,
            abortConnection: () =>
                connectionAborted.TrySetResult(),
            fallbackBarrierDelayAsync: (_, _) =>
                Task.CompletedTask);

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        await viewer.RequestFallbackForTestsAsync(2)
            .WaitAsync(timeout.Token);
        await connectionAborted.Task.WaitAsync(timeout.Token);
        await WaitUntilAsync(
            () => viewer.IsShutdownCompleted,
            timeout.Token);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task NeverCompletingStopWriteIsCoveredByFallbackDeadline()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var stopWriteStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopWriteCompletion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeadline = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionAborted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                    return Task.FromResult(true);
                }

                stopWriteStarted.TrySetResult();
                return stopWriteCompletion.Task;
            },
            _ => { },
            _ => { },
            timeout.Token,
            abortConnection: () =>
                connectionAborted.TrySetResult(),
            fallbackBarrierDelayAsync: (_, cancellationToken) =>
            {
                deadlineStarted.TrySetResult();
                return releaseDeadline.Task.WaitAsync(
                    cancellationToken);
            });

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        Task fallback = viewer.RequestFallbackForTestsAsync(2);
        await stopWriteStarted.Task.WaitAsync(timeout.Token);
        await deadlineStarted.Task.WaitAsync(timeout.Token);
        Assert.False(fallback.IsCompleted);

        releaseDeadline.TrySetResult();
        await connectionAborted.Task.WaitAsync(timeout.Token);
        await fallback.WaitAsync(timeout.Token);
        stopWriteCompletion.TrySetResult(false);
        await WaitUntilAsync(
            () => viewer.IsShutdownCompleted,
            timeout.Token);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task MissingStoppedAckAbortsHealthyConnectionAtBoundedDeadline()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer =
            Assert.IsType<LowLatencyVideoOffer>(
                host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer =
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer =
            Assert.IsType<LowLatencyVideoOffer>(
                RemoteMessageCodec.DecodeControl(encodedOffer)
                    .LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var stopWritten =
            new TaskCompletionSource<RemoteControlMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineStarted =
            new TaskCompletionSource<TimeSpan>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeadline = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionAborted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                }
                else if (control.Kind ==
                    RemoteControlKind.LowLatencyVideoStop)
                {
                    // The reliable control write succeeded and the owner is
                    // still healthy, but the peer deliberately withholds the
                    // matching Stopped acknowledgement.
                    stopWritten.TrySetResult(control);
                }

                return Task.FromResult(true);
            },
            _ => { },
            _ => { },
            timeout.Token,
            abortConnection: () =>
                connectionAborted.TrySetResult(),
            fallbackBarrierDelayAsync: (delay, cancellationToken) =>
            {
                deadlineStarted.TrySetResult(delay);
                return releaseDeadline.Task.WaitAsync(
                    cancellationToken);
            });

        await WaitUntilAsync(
            () => host.IsRouteActive,
            timeout.Token);
        await viewer.RequestFallbackForTestsAsync(2)
            .WaitAsync(timeout.Token);
        RemoteControlMessage stop =
            await stopWritten.Task.WaitAsync(timeout.Token);
        Assert.Equal(RemoteControlKind.LowLatencyVideoStop, stop.Kind);
        Assert.True(viewer.ShouldIgnoreTcpFrames);
        Assert.Equal(
            LowLatencyVideoViewerTransport.FallbackBarrierTimeout,
            await deadlineStarted.Task.WaitAsync(timeout.Token));
        Assert.False(connectionAborted.Task.IsCompleted);

        releaseDeadline.TrySetResult();
        await connectionAborted.Task.WaitAsync(timeout.Token);
        await WaitUntilAsync(
            () => viewer.IsShutdownCompleted,
            timeout.Token);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task FailedStopControlSendRestoresTcpAndShutsViewerDown()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var host = new LowLatencyVideoHostTransport(
            _ => { },
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));
        byte[] encodedOffer = RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
        LowLatencyVideoOffer viewerOffer = Assert.IsType<LowLatencyVideoOffer>(
            RemoteMessageCodec.DecodeControl(encodedOffer).LowLatencyVideoOffer);
        host.MarkOfferSent();
        host.ClearOfferSecrets();

        var stopAttempted = new TaskCompletionSource<RemoteControlMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionAborted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LowLatencyVideoViewerTransport(
            viewerOffer,
            IPAddress.Loopback,
            payload =>
            {
                RemoteControlMessage control =
                    RemoteMessageCodec.DecodeControl(payload);
                if (control.Kind == RemoteControlKind.LowLatencyVideoReady)
                {
                    Assert.True(host.TryMarkReady(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch));
                    return Task.FromResult(true);
                }

                stopAttempted.TrySetResult(control);
                return Task.FromResult(false);
            },
            _ => { },
            _ => { },
            timeout.Token,
            abortConnection: () =>
                connectionAborted.TrySetResult());

        await WaitUntilAsync(() => host.IsRouteActive, timeout.Token);
        Assert.True(viewer.ShouldIgnoreTcpFrames);
        await viewer.RequestFallbackForTestsAsync(2).WaitAsync(timeout.Token);

        RemoteControlMessage stop =
            await stopAttempted.Task.WaitAsync(timeout.Token);
        Assert.Equal(RemoteControlKind.LowLatencyVideoStop, stop.Kind);
        Assert.Equal(offer.ChannelId, stop.LowLatencyVideoChannelId);
        Assert.Equal(offer.Epoch, stop.LowLatencyVideoEpoch);
        Assert.Equal(2, stop.LowLatencyVideoStopReason);
        await connectionAborted.Task.WaitAsync(timeout.Token);
        await WaitUntilAsync(() => viewer.IsShutdownCompleted, timeout.Token);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        CryptographicOperations.ZeroMemory(encodedOffer);
    }

    [Fact]
    public async Task UnansweredHostOfferClosesUdpResourcesAndClearsSecrets()
    {
        // The policy deadline is 3.5 seconds. Leave enough scheduling and
        // socket-thread cleanup headroom when the full suite runs in parallel.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var logs = new ConcurrentQueue<string>();
        await using var host = new LowLatencyVideoHostTransport(
            logs.Enqueue,
            timeout.Token);
        LowLatencyVideoOffer offer = Assert.IsType<LowLatencyVideoOffer>(
            host.TryCreateOffer(IPAddress.Loopback));

        host.MarkOfferSent();
        await Task.Delay(
            LowLatencyVideoViewerTransport.BindTimeout +
                TimeSpan.FromMilliseconds(100),
            timeout.Token);
        Assert.False(host.IsIoShutdownCompleted);
        await WaitUntilAsync(() => host.IsIoShutdownCompleted, timeout.Token);

        Assert.False(host.IsRouteActive);
        string timeoutLog = Assert.Single(
            logs,
            message => message.Contains(
                "阶段=等待合法 Probe",
                StringComparison.Ordinal));
        Assert.Contains("state=1", timeoutLog);
        Assert.Contains("原始包=0", timeoutLog);
        Assert.Contains("地址拒绝=0", timeoutLog);
        Assert.Contains("解密失败=0", timeoutLog);
        Assert.Contains("合法Probe=0", timeoutLog);
        Assert.Contains("Ack成功=0", timeoutLog);
        Assert.All(offer.HostToViewerKey, value => Assert.Equal(0, value));
        Assert.All(offer.ViewerToHostKey, value => Assert.Equal(0, value));
        Assert.All(offer.Challenge, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task UnansweredViewerProbeClosesUdpResourcesAndClearsSecrets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logs = new ConcurrentQueue<string>();
        using var probeSink = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        probeSink.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int probeSinkPort =
            Assert.IsType<IPEndPoint>(probeSink.LocalEndPoint).Port;
        LowLatencyVideoOffer offer =
            CreateUnusedLoopbackOffer(probeSinkPort);
        await using var viewer = new LowLatencyVideoViewerTransport(
            offer,
            IPAddress.Loopback,
            _ => Task.FromResult(true),
            _ => { },
            logs.Enqueue,
            timeout.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(1800), timeout.Token);
        Assert.False(viewer.IsShutdownCompleted);
        Assert.False(viewer.ShouldIgnoreTcpFrames);
        await WaitUntilAsync(() => viewer.IsShutdownCompleted, timeout.Token);

        Assert.False(viewer.ShouldIgnoreTcpFrames);
        Assert.True(viewer.BindProbeSendSuccessCount >= 12);
        string timeoutLog = Assert.Single(
            logs,
            message => message.Contains(
                "阶段=等待 BindAck",
                StringComparison.Ordinal));
        Assert.Contains("state=1", timeoutLog);
        Assert.Contains(
            $"Probe发送成功={viewer.BindProbeSendSuccessCount}",
            timeoutLog);
        Assert.All(offer.HostToViewerKey, value => Assert.Equal(0, value));
        Assert.All(offer.ViewerToHostKey, value => Assert.Equal(0, value));
        Assert.All(offer.Challenge, value => Assert.Equal(0, value));
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static LowLatencyVideoOffer CreateUnusedLoopbackOffer(int port = 9)
    {
        return new LowLatencyVideoOffer(
            Port: port,
            MaxDatagramBytes: LowLatencyVideoProtocol.DefaultMaxDatagramBytes,
            MaxFrameBytes: LowLatencyVideoProtocol.DefaultMaxFrameBytes,
            ChannelId: 123,
            Epoch: 456,
            HostToViewerKey: RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.KeyLength),
            ViewerToHostKey: RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.KeyLength),
            HostNoncePrefix: RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.NoncePrefixLength),
            ViewerNoncePrefix: RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.NoncePrefixLength),
            Challenge: RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.ChallengeLength));
    }

    private static async Task<LowLatencyVideoDatagram>
        ReceiveAuthenticatedDatagramAsync(
            Socket socket,
            LowLatencyVideoReceiveCipher receiveCipher,
            CancellationToken cancellationToken)
    {
        byte[] receiveBuffer = new byte[ushort.MaxValue];
        EndPoint receiveFrom = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            SocketReceiveFromResult result =
                await socket.ReceiveFromAsync(
                    receiveBuffer,
                    SocketFlags.None,
                    receiveFrom,
                    cancellationToken);
            if (receiveCipher.TryDecrypt(
                    receiveBuffer.AsSpan(0, result.ReceivedBytes),
                    out LowLatencyVideoDatagram packet))
            {
                return packet;
            }
        }
    }

    private static void SetHostTargetBitsPerSecondForTest(
        LowLatencyVideoHostTransport host,
        long targetBitsPerSecond)
    {
        FieldInfo? controllerField =
            typeof(LowLatencyVideoHostTransport).GetField(
                "_congestionController",
                BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(controllerField);
        var controller = Assert.IsType<
            LowLatencyVideoCongestionController>(
                controllerField!.GetValue(host));
        FieldInfo? targetField =
            typeof(LowLatencyVideoCongestionController).GetField(
                "_targetBitsPerSecond",
                BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(targetField);
        targetField!.SetValue(controller, targetBitsPerSecond);
        Assert.Equal(
            targetBitsPerSecond,
            controller.TargetBitsPerSecond);
    }

    private static void SetHostGop1RecoveryStateForTest(
        LowLatencyVideoHostTransport host,
        long streamSignature,
        long serializedAtTimestamp)
    {
        FieldInfo? signatureField =
            typeof(LowLatencyVideoHostTransport).GetField(
                "_serializedGop1StreamSignature",
                BindingFlags.Instance |
                    BindingFlags.NonPublic);
        FieldInfo? timestampField =
            typeof(LowLatencyVideoHostTransport).GetField(
                "_lastSerializedGop1RecoveryAt",
                BindingFlags.Instance |
                    BindingFlags.NonPublic);
        Assert.NotNull(signatureField);
        Assert.NotNull(timestampField);
        signatureField!.SetValue(
            host,
            streamSignature);
        timestampField!.SetValue(
            host,
            serializedAtTimestamp);
    }

    private static RemoteFrame CloneBorrowedFrame(
        RemoteFrame frame)
    {
        byte[] payload = frame.EncodedBuffer
            .AsSpan(
                frame.EncodedOffset,
                frame.EncodedLength)
            .ToArray();
        return frame with
        {
            EncodedBuffer = payload,
            EncodedOffset = 0
        };
    }

    private sealed class RecordingQwaveFlowLease(
        Socket socket,
        IPEndPoint destination,
        WindowsQwaveTrafficType trafficType) : IDisposable
    {
        private int _disposeCount;

        public IPEndPoint LocalEndpoint { get; } =
            Assert.IsType<IPEndPoint>(
                socket.LocalEndPoint);

        public IPEndPoint Destination { get; } =
            destination;

        public WindowsQwaveTrafficType TrafficType { get; } =
            trafficType;

        public int DisposeCount =>
            Volatile.Read(ref _disposeCount);

        public bool SocketWasOpenWhenDisposed
        {
            get;
            private set;
        }

        public void Dispose()
        {
            if (Interlocked.Increment(
                    ref _disposeCount) != 1)
            {
                return;
            }

            SocketWasOpenWhenDisposed =
                !socket.SafeHandle.IsClosed &&
                !socket.SafeHandle.IsInvalid;
        }
    }

    private sealed class CallbackMemoryManager(
        byte[] buffer,
        Action onFirstGetSpan) : MemoryManager<byte>
    {
        private Action? _onFirstGetSpan = onFirstGetSpan;

        public ReadOnlyMemory<byte> CreateReadOnlyMemory()
        {
            return CreateMemory(buffer.Length);
        }

        public override Span<byte> GetSpan()
        {
            Interlocked.Exchange(ref _onFirstGetSpan, null)?.Invoke();
            return buffer;
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            throw new NotSupportedException();
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref _onFirstGetSpan, null);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = TimeSpan.FromHours(1).Ticks;

        public override long TimestampFrequency =>
            TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elapsed));
            }

            Interlocked.Add(ref _timestamp, elapsed.Ticks);
        }
    }
}
