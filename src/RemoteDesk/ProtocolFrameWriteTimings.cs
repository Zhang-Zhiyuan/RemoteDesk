namespace RemoteDesk;

// Local TCP write stages only. Socket write completion is not an ACK, remote
// decode/presentation, or end-to-end latency. No timings are added to the wire.
internal readonly record struct ProtocolFrameWriteTimings(
    double PreparationMilliseconds,
    double LockWaitMilliseconds,
    double EncryptionMilliseconds,
    double SocketWriteMilliseconds);

// Owned by a single capture loop. Aggregate per metrics window, not per-frame
// log messages; UDP admission and discarded frames are not counted as TCP writes.
internal sealed class ProtocolFrameWriteTimingsWindow
{
    private int _count;
    private ProtocolFrameWriteTimings _total;

    internal int Count => _count;
    internal double AverageSocketWriteMilliseconds =>
        _count == 0 ? 0 : _total.SocketWriteMilliseconds / _count;

    public void Record(ProtocolFrameWriteTimings timings)
    {
        _count++;
        _total = new ProtocolFrameWriteTimings(
            _total.PreparationMilliseconds + timings.PreparationMilliseconds,
            _total.LockWaitMilliseconds + timings.LockWaitMilliseconds,
            _total.EncryptionMilliseconds + timings.EncryptionMilliseconds,
            _total.SocketWriteMilliseconds + timings.SocketWriteMilliseconds);
    }

    public string DescribeAverage() => _count == 0
        ? string.Empty
        : $"，TCP 本地均值({_count}帧)：" +
          $"组包 {_total.PreparationMilliseconds / _count:F2}ms，" +
          $"等锁 {_total.LockWaitMilliseconds / _count:F2}ms，" +
          $"加密 {_total.EncryptionMilliseconds / _count:F2}ms，" +
          $"写入 {_total.SocketWriteMilliseconds / _count:F2}ms（非RTT）";

    public void Reset()
    {
        _count = 0;
        _total = default;
    }
}
