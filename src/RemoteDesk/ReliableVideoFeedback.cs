using System.Buffers.Binary;

namespace RemoteDesk;

// Negotiated TCP-only records. Sequence/cumulative wire bytes belong to one
// authenticated connection, independent of capture target and encoder restarts.
internal sealed record ReliableVideoReceipt(long Sequence, long WireBytes, int AckDelayMilliseconds = 0)
{
    internal long ReceivedAtTimestamp { get; init; }
}

internal static class ReliableVideoFeedbackProtocol
{
    internal const int SequenceBytes = 8;
    internal const int ReceiptBytes = 32;
    internal const int FrameOverhead = 25 + RemoteMessageCodec.VideoFrameHeaderLength + SequenceBytes;

    internal static byte[] EncodeReceipt(ReliableVideoReceipt receipt)
    {
        if (receipt.Sequence <= 0 || receipt.WireBytes <= 0 || receipt.AckDelayMilliseconds is < 0 or > 500)
            throw new InvalidDataException("Invalid video receipt.");
        byte[] bytes = new byte[ReceiptBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x31465652); // RVF1
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), receipt.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), receipt.WireBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), receipt.AckDelayMilliseconds);
        return bytes;
    }

    internal static ReliableVideoReceipt DecodeReceipt(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ReceiptBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x31465652 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]) != 0)
            throw new InvalidDataException("Invalid video receipt envelope.");
        var receipt = new ReliableVideoReceipt(BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]));
        if (receipt.Sequence <= 0 || receipt.WireBytes <= 0 || receipt.AckDelayMilliseconds is < 0 or > 500)
            throw new InvalidDataException("Invalid video receipt.");
        return receipt;
    }

    internal static (long Sequence, RemoteFrame Frame) DecodeFrame(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length <= SequenceBytes + RemoteMessageCodec.VideoFrameHeaderLength)
            throw new InvalidDataException("Incomplete tracked video frame.");
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
        if (sequence <= 0) throw new InvalidDataException("Invalid video sequence.");
        RemoteFrame frame = RemoteMessageCodec.DecodeVideoFrame(payload[SequenceBytes..]);
        if (frame.Encoding != RemoteFrameEncoding.H264AnnexB || !frame.Flags.HasFlag(RemoteFrameFlags.KeyFrame))
            throw new InvalidDataException("Tracked video requires independent H.264 frames.");
        return (sequence, frame);
    }
}

// Receiver-confirmed credit, not writes into the first relay hop. Keep capture
// running and discard unsent independent frames when credit is exhausted. No
// sleep, codec restart, quality reduction or delayed frame queue is introduced.
internal sealed class ReliableVideoSendWindow
{
    internal const int MaximumFrames = 128;
    internal const int MaximumWindowBytes = 8 * 1024 * 1024;
    internal const double QueueBudgetMilliseconds = 60;
    private readonly object _gate = new();
    private readonly Queue<Sent> _pending = new();
    private sealed record Sent(long Sequence, long TotalBytes, int Bytes, long At, bool StartedEmpty);
    private long _sequence, _sentBytes, _ackedBytes, _lastAckAt, _lastReductionAt = long.MinValue / 2;
    private double _windowBytes, _minimumRtt = double.MaxValue, _lastRtt, _nextSendAt;
    private int _isolatedSamples;
    private double _isolatedMinimum = double.MaxValue;
    private long _dropped;

    internal long PendingBytes { get { lock (_gate) return _sentBytes - _ackedBytes; } }
    internal int PendingFrames { get { lock (_gate) return _pending.Count; } }
    internal long DroppedFrames { get { lock (_gate) return _dropped; } }
    internal double LastRoundTripMilliseconds { get { lock (_gate) return _lastRtt; } }

