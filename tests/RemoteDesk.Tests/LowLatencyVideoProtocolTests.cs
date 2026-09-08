using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class LowLatencyVideoProtocolTests
{
    private const ulong ChannelId = 0x1020_3040_5060_7080;
    private const uint Epoch = 0x1122_3344;
    private const int TestMaxDatagramBytes = LowLatencyVideoProtocol.MinDatagramBytes;
    private const int TestMaxFrameBytes = 4096;
    private const int TestMaxFragmentPayloadBytes =
        TestMaxDatagramBytes -
        LowLatencyVideoProtocol.HeaderLength -
        LowLatencyVideoProtocol.TagLength;
    private const int FecTestMaxFrameBytes =
        (LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup *
            TestMaxFragmentPayloadBytes) +
        137;

    [Fact]
    public void LowLatencyVideoOfferRoundTripsAllSecurityParameters()
    {
        LowLatencyVideoOffer expected = CreateOffer();

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(expected));

        Assert.Equal(RemoteControlKind.LowLatencyVideoOffer, control.Kind);
        LowLatencyVideoOffer actual = Assert.IsType<LowLatencyVideoOffer>(
            control.LowLatencyVideoOffer);
        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal(expected.MaxDatagramBytes, actual.MaxDatagramBytes);
        Assert.Equal(expected.MaxFrameBytes, actual.MaxFrameBytes);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.Epoch, actual.Epoch);
        Assert.Equal(expected.HostToViewerKey, actual.HostToViewerKey);
        Assert.Equal(expected.ViewerToHostKey, actual.ViewerToHostKey);
        Assert.Equal(expected.HostNoncePrefix, actual.HostNoncePrefix);
        Assert.Equal(expected.ViewerNoncePrefix, actual.ViewerNoncePrefix);
        Assert.Equal(expected.Challenge, actual.Challenge);
    }

    [Fact]
    public void LowLatencyVideoStateControlsRoundTrip()
    {
        RemoteControlMessage ready = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeLowLatencyVideoReady(ChannelId, Epoch));
        RemoteControlMessage stop = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeLowLatencyVideoStop(ChannelId, Epoch, 7));
        RemoteControlMessage stopped = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeLowLatencyVideoStopped(ChannelId, Epoch, 9));

        Assert.Equal(RemoteControlKind.LowLatencyVideoReady, ready.Kind);
        Assert.Equal(ChannelId, ready.LowLatencyVideoChannelId);
        Assert.Equal(Epoch, ready.LowLatencyVideoEpoch);
        Assert.Equal(0, ready.LowLatencyVideoStopReason);

        Assert.Equal(RemoteControlKind.LowLatencyVideoStop, stop.Kind);
        Assert.Equal(ChannelId, stop.LowLatencyVideoChannelId);
        Assert.Equal(Epoch, stop.LowLatencyVideoEpoch);
        Assert.Equal(7, stop.LowLatencyVideoStopReason);

        Assert.Equal(RemoteControlKind.LowLatencyVideoStopped, stopped.Kind);
        Assert.Equal(ChannelId, stopped.LowLatencyVideoChannelId);
        Assert.Equal(Epoch, stopped.LowLatencyVideoEpoch);
        Assert.Equal(9, stopped.LowLatencyVideoStopReason);
    }

    [Fact]
    public void LowLatencyVideoControlsRejectTrailingAndTruncatedData()
    {
        byte[][] validPayloads =
        [
            RemoteMessageCodec.EncodeLowLatencyVideoOffer(CreateOffer()),
            RemoteMessageCodec.EncodeLowLatencyVideoReady(ChannelId, Epoch),
            RemoteMessageCodec.EncodeLowLatencyVideoStop(ChannelId, Epoch, 1),
            RemoteMessageCodec.EncodeLowLatencyVideoStopped(ChannelId, Epoch, 2)
        ];

        foreach (byte[] valid in validPayloads)
        {
            byte[] withTrailingData = [.. valid, 0xA5];
            Assert.Throws<InvalidDataException>(() =>
                RemoteMessageCodec.DecodeControl(withTrailingData));

            byte[] truncated = valid[..^1];
            Assert.Throws<EndOfStreamException>(() =>
                RemoteMessageCodec.DecodeControl(truncated));
        }
    }

    [Fact]
    public void LowLatencyVideoOfferRejectsInvalidVersionAndBoundaryFields()
    {
        byte[] valid = RemoteMessageCodec.EncodeLowLatencyVideoOffer(CreateOffer());
        var invalidPayloads = new List<byte[]>();

        byte[] invalidVersion = (byte[])valid.Clone();
        invalidVersion[1] = checked((byte)(LowLatencyVideoProtocol.Version + 1));
        invalidPayloads.Add(invalidVersion);

        byte[] zeroPort = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(zeroPort.AsSpan(2), 0);
        invalidPayloads.Add(zeroPort);

        byte[] undersizedDatagram = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(
            undersizedDatagram.AsSpan(4),
            LowLatencyVideoProtocol.MinDatagramBytes - 1);
        invalidPayloads.Add(undersizedDatagram);

        byte[] undersizedFrameLimit = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(
            undersizedFrameLimit.AsSpan(6),
            RemoteMessageCodec.FrameHeaderLength - 1);
        invalidPayloads.Add(undersizedFrameLimit);

        byte[] zeroChannel = (byte[])valid.Clone();
        zeroChannel.AsSpan(10, sizeof(ulong)).Clear();
        invalidPayloads.Add(zeroChannel);

        byte[] zeroEpoch = (byte[])valid.Clone();
        zeroEpoch.AsSpan(18, sizeof(uint)).Clear();
        invalidPayloads.Add(zeroEpoch);

        foreach (byte[] invalid in invalidPayloads)
        {
            Assert.Throws<InvalidDataException>(() =>
                RemoteMessageCodec.DecodeControl(invalid));
        }
    }

    [Fact]
    public void AuthenticatedHeaderTamperingIsRejectedAndDoesNotAdvanceReplay()
    {
        byte[] key = SequenceBytes(LowLatencyVideoProtocol.KeyLength, 0x21);
        byte[] noncePrefix = SequenceBytes(
            LowLatencyVideoProtocol.NoncePrefixLength,
            0x71);
        using var sender = new LowLatencyVideoSendCipher(
            key,
            noncePrefix,
            ChannelId,
            Epoch);
        using var receiver = new LowLatencyVideoReceiveCipher(
            key,
            noncePrefix,
            ChannelId,
            Epoch);

        byte[] first = EncryptTestPacket(sender, [1, 2, 3], frameSequence: 10);
        Assert.True(receiver.TryDecrypt(first, out LowLatencyVideoDatagram firstPacket));
        Assert.Equal(new byte[] { 1, 2, 3 }, firstPacket.Plaintext.ToArray());

        byte[] second = EncryptTestPacket(sender, [4, 5, 6], frameSequence: 11);
        byte[] aadTampered = (byte[])second.Clone();
        aadTampered[51] ^= 0x01;
        Assert.False(receiver.TryDecrypt(aadTampered, out _));
        Assert.True(receiver.TryDecrypt(second, out LowLatencyVideoDatagram secondPacket));
        Assert.Equal(new byte[] { 4, 5, 6 }, secondPacket.Plaintext.ToArray());
        Assert.False(receiver.TryDecrypt(second, out _));

        byte[] third = EncryptTestPacket(sender, [7, 8, 9], frameSequence: 12);
        byte[] forgedFarFutureSequence = (byte[])third.Clone();
        ulong actualSequence = BinaryPrimitives.ReadUInt64LittleEndian(
            third.AsSpan(20, sizeof(ulong)));
        BinaryPrimitives.WriteUInt64LittleEndian(
            forgedFarFutureSequence.AsSpan(20, sizeof(ulong)),
            actualSequence + LowLatencyVideoProtocol.ReplayWindowPackets);
        Assert.False(receiver.TryDecrypt(forgedFarFutureSequence, out _));
        Assert.True(receiver.TryDecrypt(third, out LowLatencyVideoDatagram thirdPacket));
        Assert.Equal(new byte[] { 7, 8, 9 }, thirdPacket.Plaintext.ToArray());
    }

    [Fact]
    public void ReceiveCipherCanDecryptIntoReusableCallerBuffer()
    {
        byte[] key = SequenceBytes(LowLatencyVideoProtocol.KeyLength, 0x31);
        byte[] noncePrefix = SequenceBytes(
            LowLatencyVideoProtocol.NoncePrefixLength,
            0x41);
        using var sender = new LowLatencyVideoSendCipher(
            key,
            noncePrefix,
            ChannelId,
            Epoch);
        using var receiver = new LowLatencyVideoReceiveCipher(
            key,
            noncePrefix,
            ChannelId,
            Epoch);
        byte[] encrypted = EncryptTestPacket(
            sender,
            [9, 8, 7, 6],
            frameSequence: 1);
        byte[] reusable = new byte[128];

        Assert.True(receiver.TryDecrypt(
            encrypted,
            reusable,
            out LowLatencyVideoDatagram packet));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, packet.Plaintext.ToArray());

        packet.Plaintext.Span[0] = 0xAA;
        Assert.Equal(0xAA, reusable[0]);
        Assert.Equal(4, packet.Plaintext.Length);
    }

    [Fact]
    public void ReplayWindowAcceptsOutOfOrderPacketsAndRejectsDuplicates()
    {
        var replay = new LowLatencyVideoReplayWindow();

        Assert.True(replay.TryCommit(100));
        Assert.True(replay.TryCommit(98));
        Assert.True(replay.TryCommit(99));
        Assert.False(replay.WouldAccept(98));
        Assert.False(replay.TryCommit(98));
        Assert.True(replay.TryCommit(101));
    }

    [Fact]
    public void ReplayWindowEnforcesItsExactLowerBoundary()
    {
        var replay = new LowLatencyVideoReplayWindow();
        ulong highest = checked((ulong)LowLatencyVideoProtocol.ReplayWindowPackets + 10);

        Assert.True(replay.TryCommit(highest));
        Assert.True(replay.WouldAccept(11));
        Assert.True(replay.TryCommit(11));
        Assert.False(replay.WouldAccept(10));
        Assert.False(replay.TryCommit(10));
    }

    [Fact]
    public void ReplayWindowReusesCircularSlotsWithoutAcceptingDuplicates()
    {
        var replay = new LowLatencyVideoReplayWindow();
        ulong packetCount = checked(
            (ulong)LowLatencyVideoProtocol.ReplayWindowPackets * 3);
        for (ulong sequence = 0;
            sequence < packetCount;
            sequence++)
        {
            Assert.True(replay.TryCommit(sequence));
        }

        ulong highest = packetCount - 1;
        Assert.False(replay.WouldAccept(highest));
        Assert.False(replay.TryCommit(highest - 1));
        Assert.False(replay.TryCommit(
            highest -
            (ulong)LowLatencyVideoProtocol.ReplayWindowPackets));
    }

    [Fact]
    public void ReplayWindowSteadyStateDoesNotAllocatePerDatagram()
    {
        var replay = new LowLatencyVideoReplayWindow();
        ulong warmupPackets = checked(
            (ulong)LowLatencyVideoProtocol.ReplayWindowPackets * 2);
        for (ulong sequence = 0;
            sequence < warmupPackets;
            sequence++)
        {
            Assert.True(replay.TryCommit(sequence));
        }

        const ulong measuredPackets = 10_000;
        _ = MeasureReplayWindowAllocation(
            replay,
            warmupPackets,
            measuredPackets,
            out _);

        long allocated = MeasureReplayWindowAllocation(
            replay,
            warmupPackets + measuredPackets,
            measuredPackets,
            out bool allAccepted);
        Assert.True(allAccepted);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FrameFragmentsReassembleInAnyOrder()
    {
        byte[] expected = SequenceBytes(1100, 0x31);
        LowLatencyVideoDatagram[] fragments = CreateFragments(7, expected);
        var reassembler = CreateReassembler();

        Assert.False(reassembler.TryAdd(fragments[2], out _, out _));
        Assert.False(reassembler.TryAdd(fragments[0], out _, out _));
        Assert.True(reassembler.TryAdd(
            fragments[1],
            out MessageType frameKind,
            out byte[]? completed));

        Assert.Equal(MessageType.Frame, frameKind);
        Assert.Equal(expected, Assert.IsType<byte[]>(completed));
    }

    [Fact]
    public void NewerFrameEvictsIncompleteOlderFrame()
    {
        byte[] oldPayload = SequenceBytes(800, 0x11);
        byte[] newPayload = SequenceBytes(900, 0x81);
        LowLatencyVideoDatagram[] oldFragments = CreateFragments(40, oldPayload);
        LowLatencyVideoDatagram[] newFragments = CreateFragments(41, newPayload);
        var reassembler = CreateReassembler();

        Assert.False(reassembler.TryAdd(oldFragments[0], out _, out _));
        Assert.False(reassembler.TryAdd(newFragments[1], out _, out _));
        Assert.False(reassembler.TryAdd(oldFragments[1], out _, out _));
        Assert.True(reassembler.TryAdd(
            newFragments[0],
            out MessageType frameKind,
            out byte[]? completed));
        Assert.Equal(MessageType.Frame, frameKind);
        Assert.Equal(newPayload, Assert.IsType<byte[]>(completed));

        Assert.False(reassembler.TryAdd(oldFragments[0], out _, out _));
        Assert.False(reassembler.TryAdd(oldFragments[1], out _, out _));
    }

    [Fact]
    public void ReassemblerAcceptsExactFrameLimitAndRejectsMetadataOutsideBoundaries()
    {
        byte[] maximumPayload = SequenceBytes(TestMaxFrameBytes, 0x43);
        LowLatencyVideoDatagram[] maximumFragments = CreateFragments(50, maximumPayload);
        var validReassembler = CreateReassembler();

        for (int index = maximumFragments.Length - 1; index >= 0; index--)
        {
            bool completed = validReassembler.TryAdd(
                maximumFragments[index],
                out MessageType frameKind,
                out byte[]? payload);
            if (index == 0)
            {
                Assert.True(completed);
                Assert.Equal(MessageType.Frame, frameKind);
                Assert.Equal(maximumPayload, Assert.IsType<byte[]>(payload));
            }
            else
            {
                Assert.False(completed);
            }
        }

        LowLatencyVideoDatagram baseline = CreateFragments(
            51,
            SequenceBytes(25, 0x15))[0];
        LowLatencyVideoDatagram[] invalidPackets =
        [
            baseline with { Kind = LowLatencyVideoDatagramKind.Feedback },
            baseline with { Flags = 1 },
            baseline with { FrameKind = MessageType.Control },
            baseline with { FrameLength = 0 },
            baseline with
            {
                FrameLength = TestMaxFrameBytes + 1,
                Plaintext = new byte[TestMaxFrameBytes + 1]
            },
            baseline with { FragmentCount = 0 },
            baseline with
            {
                FragmentCount = LowLatencyVideoProtocol.MaxFragmentCount + 1
            },
            baseline with { FragmentIndex = baseline.FragmentCount },
            baseline with { FragmentOffset = -1 },
            baseline with { FragmentOffset = 1 },
            baseline with { FragmentCount = 2 },
            baseline with { Plaintext = baseline.Plaintext[..^1] }
        ];

        foreach (LowLatencyVideoDatagram invalid in invalidPackets)
        {
            var reassembler = CreateReassembler();
            Assert.False(reassembler.TryAdd(invalid, out _, out _));
        }
    }

    [Fact]
    public void CompletedFrameHasExclusiveOwnershipOfItsBuffer()
    {
        byte[] firstPayload = LowLatencyVideoProtocol.CreateJpegFramePayload(
            64,
            48,
            1.25,
            2.5,
            SequenceBytes(700, 0x27));
        byte[] expectedFirst = (byte[])firstPayload.Clone();
        LowLatencyVideoDatagram[] firstFragments = CreateFragments(60, firstPayload);
        var reassembler = CreateReassembler();

        byte[]? completedFirst = null;
        foreach (LowLatencyVideoDatagram fragment in firstFragments.Reverse())
        {
            if (reassembler.TryAdd(fragment, out _, out byte[]? completed))
            {
                completedFirst = completed;
            }
        }

        byte[] ownedFirst = Assert.IsType<byte[]>(completedFirst);
        RemoteFrame decodedFirst = RemoteMessageCodec.DecodeFrame(ownedFirst);
        Assert.Same(ownedFirst, decodedFirst.EncodedBuffer);
        firstFragments[0].Plaintext.Span.Fill(0xEE);

        byte[] secondPayload = LowLatencyVideoProtocol.CreateJpegFramePayload(
            80,
            60,
            0.5,
            1.0,
            SequenceBytes(620, 0x91));
        byte[]? completedSecond = null;
        foreach (LowLatencyVideoDatagram fragment in CreateFragments(61, secondPayload))
        {
            if (reassembler.TryAdd(fragment, out _, out byte[]? completed))
            {
                completedSecond = completed;
            }
        }

        reassembler.Reset();
        Assert.NotSame(ownedFirst, Assert.IsType<byte[]>(completedSecond));
        Assert.Equal(expectedFirst, ownedFirst);
        Assert.Equal(700, decodedFirst.EncodedLength);
    }

    [Fact]
    public void SynchronousPublishBorrowsExactPayloadAndReturnsPoolLease()
    {
        byte[] expected = SequenceBytes(1100, 0x39);
        LowLatencyVideoDatagram[] fragments =
            CreateFragments(62, expected);
        var pool = new TrackingByteArrayPool();
        var reassembler =
            new LowLatencyVideoFrameReassembler(
                TestMaxFrameBytes,
                TestMaxDatagramBytes,
                bufferPool: pool);
        for (int index = 0;
            index < fragments.Length - 1;
            index++)
        {
            Assert.False(
                reassembler.TryAddAndPublish(
                    fragments[index],
                    (_, _, _) =>
                        throw new InvalidOperationException(
                            "An incomplete frame was published.")));
        }

        Assert.True(
            reassembler.TryAddAndPublish(
                fragments[^1],
                (sequence, kind, payload) =>
                {
                    Assert.Equal(62UL, sequence);
                    Assert.Equal(MessageType.Frame, kind);
                    Assert.Equal(expected.Length, payload.Length);
                    Assert.Equal(expected, payload.ToArray());
                    Assert.Equal(1, pool.ActiveCount);
                    Assert.Equal(0, pool.ReturnCount);
                }));

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void SynchronousPublishReturnsPoolLeaseWhenSubscriberThrows()
    {
        byte[] expected = SequenceBytes(800, 0x49);
        LowLatencyVideoDatagram[] fragments =
            CreateFragments(63, expected);
        var pool = new TrackingByteArrayPool();
        var reassembler =
            new LowLatencyVideoFrameReassembler(
                TestMaxFrameBytes,
                TestMaxDatagramBytes,
                bufferPool: pool);
        Assert.False(
            reassembler.TryAddAndPublish(
                fragments[0],
                (_, _, _) => { }));

        Assert.Throws<InvalidOperationException>(() =>
            reassembler.TryAddAndPublish(
                fragments[1],
                (_, _, payload) =>
                {
                    Assert.Equal(expected.Length, payload.Length);
                    throw new InvalidOperationException(
                        "subscriber failure");
                }));

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void NewerFrameAndResetReturnIncompletePoolLeasesExactlyOnce()
    {
        LowLatencyVideoDatagram[] oldFragments =
            CreateFragments(
                64,
                SequenceBytes(800, 0x59));
        LowLatencyVideoDatagram[] newerFragments =
            CreateFragments(
                65,
                SequenceBytes(900, 0x69));
        var pool = new TrackingByteArrayPool();
        var reassembler =
            new LowLatencyVideoFrameReassembler(
                TestMaxFrameBytes,
                TestMaxDatagramBytes,
                bufferPool: pool);

        Assert.False(
            reassembler.TryAddAndPublish(
                oldFragments[0],
                (_, _, _) => { }));
        Assert.Equal(1, pool.ActiveCount);
        Assert.False(
            reassembler.TryAddAndPublish(
                newerFragments[0],
                (_, _, _) => { }));
        Assert.Equal(2, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(1, pool.ActiveCount);

        reassembler.Reset();
        reassembler.Reset();

        Assert.Equal(2, pool.RentCount);
        Assert.Equal(2, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Theory]
    [InlineData(7, false, 1)]
    [InlineData(8, true, 1)]
    [InlineData(16, true, 1)]
    [InlineData(17, true, 2)]
    public void XorFecSizingCoversRecommendationAndGroupBoundaries(
        int fragmentCount,
        bool shouldSend,
        int expectedGroupCount)
    {
        int frameLength = checked(
            ((fragmentCount - 1) * TestMaxFragmentPayloadBytes) + 37);

        Assert.Equal(
            fragmentCount,
            LowLatencyVideoProtocol.GetFrameFragmentCount(
                frameLength,
                TestMaxDatagramBytes));
        Assert.Equal(
            shouldSend,
            LowLatencyVideoProtocol.ShouldSendXorFec(fragmentCount));
        Assert.Equal(
            expectedGroupCount,
            LowLatencyVideoProtocol.GetXorFecGroupCount(fragmentCount));

        for (int groupIndex = 0; groupIndex < expectedGroupCount; groupIndex++)
        {
            int groupStart = checked(
                groupIndex *
                LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup *
                TestMaxFragmentPayloadBytes);
            Assert.Equal(
                Math.Min(
                    TestMaxFragmentPayloadBytes,
                    frameLength - groupStart),
                LowLatencyVideoProtocol.GetXorFecParityPayloadLength(
                    frameLength,
                    groupIndex,
                    TestMaxDatagramBytes));
        }
    }

    [Theory]
    [InlineData(7, 3)]
    [InlineData(8, 7)]
    [InlineData(16, 5)]
    [InlineData(17, 16)]
    public void XorFecRecoversSingleMissingFragmentAtBoundaries(
        int fragmentCount,
        int missingFragment)
    {
        byte[] expected = CreatePayloadWithFragmentCount(fragmentCount, 37);
        LowLatencyVideoDatagram[] fragments = CreateFragments(70, expected);
        LowLatencyVideoDatagram[] parity = CreateXorParityPackets(70, expected);
        var reassembler = CreateFecReassembler();

        foreach (LowLatencyVideoDatagram fragment in fragments)
        {
            if (fragment.FragmentIndex != missingFragment)
            {
                Assert.False(reassembler.TryAdd(fragment, out _, out _));
            }
        }

        byte[]? completed = null;
        foreach (LowLatencyVideoDatagram parityPacket in parity)
        {
            if (reassembler.TryAdd(
                parityPacket,
                out MessageType frameKind,
                out byte[]? payload))
            {
                Assert.Equal(MessageType.Frame, frameKind);
                completed = payload;
            }
        }

        Assert.Equal(expected, Assert.IsType<byte[]>(completed));
        Assert.Equal(1, reassembler.CompletedFrameCount);
        Assert.Equal(0, reassembler.AbandonedIncompleteFrameCount);
        Assert.Equal(1, reassembler.RecoveredFragmentCount);
    }

    [Fact]
    public void XorFecRecoversOneMissingFragmentInEachGroup()
    {
        byte[] expected = CreatePayloadWithFragmentCount(17, 37);
        LowLatencyVideoDatagram[] fragments = CreateFragments(71, expected);
        LowLatencyVideoDatagram[] parity = CreateXorParityPackets(71, expected);
        var reassembler = CreateFecReassembler();

        foreach (LowLatencyVideoDatagram fragment in fragments.Reverse())
        {
            if (fragment.FragmentIndex is not (4 or 16))
            {
                Assert.False(reassembler.TryAdd(fragment, out _, out _));
            }
        }

        Assert.False(reassembler.TryAdd(parity[0], out _, out _));
        Assert.True(reassembler.TryAdd(
            parity[1],
            out MessageType frameKind,
            out byte[]? completed));

        Assert.Equal(MessageType.Frame, frameKind);
        Assert.Equal(expected, Assert.IsType<byte[]>(completed));
        Assert.Equal(1, reassembler.CompletedFrameCount);
        Assert.Equal(2, reassembler.RecoveredFragmentCount);
    }

    [Fact]
    public void XorFecDoesNotRecoverTwoMissingFragmentsInSameGroup()
    {
        byte[] expected = CreatePayloadWithFragmentCount(16, 37);
        LowLatencyVideoDatagram[] fragments = CreateFragments(72, expected);
        LowLatencyVideoDatagram parity = CreateXorParityPackets(72, expected)[0];
        var reassembler = CreateFecReassembler();

        foreach (LowLatencyVideoDatagram fragment in fragments)
        {
            if (fragment.FragmentIndex is not (2 or 9))
            {
                Assert.False(reassembler.TryAdd(fragment, out _, out _));
            }
        }

        Assert.False(reassembler.TryAdd(parity, out _, out _));
        Assert.Equal(0, reassembler.CompletedFrameCount);
        Assert.Equal(0, reassembler.RecoveredFragmentCount);

        reassembler.Reset();
        Assert.Equal(1, reassembler.AbandonedIncompleteFrameCount);
    }

    [Fact]
    public void XorFecParityCanArriveBeforeOutOfOrderData()
    {
        byte[] expected = CreatePayloadWithFragmentCount(8, 29);
        LowLatencyVideoDatagram[] fragments = CreateFragments(73, expected);
        LowLatencyVideoDatagram parity = CreateXorParityPackets(73, expected)[0];
        var reassembler = CreateFecReassembler();

        Assert.False(reassembler.TryAdd(parity, out _, out _));
        byte[]? completed = null;
        foreach (LowLatencyVideoDatagram fragment in fragments
            .Where(fragment => fragment.FragmentIndex != 6)
            .Reverse())
        {
            if (reassembler.TryAdd(fragment, out _, out byte[]? payload))
            {
                completed = payload;
            }
        }

        Assert.Equal(expected, Assert.IsType<byte[]>(completed));
        Assert.Equal(1, reassembler.CompletedFrameCount);
        Assert.Equal(1, reassembler.RecoveredFragmentCount);
    }

    [Fact]
    public void XorFecCompleteDataDoesNotWaitForParity()
    {
        byte[] expected = CreatePayloadWithFragmentCount(8, 41);
        var reassembler = CreateFecReassembler();
        byte[]? completed = null;

        foreach (LowLatencyVideoDatagram fragment in CreateFragments(74, expected))
        {
            if (reassembler.TryAdd(fragment, out _, out byte[]? payload))
            {
                completed = payload;
            }
        }

        Assert.Equal(expected, Assert.IsType<byte[]>(completed));
        Assert.Equal(1, reassembler.CompletedFrameCount);
        Assert.Equal(0, reassembler.RecoveredFragmentCount);
    }

    [Fact]
    public void XorFecIsRejectedUnlessExplicitlyEnabled()
    {
        byte[] expected = CreatePayloadWithFragmentCount(8, 31);
        LowLatencyVideoDatagram[] fragments = CreateFragments(75, expected);
        LowLatencyVideoDatagram parity = CreateXorParityPackets(75, expected)[0];
        var reassembler = new LowLatencyVideoFrameReassembler(
            FecTestMaxFrameBytes,
            TestMaxDatagramBytes);

        Assert.False(reassembler.TryAdd(parity, out _, out _));
        foreach (LowLatencyVideoDatagram fragment in fragments.SkipLast(1))
        {
            Assert.False(reassembler.TryAdd(fragment, out _, out _));
        }

        Assert.Equal(0, reassembler.CompletedFrameCount);
        Assert.Equal(0, reassembler.RecoveredFragmentCount);
    }

    [Fact]
    public void DuplicateXorFecParityIsIgnoredWithoutChangingRecovery()
    {
        byte[] expected = CreatePayloadWithFragmentCount(8, 43);
        LowLatencyVideoDatagram[] fragments = CreateFragments(76, expected);
        LowLatencyVideoDatagram parity = CreateXorParityPackets(76, expected)[0];
        var reassembler = CreateFecReassembler();

        foreach (LowLatencyVideoDatagram fragment in fragments)
        {
            if (fragment.FragmentIndex is not (2 or 7))
            {
                Assert.False(reassembler.TryAdd(fragment, out _, out _));
            }
        }

        Assert.False(reassembler.TryAdd(parity, out _, out _));
        Assert.False(reassembler.TryAdd(parity, out _, out _));
        Assert.True(reassembler.TryAdd(
            fragments[7],
            out _,
            out byte[]? completed));

        Assert.Equal(expected, Assert.IsType<byte[]>(completed));
        Assert.Equal(1, reassembler.CompletedFrameCount);
        Assert.Equal(1, reassembler.RecoveredFragmentCount);
    }

    [Fact]
    public void XorFecRejectsMalformedMetadataWithoutEvictingValidFrame()
    {
        byte[] expected = CreatePayloadWithFragmentCount(8, 47);
        LowLatencyVideoDatagram baseline = CreateXorParityPackets(77, expected)[0];
        LowLatencyVideoDatagram[] invalidPackets =
        [
            baseline with { Kind = LowLatencyVideoDatagramKind.FeedbackV2 },
            baseline with { Flags = 1 },
            baseline with { FrameKind = MessageType.Control },
            baseline with { FrameSequence = (ulong)long.MaxValue + 1 },
            baseline with { FrameLength = 0 },
            baseline with { FrameLength = FecTestMaxFrameBytes + 1 },
            baseline with { FragmentCount = 0 },
            baseline with
            {
                FragmentCount = checked(
                    (ushort)(baseline.FragmentCount - 1))
            },
            baseline with
            {
                FragmentCount =
                    LowLatencyVideoProtocol.MaxFragmentCount + 1
            },
            baseline with { FragmentIndex = 1 },
            baseline with { FragmentOffset = 1 },
            baseline with { Plaintext = baseline.Plaintext[..^1] },
            baseline with
            {
                Plaintext =
                    (byte[])[.. baseline.Plaintext.ToArray(), 0x5A]
            }
        ];

        foreach (LowLatencyVideoDatagram invalid in invalidPackets)
        {
            var reassembler = CreateFecReassembler();
            Assert.False(reassembler.TryAdd(invalid, out _, out _));
            Assert.Equal(0, reassembler.CompletedFrameCount);
            Assert.Equal(0, reassembler.AbandonedIncompleteFrameCount);
            Assert.Equal(0, reassembler.RecoveredFragmentCount);
        }
    }

    [Fact]
    public void ResetAndNewerFrameEvictionClearParityAndUpdateStatistics()
    {
        byte[] oldPayload = CreatePayloadWithFragmentCount(8, 53);
        LowLatencyVideoDatagram oldParity =
            CreateXorParityPackets(80, oldPayload)[0];
        var reassembler = CreateFecReassembler();

        Assert.False(reassembler.TryAdd(oldParity, out _, out _));
        byte[] retainedBeforeReset = GetRetainedParity(reassembler, 0);
        Assert.Contains(retainedBeforeReset, value => value != 0);

        reassembler.Reset();
        Assert.All(retainedBeforeReset, value => Assert.Equal(0, value));
        Assert.Equal(1, reassembler.AbandonedIncompleteFrameCount);

        Assert.False(reassembler.TryAdd(oldParity, out _, out _));
        byte[] retainedBeforeEviction = GetRetainedParity(reassembler, 0);
        byte[] newerPayload = SequenceBytes(25, 0xA4);
        LowLatencyVideoDatagram newer = CreateFragments(81, newerPayload)[0];

        Assert.True(reassembler.TryAdd(
            newer,
            out MessageType frameKind,
            out byte[]? completed));
        Assert.Equal(MessageType.Frame, frameKind);
        Assert.Equal(newerPayload, Assert.IsType<byte[]>(completed));
        Assert.All(retainedBeforeEviction, value => Assert.Equal(0, value));
        Assert.Equal(1, reassembler.CompletedFrameCount);
        Assert.Equal(2, reassembler.AbandonedIncompleteFrameCount);
        Assert.Equal(0, reassembler.RecoveredFragmentCount);

        foreach (LowLatencyVideoDatagram oldFragment in CreateFragments(
            80,
            oldPayload))
        {
            Assert.False(reassembler.TryAdd(oldFragment, out _, out _));
        }
    }

    [Fact]
    public void AdjacentFrameWindowRecoversOlderFecAfterNewerFrameStarts()
    {
        byte[] olderPayload = CreatePayloadWithFragmentCount(8, 61);
        byte[] newerPayload = CreatePayloadWithFragmentCount(8, 67);
        LowLatencyVideoDatagram[] older =
            CreateFragments(90, olderPayload);
        LowLatencyVideoDatagram[] newer =
            CreateFragments(91, newerPayload);
        LowLatencyVideoDatagram parity =
            CreateXorParityPackets(90, olderPayload)[0];
        var published =
            new List<(ulong Sequence, byte[] Payload)>();
        var reassembler =
            new LowLatencyVideoAdjacentFrameReassembler(
                FecTestMaxFrameBytes,
                TestMaxDatagramBytes,
                (sequence, _, payload) =>
                    published.Add((sequence, payload.ToArray())),
                enableXorFec: true);

        foreach (LowLatencyVideoDatagram fragment in older)
        {
            if (fragment.FragmentIndex != 6)
            {
                Assert.False(reassembler.TryAdd(fragment));
            }
        }

        foreach (LowLatencyVideoDatagram fragment in newer.Take(3))
        {
            Assert.False(reassembler.TryAdd(fragment));
        }

        Assert.True(reassembler.TryAdd(parity));
        foreach (LowLatencyVideoDatagram fragment in newer.Skip(3))
        {
            reassembler.TryAdd(fragment);
        }

        Assert.Collection(
            published,
            completed =>
            {
                Assert.Equal(90UL, completed.Sequence);
                Assert.Equal(olderPayload, completed.Payload);
            },
            completed =>
            {
                Assert.Equal(91UL, completed.Sequence);
                Assert.Equal(newerPayload, completed.Payload);
            });
        Assert.Equal(2, reassembler.CompletedFrameCount);
        Assert.Equal(0, reassembler.AbandonedIncompleteFrameCount);
        Assert.Equal(1, reassembler.RecoveredFragmentCount);
        Assert.Equal(0, reassembler.WindowEvictionCount);
    }

    [Fact]
    public void AdjacentFrameWindowPublishesCompletedNewerAndDropsLateOlderTail()
    {
        byte[] olderPayload = CreatePayloadWithFragmentCount(8, 71);
        byte[] newerPayload = CreatePayloadWithFragmentCount(8, 73);
        LowLatencyVideoDatagram[] older =
            CreateFragments(92, olderPayload);
        LowLatencyVideoDatagram[] newer =
            CreateFragments(93, newerPayload);
        var published =
            new List<(ulong Sequence, byte[] Payload)>();
        var reassembler =
            new LowLatencyVideoAdjacentFrameReassembler(
                FecTestMaxFrameBytes,
                TestMaxDatagramBytes,
                (sequence, _, payload) =>
                    published.Add((sequence, payload.ToArray())),
                enableXorFec: true);

        Assert.False(reassembler.TryAdd(older[0]));
        foreach (LowLatencyVideoDatagram fragment in newer)
        {
            reassembler.TryAdd(fragment);
        }

        Assert.Single(published);
        Assert.Equal(93UL, published[0].Sequence);
        Assert.Equal(newerPayload, published[0].Payload);
        Assert.Equal(1, reassembler.CompletedFrameCount);
        Assert.Equal(1, reassembler.AbandonedIncompleteFrameCount);

        foreach (LowLatencyVideoDatagram fragment in older.Skip(1))
        {
            Assert.False(reassembler.TryAdd(fragment));
        }

        Assert.Single(published);
        Assert.Equal(1, reassembler.CompletedFrameCount);
        Assert.Equal(1, reassembler.AbandonedIncompleteFrameCount);
    }

    [Fact]
    public void AdjacentFrameWindowKeepsNewestTwoSequencesWhenMiddleArrivesLate()
    {
        byte[] oldestPayload = CreatePayloadWithFragmentCount(2, 79);
        byte[] middlePayload = CreatePayloadWithFragmentCount(2, 83);
        byte[] newestPayload = CreatePayloadWithFragmentCount(2, 89);
        LowLatencyVideoDatagram[] oldest =
            CreateFragments(100, oldestPayload);
        LowLatencyVideoDatagram[] middle =
            CreateFragments(101, middlePayload);
        LowLatencyVideoDatagram[] newest =
            CreateFragments(102, newestPayload);
        var published = new List<ulong>();
        var reassembler =
            new LowLatencyVideoAdjacentFrameReassembler(
                TestMaxFrameBytes,
                TestMaxDatagramBytes,
                (sequence, _, _) => published.Add(sequence));

        Assert.False(reassembler.TryAdd(oldest[0]));
        Assert.False(reassembler.TryAdd(newest[0]));
        Assert.False(reassembler.TryAdd(middle[0]));
        Assert.True(reassembler.TryAdd(middle[1]));
        Assert.True(reassembler.TryAdd(newest[1]));

        Assert.Equal([101UL, 102UL], published);
        Assert.Equal(2, reassembler.CompletedFrameCount);
        Assert.Equal(1, reassembler.AbandonedIncompleteFrameCount);
        Assert.Equal(1, reassembler.WindowEvictionCount);
        Assert.False(reassembler.TryAdd(oldest[1]));
    }

    [Fact]
    public void InvalidHighSequenceDoesNotEvictAdjacentFrameWindow()
    {
        byte[] olderPayload = CreatePayloadWithFragmentCount(2, 91);
        byte[] newerPayload = CreatePayloadWithFragmentCount(2, 97);
        LowLatencyVideoDatagram[] older =
            CreateFragments(110, olderPayload);
        LowLatencyVideoDatagram[] newer =
            CreateFragments(111, newerPayload);
        LowLatencyVideoDatagram invalidBaseline =
            CreateFragments(
                999,
                CreatePayloadWithFragmentCount(2, 101))[0];
        LowLatencyVideoDatagram invalid =
            invalidBaseline with
            {
                Plaintext = invalidBaseline.Plaintext[..^1]
            };
        var published = new List<ulong>();
        var reassembler =
            new LowLatencyVideoAdjacentFrameReassembler(
                TestMaxFrameBytes,
                TestMaxDatagramBytes,
                (sequence, _, _) => published.Add(sequence));

        Assert.False(reassembler.TryAdd(older[0]));
        Assert.False(reassembler.TryAdd(newer[0]));
        Assert.False(reassembler.TryAdd(invalid));
        Assert.Equal(0, reassembler.AbandonedIncompleteFrameCount);
        Assert.Equal(0, reassembler.WindowEvictionCount);

        Assert.True(reassembler.TryAdd(older[1]));
        Assert.True(reassembler.TryAdd(newer[1]));
        Assert.Equal([110UL, 111UL], published);
        Assert.Equal(0, reassembler.AbandonedIncompleteFrameCount);
    }

    [Fact]
    public void AdjacentFrameWindowReturnsPoolLeaseWhenSubscriberThrows()
    {
        byte[] payload = SequenceBytes(1100, 0xB7);
        LowLatencyVideoDatagram[] fragments =
            CreateFragments(120, payload);
        var pool = new TrackingByteArrayPool();
        var reassembler =
            new LowLatencyVideoAdjacentFrameReassembler(
                TestMaxFrameBytes,
                TestMaxDatagramBytes,
                (_, _, _) =>
                    throw new InvalidOperationException(
                        "subscriber failure"),
                bufferPool: pool);

        foreach (LowLatencyVideoDatagram fragment in fragments.SkipLast(1))
        {
            Assert.False(reassembler.TryAdd(fragment));
        }

        Assert.Throws<InvalidOperationException>(() =>
            reassembler.TryAdd(fragments[^1]));

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
        reassembler.Reset();
        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public void AdjacentFrameWindowResetReturnsBothFecSlotsExactlyOnce()
    {
        byte[] firstPayload = CreatePayloadWithFragmentCount(8, 107);
        byte[] secondPayload = CreatePayloadWithFragmentCount(8, 109);
        LowLatencyVideoDatagram[] first =
            CreateFragments(121, firstPayload);
        LowLatencyVideoDatagram[] second =
            CreateFragments(122, secondPayload);
        var pool = new TrackingByteArrayPool();
        var reassembler =
            new LowLatencyVideoAdjacentFrameReassembler(
                FecTestMaxFrameBytes,
                TestMaxDatagramBytes,
                (_, _, _) => { },
                enableXorFec: true,
                bufferPool: pool);

        Assert.False(reassembler.TryAdd(
            CreateXorParityPackets(121, firstPayload)[0]));
        Assert.False(reassembler.TryAdd(first[0]));
        Assert.False(reassembler.TryAdd(
            CreateXorParityPackets(122, secondPayload)[0]));
        Assert.False(reassembler.TryAdd(second[0]));
        Assert.Equal(4, pool.RentCount);
        Assert.Equal(4, pool.ActiveCount);
        byte[] firstParity = GetRetainedParity(
            GetAdjacentInner(reassembler, "_first"),
            0);
        byte[] secondParity = GetRetainedParity(
            GetAdjacentInner(reassembler, "_second"),
            0);

        reassembler.Reset();
        reassembler.Reset();

        Assert.Equal(2, reassembler.AbandonedIncompleteFrameCount);
        Assert.Equal(4, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
        Assert.All(firstParity, value => Assert.Equal(0, value));
        Assert.All(secondParity, value => Assert.Equal(0, value));
    }

    [Fact]
    public void AdjacentFrameWindowRecoversAfterNewerCallbackThrows()
    {
        byte[] olderPayload = CreatePayloadWithFragmentCount(2, 113);
        byte[] newerPayload = CreatePayloadWithFragmentCount(2, 127);
        byte[] recoveryPayload = CreatePayloadWithFragmentCount(2, 131);
        LowLatencyVideoDatagram[] older =
            CreateFragments(123, olderPayload);
        LowLatencyVideoDatagram[] newer =
            CreateFragments(124, newerPayload);
        LowLatencyVideoDatagram[] recovery =
            CreateFragments(125, recoveryPayload);
        var pool = new TrackingByteArrayPool();
        var published = new List<ulong>();
        int callbackCount = 0;
        var reassembler =
            new LowLatencyVideoAdjacentFrameReassembler(
                TestMaxFrameBytes,
                TestMaxDatagramBytes,
                (sequence, _, _) =>
                {
                    if (callbackCount++ == 0)
                    {
                        throw new InvalidOperationException(
                            "first subscriber failure");
                    }

                    published.Add(sequence);
                },
                bufferPool: pool);

        Assert.False(reassembler.TryAdd(older[0]));
        Assert.False(reassembler.TryAdd(newer[0]));
        Assert.Throws<InvalidOperationException>(() =>
            reassembler.TryAdd(newer[1]));
        Assert.Equal(1, reassembler.CompletedFrameCount);
        Assert.Equal(1, reassembler.AbandonedIncompleteFrameCount);
        Assert.Equal(2, pool.RentCount);
        Assert.Equal(2, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
        Assert.False(reassembler.TryAdd(older[1]));

        Assert.False(reassembler.TryAdd(recovery[0]));
        Assert.True(reassembler.TryAdd(recovery[1]));
        Assert.Equal([125UL], published);
        Assert.Equal(2, reassembler.CompletedFrameCount);
        Assert.Equal(1, reassembler.AbandonedIncompleteFrameCount);
        Assert.Equal(3, pool.RentCount);
        Assert.Equal(3, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void AdjacentFrameWindowDuplicatePacketRoutingDoesNotAllocate()
    {
        byte[] firstPayload = CreatePayloadWithFragmentCount(2, 137);
        byte[] secondPayload = CreatePayloadWithFragmentCount(2, 139);
        LowLatencyVideoDatagram first =
            CreateFragments(126, firstPayload)[0];
        LowLatencyVideoDatagram second =
            CreateFragments(127, secondPayload)[0];
        var reassembler =
            new LowLatencyVideoAdjacentFrameReassembler(
                TestMaxFrameBytes,
                TestMaxDatagramBytes,
                (_, _, _) => { });

        Assert.False(reassembler.TryAdd(first));
        Assert.False(reassembler.TryAdd(second));
        for (int index = 0; index < 100; index++)
        {
            reassembler.TryAdd(first);
            reassembler.TryAdd(second);
        }

        const int measuredPackets = 10_000;
        _ = MeasureAdjacentFrameWindowAllocation(
            reassembler,
            first,
            second,
            measuredPackets,
            out _);

        long allocated = MeasureAdjacentFrameWindowAllocation(
            reassembler,
            first,
            second,
            measuredPackets,
            out bool completed);
        Assert.False(completed);
        Assert.Equal(0, allocated);
        reassembler.Reset();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureReplayWindowAllocation(
        LowLatencyVideoReplayWindow replay,
        ulong firstSequence,
        ulong packetCount,
        out bool allAccepted)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        allAccepted = true;
        for (ulong offset = 0; offset < packetCount; offset++)
        {
            allAccepted &= replay.TryCommit(firstSequence + offset);
        }

        return GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureAdjacentFrameWindowAllocation(
        LowLatencyVideoAdjacentFrameReassembler reassembler,
        LowLatencyVideoDatagram first,
        LowLatencyVideoDatagram second,
        int packetCount,
        out bool completed)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        completed = false;
        for (int index = 0; index < packetCount; index++)
        {
            completed |= reassembler.TryAdd(first);
            completed |= reassembler.TryAdd(second);
        }

        return GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    }

    private static LowLatencyVideoOffer CreateOffer()
    {
        return new LowLatencyVideoOffer(
            Port: 47999,
            MaxDatagramBytes: LowLatencyVideoProtocol.DefaultMaxDatagramBytes,
            MaxFrameBytes: LowLatencyVideoProtocol.DefaultMaxFrameBytes,
            ChannelId,
            Epoch,
            HostToViewerKey: SequenceBytes(LowLatencyVideoProtocol.KeyLength, 0x10),
            ViewerToHostKey: SequenceBytes(LowLatencyVideoProtocol.KeyLength, 0x50),
            HostNoncePrefix: SequenceBytes(LowLatencyVideoProtocol.NoncePrefixLength, 0x90),
            ViewerNoncePrefix: SequenceBytes(LowLatencyVideoProtocol.NoncePrefixLength, 0xA0),
            Challenge: SequenceBytes(LowLatencyVideoProtocol.ChallengeLength, 0xB0));
    }

    private static byte[] EncryptTestPacket(
        LowLatencyVideoSendCipher sender,
        byte[] plaintext,
        ulong frameSequence)
    {
        return sender.Encrypt(
            LowLatencyVideoDatagramKind.FrameFragment,
            frameSequence,
            plaintext.Length,
            0,
            0,
            1,
            MessageType.Frame,
            0,
            plaintext);
    }

    private static LowLatencyVideoFrameReassembler CreateReassembler()
    {
        return new LowLatencyVideoFrameReassembler(
            TestMaxFrameBytes,
            TestMaxDatagramBytes);
    }

    private static LowLatencyVideoFrameReassembler CreateFecReassembler()
    {
        return new LowLatencyVideoFrameReassembler(
            FecTestMaxFrameBytes,
            TestMaxDatagramBytes,
            enableXorFec: true);
    }

    private static LowLatencyVideoDatagram[] CreateFragments(
        ulong frameSequence,
        byte[] payload,
        MessageType frameKind = MessageType.Frame)
    {
        int maxPayload = LowLatencyVideoProtocol.GetMaxFragmentPayloadBytes(
            TestMaxDatagramBytes);
        int count = checked((payload.Length + maxPayload - 1) / maxPayload);
        var fragments = new LowLatencyVideoDatagram[count];
        for (int index = 0; index < count; index++)
        {
            int offset = checked(index * maxPayload);
            int length = Math.Min(maxPayload, payload.Length - offset);
            fragments[index] = new LowLatencyVideoDatagram(
                LowLatencyVideoDatagramKind.FrameFragment,
                checked((ulong)index),
                frameSequence,
                payload.Length,
                offset,
                checked((ushort)index),
                checked((ushort)count),
                frameKind,
                0,
                payload.AsSpan(offset, length).ToArray());
        }

        return fragments;
    }

    private static LowLatencyVideoDatagram[] CreateXorParityPackets(
        ulong frameSequence,
        byte[] payload,
        MessageType frameKind = MessageType.Frame)
    {
        int fragmentCount = LowLatencyVideoProtocol.GetFrameFragmentCount(
            payload.Length,
            TestMaxDatagramBytes);
        int groupCount = LowLatencyVideoProtocol.GetXorFecGroupCount(
            fragmentCount);
        var parityPackets = new LowLatencyVideoDatagram[groupCount];
        for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            int parityLength =
                LowLatencyVideoProtocol.GetXorFecParityPayloadLength(
                    payload.Length,
                    groupIndex,
                    TestMaxDatagramBytes);
            byte[] parity = new byte[parityLength];
            Assert.Equal(
                parityLength,
                LowLatencyVideoProtocol.WriteXorFecParityPayload(
                    payload,
                    groupIndex,
                    TestMaxDatagramBytes,
                    parity));
            int groupStartOffset = checked(
                groupIndex *
                LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup *
                TestMaxFragmentPayloadBytes);
            parityPackets[groupIndex] = new LowLatencyVideoDatagram(
                LowLatencyVideoDatagramKind.FrameXorParity,
                checked((ulong)(fragmentCount + groupIndex)),
                frameSequence,
                payload.Length,
                groupStartOffset,
                checked((ushort)groupIndex),
                checked((ushort)fragmentCount),
                frameKind,
                0,
                parity);
        }

        return parityPackets;
    }

    private static byte[] CreatePayloadWithFragmentCount(
        int fragmentCount,
        int lastFragmentLength)
    {
        Assert.InRange(fragmentCount, 1, LowLatencyVideoProtocol.MaxFragmentCount);
        Assert.InRange(lastFragmentLength, 1, TestMaxFragmentPayloadBytes);
        int frameLength = checked(
            ((fragmentCount - 1) * TestMaxFragmentPayloadBytes) +
            lastFragmentLength);
        return SequenceBytes(frameLength, 0x39);
    }

    private static byte[] GetRetainedParity(
        LowLatencyVideoFrameReassembler reassembler,
        int groupIndex)
    {
        var field = typeof(LowLatencyVideoFrameReassembler).GetField(
            "_xorParityByGroup",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        byte[]?[] groups = Assert.IsType<byte[]?[]>(field.GetValue(reassembler));
        return Assert.IsType<byte[]>(groups[groupIndex]);
    }

    private static LowLatencyVideoFrameReassembler GetAdjacentInner(
        LowLatencyVideoAdjacentFrameReassembler reassembler,
        string fieldName)
    {
        var field =
            typeof(LowLatencyVideoAdjacentFrameReassembler)
                .GetField(
                    fieldName,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<LowLatencyVideoFrameReassembler>(
            field.GetValue(reassembler));
    }

    private static byte[] SequenceBytes(int length, byte seed)
    {
        byte[] bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = unchecked((byte)(seed + index));
        }

        return bytes;
    }
}
