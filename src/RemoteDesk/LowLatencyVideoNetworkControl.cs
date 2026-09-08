using System.Buffers.Binary;
using System.Diagnostics;

namespace RemoteDesk;

[Flags]
internal enum LowLatencyVideoFeatures
{
    None = 0,
    CongestionFeedback = 1 << 0,
    XorFec = 1 << 1,
    UdpMouseInput = 1 << 2,
    UdpMouseInputAppliedAck = 1 << 3,
    ShortGopH264 = 1 << 4,
    AuthenticatedHeartbeat = 1 << 5
}

[Flags]
internal enum LowLatencyVideoFeedbackMetrics : uint
{
    None = 0,
    PacketDelivery = 1 << 0,
    FrameAssembly = 1 << 1,
    ConsumerQueue = 1 << 2,
    Decode = 1 << 3,
    Known = PacketDelivery | FrameAssembly | ConsumerQueue | Decode
}

internal readonly record struct LowLatencyVideoFeedbackV2(
    ulong ReceiverElapsedMicroseconds,
    ulong LargestReceivedPacketSequence,
    uint AckDelayMicroseconds,
    LowLatencyVideoFeedbackMetrics ValidMetrics,
    ulong TotalAuthenticatedPackets,
    ulong TotalAuthenticatedWireBytes,
    ulong TotalSettledLostPackets,
    ulong TotalLatePackets,
    ulong HighestCompletedFrameSequence,
    ulong TotalCompletedFrames,
    ulong TotalAbandonedIncompleteFrames,
    ulong TotalConsumerDroppedFrames,
    ushort ConsumerQueueDepth,
    ushort ConsumerQueueCapacity,
    uint AverageDecodeMicroseconds);

internal static class LowLatencyVideoFeedbackV2Codec
{
    public const int PayloadLength = 96;

