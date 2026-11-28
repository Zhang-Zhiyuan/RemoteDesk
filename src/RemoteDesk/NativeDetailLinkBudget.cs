using RemoteDesk.NativeDetail;

namespace RemoteDesk;

// Spend extra credit only after independent receiver reports stay healthy.
// A quiet screen does not establish unused capacity; normal refinement still works.
internal sealed class NativeDetailHeadroom
{
    internal const long StandardLimit = 1_000_000;
    internal const long MinimumDelivery = 20_000_000;
    private long _since = -1, _last = -1;

    internal void Reset() => _since = _last = -1;

    internal void Observe(long now, bool healthy, long maximumGap)
    {
        if (!healthy) { Reset(); return; }
        if (_since < 0 || now < _last || now - _last > maximumGap) _since = now;
        _last = now;
    }

    internal long Rate(double delivered, long now, long maximumGap, bool allowBoost) =>
        allowBoost && _since >= 0 && _last - _since >= 2000 && now >= _last && now - _last <= maximumGap
            ? (long)Math.Min(3_000_000, delivered * .08)
            : (long)Math.Min(StandardLimit, delivered * .04);
}

// End-to-end receiver acknowledgements, not NetworkStream.WriteAsync timing:
// on a relay, a fast write only describes the local tunnel's socket buffer.
// All times below are host-local monotonic milliseconds. One small window
// bounds unsent/in-flight bases; optional traffic is default-deny.
internal sealed class NativeDetailLinkBudget
{
    private readonly object _gate = new();
    private readonly Queue<Sent> _history = new();
    private readonly Queue<(DetailContext Context, long Sequence)> _recentBases = new();
    private readonly NativeDetailHeadroom _headroom = new();
    private int _inFlightBases, _renderLag;
    private long _sentBytes, _ackedBytes, _lastAckAt, _lastSequence, _presented, _firstAckAt;
    private (DetailContext Context, long Sequence) _receivedBase;
    private double _minimumRtt = double.MaxValue, _minimumBaseRtt = double.MaxValue, _lastRtt, _rate;
    private int _samples;
    private long _captureStartedBytes;
    private long _rateWindowAt, _rateWindowBytes;
    private sealed record Sent(DetailContext Context, long Sequence, long Bytes, long At, bool Base);

    internal long SentBytes { get { lock (_gate) return _sentBytes; } }
    internal long AcknowledgedBytes { get { lock (_gate) return _ackedBytes; } }

    internal string DescribeDelivery(long now)
    {
        lock (_gate)
            return _samples == 0 ? "等待首个接收回执" :
                $"回执 RTT {_lastRtt:F0}/{_minimumRtt:F0} ms，实收 {_rate / 1_000_000:F1} Mbps，在途 {_inFlightBases} 帧，" +
                $"待呈现 {(_renderLag == int.MaxValue ? "未知" : _renderLag.ToString())} 帧，上次回执距今 {Math.Max(0, now - _lastAckAt)} ms";
    }

    internal void BeginCapture()
    {
        lock (_gate)
        {
            // Keep the connection's absolute wire counter, but do not let a
            // retired capture's missing final ACK deadlock a later re-enable.
            _history.Clear(); _recentBases.Clear(); _inFlightBases = _renderLag = 0; _captureStartedBytes = _sentBytes;
            _samples = 0; _rate = 0; _minimumRtt = _minimumBaseRtt = double.MaxValue;
            _headroom.Reset();
            _lastAckAt = _firstAckAt = _presented = 0;
            _receivedBase = default;
        }
    }

    internal bool FeedbackStalled(long now)
    {
        lock (_gate)
        {
            if (!_history.TryPeek(out var first)) return false;
            // A late oldest frame on a long-haul path is not a silent peer.
            // Stop adding frames in CanSendBase, and retire the encoder only
            // after cumulative receipts themselves stop making progress.
            double timeout = _samples == 0 ? 1500 : Math.Clamp(2 * _minimumRtt + 650, 1500, 5000);
            return now - Math.Max(first.At, _lastAckAt) > timeout;
        }
    }

