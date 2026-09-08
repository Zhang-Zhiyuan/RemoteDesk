using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace RemoteDesk;

internal enum LowLatencyVideoDatagramKind : byte
{
    BindProbe = 1,
    BindAck = 2,
    FrameFragment = 3,
    Feedback = 4,
    FeedbackV2 = 5,
    FrameXorParity = 6,
    MouseMove = 7,
    MouseMoveAppliedAck = 8,
    Heartbeat = 9
}

internal readonly record struct LowLatencyVideoDatagram(
    LowLatencyVideoDatagramKind Kind,
    ulong PacketSequence,
    ulong FrameSequence,
    int FrameLength,
    int FragmentOffset,
    ushort FragmentIndex,
    ushort FragmentCount,
    MessageType FrameKind,
    byte Flags,
    Memory<byte> Plaintext);

internal static class LowLatencyVideoProtocol
{
    public const byte Version = 1;
    public const int HeaderLength = 52;
    public const int TagLength = 16;
    public const int KeyLength = 32;
    public const int NoncePrefixLength = 4;
    public const int ChallengeLength = 16;
    public const int DefaultMaxDatagramBytes = 1200;
    public const int MinDatagramBytes = 576;
    public const int DefaultMaxFrameBytes = 8 * 1024 * 1024;
    public const int MaxFragmentCount = 8192;
    public const int ReplayWindowPackets = 4096;
    public const int XorFecDataFragmentsPerGroup = 16;
    public const int RecommendedMinimumXorFecDataFragments = 8;

    private const uint Magic = 0x31554452; // "RDU1" in little-endian order.

