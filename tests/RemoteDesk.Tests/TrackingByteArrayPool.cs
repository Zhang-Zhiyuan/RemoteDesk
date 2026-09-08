using System.Buffers;
using System.Collections.Concurrent;

namespace RemoteDesk.Tests;

internal sealed class TrackingByteArrayPool : ArrayPool<byte>
{
    private readonly ConcurrentDictionary<byte[], byte> _active =
        new();
    private int _rentCount;
    private int _returnCount;

    public int RentCount =>
        Volatile.Read(ref _rentCount);

    public int ReturnCount =>
        Volatile.Read(ref _returnCount);

    public int ActiveCount => _active.Count;

    public override byte[] Rent(int minimumLength)
    {
        if (minimumLength < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumLength));
        }

        byte[] buffer = new byte[
            checked(minimumLength + 31)];
        if (!_active.TryAdd(buffer, 0))
        {
            throw new InvalidOperationException(
                "The tracking pool rented the same array twice.");
        }

        Interlocked.Increment(ref _rentCount);
        return buffer;
    }

    public override void Return(
        byte[] array,
        bool clearArray = false)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (!_active.TryRemove(array, out _))
        {
            throw new InvalidOperationException(
                "An array was returned to the tracking pool more than once.");
        }

        if (clearArray)
        {
            Array.Clear(array);
        }

        Interlocked.Increment(ref _returnCount);
    }
}