    internal void RecordSent(DetailContext context, long sequence, int wireBytes, long now, bool isBase = true)
    {
        if (sequence <= 0 || wireBytes <= 0 || now < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        lock (_gate)
        {
            _sentBytes = checked(_sentBytes + wireBytes);
            _lastSequence = Math.Max(_lastSequence, sequence);
            if (isBase)
            {
                _inFlightBases++;
                _recentBases.Enqueue((context, sequence));
                if (_recentBases.Count > 128) _recentBases.Dequeue();
            }
            _history.Enqueue(new(context, sequence, _sentBytes, now, isBase));
            if (_history.Count > 256 && _history.Dequeue().Base) _inFlightBases--;
        }
    }

    internal bool Observe(NativeDetailFeedback feedback, long now)
    {
        lock (_gate)
        {
            if (now < _lastAckAt || feedback.ReceivedWireBytes < _ackedBytes ||
                feedback.ReceivedWireBytes > _sentBytes || feedback.ReceivedSequence > _lastSequence ||
                feedback.PresentedSequence < 0 || feedback.PresentedSequence > feedback.ReceivedSequence)
                return false;
            if (feedback.ReceivedWireBytes == _ackedBytes)
            {
                if (_samples == 0 || _receivedBase != (feedback.Context, feedback.ReceivedSequence) ||
                    feedback.PresentedSequence <= _presented || feedback.AcknowledgementDelayMilliseconds != 0 ||
                    !_recentBases.Contains((feedback.Context, feedback.PresentedSequence))) return false;
                UpdatePresentation(feedback);
                return true;
            }
            Sent? exact = _history.FirstOrDefault(item => item.Bytes == feedback.ReceivedWireBytes &&
                item.Context == feedback.Context && item.Sequence == feedback.ReceivedSequence);
            if (exact is null || now < exact.At) return false;
            if (feedback.AcknowledgementDelayMilliseconds < 0 || feedback.AcknowledgementDelayMilliseconds > 500 ||
                feedback.AcknowledgementDelayMilliseconds > now - exact.At + 2) return false;
            _lastRtt = Math.Max(0, now - exact.At - feedback.AcknowledgementDelayMilliseconds);
            _minimumRtt = Math.Min(_minimumRtt, _lastRtt);
            if (exact.Base) _minimumBaseRtt = Math.Min(_minimumBaseRtt, _lastRtt);
            if (_samples > 0 && now - _rateWindowAt >= Math.Clamp(1.5 * _minimumRtt, 300, 1500))
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
            _lastAckAt = now; _ackedBytes = feedback.ReceivedWireBytes;
            _receivedBase = (feedback.Context, feedback.ReceivedSequence);
            UpdatePresentation(feedback);
            while (_history.TryPeek(out var item) && item.Bytes <= _ackedBytes)
                if (_history.Dequeue().Base) _inFlightBases--;
            // A tiny fragment can be acknowledged sooner than even an unqueued H.264
            // frame. Allow up to 8 ms of measured base serialization before judging jitter.
            double baseRtt = _minimumBaseRtt == double.MaxValue ? _minimumRtt : Math.Min(_minimumBaseRtt, _minimumRtt + 8);
            _headroom.Observe(now, _rate >= NativeDetailHeadroom.MinimumDelivery && _renderLag <= 1 &&
                _presented > 0 && _lastRtt <= baseRtt + 8 && HasDetailCredit(), ReceiptLifetime);
            return true;
        }
    }

    private void UpdatePresentation(NativeDetailFeedback feedback)
    {
        _presented = feedback.PresentedSequence;
        // Sequence numbers include captures that were never transmitted.
        _renderLag = _recentBases.Contains((feedback.Context, _presented))
            ? _recentBases.Count(item => item.Context == feedback.Context &&
                item.Sequence > _presented && item.Sequence <= feedback.ReceivedSequence)
            : int.MaxValue;
    }

    internal bool CanSendBase(long now, int nextWireBytes = 0)
    {
        lock (_gate)
        {
            // Bootstrap is bounded too, even if the peer never sends ACKs.
            int baseWindow = _samples < 4 ? 4 : Math.Clamp((int)Math.Ceiling((_minimumRtt + 40) / 40), 4, 16);
            long inFlight = _sentBytes - Math.Max(_ackedBytes, _captureStartedBytes);
            if (_inFlightBases >= baseWindow || _history.Count >= 256 || inFlight > 1024 * 1024) return false;
            // A burst of large IDRs must not get one full frame of credit for
            // every 40 ms of RTT. Bound their bytes by observed delivery too,
            // while allowing one replacement frame through an empty window.
            if (nextWireBytes > 0 && inFlight > 0)
            {
                if (inFlight + nextWireBytes > 1024 * 1024) return false;
                // With stable RTT a quiet, application-limited sample is not a
                // capacity ceiling. Applying it here would lower the next sample
                // further until every interactive change waited a complete RTT.
                if (_rate > 0 && _lastRtt > _minimumRtt + Math.Max(40, _minimumRtt * .08) &&
                    inFlight > Math.Max(nextWireBytes, _rate * (_minimumRtt + 60) / 8000d)) return false;
            }
            return !_history.TryPeek(out var first) || now - first.At < Math.Max(120, _minimumRtt == double.MaxValue ? 120 : _minimumRtt + 80);
        }
    }

    internal const int SmallChunkWireBytes = 512 + DetailWire.ChunkHeaderBytes + DetailWire.OuterRecordBytes;
    private long ReceiptLifetime => (long)Math.Clamp(100 + _minimumRtt * .4, 150, 450);
    private bool HasDetailCredit() => _history.Count < 192 &&
        _sentBytes - _ackedBytes <= Math.Min(896 * 1024, _rate * _minimumRtt / 8000d) + 128 * 1024;

    internal long DetailBitsPerSecond(long now, int nextWireBytes = SmallChunkWireBytes, bool allowBoost = false)
    {
        lock (_gate)
        {
            if (_samples < 4 || now - _firstAckAt < 300 ||
                now - _lastAckAt > ReceiptLifetime ||
                _lastRtt > _minimumRtt + Math.Clamp(_minimumRtt * .1, 20, 80) || !HasDetailCredit() ||
                _renderLag > 4 || _presented == 0 ||
                _rate <= 0 || nextWireBytes * 8000d / _rate > 2)
                return 0;
            return _headroom.Rate(_rate, now, ReceiptLifetime, allowBoost);
        }
    }
}

// UDP bases are latest-only: counting queued frames as delivered would overestimate bandwidth
// after replacement or packet loss. Use the transport's measured goodput for the base, and
// count only ordered optional TCP fragments in this lane's cumulative byte receipts.
internal sealed class NativeDetailUdpLinkBudget
{
    private readonly object _gate = new();
    private readonly Queue<long> _recentBases = new();
    private DetailContext _context;
    private int _renderLag;
    private long _lastBase, _received, _presented, _lastReceiptAt, _sentBytes, _ackedBytes;
    private double _delivered;
    private ulong _packetReport;
    private readonly NativeDetailHeadroom _headroom = new();