    public static int GetMaxFragmentPayloadBytes(int maxDatagramBytes)
    {
        if (maxDatagramBytes < MinDatagramBytes || maxDatagramBytes > DefaultMaxDatagramBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDatagramBytes));
        }

        return maxDatagramBytes - HeaderLength - TagLength;
    }

    public static int GetFrameFragmentCount(int frameLength, int maxDatagramBytes)
    {
        if (frameLength <= 0 || frameLength > DefaultMaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(frameLength));
        }

        int maxPayload = GetMaxFragmentPayloadBytes(maxDatagramBytes);
        int fragmentCount = checked((frameLength + maxPayload - 1) / maxPayload);
        if (fragmentCount > MaxFragmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameLength));
        }

        return fragmentCount;
    }

    public static bool ShouldSendXorFec(int dataFragmentCount)
    {
        return dataFragmentCount is >= RecommendedMinimumXorFecDataFragments and <= MaxFragmentCount;
    }

    public static int GetXorFecGroupCount(int dataFragmentCount)
    {
        if (dataFragmentCount is <= 0 or > MaxFragmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(dataFragmentCount));
        }

        return checked(
            (dataFragmentCount + XorFecDataFragmentsPerGroup - 1) /
            XorFecDataFragmentsPerGroup);
    }

    public static int GetXorFecParityPayloadLength(
        int frameLength,
        int groupIndex,
        int maxDatagramBytes)
    {
        int maxPayload = GetMaxFragmentPayloadBytes(maxDatagramBytes);
        int fragmentCount = GetFrameFragmentCount(frameLength, maxDatagramBytes);
        int groupCount = GetXorFecGroupCount(fragmentCount);
        if (groupIndex < 0 || groupIndex >= groupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(groupIndex));
        }

        int groupStartOffset = checked(
            groupIndex * XorFecDataFragmentsPerGroup * maxPayload);
        return Math.Min(maxPayload, frameLength - groupStartOffset);
    }

    public static int WriteXorFecParityPayload(
        ReadOnlySpan<byte> framePayload,
        int groupIndex,
        int maxDatagramBytes,
        Span<byte> destination)
    {
        int parityLength = GetXorFecParityPayloadLength(
            framePayload.Length,
            groupIndex,
            maxDatagramBytes);
        if (destination.Length < parityLength)
        {
            throw new ArgumentException(
                "低延迟画面 XOR FEC 缓冲区过小。",
                nameof(destination));
        }

        Span<byte> parity = destination[..parityLength];
        parity.Clear();
        int maxPayload = GetMaxFragmentPayloadBytes(maxDatagramBytes);
        int fragmentCount = GetFrameFragmentCount(
            framePayload.Length,
            maxDatagramBytes);
        int firstFragment = checked(groupIndex * XorFecDataFragmentsPerGroup);
        int endFragment = Math.Min(
            fragmentCount,
            firstFragment + XorFecDataFragmentsPerGroup);
        for (int fragmentIndex = firstFragment;
            fragmentIndex < endFragment;
            fragmentIndex++)
        {
            int offset = checked(fragmentIndex * maxPayload);
            int length = Math.Min(maxPayload, framePayload.Length - offset);
            ReadOnlySpan<byte> source = framePayload.Slice(offset, length);
            for (int index = 0; index < source.Length; index++)
            {
                parity[index] ^= source[index];
            }
        }

        return parityLength;
    }

    public static void ValidateOffer(LowLatencyVideoOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (offer.Port is <= 0 or > ushort.MaxValue ||
            offer.MaxDatagramBytes is < MinDatagramBytes or > DefaultMaxDatagramBytes ||
            offer.MaxFrameBytes is < RemoteMessageCodec.FrameHeaderLength or > DefaultMaxFrameBytes ||
            offer.ChannelId == 0 ||
            offer.Epoch == 0 ||
            offer.HostToViewerKey.Length != KeyLength ||
            offer.ViewerToHostKey.Length != KeyLength ||
            offer.HostNoncePrefix.Length != NoncePrefixLength ||
            offer.ViewerNoncePrefix.Length != NoncePrefixLength ||
            offer.Challenge.Length != ChallengeLength)
        {
            throw new InvalidDataException("低延迟画面通道参数异常。");
        }

        int maxPayload = GetMaxFragmentPayloadBytes(offer.MaxDatagramBytes);
        int fragments = checked((offer.MaxFrameBytes + maxPayload - 1) / maxPayload);
        if (fragments > MaxFragmentCount)
        {
            throw new InvalidDataException("低延迟画面分片数量超出限制。");
        }
    }

    public static byte[] CreateJpegFramePayload(
        int width,
        int height,
        double captureMilliseconds,
        double encodeMilliseconds,
        ReadOnlyMemory<byte> jpegBytes)
    {
        int frameLength = checked(RemoteMessageCodec.FrameHeaderLength + jpegBytes.Length);
        if (frameLength > DefaultMaxFrameBytes)
        {
            throw new InvalidDataException("低延迟画面帧过大。");
        }

        byte[] payload = new byte[frameLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), height);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(8), captureMilliseconds);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(16), encodeMilliseconds);
        jpegBytes.CopyTo(payload.AsMemory(RemoteMessageCodec.FrameHeaderLength));
        return payload;
    }

    internal static bool TryReadAuthenticatedHeader(
        ReadOnlySpan<byte> datagram,
        ulong channelId,
        uint epoch,
        out LowLatencyVideoDatagramKind kind,
        out ulong packetSequence,
        out int payloadLength)
    {
        kind = default;
        packetSequence = 0;
        payloadLength = 0;
        if (datagram.Length < HeaderLength + TagLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(datagram) != Magic ||
            datagram[4] != Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[6..]) != HeaderLength ||
            BinaryPrimitives.ReadUInt64LittleEndian(datagram[8..]) != channelId ||
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[16..]) != epoch)
        {
            return false;
        }

        kind = (LowLatencyVideoDatagramKind)datagram[5];
        if (!Enum.IsDefined(kind))
        {
            return false;
        }

        packetSequence = BinaryPrimitives.ReadUInt64LittleEndian(datagram[20..]);
        payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(datagram[48..]);
        return payloadLength <= datagram.Length - HeaderLength - TagLength &&
            datagram.Length == HeaderLength + payloadLength + TagLength;
    }

    internal static void WriteHeader(
        Span<byte> header,
        LowLatencyVideoDatagramKind kind,
        ulong channelId,
        uint epoch,
        ulong packetSequence,
        ulong frameSequence,
        int frameLength,
        int fragmentOffset,
        ushort fragmentIndex,
        ushort fragmentCount,
        int payloadLength,
        MessageType frameKind,
        byte flags)
    {
        if (header.Length < HeaderLength ||
            payloadLength is < 0 or > ushort.MaxValue ||
            frameLength < 0 ||
            fragmentOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(header));
        }

        header[..HeaderLength].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        header[4] = Version;
        header[5] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], HeaderLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], channelId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], epoch);
        BinaryPrimitives.WriteUInt64LittleEndian(header[20..], packetSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(header[28..], frameSequence);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], frameLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], fragmentOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(header[44..], fragmentIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(header[46..], fragmentCount);
        BinaryPrimitives.WriteUInt16LittleEndian(header[48..], checked((ushort)payloadLength));
        header[50] = (byte)frameKind;
        header[51] = flags;
    }

    internal static LowLatencyVideoDatagram ParseDecryptedDatagram(
        ReadOnlySpan<byte> header,
        Memory<byte> plaintext)
    {
        return new LowLatencyVideoDatagram(
            (LowLatencyVideoDatagramKind)header[5],
            BinaryPrimitives.ReadUInt64LittleEndian(header[20..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[28..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[36..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[40..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[44..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[46..]),
            (MessageType)header[50],
            header[51],
            plaintext);
    }

    internal static void WriteNonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, ulong sequence)
    {
        if (nonce.Length != 12 || prefix.Length != NoncePrefixLength)
        {
            throw new ArgumentException("低延迟画面 nonce 长度异常。");
        }

        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[NoncePrefixLength..], sequence);
    }
}

internal sealed class LowLatencyVideoSendCipher : IDisposable
{
    private readonly object _lock = new();
    private readonly AesGcm _cipher;
    private readonly byte[] _noncePrefix;
    private readonly ulong _channelId;
    private readonly uint _epoch;
    private ulong _nextSequence;
    private bool _disposed;

    public LowLatencyVideoSendCipher(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> noncePrefix,
        ulong channelId,
        uint epoch)
    {
        if (key.Length != LowLatencyVideoProtocol.KeyLength ||
            noncePrefix.Length != LowLatencyVideoProtocol.NoncePrefixLength)
        {
            throw new ArgumentException("低延迟画面密钥长度异常。");
        }

        _cipher = new AesGcm(key, LowLatencyVideoProtocol.TagLength);
        _noncePrefix = noncePrefix.ToArray();
        _channelId = channelId;
        _epoch = epoch;
    }

    public byte[] Encrypt(
        LowLatencyVideoDatagramKind kind,
        ulong frameSequence,
        int frameLength,
        int fragmentOffset,
        ushort fragmentIndex,
        ushort fragmentCount,
        MessageType frameKind,
        byte flags,
        ReadOnlySpan<byte> plaintext)
    {
        byte[] datagram = new byte[
            LowLatencyVideoProtocol.HeaderLength +
            plaintext.Length +
            LowLatencyVideoProtocol.TagLength];
        int written = EncryptInto(
            datagram,
            kind,
            frameSequence,
            frameLength,
            fragmentOffset,
            fragmentIndex,
            fragmentCount,
            frameKind,
            flags,
            plaintext);
        Debug.Assert(written == datagram.Length);
        return datagram;
    }

    public int EncryptInto(
        Span<byte> datagram,
        LowLatencyVideoDatagramKind kind,
        ulong frameSequence,
        int frameLength,
        int fragmentOffset,
        ushort fragmentIndex,
        ushort fragmentCount,
        MessageType frameKind,
        byte flags,
        ReadOnlySpan<byte> plaintext)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_nextSequence == ulong.MaxValue)
            {
                throw new CryptographicException("低延迟画面包序号已耗尽。");
            }

            ulong sequence = _nextSequence++;
            int datagramLength =
                LowLatencyVideoProtocol.HeaderLength +
                plaintext.Length +
                LowLatencyVideoProtocol.TagLength;
            if (datagram.Length < datagramLength)
            {
                throw new ArgumentException("低延迟画面数据报缓冲区过小。", nameof(datagram));
            }

            Span<byte> output = datagram[..datagramLength];
            Span<byte> header = output[..LowLatencyVideoProtocol.HeaderLength];
            LowLatencyVideoProtocol.WriteHeader(
                header,
                kind,
                _channelId,
                _epoch,
                sequence,
                frameSequence,
                frameLength,
                fragmentOffset,
                fragmentIndex,
                fragmentCount,
                plaintext.Length,
                frameKind,
                flags);
            Span<byte> nonce = stackalloc byte[12];
            LowLatencyVideoProtocol.WriteNonce(nonce, _noncePrefix, sequence);
            Span<byte> ciphertext = output.Slice(
                LowLatencyVideoProtocol.HeaderLength,
                plaintext.Length);
            Span<byte> tag = output.Slice(
                LowLatencyVideoProtocol.HeaderLength + plaintext.Length,
                LowLatencyVideoProtocol.TagLength);
            _cipher.Encrypt(nonce, plaintext, ciphertext, tag, header);
            return datagramLength;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cipher.Dispose();
            CryptographicOperations.ZeroMemory(_noncePrefix);
        }
    }
}