    public static byte[] Encode(LowLatencyVideoFeedbackV2 feedback)
    {
        Validate(feedback);
        byte[] payload = new byte[PayloadLength];
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload,
            feedback.ReceiverElapsedMicroseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(8),
            feedback.LargestReceivedPacketSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(16),
            feedback.AckDelayMicroseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(20),
            (uint)feedback.ValidMetrics);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(24),
            feedback.TotalAuthenticatedPackets);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(32),
            feedback.TotalAuthenticatedWireBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(40),
            feedback.TotalSettledLostPackets);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(48),
            feedback.TotalLatePackets);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(56),
            feedback.HighestCompletedFrameSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(64),
            feedback.TotalCompletedFrames);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(72),
            feedback.TotalAbandonedIncompleteFrames);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(80),
            feedback.TotalConsumerDroppedFrames);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(88),
            feedback.ConsumerQueueDepth);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(90),
            feedback.ConsumerQueueCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(92),
            feedback.AverageDecodeMicroseconds);
        return payload;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out LowLatencyVideoFeedbackV2 feedback)
    {
        feedback = default;
        if (payload.Length != PayloadLength)
        {
            return false;
        }

        var candidate = new LowLatencyVideoFeedbackV2(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]),
            (LowLatencyVideoFeedbackMetrics)
                BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[32..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[48..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[56..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[64..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[72..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[80..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[88..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[90..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[92..]));
        if (!IsValid(candidate))
        {
            return false;
        }

        feedback = candidate;
        return true;
    }

    private static void Validate(LowLatencyVideoFeedbackV2 feedback)
    {
        if (!IsValid(feedback))
        {
            throw new ArgumentException("低延迟画面 FeedbackV2 参数异常。", nameof(feedback));
        }
    }

    private static bool IsValid(LowLatencyVideoFeedbackV2 feedback)
    {
        if ((feedback.ValidMetrics & ~LowLatencyVideoFeedbackMetrics.Known) != 0 ||
            feedback.AckDelayMicroseconds > feedback.ReceiverElapsedMicroseconds ||
            feedback.TotalLatePackets > feedback.TotalAuthenticatedPackets ||
            feedback.ConsumerQueueDepth > feedback.ConsumerQueueCapacity)
        {
            return false;
        }

        if (!feedback.ValidMetrics.HasFlag(
                LowLatencyVideoFeedbackMetrics.ConsumerQueue) &&
            (feedback.TotalConsumerDroppedFrames != 0 ||
                feedback.ConsumerQueueDepth != 0 ||
                feedback.ConsumerQueueCapacity != 0))
        {
            return false;
        }

        return feedback.ValidMetrics.HasFlag(
                LowLatencyVideoFeedbackMetrics.Decode) ||
            feedback.AverageDecodeMicroseconds == 0;
    }
}

internal sealed class LowLatencyVideoPacketArrivalTracker
{
    internal const ulong ReorderingWindowPackets = 64;

    private readonly object _lock = new();
    private readonly long _startedAt;
    private readonly HashSet<ulong> _unsettledPackets = [];
    private bool _initialized;
    private bool _hasSettledAny;
    private ulong _nextSequenceToSettle;
    private ulong _largestSequence;
    private long _largestReceivedAt;
    private ulong _totalAuthenticatedPackets;
    private ulong _totalAuthenticatedWireBytes;
    private ulong _totalSettledLostPackets;
    private ulong _totalLatePackets;

    public LowLatencyVideoPacketArrivalTracker()
        : this(Stopwatch.GetTimestamp())
    {
    }

    internal LowLatencyVideoPacketArrivalTracker(long startedAt)
    {
        _startedAt = startedAt;
    }

    public void Observe(ulong packetSequence, int wireBytes)
    {
        Observe(packetSequence, wireBytes, Stopwatch.GetTimestamp());
    }

    internal void Observe(
        ulong packetSequence,
        int wireBytes,
        long receivedAt)
    {
        if (wireBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wireBytes));
        }

        if (receivedAt < _startedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(receivedAt));
        }

        lock (_lock)
        {
            _totalAuthenticatedPackets = SaturatingIncrement(
                _totalAuthenticatedPackets);
            _totalAuthenticatedWireBytes = SaturatingAdd(
                _totalAuthenticatedWireBytes,
                checked((ulong)wireBytes));

            if (!_initialized)
            {
                _initialized = true;
                _nextSequenceToSettle = packetSequence;
                _largestSequence = packetSequence;
                _largestReceivedAt = receivedAt;
                _unsettledPackets.Add(packetSequence);
                return;
            }

            if (packetSequence < _nextSequenceToSettle)
            {
                if (!_hasSettledAny)
                {
                    _nextSequenceToSettle = packetSequence;
                    _unsettledPackets.Add(packetSequence);
                }
                else
                {
                    _totalLatePackets = SaturatingIncrement(
                        _totalLatePackets);
                }
            }
            else
            {
                _unsettledPackets.Add(packetSequence);
            }

            if (packetSequence > _largestSequence)
            {
                _largestSequence = packetSequence;
                _largestReceivedAt = receivedAt;
            }

            SettleOldPackets();
        }
    }

    public LowLatencyVideoFeedbackV2 CreateFeedback(
        ulong highestCompletedFrameSequence,
        ulong totalCompletedFrames,
        ulong totalAbandonedIncompleteFrames)
    {
        lock (_lock)
        {
            return CreateFeedbackLocked(
                highestCompletedFrameSequence,
                totalCompletedFrames,
                totalAbandonedIncompleteFrames,
                Stopwatch.GetTimestamp());
        }
    }

    internal LowLatencyVideoFeedbackV2 CreateFeedback(
        ulong highestCompletedFrameSequence,
        ulong totalCompletedFrames,
        ulong totalAbandonedIncompleteFrames,
        long createdAt)
    {
        lock (_lock)
        {
            return CreateFeedbackLocked(
                highestCompletedFrameSequence,
                totalCompletedFrames,
                totalAbandonedIncompleteFrames,
                createdAt);
        }
    }

    private LowLatencyVideoFeedbackV2 CreateFeedbackLocked(
        ulong highestCompletedFrameSequence,
        ulong totalCompletedFrames,
        ulong totalAbandonedIncompleteFrames,
        long createdAt)
    {
        if (createdAt < _startedAt ||
            (_initialized && createdAt < _largestReceivedAt))
        {
            throw new ArgumentOutOfRangeException(nameof(createdAt));
        }

        ulong elapsedMicroseconds = ElapsedMicroseconds(
            _startedAt,
            createdAt);
        uint ackDelayMicroseconds = _initialized
            ? checked((uint)Math.Min(
                uint.MaxValue,
                ElapsedMicroseconds(_largestReceivedAt, createdAt)))
            : 0;
        return new LowLatencyVideoFeedbackV2(
            elapsedMicroseconds,
            _initialized ? _largestSequence : 0,
            ackDelayMicroseconds,
            LowLatencyVideoFeedbackMetrics.PacketDelivery |
                LowLatencyVideoFeedbackMetrics.FrameAssembly,
            _totalAuthenticatedPackets,
            _totalAuthenticatedWireBytes,
            _totalSettledLostPackets,
            _totalLatePackets,
            highestCompletedFrameSequence,
            totalCompletedFrames,
            totalAbandonedIncompleteFrames,
            0,
            0,
            0,
            0);
    }

    private void SettleOldPackets()
    {
        if (_largestSequence < ReorderingWindowPackets)
        {
            return;
        }

        ulong settleThrough = _largestSequence - ReorderingWindowPackets;
        if (settleThrough < _nextSequenceToSettle)
        {
            return;
        }

        ulong expected = settleThrough - _nextSequenceToSettle + 1;
        // HashSet.RemoveWhere with a capturing predicate allocated a closure
        // for every authenticated video datagram (roughly 7,000-8,000/s at
        // native 4K). Those short-lived objects caused avoidable Gen0 pauses
        // on the latency-critical receive thread. The unsettled set is bounded
        // by the 64-packet reordering window, so collect its settled keys in a
        // small stack buffer and remove them without heap allocation.
        Span<ulong> settledPackets = stackalloc ulong[
            checked((int)ReorderingWindowPackets + 1)];
        int settledPacketCount = 0;
        foreach (ulong sequence in _unsettledPackets)
        {
            if (sequence <= settleThrough)
            {
                if (settledPacketCount >= settledPackets.Length)
                {
                    throw new InvalidOperationException(
                        "Packet arrival reordering window exceeded its bound.");
                }

                settledPackets[settledPacketCount++] = sequence;
            }
        }

        for (int index = 0; index < settledPacketCount; index++)
        {
            _unsettledPackets.Remove(settledPackets[index]);
        }

        ulong received = checked((ulong)settledPacketCount);
        if (received < expected)
        {
            _totalSettledLostPackets = SaturatingAdd(
                _totalSettledLostPackets,
                expected - received);
        }

        _hasSettledAny = true;
        _nextSequenceToSettle = settleThrough == ulong.MaxValue
            ? ulong.MaxValue
            : settleThrough + 1;
    }

    private static ulong ElapsedMicroseconds(long startedAt, long endedAt)
    {
        if (endedAt <= startedAt)
        {
            return 0;
        }

        double microseconds =
            (endedAt - startedAt) * 1_000_000d / Stopwatch.Frequency;
        return microseconds >= ulong.MaxValue
            ? ulong.MaxValue
            : checked((ulong)microseconds);
    }

    private static ulong SaturatingIncrement(ulong value)
    {
        return value == ulong.MaxValue ? value : value + 1;
    }

    private static ulong SaturatingAdd(ulong value, ulong increment)
    {
        return ulong.MaxValue - value < increment
            ? ulong.MaxValue
            : value + increment;
    }
}

internal readonly record struct LowLatencyVideoNetworkSnapshot(
    bool HasFeedbackSample,
    double PacketLossRatio,
    double FrameAbandonRatio,
    double SenderQueueDropRatio,
    double DeliveryMegabitsPerSecond,
    double TargetMegabitsPerSecond,
    bool XorFecEnabled,
    long OversizedFrameDrops,
    long AbortedFrameSends)
{
    public bool IsSeverelyCongested =>
        OversizedFrameDrops > 0 ||
        SenderQueueDropRatio >= 0.25 ||
        PacketLossRatio >= 0.08 ||
        FrameAbandonRatio >= 0.30;

    public bool IsCongested =>
        IsSeverelyCongested ||
        AbortedFrameSends > 0 ||
        SenderQueueDropRatio >= 0.08 ||
        PacketLossRatio >= 0.02 ||
        FrameAbandonRatio >= 0.10;

    public bool IsComfortable =>
        HasFeedbackSample &&
        SenderQueueDropRatio < 0.02 &&
        PacketLossRatio < 0.005 &&
        FrameAbandonRatio < 0.02 &&
        OversizedFrameDrops == 0 &&
        AbortedFrameSends == 0;

    public bool AllowsInteractiveBoost =>
        HasFeedbackSample &&
        SenderQueueDropRatio < 0.25 &&
        PacketLossRatio < 0.005 &&
        FrameAbandonRatio < 0.02 &&
        OversizedFrameDrops == 0 &&
        AbortedFrameSends == 0;
}

internal sealed class LowLatencyVideoCongestionController
{
    public const long InitialTargetBitsPerSecond = 100_000_000;
    public const long MinimumTargetBitsPerSecond = 10_000_000;
    public const long MaximumTargetBitsPerSecond = 200_000_000;
    public const int MinimumFrameBudgetBytes = 128 * 1024;
    public const int MaximumFrameBudgetBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan FrameSerializationBudget =
        TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan
        HighFrameRateFrameSerializationBudget =
            TimeSpan.FromMilliseconds(50);
    internal const double VideoPacingOverheadMultiplier = 1.28d;

    private const int CleanSamplesBeforeIncrease = 10;
    private const ulong MinimumControlSampleMicroseconds = 200_000;

    private readonly object _lock = new();
    private readonly bool _allowXorFec;
    private LowLatencyVideoFeedbackV2 _previousFeedback;
    private LowLatencyVideoFeedbackV2 _packetFeedbackBaseline;
    private LowLatencyVideoFeedbackV2 _frameFeedbackBaseline;
    private bool _hasPreviousFeedback;
    private bool _hasFeedbackSample;
    private bool _hasPacketFeedbackSample;
    private bool _hasFrameFeedbackSample;
    private double _packetLossRatio;
    private double _frameAbandonRatio;
    private double _deliveryMegabitsPerSecond;
    private long _targetBitsPerSecond = InitialTargetBitsPerSecond;
    private long _nominalPacingTargetBitsPerSecond =
        InitialTargetBitsPerSecond;
    private bool _hasConfiguredVideoRateBudget;
    private TimeSpan _frameSerializationBudget =
        FrameSerializationBudget;
    private int _cleanSamples;
    private bool _xorFecEnabled;
    private bool _proactiveHighFrameRateXorFec;
    private bool _proactiveXorFecSuspended;
    private int _proactiveXorFecCleanSamples;
    private long _queuedFrames;
    private long _replacedFrames;
    private long _oversizedFrameDrops;
    private long _abortedFrameSends;
    private long _collectedQueuedFrames;
    private long _collectedReplacedFrames;
    private long _collectedOversizedFrameDrops;
    private long _collectedAbortedFrameSends;

    public LowLatencyVideoCongestionController(
        LowLatencyVideoFeatures features)
    {
        _allowXorFec = features.HasFlag(LowLatencyVideoFeatures.XorFec);
    }

    public long TargetBitsPerSecond
    {
        get
        {
            lock (_lock)
            {
                return _targetBitsPerSecond;
            }
        }
    }

    public TimeSpan GetFrameSerializationBudget()
    {
        lock (_lock)
        {
            return _frameSerializationBudget;
        }
    }

    public long GetFramePacingTargetBitsPerSecond(
        int frameBytes,
        bool allowLargeIndependentVideoBurst)
    {
        if (frameBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameBytes));
        }

        lock (_lock)
        {
            bool hasAdverseNetworkFeedback =
                HasAdverseNetworkFeedbackLocked();
            long pacingTargetBitsPerSecond =
                hasAdverseNetworkFeedback
                    ? _targetBitsPerSecond
                    : Math.Max(
                        _targetBitsPerSecond,
                        _nominalPacingTargetBitsPerSecond);
            return LowLatencyVideoPacer.SelectFrameTargetBitsPerSecond(
                frameBytes,
                pacingTargetBitsPerSecond,
                allowLargeIndependentVideoBurst,
                hasAdverseNetworkFeedback);
        }
    }

    public void ConfigureVideoRateBudget(
        int encoderBitrateBitsPerSecond,
        int framesPerSecond)
    {
        if (encoderBitrateBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encoderBitrateBitsPerSecond));
        }

        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond));
        }

        lock (_lock)
        {
            long previousNominalPacingTargetBitsPerSecond =
                _nominalPacingTargetBitsPerSecond;
            long nextNominalPacingTargetBitsPerSecond =
                CalculateNominalPacingTargetBitsPerSecond(
                    encoderBitrateBitsPerSecond);
            // Start congestion control from the rate required by the active
            // hardware stream. Otherwise one incomplete frame during UDP
            // handoff can expose the legacy 100 Mbps bootstrap target, which
            // is already below a valid 4K60 GOP1 stream. That self-inflicted
            // under-pacing creates sender replacements and a false FPS
            // reduction. Genuine receiver pressure still reduces this
            // baseline immediately through UpdateTargetAndFec. A later
            // capture-backend reconfiguration preserves any congestion
            // reduction as a ratio of the old nominal rate instead of
            // silently resetting the network to a clean-link budget.
            if (_hasConfiguredVideoRateBudget)
            {
                long cappedPreviousTargetBitsPerSecond = Math.Min(
                    _targetBitsPerSecond,
                    previousNominalPacingTargetBitsPerSecond);
                _targetBitsPerSecond = Math.Clamp(
                    checked(
                        nextNominalPacingTargetBitsPerSecond *
                        cappedPreviousTargetBitsPerSecond /
                        previousNominalPacingTargetBitsPerSecond),
                    MinimumTargetBitsPerSecond,
                    nextNominalPacingTargetBitsPerSecond);
            }
            else
            {
                _targetBitsPerSecond = Math.Max(
                    _targetBitsPerSecond,
                    nextNominalPacingTargetBitsPerSecond);
                _hasConfiguredVideoRateBudget = true;
            }

            _nominalPacingTargetBitsPerSecond =
                nextNominalPacingTargetBitsPerSecond;
            _frameSerializationBudget = framesPerSecond > 30
                ? HighFrameRateFrameSerializationBudget
                : FrameSerializationBudget;
            bool proactiveHighFrameRateXorFec =
                _allowXorFec && framesPerSecond > 30;
            if (proactiveHighFrameRateXorFec &&
                !_proactiveHighFrameRateXorFec)
            {
                // Sparse Wi-Fi loss cannot be repaired by parity that is
                // enabled only after the first incomplete frame. High-frame-
                // rate streams already reserve encrypted datagram and FEC
                // overhead in their nominal pacing target, so protect them
                // from the first loss window.
                _proactiveXorFecSuspended = false;
                _proactiveXorFecCleanSamples = 0;
                _xorFecEnabled = true;
            }
            else if (!proactiveHighFrameRateXorFec)
            {
                _proactiveXorFecSuspended = false;
                _proactiveXorFecCleanSamples = 0;
            }
            else if (!_proactiveXorFecSuspended)
            {
                _xorFecEnabled = true;
            }

            _proactiveHighFrameRateXorFec =
                proactiveHighFrameRateXorFec;
        }
    }

    internal static long
        CalculateNominalPacingTargetBitsPerSecond(
            int encoderBitrateBitsPerSecond)
    {
        if (encoderBitrateBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encoderBitrateBitsPerSecond));
        }

        long requested = checked(
            (long)Math.Ceiling(
                encoderBitrateBitsPerSecond *
                VideoPacingOverheadMultiplier));
        return Math.Clamp(
            requested,
            InitialTargetBitsPerSecond,
            MaximumTargetBitsPerSecond);
    }

    public bool XorFecEnabled
    {
        get
        {
            lock (_lock)
            {
                return _xorFecEnabled;
            }
        }
    }

    public bool TryObserveFeedback(LowLatencyVideoFeedbackV2 feedback)
    {
        lock (_lock)
        {
            const LowLatencyVideoFeedbackMetrics requiredMetrics =
                LowLatencyVideoFeedbackMetrics.PacketDelivery |
                LowLatencyVideoFeedbackMetrics.FrameAssembly;
            if ((feedback.ValidMetrics & requiredMetrics) !=
                requiredMetrics)
            {
                return false;
            }

            if (!_hasPreviousFeedback)
            {
                _previousFeedback = feedback;
                _packetFeedbackBaseline = feedback;
                _frameFeedbackBaseline = feedback;
                _hasPreviousFeedback = true;
                return true;
            }

            if (!IsMonotonic(_previousFeedback, feedback))
            {
                return false;
            }

            double? packetLoss = null;
            ulong packetElapsedMicroseconds =
                feedback.ReceiverElapsedMicroseconds -
                _packetFeedbackBaseline.ReceiverElapsedMicroseconds;
            if (packetElapsedMicroseconds >=
                MinimumControlSampleMicroseconds)
            {
                ulong receivedPackets =
                    feedback.TotalAuthenticatedPackets -
                    _packetFeedbackBaseline.TotalAuthenticatedPackets;
                ulong receivedBytes =
                    feedback.TotalAuthenticatedWireBytes -
                    _packetFeedbackBaseline.TotalAuthenticatedWireBytes;
                ulong settledLost =
                    feedback.TotalSettledLostPackets -
                    _packetFeedbackBaseline.TotalSettledLostPackets;
                ulong latePackets =
                    feedback.TotalLatePackets -
                    _packetFeedbackBaseline.TotalLatePackets;
                ulong permanentLoss = settledLost > latePackets
                    ? settledLost - latePackets
                    : 0;
                ulong expectedPackets = SaturatingAdd(
                    receivedPackets,
                    permanentLoss);
                if (expectedPackets >= 8)
                {
                    packetLoss = Math.Clamp(
                        (double)permanentLoss / expectedPackets,
                        0,
                        1);
                    _packetLossRatio = _hasPacketFeedbackSample
                        ? (_packetLossRatio * 0.6) +
                            (packetLoss.Value * 0.4)
                        : packetLoss.Value;
                    _hasPacketFeedbackSample = true;
                    _deliveryMegabitsPerSecond =
                        receivedBytes * 8d / packetElapsedMicroseconds;
                    _packetFeedbackBaseline = feedback;
                }
            }

            double? frameAbandon = null;
            ulong frameElapsedMicroseconds =
                feedback.ReceiverElapsedMicroseconds -
                _frameFeedbackBaseline.ReceiverElapsedMicroseconds;
            if (frameElapsedMicroseconds >=
                MinimumControlSampleMicroseconds)
            {
                ulong completedFrames =
                    feedback.TotalCompletedFrames -
                    _frameFeedbackBaseline.TotalCompletedFrames;
                ulong abandonedFrames =
                    feedback.TotalAbandonedIncompleteFrames -
                    _frameFeedbackBaseline
                        .TotalAbandonedIncompleteFrames;
                ulong settledFrames = SaturatingAdd(
                    completedFrames,
                    abandonedFrames);
                if (settledFrames > 0)
                {
                    frameAbandon = Math.Clamp(
                        (double)abandonedFrames / settledFrames,
                        0,
                        1);
                    _frameAbandonRatio = _hasFrameFeedbackSample
                        ? (_frameAbandonRatio * 0.6) +
                            (frameAbandon.Value * 0.4)
                        : frameAbandon.Value;
                    _hasFrameFeedbackSample = true;
                    _frameFeedbackBaseline = feedback;
                }
            }

            _previousFeedback = feedback;
            if (!packetLoss.HasValue && !frameAbandon.HasValue)
            {
                return true;
            }

            _hasFeedbackSample = true;
            UpdateTargetAndFec(packetLoss, frameAbandon);
            return true;
        }
    }

    public void RecordQueuedFrame(bool replacedPendingFrame)
    {
        lock (_lock)
        {
            _queuedFrames++;
            if (replacedPendingFrame)
            {
                _replacedFrames++;
            }
        }
    }

    public void RecordOversizedFrameDrop()
    {
        lock (_lock)
        {
            _oversizedFrameDrops++;
        }
    }

    public void RecordAbortedFrameSend()
    {
        lock (_lock)
        {
            _abortedFrameSends++;
        }
    }

    public int GetFrameBudgetBytes()
    {
        lock (_lock)
        {
            double fecExpansion = _xorFecEnabled
                ? (LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup + 1d) /
                    LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup
                : 1;
            double datagramExpansion =
                (double)LowLatencyVideoProtocol.DefaultMaxDatagramBytes /
                LowLatencyVideoProtocol.GetMaxFragmentPayloadBytes(
                    LowLatencyVideoProtocol.DefaultMaxDatagramBytes);
            long pacingTargetBitsPerSecond =
                HasAdverseNetworkFeedbackLocked()
                    ? _targetBitsPerSecond
                    : Math.Max(
                        _targetBitsPerSecond,
                        _nominalPacingTargetBitsPerSecond);
            double budget =
                pacingTargetBitsPerSecond *
                _frameSerializationBudget.TotalSeconds /
                8d /
                fecExpansion /
                datagramExpansion;
            return (int)Math.Clamp(
                budget,
                MinimumFrameBudgetBytes,
                MaximumFrameBudgetBytes);
        }
    }

    public LowLatencyVideoNetworkSnapshot CollectSnapshot()
    {
        lock (_lock)
        {
            long queuedDelta = Math.Max(
                0,
                _queuedFrames - _collectedQueuedFrames);
            long replacedDelta = Math.Max(
                0,
                _replacedFrames - _collectedReplacedFrames);
            long oversizedDelta = Math.Max(
                0,
                _oversizedFrameDrops - _collectedOversizedFrameDrops);
            long abortedDelta = Math.Max(
                0,
                _abortedFrameSends - _collectedAbortedFrameSends);
            long droppedDelta =
                replacedDelta + oversizedDelta + abortedDelta;
            double senderDropRatio = queuedDelta > 0
                ? Math.Clamp((double)droppedDelta / queuedDelta, 0, 1)
                : 0;

            // Pending-frame replacement is a freshness decision made before a
            // frame reaches the socket. Reducing the socket pacing target in
            // response creates a positive feedback loop: slower serialization
            // causes more latest-only replacements, which then slows the
            // sender again. Receiver packet loss and incomplete-frame feedback
            // remain the authoritative signals for lowering the network rate.
            // The ratio is still reported so capture can temporarily leave the
            // interactive tier until the sender catches up.

            _collectedQueuedFrames = _queuedFrames;
            _collectedReplacedFrames = _replacedFrames;
            _collectedOversizedFrameDrops = _oversizedFrameDrops;
            _collectedAbortedFrameSends = _abortedFrameSends;
            return new LowLatencyVideoNetworkSnapshot(
                _hasFeedbackSample,
                _packetLossRatio,
                _frameAbandonRatio,
                senderDropRatio,
                _deliveryMegabitsPerSecond,
                (HasAdverseNetworkFeedbackLocked()
                    ? _targetBitsPerSecond
                    : Math.Max(
                        _targetBitsPerSecond,
                        _nominalPacingTargetBitsPerSecond)) /
                    1_000_000d,
                _xorFecEnabled,
                oversizedDelta,
                abortedDelta);
        }
    }

    private void UpdateTargetAndFec(
        double? packetLoss,
        double? frameAbandon)
    {
        bool severe =
            (packetLoss.HasValue && packetLoss.Value >= 0.08) ||
            (frameAbandon.HasValue && frameAbandon.Value >= 0.30);
        bool congested = severe ||
            (packetLoss.HasValue && packetLoss.Value >= 0.02) ||
            (frameAbandon.HasValue && frameAbandon.Value >= 0.10);
        if (severe)
        {
            ReduceTarget(0.65);
            _cleanSamples = 0;
            _xorFecEnabled = false;
            if (_proactiveHighFrameRateXorFec)
            {
                _proactiveXorFecSuspended = true;
                _proactiveXorFecCleanSamples = 0;
            }

            return;
        }

        if (congested)
        {
            ReduceTarget(0.85);
            _cleanSamples = 0;
        }
        else if ((!packetLoss.HasValue || packetLoss.Value < 0.005) &&
            (!frameAbandon.HasValue || frameAbandon.Value < 0.02))
        {
            _cleanSamples++;
            if (_cleanSamples >= CleanSamplesBeforeIncrease)
            {
                long increased = Math.Max(
                    _targetBitsPerSecond + 250_000,
                    checked((long)(_targetBitsPerSecond * 1.05)));
                // The configured nominal rate already includes protocol
                // overhead. Increasing beyond it cannot improve a fixed-rate
                // encoder; it only compresses each frame into a shorter
                // socket/Wi-Fi burst and creates avoidable queue spikes.
                _targetBitsPerSecond = Math.Min(
                    _nominalPacingTargetBitsPerSecond,
                    increased);
                _cleanSamples = 0;
            }
        }
        else
        {
            _cleanSamples = 0;
        }

        if (!_allowXorFec)
        {
            _xorFecEnabled = false;
        }
        else if (_proactiveHighFrameRateXorFec)
        {
            if (!_proactiveXorFecSuspended)
            {
                _xorFecEnabled = true;
            }
            else if ((!packetLoss.HasValue ||
                    packetLoss.Value < 0.001) &&
                (!frameAbandon.HasValue ||
                    frameAbandon.Value == 0))
            {
                _proactiveXorFecCleanSamples++;
                if (_proactiveXorFecCleanSamples >=
                    CleanSamplesBeforeIncrease)
                {
                    _proactiveXorFecSuspended = false;
                    _proactiveXorFecCleanSamples = 0;
                    _xorFecEnabled = true;
                }
            }
            else
            {
                // Recovery must be based on consecutive clean samples. Do not
                // let ordinary adaptive-FEC thresholds immediately undo the
                // severe-congestion suspension.
                _proactiveXorFecCleanSamples = 0;
            }
        }
        else if ((packetLoss.HasValue && packetLoss.Value >= 0.003) ||
            (frameAbandon.HasValue && frameAbandon.Value > 0))
        {
            _xorFecEnabled = true;
        }
        else if (_cleanSamples == 0 &&
            (!packetLoss.HasValue || packetLoss.Value < 0.001) &&
            (!frameAbandon.HasValue || frameAbandon.Value == 0))
        {
            // Reaching a clean increase window also retires temporary parity.
            _xorFecEnabled = false;
        }
    }

    private void ReduceTarget(double factor)
    {
        long reduced = checked((long)(_targetBitsPerSecond * factor));
        _targetBitsPerSecond = Math.Clamp(
            reduced,
            MinimumTargetBitsPerSecond,
            MaximumTargetBitsPerSecond);
    }

    private bool HasAdverseNetworkFeedbackLocked() =>
        // High-frame-rate streams enable parity before the first loss sample.
        // That prophylactic FEC is not evidence of congestion and must not
        // suppress the clean-link 4K frame burst. Legacy/adaptive FEC still
        // denotes receiver pressure, a feedback-reduced target remains
        // authoritative until it recovers to nominal, and measured loss or
        // abandonment always takes precedence for every profile.
        (_hasConfiguredVideoRateBudget &&
            _targetBitsPerSecond <
                _nominalPacingTargetBitsPerSecond) ||
        (_xorFecEnabled && !_proactiveHighFrameRateXorFec) ||
        (_hasFeedbackSample &&
            (_packetLossRatio >= 0.005 ||
             _frameAbandonRatio >= 0.02));

    private static bool IsMonotonic(
        LowLatencyVideoFeedbackV2 previous,
        LowLatencyVideoFeedbackV2 current)
    {
        return current.ReceiverElapsedMicroseconds >
                previous.ReceiverElapsedMicroseconds &&
            current.LargestReceivedPacketSequence >=
                previous.LargestReceivedPacketSequence &&
            current.TotalAuthenticatedPackets >=
                previous.TotalAuthenticatedPackets &&
            current.TotalAuthenticatedWireBytes >=
                previous.TotalAuthenticatedWireBytes &&
            current.TotalSettledLostPackets >=
                previous.TotalSettledLostPackets &&
            current.TotalLatePackets >= previous.TotalLatePackets &&
            current.HighestCompletedFrameSequence >=
                previous.HighestCompletedFrameSequence &&
            current.TotalCompletedFrames >=
                previous.TotalCompletedFrames &&
            current.TotalAbandonedIncompleteFrames >=
                previous.TotalAbandonedIncompleteFrames &&
            current.TotalConsumerDroppedFrames >=
                previous.TotalConsumerDroppedFrames;
    }

    private static ulong SaturatingAdd(ulong first, ulong second)
    {
        return ulong.MaxValue - first < second
            ? ulong.MaxValue
            : first + second;
    }
}

