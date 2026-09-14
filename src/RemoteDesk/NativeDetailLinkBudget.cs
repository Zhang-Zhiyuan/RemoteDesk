using RemoteDesk.NativeDetail;

namespace RemoteDesk;

// End-to-end receiver acknowledgements, not NetworkStream.WriteAsync timing:
// on a relay, a fast write only describes the local tunnel's socket buffer.
// All times below are host-local monotonic milliseconds. One small window
// bounds unsent/in-flight bases; optional traffic is default-deny.
internal sealed class NativeDetailLinkBudget
{
    private readonly object _gate = new();
    private readonly Queue<Sent> _history = new();
    private long _sentBytes, _ackedBytes, _lastAckAt, _lastSequence, _presented, _firstAckAt;
    private double _minimumRtt = double.MaxValue, _lastRtt, _rate;
    private int _samples;
    private long _captureStartedBytes;
    private long _rateWindowAt, _rateWindowBytes;
    private sealed record Sent(DetailContext Context, long Sequence, long Bytes, long At);

    internal long SentBytes { get { lock (_gate) return _sentBytes; } }
    internal long AcknowledgedBytes { get { lock (_gate) return _ackedBytes; } }

    internal void BeginCapture()
    {
        lock (_gate)
        {
            // Keep the connection's absolute wire counter, but do not let a
            // retired capture's missing final ACK deadlock a later re-enable.
            _history.Clear(); _captureStartedBytes = _sentBytes;
            _samples = 0; _rate = 0; _minimumRtt = double.MaxValue;
            _lastAckAt = _firstAckAt = _presented = 0;
        }
    }

    internal bool FeedbackStalled(long now)
    {
        lock (_gate) return _history.TryPeek(out var first) && now - first.At > 1500;
    }

    internal void RecordSent(DetailContext context, long sequence, int wireBytes, long now)
    {
        if (sequence <= 0 || wireBytes <= 0 || now < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        lock (_gate)
        {
            _sentBytes = checked(_sentBytes + wireBytes);
            _lastSequence = Math.Max(_lastSequence, sequence);
            _history.Enqueue(new(context, sequence, _sentBytes, now));
            while (_history.Count > 128) _history.Dequeue();
        }
    }

    internal bool Observe(NativeDetailFeedback feedback, long now)
    {
        lock (_gate)
        {
            if (now < _lastAckAt || feedback.ReceivedWireBytes <= _ackedBytes ||
                feedback.ReceivedWireBytes > _sentBytes || feedback.ReceivedSequence > _lastSequence ||
                feedback.PresentedSequence < 0 || feedback.PresentedSequence > feedback.ReceivedSequence)
                return false;
            Sent? exact = _history.FirstOrDefault(item => item.Bytes == feedback.ReceivedWireBytes &&
                item.Context == feedback.Context && item.Sequence == feedback.ReceivedSequence);
            if (exact is null || now < exact.At) return false;
            if (feedback.AcknowledgementDelayMilliseconds < 0 || feedback.AcknowledgementDelayMilliseconds > 500 ||
                feedback.AcknowledgementDelayMilliseconds > now - exact.At + 2) return false;
            _lastRtt = Math.Max(0, now - exact.At - feedback.AcknowledgementDelayMilliseconds);
            _minimumRtt = Math.Min(_minimumRtt, _lastRtt);
            if (_samples > 0 && now - _rateWindowAt >= 300)
            {
                // A coalesced ACK may contain only one 512-byte fragment. Use
                // a sustained window, not that single tiny ACK interval, or
                // transmitting detail would repeatedly collapse its own rate.
                double rate = (feedback.ReceivedWireBytes - _rateWindowBytes) * 8000d / (now - _rateWindowAt);
                // A sudden ACK burst must not grant burst-rate bandwidth.
                _rate = _rate == 0 ? rate : Math.Min(rate, _rate * 1.15);
                _rateWindowAt = now; _rateWindowBytes = feedback.ReceivedWireBytes;
            }
            if (_samples++ == 0)
            { _firstAckAt = _rateWindowAt = now; _rateWindowBytes = feedback.ReceivedWireBytes; }
            _lastAckAt = now; _ackedBytes = feedback.ReceivedWireBytes; _presented = feedback.PresentedSequence;
            while (_history.TryPeek(out var item) && item.Bytes <= _ackedBytes) _history.Dequeue();
            return true;
        }
    }

    internal bool CanSendBase(long now)
    {
        lock (_gate)
        {
            // Bootstrap is bounded too, even if the peer never sends ACKs.
            if (_history.Count >= 4 || _sentBytes - Math.Max(_ackedBytes, _captureStartedBytes) > 1024 * 1024) return false;
            return !_history.TryPeek(out var first) || now - first.At < Math.Max(120, _minimumRtt == double.MaxValue ? 120 : _minimumRtt + 80);
        }
    }

    internal const int SmallChunkWireBytes = 512 + DetailWire.ChunkHeaderBytes + DetailWire.OuterRecordBytes;
    internal long DetailBitsPerSecond(long now, int nextWireBytes = SmallChunkWireBytes)
    {
        lock (_gate)
        {
            if (_samples < 4 || now - _firstAckAt < 300 || now - _lastAckAt > 150 ||
                _lastRtt > _minimumRtt + 20 || _sentBytes - _ackedBytes > 128 * 1024 ||
                _lastSequence - _presented > 4 || _presented == 0 ||
                _rate <= 0 || nextWireBytes * 8000d / _rate > 2)
                return 0;
            return (long)Math.Min(1_000_000, _rate * .04);
        }
    }
}