internal sealed class LowLatencyVideoReceiveCipher : IDisposable
{
    private readonly AesGcm _cipher;
    private readonly byte[] _noncePrefix;
    private readonly ulong _channelId;
    private readonly uint _epoch;
    private readonly LowLatencyVideoReplayWindow _replayWindow = new();
    private bool _disposed;

    public LowLatencyVideoReceiveCipher(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> noncePrefix,
        ulong channelId,
        uint epoch)
    {
        if (key.Length != LowLatencyVideoProtocol.KeyLength ||
            noncePrefix.Length != LowLatencyVideoProtocol.NoncePrefixLength)
        {
            throw new ArgumentException("低延迟画面密钥长度异常。");
        }

        _cipher = new AesGcm(key, LowLatencyVideoProtocol.TagLength);
        _noncePrefix = noncePrefix.ToArray();
        _channelId = channelId;
        _epoch = epoch;
    }

    public bool TryDecrypt(ReadOnlySpan<byte> datagram, out LowLatencyVideoDatagram packet)
    {
        packet = default;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!LowLatencyVideoProtocol.TryReadAuthenticatedHeader(
                datagram,
                _channelId,
                _epoch,
                out _,
                out _,
                out int payloadLength))
        {
            return false;
        }

        byte[] plaintext = new byte[payloadLength];
        return TryDecrypt(datagram, plaintext, out packet);
    }

    public bool TryDecrypt(
        ReadOnlySpan<byte> datagram,
        Memory<byte> plaintextBuffer,
        out LowLatencyVideoDatagram packet)
    {
        packet = default;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!LowLatencyVideoProtocol.TryReadAuthenticatedHeader(
                datagram,
                _channelId,
                _epoch,
                out _,
                out ulong sequence,
                out int payloadLength) ||
            plaintextBuffer.Length < payloadLength ||
            !_replayWindow.WouldAccept(sequence))
        {
            return false;
        }

        Memory<byte> plaintext = plaintextBuffer[..payloadLength];
        Span<byte> nonce = stackalloc byte[12];
        LowLatencyVideoProtocol.WriteNonce(nonce, _noncePrefix, sequence);
        ReadOnlySpan<byte> header = datagram[..LowLatencyVideoProtocol.HeaderLength];
        ReadOnlySpan<byte> ciphertext = datagram.Slice(
            LowLatencyVideoProtocol.HeaderLength,
            payloadLength);
        ReadOnlySpan<byte> tag = datagram.Slice(
            LowLatencyVideoProtocol.HeaderLength + payloadLength,
            LowLatencyVideoProtocol.TagLength);
        try
        {
            _cipher.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext.Span,
                header);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext.Span);
            return false;
        }

        if (!_replayWindow.TryCommit(sequence))
        {
            CryptographicOperations.ZeroMemory(plaintext.Span);
            return false;
        }

        packet = LowLatencyVideoProtocol.ParseDecryptedDatagram(header, plaintext);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cipher.Dispose();
        CryptographicOperations.ZeroMemory(_noncePrefix);
    }
}

