using System.Buffers;
using System.Buffers.Binary;

namespace RemoteDesk;

/// <summary>
/// Reassembles RFC 6184 H.264 RTP payloads into independently framed
/// Annex-B access units. RTP marker bits complete access units immediately,
/// so no following AUD or frame is required to publish the current frame.
/// </summary>
internal sealed class H264RtpAccessUnitAssembler
{
    internal const int DefaultMaxAccessUnitBytes = 8 * 1024 * 1024;
    // Byte arrays at and above roughly 85 KiB enter the large object heap.
    // Start pooling below that boundary so every native-4K access unit uses a
    // reusable backing array rather than forcing periodic Gen2 collections.
    internal const int PooledAccessUnitMinimumBytes = 80 * 1024;
    private const int RtpFixedHeaderBytes = 12;
    private const int H264PayloadType = 96;
    private static readonly byte[] AnnexBStartCode = [0, 0, 0, 1];

    private readonly int _maxAccessUnitBytes;
    private readonly ArrayBufferWriter<byte> _accessUnit;
    private readonly ArrayPool<byte> _accessUnitPool;

    private byte[]? _cachedSps;
    private byte[]? _cachedPps;
    private bool _hasExpectedSequence;
    private ushort _expectedSequence;
    private bool _hasCurrentTimestamp;
    private uint _currentTimestamp;
    private bool _discardingTimestamp;
    private uint _discardedTimestamp;
    private bool _discardNextTimestamp;
    private bool _hasVcl;
    private bool _isIdr;
    private bool _hasSps;
    private bool _hasPps;
    private bool _fuInProgress;
    private byte _fuNalHeader;
    private int _fuNalOffset;

