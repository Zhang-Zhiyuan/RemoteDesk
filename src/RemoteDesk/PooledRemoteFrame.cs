using System.Buffers;
using System.Security.Cryptography;

namespace RemoteDesk;

/// <summary>
/// Owns the encoded bytes copied from a synchronously borrowed transport frame.
/// The valid payload is always described by <see cref="RemoteFrame.EncodedLength"/>;
/// an ArrayPool implementation may return a larger backing array.
/// </summary>
internal sealed class PooledRemoteFrame : IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private readonly int _encodedLength;
    private byte[]? _buffer;

    private PooledRemoteFrame(
        ArrayPool<byte> pool,
        byte[] buffer,
        RemoteFrameMetadata metadata,
        long presentationGeneration)
    {
        _pool = pool;
        _buffer = buffer;
        _encodedLength = metadata.EncodedLength;
        Metadata = metadata;
        PresentationGeneration = presentationGeneration;
    }

    public RemoteFrameMetadata Metadata { get; }

    public long PresentationGeneration { get; }

    public RemoteFrame Frame
    {
        get
        {
            byte[] buffer =
                Volatile.Read(ref _buffer) ??
                throw new ObjectDisposedException(
                    nameof(PooledRemoteFrame));
            return new(
                Metadata.Width,
                Metadata.Height,
                Metadata.Encoding,
                Metadata.Flags,
                buffer,
                EncodedOffset: 0,
                Metadata.EncodedLength,
                Metadata.CaptureMilliseconds,
                Metadata.EncodeMilliseconds,
                Metadata.ReceivedAtTimestamp,
                Metadata.NativeDetails);
        }
    }

    public static PooledRemoteFrame CopyFrom(
        RemoteFrame source,
        ArrayPool<byte> pool,
        long presentationGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (source.EncodedLength <= 0 ||
            source.EncodedOffset < 0 ||
            source.EncodedOffset >
                source.EncodedBuffer.Length -
                source.EncodedLength)
        {
            throw new ArgumentException(
                "The encoded frame payload range is invalid.",
                nameof(source));
        }

        byte[] buffer = pool.Rent(source.EncodedLength);
        if (buffer.Length < source.EncodedLength)
        {
            pool.Return(buffer, clearArray: false);
            throw new InvalidOperationException(
                "The byte array pool returned a buffer smaller " +
                "than the requested frame payload.");
        }

        try
        {
            source.EncodedBuffer.AsSpan(
                    source.EncodedOffset,
                    source.EncodedLength)
                .CopyTo(buffer);
            return new(
                pool,
                buffer,
                RemoteFrameMetadata.FromFrame(source),
                presentationGeneration);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(
                buffer.AsSpan(0, source.EncodedLength));
            pool.Return(buffer, clearArray: false);
            throw;
        }
    }

    public void Dispose()
    {
        byte[]? buffer =
            Interlocked.Exchange(ref _buffer, null);
        if (buffer is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(
            buffer.AsSpan(0, _encodedLength));
        _pool.Return(buffer, clearArray: false);
    }
}
