using Xunit;

namespace RemoteDesk.Tests;

public sealed class PooledRemoteFrameTests
{
    [Fact]
    public void CopyUsesOnlyDeclaredSliceAndReturnsLeaseIdempotently()
    {
        byte[] transportBuffer =
            [0xEE, 0xEF, 1, 2, 3, 4, 0xFA];
        var source = new RemoteFrame(
            Width: 3840,
            Height: 2160,
            RemoteFrameEncoding.H264AnnexB,
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            transportBuffer,
            EncodedOffset: 2,
            EncodedLength: 4,
            CaptureMilliseconds: 1.25,
            EncodeMilliseconds: 2.5,
            ReceivedAtTimestamp: 1234);
        var pool = new TrackingByteArrayPool();

        PooledRemoteFrame owned =
            PooledRemoteFrame.CopyFrom(source, pool);
        RemoteFrame copied = owned.Frame;

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ActiveCount);
        Assert.NotSame(transportBuffer, copied.EncodedBuffer);
        Assert.Equal(0, copied.EncodedOffset);
        Assert.Equal(4, copied.EncodedLength);
        Assert.True(
            copied.EncodedBuffer.Length >
            copied.EncodedLength);
        Assert.Equal(
            new byte[] { 1, 2, 3, 4 },
            copied.EncodedBuffer.AsSpan(
                    copied.EncodedOffset,
                    copied.EncodedLength)
                .ToArray());
        Assert.Equal(
            RemoteFrameMetadata.FromFrame(source),
            owned.Metadata);

        owned.Dispose();
        owned.Dispose();

        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Throws<ObjectDisposedException>(
            () => _ = owned.Frame);
        Assert.Equal(
            new byte[]
            {
                0xEE, 0xEF, 1, 2, 3, 4, 0xFA
            },
            transportBuffer);
    }

    [Fact]
    public void InvalidSourceRangeDoesNotRentBuffer()
    {
        var pool = new TrackingByteArrayPool();
        var invalid = new RemoteFrame(
            64,
            64,
            RemoteFrameEncoding.Jpeg,
            RemoteFrameFlags.None,
            [1, 2, 3],
            EncodedOffset: 2,
            EncodedLength: 2,
            CaptureMilliseconds: 0,
            EncodeMilliseconds: 0);

        Assert.Throws<ArgumentException>(
            () => PooledRemoteFrame.CopyFrom(
                invalid,
                pool));
        Assert.Equal(0, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }
}
