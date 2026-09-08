using System.Diagnostics;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class InteractiveH264FrameControllerTests
{
    [Fact]
    public void NetworkObservationMatchesFeedbackCadence()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            InteractiveH264FrameController
                .NetworkObservationInterval);
    }

    private const RemoteDeviceCapabilities FeedbackUdpCapabilities =
        RemoteDeviceCapabilities.LowLatencyUdpVideo |
        RemoteDeviceCapabilities.UdpVideoCongestionFeedback;
    private const RemoteDeviceCapabilities
        ShortGopFeedbackUdpCapabilities =
            FeedbackUdpCapabilities |
            RemoteDeviceCapabilities.ShortGopH264;

    [Theory]
    [InlineData(30, true, true, 60)]
    [InlineData(40, true, true, 60)]
    [InlineData(60, true, true, 60)]
    [InlineData(29, true, true, 29)]
    [InlineData(30, false, true, 30)]
    [InlineData(30, true, false, 30)]
    public void SourceRateBoostRequiresAdaptiveFeedbackControlledUdp(
        int configuredFps,
        bool adaptiveQuality,
        bool supportsFeedbackUdp,
        int expectedFps)
    {
        RemoteDeviceCapabilities capabilities =
            supportsFeedbackUdp
                ? FeedbackUdpCapabilities
                : RemoteDeviceCapabilities.LowLatencyUdpVideo;

        Assert.Equal(
            expectedFps,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFps,
                    adaptiveQuality,
                    capabilities));
    }

    [Fact]
    public void SystemMemoryCaptureKeepsConfiguredSourceRate()
    {
        Assert.Equal(
            30,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond: 30,
                    adaptiveQuality: true,
                    viewerCapabilities:
                        ShortGopFeedbackUdpCapabilities,
                    supportsGpuSurfaceCapture: false));
    }

    [Theory]
    [InlineData(30, true, true, 90)]
    [InlineData(30, false, true, 30)]
    [InlineData(30, true, false, 30)]
    [InlineData(40, true, true, 60)]
    [InlineData(60, true, true, 60)]
    public void NinetyHertzSourceRequiresThirtyFpsAdaptiveGpuShortGop(
        int configuredFramesPerSecond,
        bool adaptiveQuality,
        bool supportsGpuSurfaceCapture,
        int expectedFramesPerSecond)
    {
        Assert.Equal(
            expectedFramesPerSecond,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond,
                    adaptiveQuality,
                    ShortGopFeedbackUdpCapabilities,
                    supportsGpuSurfaceCapture));
    }

    [Fact]
    public void ControllerReportsNinetyHertzInteractiveMaximum()
    {
        var controller =
            new InteractiveH264FrameController(30, 90);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        long inputAt = TimestampAtMilliseconds(1_000);

        Assert.True(controller.ShouldTransmitGop2Frame(
            inputAt,
            inputAt,
            udpRouteActive: true,
            recoveryFrame: true));
        Assert.True(controller.CanBoost);
        Assert.True(controller.IsBoostActive);
        Assert.Equal(90, controller.SourceFramesPerSecond);
        Assert.Equal(90, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void IdleTransmissionStaysAtConfiguredRate()
    {
        var controller =
            new InteractiveH264FrameController(
                configuredFramesPerSecond: 30,
                sourceFramesPerSecond: 60);
        long origin = TimestampAtMilliseconds(1_000);
        int transmitted = 0;

        for (int frame = 0; frame < 60; frame++)
        {
            long timestamp = AddMilliseconds(
                origin,
                frame * (1_000d / 60d));
            if (controller.ShouldTransmitFrame(
                    timestamp,
                    lastInputActivityAt: 0,
                    udpRouteActive: true))
            {
                transmitted++;
            }
        }

        Assert.InRange(transmitted, 29, 31);
        Assert.False(controller.IsBoostActive);
        Assert.Equal(30, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void IdlePacingDoesNotCollapseUnderEarlySourceJitter()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);
        long origin = TimestampAtMilliseconds(1_000);
        int transmitted = 0;

        // A real 60 Hz capture clock often arrives slightly before its
        // nominal 16.67 ms boundary. Scheduling from the last actual send
        // would then wait three source frames and collapse toward 20 FPS.
        for (int frame = 0; frame < 63; frame++)
        {
            long timestamp = AddMilliseconds(
                origin,
                frame * 16d);
            if (controller.ShouldTransmitFrame(
                    timestamp,
                    lastInputActivityAt: 0,
                    udpRouteActive: true))
            {
                transmitted++;
            }
        }

        Assert.InRange(transmitted, 29, 31);
    }

    [Fact]
    public void SourceAlreadyAtConfiguredRateDoesNotDropSlightlyEarlyFrames()
    {
        var controller =
            new InteractiveH264FrameController(
                configuredFramesPerSecond: 30,
                sourceFramesPerSecond: 30);
        long origin = TimestampAtMilliseconds(1_000);

        Assert.True(controller.ShouldTransmitFrame(
            origin,
            lastInputActivityAt: 0,
            udpRouteActive: true));
        Assert.True(controller.ShouldTransmitFrame(
            AddMilliseconds(origin, 32.9),
            lastInputActivityAt: 0,
            udpRouteActive: true));
        Assert.True(controller.ShouldTransmitFrame(
            AddMilliseconds(origin, 66.2),
            lastInputActivityAt: 0,
            udpRouteActive: true));
        Assert.False(controller.CanBoost);
    }

    [Fact]
    public void SixtyFpsProfileTemporallyReducesAfterSustainedCongestion()
    {
        var controller =
            new InteractiveH264FrameController(60, 60);
        LowLatencyVideoNetworkSnapshot congested =
            ComfortableNetworkSnapshot() with
            {
                PacketLossRatio = 0.03
            };

        ArmHighFrameRateAdaptation(controller);
        controller.ObserveNetworkSnapshot(congested);
        Assert.False(controller.IsHighFrameRateReduced);
        controller.ObserveNetworkSnapshot(congested);
        Assert.True(controller.IsHighFrameRateReduced);
        Assert.Equal(30, controller.CurrentTransmitFramesPerSecond);

        long origin = TimestampAtMilliseconds(1_000);
        int transmitted = 0;
        for (int frame = 0; frame < 60; frame++)
        {
            if (controller.ShouldTransmitFrame(
                    AddMilliseconds(
                        origin,
                        frame * (1_000d / 60d)),
                    lastInputActivityAt: origin,
                    udpRouteActive: true))
            {
                transmitted++;
            }
        }

        Assert.InRange(transmitted, 29, 31);
    }

    [Fact]
    public void IsolatedSenderQueueBurstDoesNotReduceSixtyFpsProfile()
    {
        var controller =
            new InteractiveH264FrameController(60, 60);
        LowLatencyVideoNetworkSnapshot senderBurst =
            ComfortableNetworkSnapshot() with
            {
                SenderQueueDropRatio = 1,
                AbortedFrameSends = 1
            };

        ArmHighFrameRateAdaptation(controller);
        for (int burst = 0; burst < 6; burst++)
        {
            controller.ObserveNetworkSnapshot(senderBurst);
            Assert.False(controller.IsHighFrameRateReduced);
            Assert.Equal(60, controller.CurrentTransmitFramesPerSecond);

            for (int cleanWindow = 0; cleanWindow < 99; cleanWindow++)
            {
                controller.ObserveNetworkSnapshot(
                    ComfortableNetworkSnapshot());
            }
        }
    }

    [Fact]
    public void SustainedSenderPressureReducesSixtyFpsAfterOneSecond()
    {
        var controller =
            new InteractiveH264FrameController(60, 60);
        LowLatencyVideoNetworkSnapshot senderPressure =
            ComfortableNetworkSnapshot() with
            {
                SenderQueueDropRatio = 0.25
            };

        ArmHighFrameRateAdaptation(controller);
        for (int window = 1;
             window <
             InteractiveH264FrameController
                 .HighFrameRateSenderPressureWindowsBeforeReduction;
             window++)
        {
            controller.ObserveNetworkSnapshot(senderPressure);
            Assert.False(controller.IsHighFrameRateReduced);
        }

        controller.ObserveNetworkSnapshot(senderPressure);
        Assert.True(controller.IsHighFrameRateReduced);
        Assert.Equal(30, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void SevereReceiverLossStillImmediatelyReducesSixtyFps()
    {
        var controller =
            new InteractiveH264FrameController(60, 60);

        ArmHighFrameRateAdaptation(controller);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot() with
            {
                PacketLossRatio = 0.08
            });

        Assert.True(controller.IsHighFrameRateReduced);
        Assert.Equal(30, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void ReducedSixtyFpsProfileRequiresTwoCleanSecondsToRecover()
    {
        var controller =
            new InteractiveH264FrameController(60, 60);
        ArmHighFrameRateAdaptation(controller);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot() with
            {
                PacketLossRatio = 0.10
            });
        Assert.True(controller.IsHighFrameRateReduced);

        for (int window = 1;
             window <
             InteractiveH264FrameController
                 .HighFrameRateComfortableWindowsBeforeRecovery;
             window++)
        {
            controller.ObserveNetworkSnapshot(
                ComfortableNetworkSnapshot());
            Assert.True(controller.IsHighFrameRateReduced);
        }

        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        Assert.False(controller.IsHighFrameRateReduced);
        Assert.Equal(60, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void RecentInputUsesEverySourceFrameOnComfortableUdp()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        long inputAt = TimestampAtMilliseconds(1_000);
        int transmitted = 0;

        for (int frame = 0; frame < 30; frame++)
        {
            long timestamp = AddMilliseconds(
                inputAt,
                frame * (1_000d / 60d));
            if (controller.ShouldTransmitFrame(
                    timestamp,
                    inputAt,
                    udpRouteActive: true))
            {
                transmitted++;
            }
        }

        Assert.Equal(30, transmitted);
        Assert.True(controller.IsBoostActive);
        Assert.Equal(60, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void Gop2IdleAlwaysSelectsRecoveryHalfDespiteCaptureJitter()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);
        long origin = TimestampAtMilliseconds(1_000);
        int transmittedRecoveryFrames = 0;
        int transmittedDependentFrames = 0;

        for (int frame = 0; frame < 64; frame++)
        {
            bool recovery = (frame & 1) == 0;
            bool transmitted =
                controller.ShouldTransmitGop2Frame(
                    AddMilliseconds(
                        origin,
                        frame * 16d),
                    lastInputActivityAt: 0,
                    udpRouteActive: true,
                    recoveryFrame: recovery);
            if (transmitted && recovery)
            {
                transmittedRecoveryFrames++;
            }
            else if (transmitted)
            {
                transmittedDependentFrames++;
            }
        }

        Assert.Equal(32, transmittedRecoveryFrames);
        Assert.Equal(0, transmittedDependentFrames);
    }

    [Fact]
    public void Gop2InteractionAddsOnlyPairedDependentFrames()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        long inputAt = TimestampAtMilliseconds(1_000);

        Assert.False(
            controller.ShouldTransmitGop2Frame(
                inputAt,
                inputAt,
                udpRouteActive: true,
                recoveryFrame: false));
        Assert.True(
            controller.ShouldTransmitGop2Frame(
                AddMilliseconds(inputAt, 16),
                inputAt,
                udpRouteActive: true,
                recoveryFrame: true));
        Assert.True(
            controller.ShouldTransmitGop2Frame(
                AddMilliseconds(inputAt, 32),
                inputAt,
                udpRouteActive: true,
                recoveryFrame: false));
        Assert.False(
            controller.ShouldTransmitGop2Frame(
                AddMilliseconds(inputAt, 48),
                inputAt,
                udpRouteActive: true,
                recoveryFrame: false));
    }

    [Fact]
    public void Gop2ThreeTimesSourceIdlesOnRecoveryFramesAtBaseRate()
    {
        var controller =
            new InteractiveH264FrameController(30, 90);
        long origin = TimestampAtMilliseconds(1_000);
        int transmittedRecoveryFrames = 0;
        int transmittedDependentFrames = 0;

        for (int frame = 0; frame < 90; frame++)
        {
            bool recovery = (frame & 1) == 0;
            bool transmitted =
                controller.ShouldTransmitGop2Frame(
                    AddMilliseconds(
                        origin,
                        frame * (1_000d / 90d)),
                    lastInputActivityAt: 0,
                    udpRouteActive: true,
                    recoveryFrame: recovery);
            if (transmitted && recovery)
            {
                transmittedRecoveryFrames++;
            }
            else if (transmitted)
            {
                transmittedDependentFrames++;
            }
        }

        Assert.InRange(transmittedRecoveryFrames, 29, 31);
        Assert.Equal(0, transmittedDependentFrames);
        Assert.False(controller.IsBoostActive);
        Assert.Equal(30, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void Gop2ThreeTimesSourceSendsEveryPairDuringHealthyInteraction()
    {
        var controller =
            new InteractiveH264FrameController(30, 90);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        long inputAt = TimestampAtMilliseconds(1_000);
        int transmittedRecoveryFrames = 0;
        int transmittedDependentFrames = 0;

        for (int frame = 0; frame < 60; frame++)
        {
            bool recovery = (frame & 1) == 0;
            bool transmitted =
                controller.ShouldTransmitGop2Frame(
                    AddMilliseconds(
                        inputAt,
                        frame * (1_000d / 90d)),
                    inputAt,
                    udpRouteActive: true,
                    recoveryFrame: recovery);
            if (transmitted && recovery)
            {
                transmittedRecoveryFrames++;
            }
            else if (transmitted)
            {
                transmittedDependentFrames++;
            }
        }

        Assert.Equal(30, transmittedRecoveryFrames);
        Assert.Equal(30, transmittedDependentFrames);
        Assert.True(controller.IsBoostActive);
        Assert.Equal(90, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void Gop2ThreeTimesSourceNeverSendsPAfterSkippedIdr()
    {
        var controller =
            new InteractiveH264FrameController(30, 90);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        long origin = TimestampAtMilliseconds(1_000);

        Assert.True(controller.ShouldTransmitGop2Frame(
            origin,
            lastInputActivityAt: 0,
            udpRouteActive: true,
            recoveryFrame: true));
        Assert.False(controller.ShouldTransmitGop2Frame(
            AddMilliseconds(origin, 11),
            lastInputActivityAt: 0,
            udpRouteActive: true,
            recoveryFrame: false));
        Assert.False(controller.ShouldTransmitGop2Frame(
            AddMilliseconds(origin, 22),
            lastInputActivityAt: 0,
            udpRouteActive: true,
            recoveryFrame: true));

        long inputAt = AddMilliseconds(origin, 23);
        Assert.False(controller.ShouldTransmitGop2Frame(
            AddMilliseconds(origin, 33),
            inputAt,
            udpRouteActive: true,
            recoveryFrame: false));
        Assert.True(controller.ShouldTransmitGop2Frame(
            AddMilliseconds(origin, 44),
            inputAt,
            udpRouteActive: true,
            recoveryFrame: true));
        Assert.True(controller.ShouldTransmitGop2Frame(
            AddMilliseconds(origin, 55),
            inputAt,
            udpRouteActive: true,
            recoveryFrame: false));
    }

    [Fact]
    public void Gop2OneTimesSourcePreservesCompletePairs()
    {
        var controller =
            new InteractiveH264FrameController(30, 30);
        long origin = TimestampAtMilliseconds(1_000);

        Assert.True(controller.ShouldTransmitGop2Frame(
            origin,
            lastInputActivityAt: 0,
            udpRouteActive: true,
            recoveryFrame: true));
        Assert.True(controller.ShouldTransmitGop2Frame(
            AddMilliseconds(origin, 33),
            lastInputActivityAt: 0,
            udpRouteActive: true,
            recoveryFrame: false));
        Assert.False(controller.CanBoost);
        Assert.Equal(30, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void InputNewerThanCapturedFrameKeepsGop2BoostActive()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        long origin = TimestampAtMilliseconds(1_000);

        Assert.True(controller.ShouldTransmitGop2Frame(
            origin,
            AddMilliseconds(origin, 1),
            udpRouteActive: true,
            recoveryFrame: true));
        long dependentProducedAt = AddMilliseconds(origin, 16);
        Assert.True(controller.ShouldTransmitGop2Frame(
            dependentProducedAt,
            AddMilliseconds(dependentProducedAt, 2),
            udpRouteActive: true,
            recoveryFrame: false));
        Assert.True(controller.IsBoostActive);
        Assert.Equal(60, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void BoostExpiresAndReturnsToConfiguredRate()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        long inputAt = TimestampAtMilliseconds(1_000);
        long afterBoostWindow = AddMilliseconds(
            inputAt,
            InteractiveH264FrameController
                .ActivityBoostWindow.TotalMilliseconds + 1);

        Assert.True(controller.ShouldTransmitFrame(
            inputAt,
            inputAt,
            udpRouteActive: true));
        controller.ShouldTransmitFrame(
            afterBoostWindow,
            inputAt,
            udpRouteActive: true);

        Assert.False(controller.IsBoostActive);
        Assert.Equal(30, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void TcpRouteNeverUsesInteractiveBoost()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        long inputAt = TimestampAtMilliseconds(1_000);

        controller.ShouldTransmitFrame(
            inputAt,
            inputAt,
            udpRouteActive: false);

        Assert.False(controller.IsBoostActive);
        Assert.Equal(30, controller.CurrentTransmitFramesPerSecond);
    }

    [Fact]
    public void CongestionImmediatelyDisablesBoostAndRequiresCleanRecovery()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        Assert.True(controller.NetworkAllowsBoost);

        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot() with
            {
                PacketLossRatio = 0.10
            });
        Assert.False(controller.NetworkAllowsBoost);

        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        Assert.False(controller.NetworkAllowsBoost);

        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());
        Assert.True(controller.NetworkAllowsBoost);
    }

    [Fact]
    public void OccasionalLatestOnlyReplacementKeepsInteractiveProbeActive()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);

        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot() with
            {
                SenderQueueDropRatio = 0.20
            });

        Assert.True(controller.NetworkAllowsBoost);
    }

    [Fact]
    public void SustainedSenderPressureDisablesInteractiveProbe()
    {
        var controller =
            new InteractiveH264FrameController(30, 60);
        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot());

        controller.ObserveNetworkSnapshot(
            ComfortableNetworkSnapshot() with
            {
                SenderQueueDropRatio = 0.25
            });

        Assert.False(controller.NetworkAllowsBoost);
    }

    [Fact]
    public void ActivityTimestampRecordsLatestSuccessfulInput()
    {
        var activity = new RemoteInteractionActivity();
        long first = TimestampAtMilliseconds(1_000);
        long second = TimestampAtMilliseconds(1_001);

        activity.Record(first);
        activity.Record(second);

        Assert.Equal(second, activity.LastActivityAt);
    }

    private static LowLatencyVideoNetworkSnapshot
        ComfortableNetworkSnapshot()
    {
        return new LowLatencyVideoNetworkSnapshot(
            HasFeedbackSample: true,
            PacketLossRatio: 0,
            FrameAbandonRatio: 0,
            SenderQueueDropRatio: 0,
            DeliveryMegabitsPerSecond: 40,
            TargetMegabitsPerSecond: 100,
            XorFecEnabled: true,
            OversizedFrameDrops: 0,
            AbortedFrameSends: 0);
    }

    private static void ArmHighFrameRateAdaptation(
        InteractiveH264FrameController controller)
    {
        for (int window = 0;
             window <
             InteractiveH264FrameController
                 .HighFrameRateWindowsBeforeAdaptation;
             window++)
        {
            controller.ObserveNetworkSnapshot(
                ComfortableNetworkSnapshot());
        }
    }

    private static long TimestampAtMilliseconds(
        double milliseconds)
    {
        return Math.Max(
            1,
            checked((long)Math.Round(
                milliseconds *
                Stopwatch.Frequency /
                1_000d)));
    }

    private static long AddMilliseconds(
        long timestamp,
        double milliseconds)
    {
        return checked(
            timestamp +
            (long)Math.Round(
                milliseconds *
                Stopwatch.Frequency /
                1_000d));
    }
}