internal sealed class LowLatencyVideoReplayWindow
{
    private const int WordBits = 64;
    private readonly ulong[] _seen = new ulong[
        (LowLatencyVideoProtocol.ReplayWindowPackets +
            WordBits - 1) /
        WordBits];
    private ulong _highest;
    private bool _initialized;

    public bool WouldAccept(ulong sequence)
    {
        if (!_initialized || sequence > _highest)
        {
            return true;
        }

        return !IsTooOld(sequence) &&
            !IsMarked(sequence);
    }

    public bool TryCommit(ulong sequence)
    {
        if (!WouldAccept(sequence))
        {
            return false;
        }

        if (!_initialized)
        {
            _highest = sequence;
            _initialized = true;
            Mark(sequence);
            return true;
        }

        if (sequence > _highest)
        {
            ulong advance = sequence - _highest;
            if (advance >=
                (ulong)LowLatencyVideoProtocol.ReplayWindowPackets)
            {
                Array.Clear(_seen);
            }
            else
            {
                // Each newly admitted sequence reuses the circular slot of
                // the packet that just left the replay window. Clearing only
                // those slots avoids HashSet growth and a periodic 8K-entry
                // scan on the latency-critical UDP receive thread.
                for (ulong offset = 1;
                    offset <= advance;
                    offset++)
                {
                    Clear(_highest + offset);
                }
            }

            _highest = sequence;
        }

        Mark(sequence);
        return true;
    }

    private bool IsTooOld(ulong sequence)
    {
        return _initialized &&
            sequence <= _highest &&
            _highest - sequence >= LowLatencyVideoProtocol.ReplayWindowPackets;
    }

    private bool IsMarked(ulong sequence)
    {
        (int wordIndex, ulong mask) = GetBit(sequence);
        return (_seen[wordIndex] & mask) != 0;
    }

    private void Mark(ulong sequence)
    {
        (int wordIndex, ulong mask) = GetBit(sequence);
        _seen[wordIndex] |= mask;
    }

    private void Clear(ulong sequence)
    {
        (int wordIndex, ulong mask) = GetBit(sequence);
        _seen[wordIndex] &= ~mask;
    }

    private static (int WordIndex, ulong Mask) GetBit(
        ulong sequence)
    {
        int slot = checked((int)(
            sequence %
            (ulong)LowLatencyVideoProtocol.ReplayWindowPackets));
        return (
            slot / WordBits,
            1UL << (slot % WordBits));
    }
}

