using System.Buffers.Binary;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class H264RtpAccessUnitAssemblerTests
{
    [Fact]
    public void MarkerCompletesSingleNalImmediatelyWithFullRtpHeaderFeatures()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] nal = Nal(1, 0x11, 0x22);
        byte[] packet = RtpPacket(
            sequence: 100,
            timestamp: 9000,
            marker: true,
            nal,
            csrcCount: 2,
            extension: [1, 2, 3, 4, 5, 6, 7, 8],
            paddingBytes: 4);

        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(packet);

        Assert.NotNull(unit);
        Assert.Equal(
            Join([0, 0, 0, 1], nal),
            unit.Bytes.ToArray());
        Assert.False(unit.IsIdr);
        Assert.False(unit.HasSps);
        Assert.False(unit.HasPps);
        Assert.Equal(RemoteFrameFlags.None, unit.Flags);
    }

    [Fact]
    public void PrivateLoopbackPacketAtConfiguredLimitIsAccepted()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] nal = Nal(
            1,
            Enumerable.Repeat(
                (byte)0x55,
                FfmpegDesktopH264Capture
                    .LoopbackRtpPacketSizeBytes -
                    13)
                .ToArray());
        byte[] packet = RtpPacket(
            sequence: 100,
            timestamp: 9000,
            marker: true,
            nal);

        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(packet);

        Assert.Equal(
            FfmpegDesktopH264Capture
                .LoopbackRtpPacketSizeBytes,
            packet.Length);
        Assert.NotNull(unit);
        Assert.Equal(
            nal.Length + 4,
            unit.Bytes.Length);
    }

    [Fact]
    public void LargeAccessUnitUsesPoolAndReturnsLeaseExactlyOnce()
    {
        var pool = new TrackingByteArrayPool();
        var assembler = new H264RtpAccessUnitAssembler(
            accessUnitPool: pool);
        byte[] nal = Nal(
            1,
            Enumerable.Repeat(
                (byte)0x5A,
                H264RtpAccessUnitAssembler
                    .PooledAccessUnitMinimumBytes +
                4096)
                .ToArray());

        AnnexBH264AccessUnit unit =
            Assert.IsType<AnnexBH264AccessUnit>(
                AppendFragmentedNal(
                    assembler,
                    sequence: 100,
                    timestamp: 9000,
                    nal));

        Assert.True(unit.OwnsPooledBuffer);
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(nal.Length + 4, unit.Bytes.Length);
        Assert.Equal(nal[0], unit.Bytes.Span[4]);
        Assert.Equal(nal[^1], unit.Bytes.Span[^1]);

        unit.Dispose();
        Assert.True(unit.IsDisposed);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Throws<ObjectDisposedException>(
            () => _ = unit.Bytes);

        unit.Dispose();
        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public void LargeBareIdrNormalizationWritesDirectlyIntoPooledLease()
    {
        var pool = new TrackingByteArrayPool();
        var assembler = new H264RtpAccessUnitAssembler(
            accessUnitPool: pool);
        byte[] sps = Nal(7, 0x64, 0x00, 0x1F);
        byte[] pps = Nal(8, 0xEE, 0x06);
        Assert.Null(assembler.AppendPacket(RtpPacket(
            sequence: 10,
            timestamp: 100,
            marker: true,
            StapA(sps, pps))));
        byte[] idr = Nal(
            5,
            Enumerable.Repeat(
                (byte)0x6B,
                H264RtpAccessUnitAssembler
                    .PooledAccessUnitMinimumBytes +
                2048)
                .ToArray());

        AnnexBH264AccessUnit unit =
            Assert.IsType<AnnexBH264AccessUnit>(
                AppendFragmentedNal(
                    assembler,
                    sequence: 11,
                    timestamp: 200,
                    idr));

        AssertIndependentIdr(unit);
        Assert.True(unit.OwnsPooledBuffer);
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(
            [sps, pps, idr],
            ReadAnnexBNals(unit.Bytes));

        unit.Dispose();
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void NonH264RtpPacketsAreIgnoredWithoutBreakingCurrentAccessUnit()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        Assert.Null(assembler.AppendPacket(RtpPacket(
            10,
            1000,
            marker: false,
            Nal(1, 0x10))));

        Assert.Null(assembler.AppendPacket(RtpPacket(
            500,
            5000,
            marker: true,
            Nal(1, 0xEE),
            payloadType: 97)));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            501,
            5001,
            marker: true,
            Nal(1, 0xEF),
            version: 1)));

        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(RtpPacket(
                11,
                1000,
                marker: true,
                Nal(1, 0x20)));

        Assert.NotNull(unit);
        byte[][] nals = ReadAnnexBNals(unit.Bytes);
        Assert.Equal(2, nals.Length);
        Assert.Equal(0x10, nals[0][^1]);
        Assert.Equal(0x20, nals[1][^1]);
    }

    [Fact]
    public void StapACompletesConfiguredIdrOnMarker()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] sps = Nal(7, 0x64, 0x00, 0x1F);
        byte[] pps = Nal(8, 0xEE, 0x06);
        byte[] idr = Nal(5, 0x31, 0x32);

        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(RtpPacket(
                1,
                100,
                marker: true,
                StapA(sps, pps, idr)));

        AssertIndependentIdr(unit);
        Assert.Equal(
            [sps, pps, idr],
            ReadAnnexBNals(unit!.Bytes));
    }

    [Fact]
    public void CachedParameterSetsArePrependedToLaterBareIdr()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] sps = Nal(7, 0x42, 0x00, 0x1E);
        byte[] pps = Nal(8, 0xCE, 0x3C);
        byte[] idr = Nal(5, 0x41);

        Assert.Null(assembler.AppendPacket(RtpPacket(
            20,
            100,
            marker: true,
            StapA(sps, pps))));
        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(RtpPacket(
                21,
                200,
                marker: true,
                idr));

        AssertIndependentIdr(unit);
        Assert.Equal(
            [sps, pps, idr],
            ReadAnnexBNals(unit!.Bytes));
    }

    [Fact]
    public void PartiallyConfiguredIdrIsNormalizedWithoutDuplicateParameterSets()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] oldSps = Nal(7, 0x42, 0x00, 0x1E);
        byte[] newSps = Nal(7, 0x64, 0x00, 0x1F);
        byte[] pps = Nal(8, 0xCE, 0x3C);
        byte[] idr = Nal(5, 0x61);
        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: true,
            StapA(oldSps, pps))));

        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(RtpPacket(
                2,
                200,
                marker: true,
                StapA(newSps, idr)));

        AssertIndependentIdr(unit);
        Assert.Equal(
            [newSps, pps, idr],
            ReadAnnexBNals(unit!.Bytes));
    }

    [Fact]
    public void BareIdrIsWithheldUntilBothParameterSetsAreKnown()
    {
        var assembler = new H264RtpAccessUnitAssembler();

        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: true,
            Nal(5, 0x55))));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            2,
            200,
            marker: true,
            StapA(
                Nal(7, 0x64),
                Nal(8, 0xEE)))));

        AssertIndependentIdr(
            assembler.AppendPacket(RtpPacket(
                3,
                300,
                marker: true,
                Nal(5, 0x56))));
    }

    [Fact]
    public void FuAFragmentsReconstructOriginalNalAndCompleteOnEndMarker()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] sps = Nal(7, 0x64, 0x00, 0x1F);
        byte[] pps = Nal(8, 0xEE, 0x06);
        byte[] idr = Nal(
            5,
            0x10,
            0x20,
            0x30,
            0x40,
            0x50);
        Assert.Null(assembler.AppendPacket(RtpPacket(
            40,
            100,
            marker: true,
            StapA(sps, pps))));

        Assert.Null(assembler.AppendPacket(RtpPacket(
            41,
            200,
            marker: false,
            FuA(idr[0], startsNal: true, endsNal: false, 0x10, 0x20))));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            42,
            200,
            marker: false,
            FuA(idr[0], startsNal: false, endsNal: false, 0x30))));
        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(RtpPacket(
                43,
                200,
                marker: true,
                FuA(idr[0], startsNal: false, endsNal: true, 0x40, 0x50)));

        AssertIndependentIdr(unit);
        Assert.Equal(
            [sps, pps, idr],
            ReadAnnexBNals(unit!.Bytes));
    }

    [Fact]
    public void FragmentedParameterSetIsCachedOnlyAfterFuCompletes()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] sps = Nal(7, 0x64, 0x00, 0x1F);
        byte[] pps = Nal(8, 0xEE, 0x06);
        byte[] idr = Nal(5, 0x41);

        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: false,
            FuA(sps[0], startsNal: true, endsNal: false, 0x64))));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            2,
            100,
            marker: true,
            FuA(sps[0], startsNal: false, endsNal: true, 0x00, 0x1F))));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            3,
            200,
            marker: true,
            pps)));

        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(RtpPacket(
                4,
                300,
                marker: true,
                idr));

        AssertIndependentIdr(unit);
        Assert.Equal(
            [sps, pps, idr],
            ReadAnnexBNals(unit!.Bytes));
    }

    [Fact]
    public void SequenceNumberWrapPreservesFuAAssembly()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] sps = Nal(7, 0x64);
        byte[] pps = Nal(8, 0xEE);
        byte[] idr = Nal(5, 0x61, 0x62);
        Assert.Null(assembler.AppendPacket(RtpPacket(
            65534,
            10,
            marker: true,
            StapA(sps, pps))));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            65535,
            20,
            marker: false,
            FuA(idr[0], startsNal: true, endsNal: false, 0x61))));

        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(RtpPacket(
                0,
                20,
                marker: true,
                FuA(idr[0], startsNal: false, endsNal: true, 0x62)));

        AssertIndependentIdr(unit);
        Assert.Equal(idr, ReadAnnexBNals(unit!.Bytes)[2]);
    }

    [Fact]
    public void SequenceGapDropsFragmentedAccessUnitAndRecoversAtNextTimestamp()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] fragmented = Nal(1, 0x10, 0x20);
        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: false,
            FuA(fragmented[0], startsNal: true, endsNal: false, 0x10))));

        Assert.Null(assembler.AppendPacket(RtpPacket(
            3,
            100,
            marker: true,
            FuA(fragmented[0], startsNal: false, endsNal: true, 0x20))));
        AnnexBH264AccessUnit? recovered =
            assembler.AppendPacket(RtpPacket(
                4,
                200,
                marker: true,
                Nal(1, 0x44)));

        Assert.NotNull(recovered);
        Assert.Equal(
            [Nal(1, 0x44)],
            ReadAnnexBNals(recovered.Bytes));
    }

    [Fact]
    public void OutOfOrderLatePacketDropsCurrentAccessUnitWithoutRewindingSequence()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte nalHeader = Nal(1, 0x10)[0];
        Assert.Null(assembler.AppendPacket(RtpPacket(
            10,
            100,
            marker: false,
            FuA(nalHeader, startsNal: true, endsNal: false, 0x10))));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            12,
            100,
            marker: false,
            FuA(nalHeader, startsNal: false, endsNal: true, 0x30))));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            11,
            100,
            marker: true,
            FuA(nalHeader, startsNal: false, endsNal: true, 0x20))));

        AnnexBH264AccessUnit? recovered =
            assembler.AppendPacket(RtpPacket(
                13,
                200,
                marker: true,
                Nal(1, 0x55)));

        Assert.NotNull(recovered);
        Assert.Equal(
            [Nal(1, 0x55)],
            ReadAnnexBNals(recovered.Bytes));
    }

    [Fact]
    public void MarkerOnIncompleteFuDropsAccessUnit()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte nalHeader = Nal(1, 0x10)[0];

        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: true,
            FuA(nalHeader, startsNal: true, endsNal: false, 0x10))));
        Assert.NotNull(assembler.AppendPacket(RtpPacket(
            2,
            200,
            marker: true,
            Nal(1, 0x20))));
    }

    [Fact]
    public void TimestampChangeDropsUnmarkedAccessUnit()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: false,
            Nal(1, 0x10))));

        AnnexBH264AccessUnit? unit =
            assembler.AppendPacket(RtpPacket(
                2,
                200,
                marker: true,
                Nal(1, 0x20)));

        Assert.NotNull(unit);
        Assert.Equal(
            [Nal(1, 0x20)],
            ReadAnnexBNals(unit.Bytes));
    }

    [Fact]
    public void MalformedH264PayloadsDropOnlyTheirAccessUnit()
    {
        byte[][] malformedPayloads =
        [
            [0x78, 0x00, 0x00],
            [0x78, 0x00, 0x04, 0x67, 0x01],
            [0x7C, 0x05, 0x10],
            [0x7C, 0xC5, 0x10],
            [0x79, 0x10]
        ];

        foreach (byte[] malformed in malformedPayloads)
        {
            var assembler = new H264RtpAccessUnitAssembler();
            Assert.Null(assembler.AppendPacket(RtpPacket(
                1,
                100,
                marker: true,
                malformed)));
            Assert.NotNull(assembler.AppendPacket(RtpPacket(
                2,
                200,
                marker: true,
                Nal(1, 0x22))));
        }
    }

    [Fact]
    public void MalformedStapDoesNotCommitEarlierParameterSet()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        byte[] sps = Nal(7, 0x64, 0x00, 0x1F);
        byte[] malformedStap = new byte[
            1 +
            2 +
            sps.Length +
            1];
        malformedStap[0] = 0x78;
        BinaryPrimitives.WriteUInt16BigEndian(
            malformedStap.AsSpan(1, 2),
            (ushort)sps.Length);
        sps.CopyTo(malformedStap, 3);
        malformedStap[^1] = 0;

        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: true,
            malformedStap)));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            2,
            200,
            marker: true,
            Nal(8, 0xEE))));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            3,
            300,
            marker: true,
            Nal(5, 0x51))));
    }

    [Fact]
    public void DuplicateSequenceDropsCurrentAccessUnitAndRecoversAtExpectedSequence()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        Assert.Null(assembler.AppendPacket(RtpPacket(
            10,
            100,
            marker: false,
            Nal(1, 0x10))));
        Assert.Null(assembler.AppendPacket(RtpPacket(
            10,
            100,
            marker: true,
            Nal(1, 0x11))));

        AnnexBH264AccessUnit? recovered =
            assembler.AppendPacket(RtpPacket(
                11,
                200,
                marker: true,
                Nal(1, 0x20)));

        Assert.NotNull(recovered);
        Assert.Equal(
            [Nal(1, 0x20)],
            ReadAnnexBNals(recovered.Bytes));
    }

    [Fact]
    public void MalformedRtpExtensionAndPaddingDoNotLeakPartialAccessUnit()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: false,
            Nal(1, 0x10))));

        byte[] malformedExtension = RtpPacket(
            2,
            100,
            marker: false,
            Nal(1, 0x11));
        malformedExtension[0] |= 0x10;
        Assert.Null(assembler.AppendPacket(
            malformedExtension));

        Assert.Null(assembler.AppendPacket(RtpPacket(
            50,
            100,
            marker: true,
            Nal(1, 0x12))));
        Assert.NotNull(assembler.AppendPacket(RtpPacket(
            51,
            200,
            marker: true,
            Nal(1, 0x20))));

        var paddingAssembler =
            new H264RtpAccessUnitAssembler();
        byte[] malformedPadding = RtpPacket(
            1,
            300,
            marker: true,
            Nal(1, 0x30),
            paddingBytes: 2);
        malformedPadding[^1] = 0x7F;
        Assert.Null(paddingAssembler.AppendPacket(
            malformedPadding));
    }

    [Fact]
    public void OversizedAccessUnitIsDroppedAndAssemblerRecovers()
    {
        Assert.Equal(
            8 * 1024 * 1024,
            H264RtpAccessUnitAssembler.DefaultMaxAccessUnitBytes);
        var assembler = new H264RtpAccessUnitAssembler(
            maxAccessUnitBytes: 12);

        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: true,
            Nal(
                1,
                Enumerable.Repeat(
                    (byte)0x55,
                    12).ToArray()))));
        AnnexBH264AccessUnit? recovered =
            assembler.AppendPacket(RtpPacket(
                2,
                200,
                marker: true,
                Nal(1, 0x21)));

        Assert.NotNull(recovered);
        Assert.Equal(
            [Nal(1, 0x21)],
            ReadAnnexBNals(recovered.Bytes));
    }

    [Fact]
    public void ParameterSetPrependAlsoHonorsAccessUnitLimit()
    {
        var assembler = new H264RtpAccessUnitAssembler(
            maxAccessUnitBytes: 20);
        Assert.Null(assembler.AppendPacket(RtpPacket(
            1,
            100,
            marker: true,
            StapA(
                Nal(7, 0x01, 0x02, 0x03),
                Nal(8, 0x04, 0x05, 0x06)))));

        Assert.Null(assembler.AppendPacket(RtpPacket(
            2,
            200,
            marker: true,
            Nal(5, 0x10, 0x11, 0x12))));
    }

    private static void AssertIndependentIdr(
        AnnexBH264AccessUnit? unit)
    {
        Assert.NotNull(unit);
        Assert.True(unit.IsIdr);
        Assert.True(unit.HasSps);
        Assert.True(unit.HasPps);
        Assert.Equal(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            unit.Flags);
    }

    private static byte[] RtpPacket(
        ushort sequence,
        uint timestamp,
        bool marker,
        byte[] payload,
        int csrcCount = 0,
        byte[]? extension = null,
        int paddingBytes = 0,
        int payloadType = 96,
        int version = 2)
    {
        if (csrcCount is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(csrcCount));
        }

        if (extension is not null &&
            extension.Length % 4 != 0)
        {
            throw new ArgumentException(
                "RTP extension must use complete 32-bit words.",
                nameof(extension));
        }

        if (paddingBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(paddingBytes));
        }

        int extensionBytes = extension is null
            ? 0
            : 4 + extension.Length;
        int headerBytes =
            12 +
            csrcCount * 4 +
            extensionBytes;
        byte[] packet = new byte[
            headerBytes +
            payload.Length +
            paddingBytes];
        packet[0] = (byte)(
            (version << 6) |
            (paddingBytes > 0 ? 0x20 : 0) |
            (extension is not null ? 0x10 : 0) |
            csrcCount);
        packet[1] = (byte)(
            (marker ? 0x80 : 0) |
            payloadType);
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(2, 2),
            sequence);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(4, 4),
            timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(8, 4),
            0x01020304);

        int offset = 12;
        for (int index = 0; index < csrcCount; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(
                packet.AsSpan(offset, 4),
                (uint)(index + 1));
            offset += 4;
        }

        if (extension is not null)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                packet.AsSpan(offset, 2),
                0xBEDE);
            BinaryPrimitives.WriteUInt16BigEndian(
                packet.AsSpan(offset + 2, 2),
                (ushort)(extension.Length / 4));
            extension.CopyTo(packet, offset + 4);
            offset += 4 + extension.Length;
        }

        payload.CopyTo(packet, offset);
        if (paddingBytes > 0)
        {
            packet[^1] = (byte)paddingBytes;
        }

        return packet;
    }

    private static byte[] StapA(
        params byte[][] nals)
    {
        int length = 1 + nals.Sum(
            nal => 2 + nal.Length);
        byte[] payload = new byte[length];
        payload[0] = 0x78;
        int offset = 1;
        foreach (byte[] nal in nals)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                payload.AsSpan(offset, 2),
                checked((ushort)nal.Length));
            offset += 2;
            nal.CopyTo(payload, offset);
            offset += nal.Length;
        }

        return payload;
    }

    private static byte[] FuA(
        byte nalHeader,
        bool startsNal,
        bool endsNal,
        params byte[] fragment)
    {
        byte[] payload = new byte[2 + fragment.Length];
        payload[0] = (byte)(
            (nalHeader & 0xE0) |
            28);
        payload[1] = (byte)(
            (startsNal ? 0x80 : 0) |
            (endsNal ? 0x40 : 0) |
            (nalHeader & 0x1F));
        fragment.CopyTo(payload, 2);
        return payload;
    }

    private static AnnexBH264AccessUnit?
        AppendFragmentedNal(
            H264RtpAccessUnitAssembler assembler,
            ushort sequence,
            uint timestamp,
            byte[] nal)
    {
        const int fragmentBytes = 12 * 1024;
        AnnexBH264AccessUnit? completed = null;
        int offset = 1;
        while (offset < nal.Length)
        {
            int length = Math.Min(
                fragmentBytes,
                nal.Length - offset);
            bool startsNal = offset == 1;
            bool endsNal = offset + length == nal.Length;
            completed = assembler.AppendPacket(
                RtpPacket(
                    sequence++,
                    timestamp,
                    marker: endsNal,
                    FuA(
                        nal[0],
                        startsNal,
                        endsNal,
                        nal.AsSpan(offset, length)
                            .ToArray())));
            if (!endsNal)
            {
                Assert.Null(completed);
            }

            offset += length;
        }

        return completed;
    }

    private static byte[] Nal(
        int nalType,
        params byte[] payload)
    {
        byte[] nal = new byte[1 + payload.Length];
        nal[0] = (byte)(0x60 | nalType);
        payload.CopyTo(nal, 1);
        return nal;
    }

    private static byte[][] ReadAnnexBNals(
        ReadOnlyMemory<byte> memory)
    {
        ReadOnlySpan<byte> annexB = memory.Span;
        var starts = new List<int>();
        for (int index = 0;
             index <= annexB.Length - 5;
             index++)
        {
            if (annexB[index] == 0 &&
                annexB[index + 1] == 0 &&
                annexB[index + 2] == 0 &&
                annexB[index + 3] == 1)
            {
                starts.Add(index);
                index += 3;
            }
        }

        var nals = new byte[starts.Count][];
        for (int index = 0; index < starts.Count; index++)
        {
            int nalStart = starts[index] + 4;
            int nalEnd = index + 1 < starts.Count
                ? starts[index + 1]
                : annexB.Length;
            nals[index] = annexB.Slice(
                nalStart,
                nalEnd - nalStart).ToArray();
        }

        return nals;
    }

    private static byte[] Join(
        params byte[][] arrays)
    {
        byte[] joined = new byte[
            arrays.Sum(array => array.Length)];
        int offset = 0;
        foreach (byte[] array in arrays)
        {
            array.CopyTo(joined, offset);
            offset += array.Length;
        }

        return joined;
    }
}