    public H264RtpAccessUnitAssembler(
        int maxAccessUnitBytes = DefaultMaxAccessUnitBytes,
        ArrayPool<byte>? accessUnitPool = null)
    {
        if (maxAccessUnitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAccessUnitBytes));
        }

        _maxAccessUnitBytes = maxAccessUnitBytes;
        _accessUnitPool =
            accessUnitPool ??
            ArrayPool<byte>.Shared;
        _accessUnit = new ArrayBufferWriter<byte>(
            Math.Min(64 * 1024, maxAccessUnitBytes));
    }

    public AnnexBH264AccessUnit? AppendPacket(
        ReadOnlySpan<byte> packet)
    {
        RtpPacketParseStatus parseStatus = TryParseRtpPacket(
            packet,
            out ushort sequence,
            out uint timestamp,
            out bool marker,
            out int payloadOffset,
            out int payloadLength);
        if (parseStatus == RtpPacketParseStatus.Ignored)
        {
            return null;
        }

        if (parseStatus == RtpPacketParseStatus.Malformed)
        {
            RejectUnknownPacket();
            return null;
        }

        if (!AcceptSequence(sequence))
        {
            RejectTimestamp(timestamp, marker);
            return null;
        }

        if (_discardNextTimestamp)
        {
            _discardNextTimestamp = false;
            _discardingTimestamp = !marker;
            _discardedTimestamp = timestamp;
            return null;
        }

        if (_discardingTimestamp)
        {
            if (timestamp == _discardedTimestamp)
            {
                if (marker)
                {
                    _discardingTimestamp = false;
                }

                return null;
            }

            _discardingTimestamp = false;
        }

        if (_hasCurrentTimestamp &&
            timestamp != _currentTimestamp)
        {
            ClearCurrentAccessUnit();
        }

        if (!_hasCurrentTimestamp)
        {
            _hasCurrentTimestamp = true;
            _currentTimestamp = timestamp;
        }

        ReadOnlySpan<byte> payload = packet.Slice(
            payloadOffset,
            payloadLength);
        if (!AppendH264Payload(payload))
        {
            RejectTimestamp(timestamp, marker);
            return null;
        }

        if (!marker)
        {
            return null;
        }

        if (_fuInProgress)
        {
            RejectTimestamp(timestamp, marker: true);
            return null;
        }

        AnnexBH264AccessUnit? completed =
            BuildCompletedAccessUnit();
        ClearCurrentAccessUnit();
        return completed;
    }

    private bool AcceptSequence(ushort sequence)
    {
        if (!_hasExpectedSequence)
        {
            _hasExpectedSequence = true;
            _expectedSequence = unchecked((ushort)(sequence + 1));
            return true;
        }

        if (sequence == _expectedSequence)
        {
            _expectedSequence = unchecked((ushort)(sequence + 1));
            return true;
        }

        short distance = unchecked(
            (short)(sequence - _expectedSequence));
        if (distance > 0)
        {
            _expectedSequence = unchecked(
                (ushort)(sequence + 1));
        }

        return false;
    }

    private bool AppendH264Payload(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty ||
            (payload[0] & 0x80) != 0)
        {
            return false;
        }

        int nalType = payload[0] & 0x1F;
        return nalType switch
        {
            >= 1 and <= 23 =>
                !_fuInProgress &&
                AppendCompleteNal(payload),
            24 =>
                !_fuInProgress &&
                AppendStapA(payload),
            28 => AppendFuA(payload),
            _ => false
        };
    }

    private bool AppendStapA(ReadOnlySpan<byte> payload)
    {
        int offset = 1;
        int annexBBytes = 0;
        int nalCount = 0;
        while (offset < payload.Length)
        {
            if (payload.Length - offset < 2)
            {
                return false;
            }

            int nalLength = BinaryPrimitives.ReadUInt16BigEndian(
                payload.Slice(offset, 2));
            offset += 2;
            if (nalLength <= 0 ||
                nalLength > payload.Length - offset)
            {
                return false;
            }

            ReadOnlySpan<byte> nal = payload.Slice(
                offset,
                nalLength);
            int nalType = nal[0] & 0x1F;
            if ((nal[0] & 0x80) != 0 ||
                nalType is < 1 or > 23)
            {
                return false;
            }

            annexBBytes = checked(
                annexBBytes +
                AnnexBStartCode.Length +
                nalLength);
            offset += nalLength;
            nalCount++;
        }

        if (nalCount == 0 ||
            !CanAppend(annexBBytes))
        {
            return false;
        }

        offset = 1;
        while (offset < payload.Length)
        {
            int nalLength = BinaryPrimitives.ReadUInt16BigEndian(
                payload.Slice(offset, 2));
            offset += 2;
            AppendCompleteNalUnchecked(
                payload.Slice(offset, nalLength));
            offset += nalLength;
        }

        return true;
    }

    private bool AppendFuA(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
        {
            return false;
        }

        byte fuIndicator = payload[0];
        byte fuHeader = payload[1];
        bool startsNal = (fuHeader & 0x80) != 0;
        bool endsNal = (fuHeader & 0x40) != 0;
        int nalType = fuHeader & 0x1F;
        if ((fuHeader & 0x20) != 0 ||
            nalType is < 1 or > 23 ||
            (startsNal && endsNal))
        {
            return false;
        }

        byte nalHeader = (byte)(
            (fuIndicator & 0xE0) |
            nalType);
        ReadOnlySpan<byte> fragment = payload[2..];
        if (startsNal)
        {
            if (_fuInProgress ||
                !CanAppend(
                    AnnexBStartCode.Length +
                    1 +
                    fragment.Length))
            {
                return false;
            }

            int nalStart = _accessUnit.WrittenCount;
            _accessUnit.Write(AnnexBStartCode);
            _accessUnit.GetSpan(1)[0] = nalHeader;
            _accessUnit.Advance(1);
            _accessUnit.Write(fragment);
            _fuInProgress = true;
            _fuNalHeader = nalHeader;
            _fuNalOffset =
                nalStart +
                AnnexBStartCode.Length;
            UpdateNalMetadata(
                nalType,
                completedNal: default);
            return true;
        }

        if (!_fuInProgress ||
            nalHeader != _fuNalHeader ||
            !CanAppend(fragment.Length))
        {
            return false;
        }

        _accessUnit.Write(fragment);
        if (endsNal)
        {
            _fuInProgress = false;
            int nalLength =
                _accessUnit.WrittenCount -
                _fuNalOffset;
            CacheParameterSet(
                nalType,
                _accessUnit.WrittenSpan.Slice(
                    _fuNalOffset,
                    nalLength));
        }

        return true;
    }

    private bool AppendCompleteNal(
        ReadOnlySpan<byte> nal)
    {
        if (nal.IsEmpty ||
            !CanAppend(
                AnnexBStartCode.Length +
                nal.Length))
        {
            return false;
        }

        AppendCompleteNalUnchecked(nal);
        return true;
    }

    private void AppendCompleteNalUnchecked(
        ReadOnlySpan<byte> nal)
    {
        _accessUnit.Write(AnnexBStartCode);
        _accessUnit.Write(nal);
        int nalType = nal[0] & 0x1F;
        UpdateNalMetadata(nalType, nal);
    }

    private void UpdateNalMetadata(
        int nalType,
        ReadOnlySpan<byte> completedNal)
    {
        if (nalType is >= 1 and <= 5)
        {
            _hasVcl = true;
            _isIdr |= nalType == 5;
        }
        else if (nalType == 7)
        {
            _hasSps = true;
        }
        else if (nalType == 8)
        {
            _hasPps = true;
        }

        if (!completedNal.IsEmpty)
        {
            CacheParameterSet(nalType, completedNal);
        }
    }

    private void CacheParameterSet(
        int nalType,
        ReadOnlySpan<byte> nal)
    {
        if (nalType == 7)
        {
            _cachedSps = nal.ToArray();
        }
        else if (nalType == 8)
        {
            _cachedPps = nal.ToArray();
        }
    }

    private AnnexBH264AccessUnit? BuildCompletedAccessUnit()
    {
        if (!_hasVcl)
        {
            return null;
        }

        if (!_isIdr)
        {
            return CreateAccessUnit(
                _accessUnit.WrittenSpan,
                _hasSps || _hasPps
                    ? RemoteFrameFlags.CodecConfig
                    : RemoteFrameFlags.None,
                IsIdr: false,
                HasSps: _hasSps,
                HasPps: _hasPps);
        }

        if (_cachedSps is null ||
            _cachedPps is null)
        {
            return null;
        }

        if (_hasSps && _hasPps)
        {
            return CreateAccessUnit(
                _accessUnit.WrittenSpan,
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig,
                IsIdr: true,
                HasSps: true,
                HasPps: true);
        }

        ReadOnlySpan<byte> currentBytes =
            _accessUnit.WrittenSpan;
        int nonParameterSetBytes =
            CountBytesExcludingParameterSets(
                currentBytes);
        if (nonParameterSetBytes < 0)
        {
            return null;
        }

        int requiredBytes = checked(
            AnnexBStartCode.Length +
            _cachedSps.Length +
            AnnexBStartCode.Length +
            _cachedPps.Length +
            nonParameterSetBytes);
        if (requiredBytes > _maxAccessUnitBytes)
        {
            return null;
        }

        H264FrameBufferLease normalized =
            H264FrameBufferLease.Allocate(
                requiredBytes,
                _accessUnitPool,
                ShouldPool(requiredBytes));
        try
        {
            Span<byte> destination =
                normalized.WritableSpan;
            int destinationOffset = 0;
            AnnexBStartCode.CopyTo(
                destination[destinationOffset..]);
            destinationOffset += AnnexBStartCode.Length;
            _cachedSps.CopyTo(
                destination[destinationOffset..]);
            destinationOffset += _cachedSps.Length;
            AnnexBStartCode.CopyTo(
                destination[destinationOffset..]);
            destinationOffset += AnnexBStartCode.Length;
            _cachedPps.CopyTo(
                destination[destinationOffset..]);
            destinationOffset += _cachedPps.Length;
            destinationOffset += CopyNalsExcludingParameterSets(
                currentBytes,
                destination[destinationOffset..]);
            if (destinationOffset != requiredBytes)
            {
                throw new InvalidDataException(
                    "The normalized H.264 access unit length changed " +
                    "while it was copied.");
            }

            return new AnnexBH264AccessUnit(
                normalized,
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig,
                IsIdr: true,
                HasSps: true,
                HasPps: true);
        }
        catch
        {
            normalized.Dispose();
            throw;
        }
    }

    private AnnexBH264AccessUnit CreateAccessUnit(
        ReadOnlySpan<byte> bytes,
        RemoteFrameFlags flags,
        bool IsIdr,
        bool HasSps,
        bool HasPps)
    {
        return new AnnexBH264AccessUnit(
            H264FrameBufferLease.CopyFrom(
                bytes,
                _accessUnitPool,
                ShouldPool(bytes.Length)),
            flags,
            IsIdr,
            HasSps,
            HasPps);
    }

    private static bool ShouldPool(int byteCount) =>
        byteCount >= PooledAccessUnitMinimumBytes;

    private static int CountBytesExcludingParameterSets(
        ReadOnlySpan<byte> annexB)
    {
        int total = 0;
        int offset = 0;
        while (offset < annexB.Length)
        {
            if (!HasStartCodeAt(annexB, offset) ||
                offset + AnnexBStartCode.Length >=
                    annexB.Length)
            {
                return -1;
            }

            int nextOffset = FindNextStartCode(
                annexB,
                offset + AnnexBStartCode.Length + 1);
            int nalEnd = nextOffset >= 0
                ? nextOffset
                : annexB.Length;
            int nalType =
                annexB[offset + AnnexBStartCode.Length] &
                0x1F;
            if (nalType is not (7 or 8))
            {
                total = checked(
                    total +
                    nalEnd -
                    offset);
            }

            offset = nalEnd;
        }

        return total;
    }

    private static int CopyNalsExcludingParameterSets(
        ReadOnlySpan<byte> annexB,
        Span<byte> destination)
    {
        int offset = 0;
        int destinationOffset = 0;
        while (offset < annexB.Length)
        {
            int nextOffset = FindNextStartCode(
                annexB,
                offset + AnnexBStartCode.Length + 1);
            int nalEnd = nextOffset >= 0
                ? nextOffset
                : annexB.Length;
            int nalType =
                annexB[offset + AnnexBStartCode.Length] &
                0x1F;
            if (nalType is not (7 or 8))
            {
                ReadOnlySpan<byte> nal =
                    annexB.Slice(
                        offset,
                        nalEnd - offset);
                nal.CopyTo(
                    destination[destinationOffset..]);
                destinationOffset += nal.Length;
            }

            offset = nalEnd;
        }

        return destinationOffset;
    }

    private static int FindNextStartCode(
        ReadOnlySpan<byte> annexB,
        int startIndex)
    {
        for (int index = Math.Max(0, startIndex);
             index <=
                annexB.Length -
                AnnexBStartCode.Length;
             index++)
        {
            if (HasStartCodeAt(annexB, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasStartCodeAt(
        ReadOnlySpan<byte> annexB,
        int offset)
    {
        return offset >= 0 &&
            offset <=
                annexB.Length -
                AnnexBStartCode.Length &&
            annexB[offset] == 0 &&
            annexB[offset + 1] == 0 &&
            annexB[offset + 2] == 0 &&
            annexB[offset + 3] == 1;
    }

    private bool CanAppend(int byteCount)
    {
        return byteCount >= 0 &&
            _accessUnit.WrittenCount <=
                _maxAccessUnitBytes - byteCount;
    }

    private void RejectUnknownPacket()
    {
        ClearCurrentAccessUnit();
        _hasExpectedSequence = false;
        _discardingTimestamp = false;
        _discardNextTimestamp = true;
    }

    private void RejectTimestamp(
        uint timestamp,
        bool marker)
    {
        ClearCurrentAccessUnit();
        _discardingTimestamp = !marker;
        _discardedTimestamp = timestamp;
    }

    private void ClearCurrentAccessUnit()
    {
        _accessUnit.Clear();
        _hasCurrentTimestamp = false;
        _hasVcl = false;
        _isIdr = false;
        _hasSps = false;
        _hasPps = false;
        _fuInProgress = false;
        _fuNalHeader = 0;
        _fuNalOffset = 0;
    }

    private static RtpPacketParseStatus TryParseRtpPacket(
        ReadOnlySpan<byte> packet,
        out ushort sequence,
        out uint timestamp,
        out bool marker,
        out int payloadOffset,
        out int payloadLength)
    {
        sequence = 0;
        timestamp = 0;
        marker = false;
        payloadOffset = 0;
        payloadLength = 0;

        if (packet.Length < 2)
        {
            return RtpPacketParseStatus.Malformed;
        }

        int version = packet[0] >> 6;
        int payloadType = packet[1] & 0x7F;
        if (version != 2 ||
            payloadType != H264PayloadType)
        {
            return RtpPacketParseStatus.Ignored;
        }

        if (packet.Length < RtpFixedHeaderBytes)
        {
            return RtpPacketParseStatus.Malformed;
        }

        bool hasPadding = (packet[0] & 0x20) != 0;
        bool hasExtension = (packet[0] & 0x10) != 0;
        int csrcCount = packet[0] & 0x0F;
        int headerLength =
            RtpFixedHeaderBytes +
            csrcCount * sizeof(uint);
        if (headerLength > packet.Length)
        {
            return RtpPacketParseStatus.Malformed;
        }

        if (hasExtension)
        {
            if (packet.Length - headerLength < 4)
            {
                return RtpPacketParseStatus.Malformed;
            }

            int extensionWords =
                BinaryPrimitives.ReadUInt16BigEndian(
                    packet.Slice(headerLength + 2, 2));
            int extensionBytes = checked(
                4 +
                extensionWords * sizeof(uint));
            if (extensionBytes >
                packet.Length - headerLength)
            {
                return RtpPacketParseStatus.Malformed;
            }

            headerLength += extensionBytes;
        }

        int payloadEnd = packet.Length;
        if (hasPadding)
        {
            int paddingBytes = packet[^1];
            if (paddingBytes <= 0 ||
                paddingBytes >
                    payloadEnd - headerLength)
            {
                return RtpPacketParseStatus.Malformed;
            }

            payloadEnd -= paddingBytes;
        }

        if (payloadEnd <= headerLength)
        {
            return RtpPacketParseStatus.Malformed;
        }

        sequence = BinaryPrimitives.ReadUInt16BigEndian(
            packet.Slice(2, 2));
        timestamp = BinaryPrimitives.ReadUInt32BigEndian(
            packet.Slice(4, 4));
        marker = (packet[1] & 0x80) != 0;
        payloadOffset = headerLength;
        payloadLength = payloadEnd - headerLength;
        return RtpPacketParseStatus.Valid;
    }

    private enum RtpPacketParseStatus
    {
        Valid,
        Ignored,
        Malformed
    }
}