    internal void BeginCapture()
    {
        lock (_gate)
        {
            _context = default;
            _recentBases.Clear(); _renderLag = 0;
            _lastBase = _received = _presented = _lastReceiptAt = 0;
            _delivered = 0; _packetReport = 0; _headroom.Reset();
            // Fragment counters belong to the ordered connection and survive an off/on cycle.
        }
    }

    internal void ObserveNetwork(LowLatencyVideoNetworkSnapshot network, long now)
    {
        lock (_gate)
        {
            double delivered = network.DeliveryMegabitsPerSecond * 1_000_000;
            _delivered = network.IsComfortable && double.IsFinite(delivered) &&
                delivered >= NativeDetailLinkBudget.SmallChunkWireBytes * 4000d ? delivered : 0;
            if (_delivered < NativeDetailHeadroom.MinimumDelivery || _presented == 0 || _renderLag > 1 ||
                now - _lastReceiptAt > 180) _headroom.Reset();
            else if (network.PacketReportMicroseconds > _packetReport)
                _headroom.Observe(now, true, 600);
            _packetReport = network.PacketReportMicroseconds;
        }
    }

    internal void RecordBase(DetailContext context, long sequence)
    {
        lock (_gate)
        {
            if (context != _context)
            {
                _context = context;
                _headroom.Reset();
                _recentBases.Clear(); _renderLag = 0;
                _received = _presented = _lastReceiptAt = 0;
            }
            _lastBase = sequence;
            _recentBases.Enqueue(sequence);
            if (_recentBases.Count > 128) _recentBases.Dequeue();
        }
    }

    internal void RecordChunk(int bytes) { lock (_gate) _sentBytes = checked(_sentBytes + bytes); }

    internal bool Observe(NativeDetailFeedback feedback, long now)
    {
        lock (_gate)
        {
            if (now < _lastReceiptAt || feedback.Context != _context || feedback.ReceivedSequence <= 0 ||
                feedback.ReceivedSequence < _received || feedback.ReceivedSequence > _lastBase ||
                feedback.PresentedSequence < _presented || feedback.PresentedSequence > feedback.ReceivedSequence ||
                feedback.ReceivedWireBytes < _ackedBytes || feedback.ReceivedWireBytes > _sentBytes ||
                !_recentBases.Contains(feedback.ReceivedSequence)) return false;
            bool delivery = feedback.ReceivedSequence != _received || feedback.ReceivedWireBytes != _ackedBytes;
            if (!delivery && (feedback.PresentedSequence <= _presented || feedback.AcknowledgementDelayMilliseconds != 0 ||
                !_recentBases.Contains(feedback.PresentedSequence))) return false;
            _received = feedback.ReceivedSequence;
            _presented = feedback.PresentedSequence;
            _renderLag = _recentBases.Contains(_presented)
                ? _recentBases.Count(sent => sent > _presented && sent <= _received) : int.MaxValue;
            if (!delivery) return true;
            _ackedBytes = feedback.ReceivedWireBytes;
            if (_renderLag > 1 || now - _lastReceiptAt > 180) _headroom.Reset();
            _lastReceiptAt = now;
            return true;
        }
    }

    internal long DetailBitsPerSecond(long now, int nextWireBytes = NativeDetailLinkBudget.SmallChunkWireBytes, bool allowBoost = false)
    {
        lock (_gate)
            return _presented > 0 && now - _lastReceiptAt <= 180 && _renderLag <= 4 &&
                _delivered > 0 && nextWireBytes * 8000d / _delivered <= 2 &&
                _sentBytes - _ackedBytes + nextWireBytes <= Math.Max(4096, nextWireBytes)
                ? _headroom.Rate(_delivered, now, 600, allowBoost) : 0;
    }
}