internal static class LowLatencyVideoPacer
{
    internal const long ShortGopLanBurstTargetBitsPerSecond =
        100_000_000;
    internal const long MaximumShortGopLanBurstBytes =
        256 * 1024;
    internal const int LargeIndependentVideoFrameMinimumBytes =
        128 * 1024;
    internal const long LargeIndependentVideoFrameTargetBitsPerSecond =
        160_000_000;
    private static readonly TimeSpan MaximumSingleDelay =
        TimeSpan.FromMilliseconds(20);

    public static long SelectFrameTargetBitsPerSecond(
        int frameBytes,
        long congestionTargetBitsPerSecond,
        bool allowLargeIndependentVideoBurst,
        bool hasAdverseNetworkFeedback)
    {
        if (frameBytes < 0 || congestionTargetBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                congestionTargetBitsPerSecond <= 0
                    ? nameof(congestionTargetBitsPerSecond)
                    : nameof(frameBytes));
        }

        // Native-4K60 all-intra access units are about 164 KiB on the entity
        // hardware. Including authenticated UDP headers and proactive XOR
        // parity takes about 13 ms at the former 120 Mbps burst target. WGC's
        // real 160 Hz source necessarily presents some adjacent 60 Hz buckets
        // only 12.5 ms apart, so that budget serialized one frame into the
        // next and turned a valid 18.75 ms source p95 into roughly 24 ms at
        // the viewer. Use a 160 Mbps clean-link burst floor (about 9.7 ms for
        // 189 KiB on the wire) while keeping the sustained encoder budget at
        // 79.6 Mbps. A negotiated profile may supply a larger nominal target,
        // which must not be truncated back to this floor. Under adverse
        // feedback the controller target remains a strict cap, so real loss
        // or receiver pressure immediately removes the burst.
        if (!allowLargeIndependentVideoBurst ||
            frameBytes < LargeIndependentVideoFrameMinimumBytes ||
            congestionTargetBitsPerSecond <
                ShortGopLanBurstTargetBitsPerSecond)
        {
            return congestionTargetBitsPerSecond;
        }