internal sealed class LowLatencyVideoFrameReassembler
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromMilliseconds(250);

    private readonly int _maxFrameBytes;
    private readonly int _maxFragmentPayloadBytes;
    private readonly bool _enableXorFec;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly ArrayPool<bool> _fragmentStatePool;
    private byte[]? _buffer;
    private bool[]? _receivedFragments;
    private byte[]?[]? _xorParityByGroup;
    private bool[]? _receivedXorParityGroups;
    private ulong _frameSequence;
    private ulong _highestCompletedSequence;
    private int _frameLength;
    private ushort _fragmentCount;
    private MessageType _frameKind;
    private byte _flags;
    private int _receivedCount;
    private long _startedAt;
    private long _completedFrameCount;
    private long _abandonedIncompleteFrameCount;
    private long _recoveredFragmentCount;
    private bool _hasCurrentFrame;
    private bool _hasCompletedFrame;

    public LowLatencyVideoFrameReassembler(
        int maxFrameBytes,
        int maxDatagramBytes,
        bool enableXorFec = false,
        ArrayPool<byte>? bufferPool = null)
    {
        if (maxFrameBytes is < RemoteMessageCodec.FrameHeaderLength or > LowLatencyVideoProtocol.DefaultMaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        }

        _maxFrameBytes = maxFrameBytes;
        _maxFragmentPayloadBytes = LowLatencyVideoProtocol.GetMaxFragmentPayloadBytes(maxDatagramBytes);
        _enableXorFec = enableXorFec;
        _bufferPool = bufferPool ?? ArrayPool<byte>.Shared;
        _fragmentStatePool = ArrayPool<bool>.Shared;
    }

    public long CompletedFrameCount =>
        Interlocked.Read(ref _completedFrameCount);

    public long AbandonedIncompleteFrameCount =>
        Interlocked.Read(ref _abandonedIncompleteFrameCount);

    public long RecoveredFragmentCount =>
        Interlocked.Read(ref _recoveredFragmentCount);

    internal bool HasCurrentFrame => _hasCurrentFrame;

    internal ulong CurrentFrameSequence => _frameSequence;

    internal bool CanProcessPacket(LowLatencyVideoDatagram packet)
    {
        return packet.Kind switch
        {
            LowLatencyVideoDatagramKind.FrameFragment =>
                IsValidFrameFragment(packet),
            LowLatencyVideoDatagramKind.FrameXorParity when _enableXorFec =>
                IsValidXorParity(packet),
            _ => false
        };
    }

    public bool TryAdd(
        LowLatencyVideoDatagram packet,
        out MessageType frameKind,
        out byte[]? completedPayload)
    {
        MessageType localFrameKind = default;
        byte[]? localCompletedPayload = null;
        bool completed = TryAddAndPublish(
            packet,
            (_, kind, payload) =>
            {
                localFrameKind = kind;
                localCompletedPayload = payload.ToArray();
            });
        frameKind = localFrameKind;
        completedPayload = localCompletedPayload;
        return completed;
    }

    /// <summary>
    /// Publishes a completed access unit synchronously. The payload is borrowed
    /// only for the duration of <paramref name="publish"/> and must be copied
    /// by a subscriber that needs to retain it.
    /// </summary>
    public bool TryAddAndPublish(
        LowLatencyVideoDatagram packet,
        Action<ulong, MessageType, ReadOnlyMemory<byte>> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        CompletedFrame completedFrame = default;
        bool completed = packet.Kind switch
        {
            LowLatencyVideoDatagramKind.FrameFragment =>
                TryAddFrameFragment(
                    packet,
                    out completedFrame),
            LowLatencyVideoDatagramKind.FrameXorParity when _enableXorFec =>
                TryAddXorParity(
                    packet,
                    out completedFrame),
            _ => false
        };
        if (!completed)
        {
            return false;
        }

        try
        {
            publish(
                completedFrame.Sequence,
                completedFrame.Kind,
                completedFrame.Buffer.AsMemory(
                    0,
                    completedFrame.Length));
        }
        finally
        {
            ReturnBuffer(
                completedFrame.Buffer,
                completedFrame.Length);
        }

        return true;
    }

    public void Reset()
    {
        ResetCurrentFrame(countAbandoned: true);
    }

    private bool TryAddFrameFragment(
        LowLatencyVideoDatagram packet,
        out CompletedFrame completedFrame)
    {
        completedFrame = default;
        if (!IsValidFrameFragment(packet))
        {
            return false;
        }

        if (!TrySelectFrame(packet))
        {
            return false;
        }

        bool[] received = _receivedFragments!;
        if (received[packet.FragmentIndex])
        {
            return false;
        }

        try
        {
            packet.Plaintext.Span.CopyTo(
                _buffer!.AsSpan(packet.FragmentOffset));
            received[packet.FragmentIndex] = true;
            _receivedCount++;
            if (_enableXorFec)
            {
                TryRecoverXorFecGroup(
                    packet.FragmentIndex /
                    LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup);
            }
        }
        catch
        {
            ResetCurrentFrame(countAbandoned: true);
            throw;
        }

        return TryCompleteCurrentFrame(out completedFrame);
    }

    private bool TryAddXorParity(
        LowLatencyVideoDatagram packet,
        out CompletedFrame completedFrame)
    {
        completedFrame = default;
        if (!IsValidXorParity(packet))
        {
            return false;
        }

        int groupStartFragment = checked(
            packet.FragmentIndex *
            LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup);
        int expectedOffset = checked(
            groupStartFragment * _maxFragmentPayloadBytes);
        int expectedLength = Math.Min(
            _maxFragmentPayloadBytes,
            packet.FrameLength - expectedOffset);
        if (!TrySelectFrame(packet))
        {
            return false;
        }

        bool[] receivedParity = _receivedXorParityGroups!;
        int groupIndex = packet.FragmentIndex;
        if (receivedParity[groupIndex])
        {
            return false;
        }

        byte[] parity = _bufferPool.Rent(expectedLength);
        if (parity.Length < expectedLength)
        {
            ReturnParityBuffer(parity);
            throw new InvalidOperationException(
                "The byte array pool returned a parity buffer smaller " +
                "than the requested payload.");
        }

        try
        {
            packet.Plaintext.Span.CopyTo(parity);
        }
        catch
        {
            ReturnParityBuffer(parity);
            ResetCurrentFrame(countAbandoned: true);
            throw;
        }

        receivedParity[groupIndex] = true;
        _xorParityByGroup![groupIndex] = parity;
        try
        {
            TryRecoverXorFecGroup(groupIndex);
        }
        catch
        {
            ResetCurrentFrame(countAbandoned: true);
            throw;
        }

        return TryCompleteCurrentFrame(out completedFrame);
    }

    private bool IsValidFrameFragment(
        LowLatencyVideoDatagram packet)
    {
        if (packet.Flags != 0 ||
            packet.FrameKind is not (MessageType.Frame or MessageType.VideoFrame) ||
            packet.FrameSequence > long.MaxValue ||
            packet.FrameLength <= 0 ||
            packet.FrameLength > _maxFrameBytes ||
            packet.FragmentCount == 0 ||
            packet.FragmentCount > LowLatencyVideoProtocol.MaxFragmentCount ||
            packet.FragmentIndex >= packet.FragmentCount ||
            packet.FragmentOffset < 0)
        {
            return false;
        }

        int expectedFragmentCount = checked(
            (packet.FrameLength + _maxFragmentPayloadBytes - 1) /
            _maxFragmentPayloadBytes);
        int expectedOffset = checked(
            packet.FragmentIndex * _maxFragmentPayloadBytes);
        int expectedLength = Math.Min(
            _maxFragmentPayloadBytes,
            packet.FrameLength - expectedOffset);
        return
            packet.FragmentCount == expectedFragmentCount &&
            packet.FragmentOffset == expectedOffset &&
            expectedLength > 0 &&
            packet.Plaintext.Length == expectedLength;
    }

    private bool IsValidXorParity(
        LowLatencyVideoDatagram packet)
    {
        if (packet.Flags != 0 ||
            packet.FrameKind is not (MessageType.Frame or MessageType.VideoFrame) ||
            packet.FrameSequence > long.MaxValue ||
            packet.FrameLength <= 0 ||
            packet.FrameLength > _maxFrameBytes ||
            packet.FragmentCount == 0 ||
            packet.FragmentCount > LowLatencyVideoProtocol.MaxFragmentCount)
        {
            return false;
        }

        int expectedFragmentCount = checked(
            (packet.FrameLength + _maxFragmentPayloadBytes - 1) /
            _maxFragmentPayloadBytes);
        if (expectedFragmentCount > LowLatencyVideoProtocol.MaxFragmentCount)
        {
            return false;
        }

        int groupCount = LowLatencyVideoProtocol.GetXorFecGroupCount(
            expectedFragmentCount);
        if (packet.FragmentCount != expectedFragmentCount ||
            packet.FragmentIndex >= groupCount)
        {
            return false;
        }

        int groupStartFragment = checked(
            packet.FragmentIndex *
            LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup);
        int expectedOffset = checked(
            groupStartFragment * _maxFragmentPayloadBytes);
        int expectedLength = Math.Min(
            _maxFragmentPayloadBytes,
            packet.FrameLength - expectedOffset);
        return
            packet.FragmentOffset == expectedOffset &&
            expectedLength > 0 &&
            packet.Plaintext.Length == expectedLength;
    }

    private void StartFrame(LowLatencyVideoDatagram packet)
    {
        ResetCurrentFrame(countAbandoned: true);
        bool[]? receivedFragments = null;
        byte[]? frameBuffer = null;
        try
        {
            receivedFragments =
                _fragmentStatePool.Rent(packet.FragmentCount);
            receivedFragments.AsSpan(
                    0,
                    packet.FragmentCount)
                .Clear();
            frameBuffer = _bufferPool.Rent(packet.FrameLength);
            if (frameBuffer.Length < packet.FrameLength)
            {
                throw new InvalidOperationException(
                    "The byte array pool returned a frame buffer smaller " +
                    "than the requested payload.");
            }

            if (_enableXorFec)
            {
                int groupCount = LowLatencyVideoProtocol.GetXorFecGroupCount(
                    packet.FragmentCount);
                _xorParityByGroup = new byte[]?[groupCount];
                _receivedXorParityGroups = new bool[groupCount];
            }

            _buffer = frameBuffer;
            frameBuffer = null;
            _receivedFragments = receivedFragments;
            receivedFragments = null;
            _frameSequence = packet.FrameSequence;
            _frameLength = packet.FrameLength;
            _fragmentCount = packet.FragmentCount;
            _frameKind = packet.FrameKind;
            _flags = packet.Flags;
            _receivedCount = 0;
            _startedAt = Stopwatch.GetTimestamp();
            _hasCurrentFrame = true;
        }
        catch
        {
            if (frameBuffer is not null)
            {
                ReturnBuffer(
                    frameBuffer,
                    Math.Min(
                        packet.FrameLength,
                        frameBuffer.Length));
            }

            if (receivedFragments is not null)
            {
                receivedFragments.AsSpan(
                        0,
                        Math.Min(
                            packet.FragmentCount,
                            receivedFragments.Length))
                    .Clear();
                _fragmentStatePool.Return(
                    receivedFragments,
                    clearArray: false);
            }

            ClearAllXorParityGroups();
            throw;
        }
    }

    private bool TrySelectFrame(LowLatencyVideoDatagram packet)
    {
        ExpireTimedOutFrame();
        if ((_hasCompletedFrame && packet.FrameSequence <= _highestCompletedSequence) ||
            (_hasCurrentFrame && packet.FrameSequence < _frameSequence))
        {
            return false;
        }

        if (!_hasCurrentFrame || packet.FrameSequence > _frameSequence)
        {
            StartFrame(packet);
        }
        else if (packet.FrameLength != _frameLength ||
            packet.FragmentCount != _fragmentCount ||
            packet.FrameKind != _frameKind ||
            packet.Flags != _flags)
        {
            return false;
        }

        return true;
    }

    private void TryRecoverXorFecGroup(int groupIndex)
    {
        byte[]? parity = _xorParityByGroup?[groupIndex];
        if (parity is null)
        {
            return;
        }

        int firstFragment = checked(
            groupIndex * LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup);
        int endFragment = Math.Min(
            _fragmentCount,
            firstFragment + LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup);
        int missingFragment = -1;
        int missingCount = 0;
        for (int fragmentIndex = firstFragment;
            fragmentIndex < endFragment;
            fragmentIndex++)
        {
            if (!_receivedFragments![fragmentIndex])
            {
                missingFragment = fragmentIndex;
                missingCount++;
                if (missingCount > 1)
                {
                    return;
                }
            }
        }

        if (missingCount == 0)
        {
            ClearXorParityGroup(groupIndex);
            return;
        }

        for (int fragmentIndex = firstFragment;
            fragmentIndex < endFragment;
            fragmentIndex++)
        {
            if (fragmentIndex == missingFragment)
            {
                continue;
            }

            int offset = checked(fragmentIndex * _maxFragmentPayloadBytes);
            int length = Math.Min(
                _maxFragmentPayloadBytes,
                _frameLength - offset);
            ReadOnlySpan<byte> source = _buffer!.AsSpan(offset, length);
            for (int index = 0; index < source.Length; index++)
            {
                parity[index] ^= source[index];
            }
        }

        int missingOffset = checked(
            missingFragment * _maxFragmentPayloadBytes);
        int missingLength = Math.Min(
            _maxFragmentPayloadBytes,
            _frameLength - missingOffset);
        int parityLength = Math.Min(
            _maxFragmentPayloadBytes,
            _frameLength -
            (firstFragment * _maxFragmentPayloadBytes));
        ReadOnlySpan<byte> padding = parity.AsSpan(
            Math.Min(missingLength, parityLength),
            Math.Max(0, parityLength - missingLength));
        if (missingLength > parityLength ||
            (!padding.IsEmpty && !IsAllZero(padding)))
        {
            ClearXorParityGroup(groupIndex);
            return;
        }

        parity.AsSpan(0, missingLength).CopyTo(
            _buffer!.AsSpan(missingOffset, missingLength));
        _receivedFragments![missingFragment] = true;
        _receivedCount++;
        Interlocked.Increment(ref _recoveredFragmentCount);
        ClearXorParityGroup(groupIndex);
    }

    private bool TryCompleteCurrentFrame(
        out CompletedFrame completedFrame)
    {
        completedFrame = default;
        if (!_hasCurrentFrame || _receivedCount != _fragmentCount)
        {
            return false;
        }

        byte[] completedBuffer = _buffer!;
        completedFrame = new(
            completedBuffer,
            _frameLength,
            _frameKind,
            _frameSequence);
        _buffer = null;
        _highestCompletedSequence = _frameSequence;
        _hasCompletedFrame = true;
        Interlocked.Increment(ref _completedFrameCount);
        ResetCurrentFrame(countAbandoned: false);
        return true;
    }

    internal void ExpireTimedOutFrame()
    {
        if (_hasCurrentFrame &&
            Stopwatch.GetElapsedTime(_startedAt) >= FrameTimeout)
        {
            ResetCurrentFrame(countAbandoned: true);
        }
    }

    private void ResetCurrentFrame(bool countAbandoned)
    {
        if (countAbandoned && _hasCurrentFrame &&
            _receivedCount < _fragmentCount)
        {
            Interlocked.Increment(ref _abandonedIncompleteFrameCount);
        }

        if (_buffer is not null)
        {
            ReturnBuffer(_buffer, _frameLength);
        }

        ClearAllXorParityGroups();
        _buffer = null;
        if (_receivedFragments is not null)
        {
            _receivedFragments.AsSpan(
                    0,
                    Math.Min(
                        _fragmentCount,
                        _receivedFragments.Length))
                .Clear();
            _fragmentStatePool.Return(
                _receivedFragments,
                clearArray: false);
        }

        _receivedFragments = null;
        _frameLength = 0;
        _fragmentCount = 0;
        _receivedCount = 0;
        _startedAt = 0;
        _hasCurrentFrame = false;
    }

    private void ClearXorParityGroup(int groupIndex)
    {
        byte[]? parity = _xorParityByGroup?[groupIndex];
        if (parity is null)
        {
            return;
        }

        ReturnParityBuffer(parity);
        _xorParityByGroup![groupIndex] = null;
    }

    private void ClearAllXorParityGroups()
    {
        if (_xorParityByGroup is not null)
        {
            for (int index = 0; index < _xorParityByGroup.Length; index++)
            {
                ClearXorParityGroup(index);
            }
        }

        _xorParityByGroup = null;
        _receivedXorParityGroups = null;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private void ReturnBuffer(
        byte[] buffer,
        int validLength)
    {
        if (validLength > 0)
        {
            CryptographicOperations.ZeroMemory(
                buffer.AsSpan(
                    0,
                    Math.Min(validLength, buffer.Length)));
        }

        _bufferPool.Return(buffer, clearArray: false);
    }

    private void ReturnParityBuffer(byte[] buffer)
    {
        // ArrayPool can return a bucket larger than the requested parity
        // payload. Clear the full lease: otherwise unused tail bytes from a
        // previous renter remain observable and make reset zeroization
        // dependent on pool history.
        CryptographicOperations.ZeroMemory(buffer);
        _bufferPool.Return(buffer, clearArray: false);
    }

    private readonly record struct CompletedFrame(
        byte[] Buffer,
        int Length,
        MessageType Kind,
        ulong Sequence);
}