    internal bool TryReserve(int bytes, long now, out long sequence)
    {
        if (bytes <= 0 || now < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            sequence = 0;
            if (_windowBytes == 0) _windowBytes = Math.Min(MaximumWindowBytes, bytes * 2d);
            if (_pending.Count == 0 && _lastAckAt > 0 && now - _lastAckAt > Math.Max(1000, _minimumRtt * 3))
                _windowBytes = Math.Min(_windowBytes, bytes * 2d);
            bool aged = _minimumRtt != double.MaxValue && _pending.TryPeek(out var first) &&
                now - first.At > _minimumRtt + QueueBudgetMilliseconds;
            // Spread credit over a round trip rather than emitting an ACK burst.
            // Admission never sleeps or holds a frame; the capture loop keeps
            // producing fresh independent frames. A previously idle stream may
            // send immediately, with no accumulated catch-up allowance.
            bool paced = _pending.Count > 0 && now < _nextSendAt;
            // Frames are indivisible: allow the last one to cross the soft
            // credit boundary, but never the hard byte/count bounds. Rounding
            // credit down to one whole frame can otherwise halve throughput.
            // One oversized keyframe may pass an empty window only.
            if (_pending.Count >= MaximumFrames ||
                (_minimumRtt == double.MaxValue && _pending.Count >= 2) || (_pending.Count > 0 &&
                (aged || paced || bytes > _windowBytes || _sentBytes - _ackedBytes >= _windowBytes ||
                 _sentBytes - _ackedBytes + (long)bytes > MaximumWindowBytes)))
            { _dropped++; return false; }
            _sentBytes = checked(_sentBytes + bytes);
            sequence = checked(++_sequence);
            _pending.Enqueue(new(sequence, _sentBytes, bytes, now, _pending.Count == 0));
            _nextSendAt = _minimumRtt == double.MaxValue ? now :
                now + Math.Min(_minimumRtt, bytes * _minimumRtt / Math.Max(bytes, _windowBytes));
            return true;
        }
    }

    internal bool Observe(ReliableVideoReceipt receipt, long now)
    {
        lock (_gate)
        {
            if (now < _lastAckAt || receipt.Sequence <= 0 || receipt.WireBytes <= _ackedBytes ||
                receipt.WireBytes > _sentBytes || receipt.AckDelayMilliseconds is < 0 or > 500) return false;
            Sent? exact = _pending.FirstOrDefault(x => x.Sequence == receipt.Sequence && x.TotalBytes == receipt.WireBytes);
            if (exact is null || now < exact.At || receipt.AckDelayMilliseconds > now - exact.At + 2) return false;
            _lastRtt = Math.Max(0, now - exact.At - receipt.AckDelayMilliseconds);
            _minimumRtt = Math.Min(_minimumRtt, _lastRtt);
            long confirmedBytes = receipt.WireBytes - _ackedBytes;
            bool congested = _lastRtt > _minimumRtt + QueueBudgetMilliseconds;
            // A slower path or a larger keyframe raises the irreducible
            // one-frame round trip too. Relearn only after three isolated
            // transmissions: all previous video was acknowledged before each
            // send, and no later video was queued. Never rebase from a backlog.
            if (congested && exact.StartedEmpty && _pending.Count == 1)
            {
                _isolatedMinimum = Math.Min(_isolatedMinimum, _lastRtt);
                if (++_isolatedSamples >= 3)
                {
                    _minimumRtt = _isolatedMinimum;
                    congested = _lastRtt > _minimumRtt + QueueBudgetMilliseconds;
                    // After proving the slower one-frame baseline, restore a
                    // two-frame pipeline rather than growing from stop/wait.
                    _windowBytes = Math.Min(MaximumWindowBytes, Math.Max(_windowBytes, exact.Bytes * 2d));
                    _isolatedSamples = 0; _isolatedMinimum = double.MaxValue;
                }
            }
            else { _isolatedSamples = 0; _isolatedMinimum = double.MaxValue; }
            if (congested)
            {
                // Coalesced/late ACKs from one congestion episode must not
                // collapse the window repeatedly before a round trip passes.
                if (now - _lastReductionAt >= Math.Max(100, _minimumRtt))
                {
                    _windowBytes = Math.Max(exact.Bytes, _windowBytes * .70);
                    _lastReductionAt = now;
                }
            }
            else
                _windowBytes = Math.Min(MaximumWindowBytes, _windowBytes + Math.Min(exact.Bytes, confirmedBytes * .5));
            _ackedBytes = receipt.WireBytes; _lastAckAt = now;
            while (_pending.TryPeek(out var item) && item.TotalBytes <= _ackedBytes) _pending.Dequeue();
            return true;
        }
    }
}