        return hasAdverseNetworkFeedback
            ? Math.Min(
                congestionTargetBitsPerSecond,
                LargeIndependentVideoFrameTargetBitsPerSecond)
            : Math.Min(
                LowLatencyVideoCongestionController
                    .MaximumTargetBitsPerSecond,
                Math.Max(
                    congestionTargetBitsPerSecond,
                    LargeIndependentVideoFrameTargetBitsPerSecond));
    }

    public static bool ShouldBypassDelayForShortGopLanBurst(
        long wireBytesSent,
        long targetBitsPerSecond)
    {
        if (wireBytesSent < 0 || targetBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                targetBitsPerSecond <= 0
                    ? nameof(targetBitsPerSecond)
                    : nameof(wireBytesSent));
        }

        return targetBitsPerSecond >=
                ShortGopLanBurstTargetBitsPerSecond &&
            wireBytesSent <= MaximumShortGopLanBurstBytes;
    }

    public static TimeSpan CalculateDelay(
        TimeSpan elapsed,
        long wireBytesSent,
        long targetBitsPerSecond)
    {
        if (elapsed < TimeSpan.Zero ||
            wireBytesSent < 0 ||
            targetBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                targetBitsPerSecond <= 0
                    ? nameof(targetBitsPerSecond)
                    : wireBytesSent < 0
                        ? nameof(wireBytesSent)
                        : nameof(elapsed));
        }

        TimeSpan desired = TimeSpan.FromSeconds(
            wireBytesSent * 8d / targetBitsPerSecond);
        TimeSpan delay = desired - elapsed;
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > MaximumSingleDelay ? MaximumSingleDelay : delay;
    }
}