/// <summary>
/// Keeps the two newest in-flight access units so a fragment or XOR parity
/// datagram that crosses one frame boundary does not evict the preceding
/// frame. A newer completed frame is still published immediately; this is a
/// reorder window, not a playback queue.
/// </summary>
internal sealed class LowLatencyVideoAdjacentFrameReassembler
{
    private readonly LowLatencyVideoFrameReassembler _first;
    private readonly LowLatencyVideoFrameReassembler _second;
    private readonly Action<
        ulong,
        MessageType,
        ReadOnlyMemory<byte>> _publish;
    private readonly Action<
        ulong,
        MessageType,
        ReadOnlyMemory<byte>> _publishFirstCompleted;
    private readonly Action<
        ulong,
        MessageType,
        ReadOnlyMemory<byte>> _publishSecondCompleted;
    private LowLatencyVideoFrameReassembler _older;
    private LowLatencyVideoFrameReassembler _newer;
    private ulong _highestPublishedSequence;
    private bool _hasPublishedFrame;
    private long _windowEvictionCount;

    public LowLatencyVideoAdjacentFrameReassembler(
        int maxFrameBytes,
        int maxDatagramBytes,
        Action<ulong, MessageType, ReadOnlyMemory<byte>> publish,
        bool enableXorFec = false,
        ArrayPool<byte>? bufferPool = null)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _first = new LowLatencyVideoFrameReassembler(
            maxFrameBytes,
            maxDatagramBytes,
            enableXorFec,
            bufferPool);
        _second = new LowLatencyVideoFrameReassembler(
            maxFrameBytes,
            maxDatagramBytes,
            enableXorFec,
            bufferPool);
        _older = _first;
        _newer = _second;
        _publish = publish;
        _publishFirstCompleted = PublishFirstCompleted;
        _publishSecondCompleted = PublishSecondCompleted;
    }

    public long CompletedFrameCount =>
        _first.CompletedFrameCount +
        _second.CompletedFrameCount;

    public long AbandonedIncompleteFrameCount =>
        _first.AbandonedIncompleteFrameCount +
        _second.AbandonedIncompleteFrameCount;

    public long RecoveredFragmentCount =>
        _first.RecoveredFragmentCount +
        _second.RecoveredFragmentCount;

    public long WindowEvictionCount =>
        Interlocked.Read(ref _windowEvictionCount);

    public bool TryAdd(LowLatencyVideoDatagram packet)
    {
        if (!_older.CanProcessPacket(packet))
        {
            return false;
        }

        PrepareSlots();
        if (_hasPublishedFrame &&
            packet.FrameSequence <= _highestPublishedSequence)
        {
            return false;
        }

        LowLatencyVideoFrameReassembler target;
        if (!_older.HasCurrentFrame ||
            packet.FrameSequence == _older.CurrentFrameSequence)
        {
            target = _older;
        }
        else if (!_newer.HasCurrentFrame ||
            packet.FrameSequence == _newer.CurrentFrameSequence)
        {
            target = _newer;
        }
        else if (packet.FrameSequence < _older.CurrentFrameSequence)
        {
            return false;
        }
        else
        {
            // The packet belongs to a third distinct frame. Keep the two
            // newest sequences and recycle the oldest partial frame.
            _older.Reset();
            Interlocked.Increment(ref _windowEvictionCount);
            target = _older;
        }

        try
        {
            return target.TryAddAndPublish(
                packet,
                ReferenceEquals(target, _first)
                    ? _publishFirstCompleted
                    : _publishSecondCompleted);
        }
        finally
        {
            PrepareSlots();
        }
    }

    public void Reset()
    {
        _older.Reset();
        _newer.Reset();
    }

    private void PublishFirstCompleted(
        ulong sequence,
        MessageType kind,
        ReadOnlyMemory<byte> payload)
    {
        PublishCompleted(
            _first,
            sequence,
            kind,
            payload);
    }

    private void PublishSecondCompleted(
        ulong sequence,
        MessageType kind,
        ReadOnlyMemory<byte> payload)
    {
        PublishCompleted(
            _second,
            sequence,
            kind,
            payload);
    }

    private void PublishCompleted(
        LowLatencyVideoFrameReassembler source,
        ulong sequence,
        MessageType kind,
        ReadOnlyMemory<byte> payload)
    {
        if (_hasPublishedFrame &&
            sequence <= _highestPublishedSequence)
        {
            return;
        }

        LowLatencyVideoFrameReassembler other =
            ReferenceEquals(source, _older)
                ? _newer
                : _older;
        if (other.HasCurrentFrame &&
            other.CurrentFrameSequence < sequence)
        {
            // A full newer frame is the latency boundary: stop waiting for
            // the older partial frame before invoking the viewer callback,
            // and reject its late tail.
            other.Reset();
        }

        _highestPublishedSequence = sequence;
        _hasPublishedFrame = true;
        _publish(sequence, kind, payload);
    }

    private void PrepareSlots()
    {
        _older.ExpireTimedOutFrame();
        _newer.ExpireTimedOutFrame();
        DiscardSupersededFrame(_older);
        DiscardSupersededFrame(_newer);

        if (!_older.HasCurrentFrame &&
            _newer.HasCurrentFrame)
        {
            SwapSlots();
        }
        else if (_older.HasCurrentFrame &&
            _newer.HasCurrentFrame)
        {
            if (_older.CurrentFrameSequence ==
                _newer.CurrentFrameSequence)
            {
                _newer.Reset();
            }
            else if (_older.CurrentFrameSequence >
                _newer.CurrentFrameSequence)
            {
                SwapSlots();
            }
        }
    }

    private void DiscardSupersededFrame(
        LowLatencyVideoFrameReassembler reassembler)
    {
        if (_hasPublishedFrame &&
            reassembler.HasCurrentFrame &&
            reassembler.CurrentFrameSequence <=
                _highestPublishedSequence)
        {
            reassembler.Reset();
        }
    }

    private void SwapSlots()
    {
        (_older, _newer) = (_newer, _older);
    }
}
