using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class LowLatencyVideoNetworkControlTests
{
    [Fact]
    public void FeedbackV2RoundTripsExactWireLayout()
    {
        var expected = new LowLatencyVideoFeedbackV2(
            10_000,
            123,
            250,
            LowLatencyVideoFeedbackMetrics.PacketDelivery |
                LowLatencyVideoFeedbackMetrics.FrameAssembly,
            100,
            120_000,
            3,
            1,
            9,
            8,
            2,
            0,
            0,
            0,
            0);

        byte[] payload = LowLatencyVideoFeedbackV2Codec.Encode(expected);

        Assert.Equal(LowLatencyVideoFeedbackV2Codec.PayloadLength, payload.Length);
        Assert.Equal(10_000UL, BinaryPrimitives.ReadUInt64LittleEndian(payload));
        Assert.True(LowLatencyVideoFeedbackV2Codec.TryDecode(payload, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FeedbackV2RejectsBadLengthUnknownMaskAndQueueMetadata()
    {
        LowLatencyVideoFeedbackV2 baseline = CreateFeedback(
            elapsed: 10_000,
            largest: 10,
            packets: 10,
            bytes: 12_000);
        byte[] valid = LowLatencyVideoFeedbackV2Codec.Encode(baseline);

        Assert.False(LowLatencyVideoFeedbackV2Codec.TryDecode(valid[..^1], out _));
        Assert.False(LowLatencyVideoFeedbackV2Codec.TryDecode([.. valid, 0], out _));

        byte[] unknownMask = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            unknownMask.AsSpan(20),
            0x8000_0000);
        Assert.False(LowLatencyVideoFeedbackV2Codec.TryDecode(unknownMask, out _));

        byte[] invalidQueue = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(invalidQueue.AsSpan(88), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(invalidQueue.AsSpan(90), 1);
        Assert.False(LowLatencyVideoFeedbackV2Codec.TryDecode(invalidQueue, out _));
    }

    [Fact]
    public void PacketArrivalTrackerAllowsReorderingBeforeSettlingLoss()
    {
        long start = Stopwatch.GetTimestamp();
        var tracker = new LowLatencyVideoPacketArrivalTracker(start);

        tracker.Observe(100, 1200, start + 1);
        tracker.Observe(102, 1200, start + 2);
        tracker.Observe(101, 1200, start + 3);
        tracker.Observe(164, 1200, start + 4);
        LowLatencyVideoFeedbackV2 feedback = tracker.CreateFeedback(
            2,
            2,
            0,
            start + Stopwatch.Frequency);

        Assert.Equal(4UL, feedback.TotalAuthenticatedPackets);
        Assert.Equal(0UL, feedback.TotalSettledLostPackets);
        Assert.Equal(0UL, feedback.TotalLatePackets);
    }

    [Fact]
    public void PacketArrivalTrackerCountsSettledLossAndLateArrivalSeparately()
    {
        long start = Stopwatch.GetTimestamp();
        var tracker = new LowLatencyVideoPacketArrivalTracker(start);

        tracker.Observe(10, 1200, start + 1);
        tracker.Observe(12, 1200, start + 2);
        tracker.Observe(75, 1200, start + 3);
        LowLatencyVideoFeedbackV2 lost = tracker.CreateFeedback(
            1,
            1,
            0,
            start + Stopwatch.Frequency);
        tracker.Observe(11, 1200, start + 4);
        LowLatencyVideoFeedbackV2 late = tracker.CreateFeedback(
            1,
            1,
            0,
            start + Stopwatch.Frequency);

        Assert.Equal(1UL, lost.TotalSettledLostPackets);
        Assert.Equal(0UL, lost.TotalLatePackets);
        Assert.Equal(1UL, late.TotalLatePackets);
    }

    [Fact]
    public void PacketArrivalTrackerRejectsFeedbackBeforeLatestArrival()
    {
        long start = Stopwatch.GetTimestamp();
        long latestReceivedAt = start + Stopwatch.Frequency;
        var tracker = new LowLatencyVideoPacketArrivalTracker(start);
        tracker.Observe(10, 1200, latestReceivedAt);

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                tracker.CreateFeedback(
                    0,
                    0,
                    0,
                    latestReceivedAt - 1));
        LowLatencyVideoFeedbackV2 simultaneous = tracker.CreateFeedback(
            0,
            0,
            0,
            latestReceivedAt);

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Equal(0U, simultaneous.AckDelayMicroseconds);
    }

    [Fact]
    public void PacketArrivalTrackerSteadyStateDoesNotAllocatePerDatagram()
    {
        long start = Stopwatch.GetTimestamp();
        var tracker = new LowLatencyVideoPacketArrivalTracker(start);
        ulong warmupPackets =
            LowLatencyVideoPacketArrivalTracker.ReorderingWindowPackets * 2;
        for (ulong sequence = 0; sequence < warmupPackets; sequence++)
        {
            tracker.Observe(
                sequence,
                1200,
                start + checked((long)sequence + 1));
        }

        const ulong measuredPackets = 10_000;
        _ = MeasurePacketArrivalAllocation(
            tracker,
            warmupPackets,
            measuredPackets,
            start);

        long allocated = MeasurePacketArrivalAllocation(
            tracker,
            warmupPackets + measuredPackets,
            measuredPackets,
            start);
        Assert.InRange(allocated, 0, 1024);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePacketArrivalAllocation(
        LowLatencyVideoPacketArrivalTracker tracker,
        ulong firstSequence,
        ulong packetCount,
        long startTimestamp)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (ulong offset = 0; offset < packetCount; offset++)
        {
            ulong sequence = firstSequence + offset;
            tracker.Observe(
                sequence,
                1200,
                startTimestamp + checked((long)sequence + 1));
        }

        return GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    }

    [Fact]
    public void CongestionControllerReducesQuicklyAndEnablesFecForModerateLoss()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback |
                LowLatencyVideoFeatures.XorFec);
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed: 1_000_000,
            largest: 100,
            packets: 100,
            bytes: 120_000,
            completed: 10)));

        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed: 1_250_000,
            largest: 200,
            packets: 197,
            bytes: 236_400,
            lost: 3,
            completed: 19,
            abandoned: 1)));

        Assert.True(controller.XorFecEnabled);
        Assert.True(
            controller.TargetBitsPerSecond <
            LowLatencyVideoCongestionController.InitialTargetBitsPerSecond);
        Assert.True(controller.CollectSnapshot().IsCongested);
    }

    [Fact]
    public void CongestionControllerDisablesFecDuringSevereLoss()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback |
                LowLatencyVideoFeatures.XorFec);
        controller.TryObserveFeedback(CreateFeedback(
            1_000_000,
            100,
            100,
            120_000,
            completed: 10));
        controller.TryObserveFeedback(CreateFeedback(
            1_250_000,
            200,
            180,
            216_000,
            lost: 20,
            completed: 15,
            abandoned: 5));

        Assert.False(controller.XorFecEnabled);
        Assert.True(controller.CollectSnapshot().IsSeverelyCongested);
    }

    [Fact]
    public void HighFrameRateFecResumesAfterTenConsecutiveCleanSamples()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback |
                LowLatencyVideoFeatures.XorFec);
        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);
        Assert.True(controller.XorFecEnabled);

        ulong elapsed = 1_000_000;
        ulong largest = 100;
        ulong packets = 100;
        ulong bytes = 120_000;
        ulong lost = 0;
        ulong completed = 10;
        ulong abandoned = 0;
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            lost: lost,
            completed: completed,
            abandoned: abandoned)));

        elapsed += 250_000;
        largest += 100;
        packets += 80;
        bytes += 96_000;
        lost += 20;
        completed += 5;
        abandoned += 5;
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            lost: lost,
            completed: completed,
            abandoned: abandoned)));
        Assert.False(controller.XorFecEnabled);

        void ObserveCleanSample()
        {
            elapsed += 250_000;
            largest += 100;
            packets += 100;
            bytes += 120_000;
            completed += 10;
            Assert.True(controller.TryObserveFeedback(CreateFeedback(
                elapsed,
                largest,
                packets,
                bytes,
                lost: lost,
                completed: completed,
                abandoned: abandoned)));
        }

        for (int index = 0; index < 5; index++)
        {
            ObserveCleanSample();
            Assert.False(controller.XorFecEnabled);
        }

        elapsed += 250_000;
        largest += 100;
        packets += 97;
        bytes += 116_400;
        lost += 3;
        completed += 10;
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            lost: lost,
            completed: completed,
            abandoned: abandoned)));
        Assert.False(controller.XorFecEnabled);

        for (int index = 0; index < 9; index++)
        {
            ObserveCleanSample();
            Assert.False(controller.XorFecEnabled);
        }

        ObserveCleanSample();
        Assert.True(controller.XorFecEnabled);
    }

    [Fact]
    public void FrameOnlySampleDoesNotReapplyPreviousPacketLoss()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        controller.TryObserveFeedback(CreateFeedback(
            1_000_000,
            100,
            100,
            120_000,
            completed: 10));
        controller.TryObserveFeedback(CreateFeedback(
            1_250_000,
            200,
            180,
            216_000,
            lost: 20,
            completed: 10));
        long targetAfterPacketLoss = controller.TargetBitsPerSecond;

        controller.TryObserveFeedback(CreateFeedback(
            1_500_000,
            201,
            181,
            217_200,
            lost: 20,
            completed: 11));

        Assert.Equal(
            (long)(LowLatencyVideoCongestionController
                .InitialTargetBitsPerSecond * 0.65),
            targetAfterPacketLoss);
        Assert.Equal(
            targetAfterPacketLoss,
            controller.TargetBitsPerSecond);
    }

    [Fact]
    public void FrameOnlyWindowsPreservePacketAccumulationUntilFreshSample()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        ulong elapsed = 1_000_000;
        ulong largest = 100;
        ulong packets = 100;
        ulong bytes = 120_000;
        ulong completed = 10;
        controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            completed: completed));
        elapsed += 250_000;
        largest += 100;
        packets += 97;
        bytes += 116_400;
        controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            lost: 3,
            completed: completed));
        long targetAfterPacketLoss = controller.TargetBitsPerSecond;
        Assert.Equal(
            0.03,
            controller.CollectSnapshot().PacketLossRatio,
            precision: 12);

        for (int index = 0; index < 3; index++)
        {
            elapsed += 250_000;
            largest += 2;
            packets += 2;
            bytes += 2_400;
            completed++;
            controller.TryObserveFeedback(CreateFeedback(
                elapsed,
                largest,
                packets,
                bytes,
                lost: 3,
                completed: completed));

            Assert.Equal(
                targetAfterPacketLoss,
                controller.TargetBitsPerSecond);
            Assert.Equal(
                0.03,
                controller.CollectSnapshot().PacketLossRatio,
                precision: 12);
        }

        elapsed += 250_000;
        largest += 2;
        packets += 2;
        bytes += 2_400;
        completed++;
        controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            lost: 3,
            completed: completed));
        LowLatencyVideoNetworkSnapshot refreshed =
            controller.CollectSnapshot();

        Assert.Equal(
            targetAfterPacketLoss,
            controller.TargetBitsPerSecond);
        Assert.Equal(0.018, refreshed.PacketLossRatio, precision: 12);
        Assert.False(refreshed.IsCongested);
    }

    [Fact]
    public void PacketOnlySampleDoesNotReapplyPreviousFrameAbandonment()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        controller.TryObserveFeedback(CreateFeedback(
            1_000_000,
            100,
            100,
            120_000,
            completed: 10));
        controller.TryObserveFeedback(CreateFeedback(
            1_250_000,
            101,
            101,
            121_200,
            completed: 17,
            abandoned: 3));
        long targetAfterFrameAbandonment =
            controller.TargetBitsPerSecond;

        controller.TryObserveFeedback(CreateFeedback(
            1_500_000,
            201,
            201,
            241_200,
            completed: 17,
            abandoned: 3));

        Assert.Equal(
            (long)(LowLatencyVideoCongestionController
                .InitialTargetBitsPerSecond * 0.65),
            targetAfterFrameAbandonment);
        Assert.Equal(
            targetAfterFrameAbandonment,
            controller.TargetBitsPerSecond);
    }

    [Fact]
    public void CongestionControllerRejectsCounterRegression()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            2_000,
            20,
            20,
            24_000)));

        Assert.False(controller.TryObserveFeedback(CreateFeedback(
            3_000,
            21,
            19,
            25_000)));
    }

    [Fact]
    public void CongestionControllerRecoversSlowlyAndRetiresTemporaryFec()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback |
                LowLatencyVideoFeatures.XorFec);
        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 40_000_000,
            framesPerSecond: 30);
        Assert.False(controller.XorFecEnabled);
        ulong elapsed = 1_000_000;
        ulong largest = 100;
        ulong packets = 100;
        ulong bytes = 120_000;
        ulong completed = 10;
        controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            completed: completed));
        elapsed += 250_000;
        largest += 100;
        packets += 97;
        bytes += 116_400;
        completed += 9;
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            lost: 3,
            completed: completed,
            abandoned: 1)));
        Assert.True(controller.XorFecEnabled);
        long reducedTarget = controller.TargetBitsPerSecond;

        for (int index = 0; index < 10; index++)
        {
            elapsed += 250_000;
            largest += 100;
            packets += 100;
            bytes += 120_000;
            completed += 10;
            Assert.True(controller.TryObserveFeedback(CreateFeedback(
                elapsed,
                largest,
                packets,
                bytes,
                lost: 3,
                completed: completed,
                abandoned: 1)));
        }

        Assert.True(controller.TargetBitsPerSecond > reducedTarget);
        Assert.False(controller.XorFecEnabled);
    }

    [Fact]
    public void PacketOnlyCleanSamplesRecoverAndRetireTemporaryFec()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback |
                LowLatencyVideoFeatures.XorFec);
        ulong elapsed = 1_000_000;
        ulong largest = 100;
        ulong packets = 100;
        ulong bytes = 120_000;
        controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            completed: 10));
        elapsed += 250_000;
        largest += 100;
        packets += 97;
        bytes += 116_400;
        controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            lost: 3,
            completed: 10));
        Assert.True(controller.XorFecEnabled);
        long reducedTarget = controller.TargetBitsPerSecond;

        for (int index = 0; index < 10; index++)
        {
            elapsed += 250_000;
            largest += 100;
            packets += 100;
            bytes += 120_000;
            Assert.True(controller.TryObserveFeedback(CreateFeedback(
                elapsed,
                largest,
                packets,
                bytes,
                lost: 3,
                completed: 10)));
        }

        Assert.True(controller.TargetBitsPerSecond > reducedTarget);
        Assert.False(controller.XorFecEnabled);
    }

    [Fact]
    public async Task SenderDropSnapshotsDoNotSplitQueuedReplacementPairs()
    {
        const int producerCount = 4;
        const int recordsPerProducer = 25_000;
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        using var start = new ManualResetEventSlim(false);
        int activeProducers = producerCount;
        var observedDropRatios = new List<double>();

        Task collector = Task.Run(() =>
        {
            start.Wait();
            while (Volatile.Read(ref activeProducers) > 0)
            {
                double ratio =
                    controller.CollectSnapshot().SenderQueueDropRatio;
                if (ratio > 0)
                {
                    observedDropRatios.Add(ratio);
                }
            }
        });
        Task[] producers = Enumerable.Range(0, producerCount)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    for (int index = 0;
                        index < recordsPerProducer;
                        index++)
                    {
                        controller.RecordQueuedFrame(
                            replacedPendingFrame: true);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeProducers);
                }
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(producers);
        await collector;
        double finalRatio =
            controller.CollectSnapshot().SenderQueueDropRatio;
        if (finalRatio > 0)
        {
            observedDropRatios.Add(finalRatio);
        }

        Assert.NotEmpty(observedDropRatios);
        Assert.All(
            observedDropRatios,
            ratio => Assert.Equal(1, ratio, precision: 12));
    }

    [Fact]
    public void PendingReplacementDropsDoNotCollapseSocketPacingTarget()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);

        for (int window = 0; window < 20; window++)
        {
            for (int frame = 0; frame < 10; frame++)
            {
                controller.RecordQueuedFrame(
                    replacedPendingFrame: frame != 0);
            }

            LowLatencyVideoNetworkSnapshot snapshot =
                controller.CollectSnapshot();
            Assert.Equal(0.9, snapshot.SenderQueueDropRatio, precision: 12);
            Assert.True(snapshot.IsSeverelyCongested);
        }

        Assert.Equal(
            LowLatencyVideoCongestionController.InitialTargetBitsPerSecond,
            controller.TargetBitsPerSecond);
    }

    [Fact]
    public void SenderDropSnapshotsAssignEventsToOneWindow()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        controller.RecordQueuedFrame(replacedPendingFrame: false);
        controller.RecordOversizedFrameDrop();

        LowLatencyVideoNetworkSnapshot oversized =
            controller.CollectSnapshot();
        LowLatencyVideoNetworkSnapshot empty =
            controller.CollectSnapshot();
        controller.RecordQueuedFrame(replacedPendingFrame: false);
        controller.RecordAbortedFrameSend();
        LowLatencyVideoNetworkSnapshot aborted =
            controller.CollectSnapshot();

        Assert.Equal(1, oversized.SenderQueueDropRatio);
        Assert.Equal(1, oversized.OversizedFrameDrops);
        Assert.Equal(0, oversized.AbortedFrameSends);
        Assert.Equal(0, empty.SenderQueueDropRatio);
        Assert.Equal(0, empty.OversizedFrameDrops);
        Assert.Equal(0, empty.AbortedFrameSends);
        Assert.Equal(1, aborted.SenderQueueDropRatio);
        Assert.Equal(0, aborted.OversizedFrameDrops);
        Assert.Equal(1, aborted.AbortedFrameSends);
    }

    [Fact]
    public void SingleAbortedFrameMarksLowDropRatioWindowCongested()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        for (int index = 0; index < 30; index++)
        {
            controller.RecordQueuedFrame(replacedPendingFrame: false);
        }

        controller.RecordAbortedFrameSend();
        LowLatencyVideoNetworkSnapshot snapshot =
            controller.CollectSnapshot();

        Assert.Equal(1d / 30, snapshot.SenderQueueDropRatio, precision: 12);
        Assert.Equal(1, snapshot.AbortedFrameSends);
        Assert.True(snapshot.IsCongested);
        Assert.False(snapshot.IsSeverelyCongested);
    }

    [Fact]
    public void CongestionControllerRequiresPacketAndFrameMetrics()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        LowLatencyVideoFeedbackV2 partial = CreateFeedback(
            1_000_000,
            100,
            100,
            120_000) with
        {
            ValidMetrics = LowLatencyVideoFeedbackMetrics.PacketDelivery
        };

        Assert.False(controller.TryObserveFeedback(partial));
        Assert.False(controller.CollectSnapshot().HasFeedbackSample);
    }

    [Fact]
    public void FrameBudgetAndPacerStayWithinLatencyBounds()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);

        int budget = controller.GetFrameBudgetBytes();
        Assert.InRange(
            budget,
            LowLatencyVideoCongestionController.MinimumFrameBudgetBytes,
            LowLatencyVideoCongestionController.MaximumFrameBudgetBytes);

        TimeSpan delay = LowLatencyVideoPacer.CalculateDelay(
            TimeSpan.FromMilliseconds(1),
            wireBytesSent: 100_000,
            targetBitsPerSecond: 100_000_000);
        Assert.Equal(TimeSpan.FromMilliseconds(7), delay);
        Assert.Equal(
            TimeSpan.Zero,
            LowLatencyVideoPacer.CalculateDelay(
                TimeSpan.FromMilliseconds(10),
                100_000,
                100_000_000));
    }

    [Fact]
    public void ConfiguredFrameSerializationBudgetTracksFrameRate()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);

        Assert.Equal(
            LowLatencyVideoCongestionController
                .FrameSerializationBudget,
            controller.GetFrameSerializationBudget());

        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);
        Assert.Equal(
            LowLatencyVideoCongestionController
                .HighFrameRateFrameSerializationBudget,
            controller.GetFrameSerializationBudget());

        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 20_000_000,
            framesPerSecond: 30);
        Assert.Equal(
            LowLatencyVideoCongestionController
                .FrameSerializationBudget,
            controller.GetFrameSerializationBudget());
    }

    [Fact]
    public void LargeIndependentVideoFramesUseBoundedBootstrapBurst()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        int largeFrameBytes =
            LowLatencyVideoPacer.LargeIndependentVideoFrameMinimumBytes;

        Assert.InRange(
            LowLatencyVideoPacer
                .LargeIndependentVideoFrameTargetBitsPerSecond,
            LowLatencyVideoCongestionController
                .InitialTargetBitsPerSecond + 1,
            LowLatencyVideoCongestionController
                .MaximumTargetBitsPerSecond - 1);
        long pacingTarget =
            controller.GetFramePacingTargetBitsPerSecond(
                largeFrameBytes,
                allowLargeIndependentVideoBurst: true);

        Assert.Equal(
            LowLatencyVideoPacer
                .LargeIndependentVideoFrameTargetBitsPerSecond,
            pacingTarget);
        Assert.Equal(
            LowLatencyVideoCongestionController
                .InitialTargetBitsPerSecond,
            controller.GetFramePacingTargetBitsPerSecond(
                largeFrameBytes - 1,
                allowLargeIndependentVideoBurst: true));
        Assert.Equal(
            LowLatencyVideoCongestionController
                .InitialTargetBitsPerSecond,
            controller.GetFramePacingTargetBitsPerSecond(
                largeFrameBytes,
                allowLargeIndependentVideoBurst: false));

        Assert.Equal(
            LowLatencyVideoCongestionController
                .MaximumTargetBitsPerSecond,
            LowLatencyVideoPacer.SelectFrameTargetBitsPerSecond(
                largeFrameBytes,
                LowLatencyVideoCongestionController
                    .MaximumTargetBitsPerSecond,
                allowLargeIndependentVideoBurst: true,
                hasAdverseNetworkFeedback: false));
    }

    [Fact]
    public void FourKSixtyBudgetUsesCleanLinkBurstForMeasuredFrameSize()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback |
                LowLatencyVideoFeatures.XorFec);
        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);
        Assert.True(controller.XorFecEnabled);

        long expectedTarget =
            LowLatencyVideoCongestionController
                .CalculateNominalPacingTargetBitsPerSecond(
                    79_600_000);
        Assert.InRange(expectedTarget, 101_000_000, 103_000_000);
        Assert.Equal(expectedTarget, controller.TargetBitsPerSecond);
        const int measuredEncodedFrameBytes = 164 * 1024;
        Assert.Equal(
            LowLatencyVideoPacer
                .LargeIndependentVideoFrameTargetBitsPerSecond,
            controller.GetFramePacingTargetBitsPerSecond(
                measuredEncodedFrameBytes,
                allowLargeIndependentVideoBurst: true));
        Assert.InRange(
            LowLatencyVideoPacer.CalculateDelay(
                TimeSpan.Zero,
                // Approximately 189 KiB on the wire after encrypted
                // datagram and proactive XOR-FEC expansion.
                wireBytesSent: 189 * 1024,
                targetBitsPerSecond:
                    LowLatencyVideoPacer
                        .LargeIndependentVideoFrameTargetBitsPerSecond)
                .TotalMilliseconds,
            9,
            11);
        Assert.True(
            controller.GetFrameBudgetBytes() >= 240 * 1024);
    }

    [Fact]
    public void ConfiguredVideoRateIsBaselineForEarlyCongestionFeedback()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);
        Assert.False(controller.XorFecEnabled);
        long nominal =
            LowLatencyVideoCongestionController
                .CalculateNominalPacingTargetBitsPerSecond(
                    79_600_000);

        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed: 1_000_000,
            largest: 100,
            packets: 100,
            bytes: 120_000,
            completed: 40)));
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed: 1_250_000,
            largest: 200,
            packets: 200,
            bytes: 240_000,
            completed: 49,
            abandoned: 1)));

        long reduced = checked((long)(nominal * 0.85));
        Assert.Equal(reduced, controller.TargetBitsPerSecond);
        Assert.Equal(
            reduced,
            controller.GetFramePacingTargetBitsPerSecond(
                240 * 1024,
                allowLargeIndependentVideoBurst: true));
    }

    [Fact]
    public void CleanRecoveryStopsAtConfiguredVideoPacingBudget()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback |
                LowLatencyVideoFeatures.XorFec);
        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);
        Assert.True(controller.XorFecEnabled);
        long nominal =
            LowLatencyVideoCongestionController
                .CalculateNominalPacingTargetBitsPerSecond(
                    79_600_000);

        ulong elapsed = 1_000_000;
        ulong largest = 100;
        ulong packets = 100;
        ulong bytes = 120_000;
        ulong lost = 0;
        ulong completed = 10;
        ulong abandoned = 0;
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            lost: lost,
            completed: completed,
            abandoned: abandoned)));

        for (int index = 0; index < 60; index++)
        {
            elapsed += 250_000;
            largest += 100;
            packets += 100;
            bytes += 120_000;
            completed += 10;
            Assert.True(controller.TryObserveFeedback(CreateFeedback(
                elapsed,
                largest,
                packets,
                bytes,
                lost: lost,
                completed: completed,
                abandoned: abandoned)));
            Assert.Equal(nominal, controller.TargetBitsPerSecond);
            Assert.True(controller.XorFecEnabled);
        }

        elapsed += 250_000;
        largest += 100;
        packets += 97;
        bytes += 116_400;
        lost += 3;
        completed += 9;
        abandoned++;
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed,
            largest,
            packets,
            bytes,
            lost: lost,
            completed: completed,
            abandoned: abandoned)));
        long reduced = checked((long)(nominal * 0.85));
        Assert.Equal(reduced, controller.TargetBitsPerSecond);
        Assert.True(controller.XorFecEnabled);
        Assert.Equal(
            reduced,
            controller.GetFramePacingTargetBitsPerSecond(
                164 * 1024,
                allowLargeIndependentVideoBurst: true));

        controller.ConfigureVideoRateBudget(
            encoderBitrateBitsPerSecond: 79_600_000,
            framesPerSecond: 60);
        Assert.Equal(
            reduced,
            controller.TargetBitsPerSecond);
        Assert.True(controller.XorFecEnabled);
        Assert.Equal(
            reduced,
            controller.GetFramePacingTargetBitsPerSecond(
                164 * 1024,
                allowLargeIndependentVideoBurst: true));

        for (int index = 0; index < 60; index++)
        {
            elapsed += 250_000;
            largest += 100;
            packets += 100;
            bytes += 120_000;
            completed += 10;
            Assert.True(controller.TryObserveFeedback(CreateFeedback(
                elapsed,
                largest,
                packets,
                bytes,
                lost: lost,
                completed: completed,
                abandoned: abandoned)));
            Assert.InRange(
                controller.TargetBitsPerSecond,
                LowLatencyVideoCongestionController
                    .MinimumTargetBitsPerSecond,
                nominal);
            Assert.True(controller.XorFecEnabled);
        }

        Assert.Equal(nominal, controller.TargetBitsPerSecond);
        Assert.True(controller.XorFecEnabled);
        Assert.Equal(
            LowLatencyVideoPacer
                .LargeIndependentVideoFrameTargetBitsPerSecond,
            controller.GetFramePacingTargetBitsPerSecond(
                240 * 1024,
                allowLargeIndependentVideoBurst: true));
    }

    [Fact]
    public void LargeIndependentVideoBurstRetiresOnAdverseFeedback()
    {
        var controller = new LowLatencyVideoCongestionController(
            LowLatencyVideoFeatures.CongestionFeedback);
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed: 1_000_000,
            largest: 100,
            packets: 100,
            bytes: 120_000,
            completed: 10)));
        Assert.True(controller.TryObserveFeedback(CreateFeedback(
            elapsed: 1_250_000,
            largest: 200,
            packets: 199,
            bytes: 238_800,
            lost: 1,
            completed: 20)));

        Assert.Equal(
            LowLatencyVideoCongestionController
                .InitialTargetBitsPerSecond,
            controller.TargetBitsPerSecond);
        Assert.Equal(
            controller.TargetBitsPerSecond,
            controller.GetFramePacingTargetBitsPerSecond(
                LowLatencyVideoPacer
                    .LargeIndependentVideoFrameMinimumBytes,
                allowLargeIndependentVideoBurst: true));
        Assert.Equal(
            LowLatencyVideoPacer
                .LargeIndependentVideoFrameTargetBitsPerSecond,
            LowLatencyVideoPacer.SelectFrameTargetBitsPerSecond(
                LowLatencyVideoPacer
                    .LargeIndependentVideoFrameMinimumBytes,
                LowLatencyVideoCongestionController
                    .MaximumTargetBitsPerSecond,
                allowLargeIndependentVideoBurst: true,
                hasAdverseNetworkFeedback: true));
        Assert.Equal(
            LowLatencyVideoCongestionController
                .InitialTargetBitsPerSecond,
            LowLatencyVideoPacer.SelectFrameTargetBitsPerSecond(
                LowLatencyVideoPacer
                    .LargeIndependentVideoFrameMinimumBytes,
                LowLatencyVideoCongestionController
                    .InitialTargetBitsPerSecond,
                allowLargeIndependentVideoBurst: true,
                hasAdverseNetworkFeedback: true));
    }

    [Fact]
    public void ShortGopLanBurstIsBoundedAndRetiresAfterCongestion()
    {
        Assert.True(
            LowLatencyVideoPacer
                .ShouldBypassDelayForShortGopLanBurst(
                    LowLatencyVideoPacer
                        .MaximumShortGopLanBurstBytes,
                    LowLatencyVideoPacer
                        .ShortGopLanBurstTargetBitsPerSecond));
        Assert.False(
            LowLatencyVideoPacer
                .ShouldBypassDelayForShortGopLanBurst(
                    LowLatencyVideoPacer
                        .MaximumShortGopLanBurstBytes + 1,
                    LowLatencyVideoPacer
                        .ShortGopLanBurstTargetBitsPerSecond));
        Assert.False(
            LowLatencyVideoPacer
                .ShouldBypassDelayForShortGopLanBurst(
                    64 * 1024,
                    LowLatencyVideoPacer
                        .ShortGopLanBurstTargetBitsPerSecond - 1));
    }

    [Fact]
    public void FeatureNegotiationRequiresCongestionBitBeforeFec()
    {
        Assert.Equal(
            LowLatencyVideoFeatures.None,
            LowLatencyVideoFeatureNegotiation.FromCapabilities(
                RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec |
                    RemoteDeviceCapabilities.LowLatencyUdpMouseInput |
                    RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck));
        Assert.Equal(
            LowLatencyVideoFeatures.CongestionFeedback |
                LowLatencyVideoFeatures.XorFec |
                LowLatencyVideoFeatures.UdpMouseInput |
                LowLatencyVideoFeatures.UdpMouseInputAppliedAck |
                LowLatencyVideoFeatures.ShortGopH264 |
                LowLatencyVideoFeatures.AuthenticatedHeartbeat,
            LowLatencyVideoFeatureNegotiation.FromCapabilities(
                RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
                    RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec |
                    RemoteDeviceCapabilities.LowLatencyUdpMouseInput |
                    RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck |
                    RemoteDeviceCapabilities.ShortGopH264 |
                    RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat));
        Assert.Equal(
            LowLatencyVideoFeatures.CongestionFeedback,
            LowLatencyVideoFeatureNegotiation.FromCapabilities(
                RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
                    RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck));
    }

    private static LowLatencyVideoFeedbackV2 CreateFeedback(
        ulong elapsed,
        ulong largest,
        ulong packets,
        ulong bytes,
        ulong lost = 0,
        ulong late = 0,
        ulong completed = 0,
        ulong abandoned = 0)
    {
        return new LowLatencyVideoFeedbackV2(
            elapsed,
            largest,
            0,
            LowLatencyVideoFeedbackMetrics.PacketDelivery |
                LowLatencyVideoFeedbackMetrics.FrameAssembly,
            packets,
            bytes,
            lost,
            late,
            completed,
            completed,
            abandoned,
            0,
            0,
            0,
            0);
    }
}