internal static class LowLatencyVideoFeatureNegotiation
{
    private const LowLatencyVideoFeatures KnownFeatures =
        LowLatencyVideoFeatures.CongestionFeedback |
        LowLatencyVideoFeatures.XorFec |
        LowLatencyVideoFeatures.UdpMouseInput |
        LowLatencyVideoFeatures.UdpMouseInputAppliedAck |
        LowLatencyVideoFeatures.ShortGopH264 |
        LowLatencyVideoFeatures.AuthenticatedHeartbeat;

    public static LowLatencyVideoFeatures Normalize(
        LowLatencyVideoFeatures features)
    {
        features &= KnownFeatures;
        if (!features.HasFlag(
                LowLatencyVideoFeatures.CongestionFeedback))
        {
            return LowLatencyVideoFeatures.None;
        }

        if (!features.HasFlag(
                LowLatencyVideoFeatures.UdpMouseInput))
        {
            features &=
                ~LowLatencyVideoFeatures
                    .UdpMouseInputAppliedAck;
        }

        return features;
    }

    public static LowLatencyVideoFeatures FromCapabilities(
        RemoteDeviceCapabilities capabilities)
    {
        if (!capabilities.HasFlag(
                RemoteDeviceCapabilities.UdpVideoCongestionFeedback))
        {
            return LowLatencyVideoFeatures.None;
        }

        LowLatencyVideoFeatures features =
            LowLatencyVideoFeatures.CongestionFeedback;
        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec))
        {
            features |= LowLatencyVideoFeatures.XorFec;
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.LowLatencyUdpMouseInput))
        {
            features |= LowLatencyVideoFeatures.UdpMouseInput;
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck))
        {
            features |=
                LowLatencyVideoFeatures
                    .UdpMouseInputAppliedAck;
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.ShortGopH264))
        {
            features |= LowLatencyVideoFeatures.ShortGopH264;
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat))
        {
            features |=
                LowLatencyVideoFeatures.AuthenticatedHeartbeat;
        }

        return Normalize(features);
    }
}
