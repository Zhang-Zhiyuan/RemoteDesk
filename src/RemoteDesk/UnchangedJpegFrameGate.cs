using System.Security.Cryptography;

namespace RemoteDesk;

// Capture-loop owned. Suppress identical reliable JPEG frames without keeping
// their large buffers alive. Changes/target transitions are never delayed, and
// a periodic full refresh also supports viewers which repaint after occlusion.
internal sealed class UnchangedJpegFrameGate
{
    internal const long RefreshIntervalMilliseconds = 5_000;
    private byte[] _sentHash = new byte[32];
    private byte[] _candidateHash = new byte[32];
    private Size _sentSize, _candidateSize;
    private int _sentGeneration, _candidateGeneration;
    private long _sentAt;
    private bool _hasSent;

    internal bool ShouldSend(ReadOnlySpan<byte> jpeg, Size size, int targetGeneration, long nowMilliseconds)
    {
        SHA256.HashData(jpeg, _candidateHash);
        _candidateSize = size;
        _candidateGeneration = targetGeneration;
        return !_hasSent || _sentSize != size || _sentGeneration != targetGeneration ||
            nowMilliseconds < _sentAt || nowMilliseconds - _sentAt >= RefreshIntervalMilliseconds ||
            !_candidateHash.AsSpan().SequenceEqual(_sentHash);
    }

    // Only call after successful publication. A frame rejected by a target or
    // control-priority barrier must not suppress the next candidate.
    internal void MarkSent(long nowMilliseconds)
    {
        (_sentHash, _candidateHash) = (_candidateHash, _sentHash);
        _sentSize = _candidateSize;
        _sentGeneration = _candidateGeneration;
        _sentAt = nowMilliseconds;
        _hasSent = true;
    }

    internal void Reset() => _hasSent = false;
}
