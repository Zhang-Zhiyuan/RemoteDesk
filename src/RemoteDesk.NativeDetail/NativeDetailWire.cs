using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace RemoteDesk.NativeDetail;

// Versioned native-detail payloads. Enable only after explicit negotiation.
// Payloads require an authenticated, encrypted outer transport; the hash below
// detects incomplete/mixed transfers and is NOT an authentication mechanism.
public readonly record struct DetailRect(int X, int Y, int Width, int Height)
{
    public bool Intersects(DetailRect other) => X < (long)other.X + other.Width &&
        other.X < (long)X + Width && Y < (long)other.Y + other.Height && other.Y < (long)Y + Height;
    public void Validate(DetailContext context)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            (long)X + Width > context.Width || (long)Y + Height > context.Height)
            throw new InvalidDataException("Invalid native viewport.");
    }
}

public readonly record struct DetailContext(long Epoch, long Request, int Width, int Height)
{
    public const int TileEdge = 128;
    public int Columns => (Width + TileEdge - 1) / TileEdge;
    public int TileCount => Columns * ((Height + TileEdge - 1) / TileEdge);
    public int PixelBytes => checked(Width * Height * 4);
    public void Validate()
    {
        if (Epoch <= 0 || Request <= 0 || Width is <= 0 or > 8192 || Height is <= 0 or > 8192 ||
            (long)Width * Height > 16_777_216 || TileCount > 2048)
            throw new InvalidDataException("Invalid detail context.");
    }
    public DetailRect Tile(int index)
    {
        Validate();
        if ((uint)index >= TileCount) throw new InvalidDataException("Invalid tile index.");
        int x = index % Columns * TileEdge, y = index / Columns * TileEdge;
        return new(x, y, Math.Min(TileEdge, Width - x), Math.Min(TileEdge, Height - y));
    }
}

public sealed class DetailManifest
{
    private readonly long[] _versions;
    public DetailContext Context { get; }
    public long Sequence { get; }
    public long this[int tile] => _versions[tile];
    public DetailManifest(DetailContext context, long sequence, ReadOnlySpan<long> versions)
    {
        context.Validate();
        if (sequence <= 0 || versions.Length != context.TileCount)
            throw new InvalidDataException("Invalid manifest.");
        foreach (long version in versions)
            if (version <= 0 || version > sequence) throw new InvalidDataException("Invalid content version.");
        Context = context; Sequence = sequence; _versions = versions.ToArray();
    }
}

public sealed class DetailTransfer
{
    public DetailContext Context { get; }
    public long ReferenceSequence { get; }
    public int Tile { get; }
    public long Version { get; }
    public ReadOnlyMemory<byte> Encoded { get; }
    public ReadOnlyMemory<byte> Digest { get; }
    private DetailTransfer(DetailContext context, long sequence, int tile, long version, byte[] encoded)
    {
        Context = context; ReferenceSequence = sequence; Tile = tile; Version = version;
        Encoded = encoded; Digest = SHA256.HashData(encoded);
    }
    public static DetailTransfer Encode(DetailManifest manifest, int tile, ReadOnlySpan<byte> rgba)
    {
        var rect = manifest.Context.Tile(tile);
        if (rgba.Length != rect.Width * rect.Height * 4) throw new InvalidDataException("RGBA size mismatch.");
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(rgba);
        if (output.Length > DetailWire.MaxCompressedBytes) throw new InvalidDataException("Compressed tile exceeds limit.");
        return new(manifest.Context, manifest.Sequence, tile, manifest[tile], output.ToArray());
    }
    public byte[] Chunk(int offset, int chunkBytes = DetailWire.ChunkBytes) => DetailWire.EncodeChunk(this, offset, chunkBytes);
}

public sealed record DetailChunk(DetailContext Context, long ReferenceSequence, int Tile,
    long Version, int TotalBytes, int Offset, ReadOnlyMemory<byte> Digest, ReadOnlyMemory<byte> Data,
    int ChunkDataBytes = DetailWire.ChunkBytes);

public static class DetailWire
{
    public const int ChunkBytes = 4096;
    public const int ChunkHeaderBytes = 96;
    public const int MaxCompressedBytes = 96 * 1024;
    public const int OuterRecordBytes = 25; // RDK1 length + envelope + AES-GCM tag.
    public const int MaxWireChunkBytes = ChunkHeaderBytes + ChunkBytes + OuterRecordBytes;
    private const uint Magic = 0x314C444E; // ASCII NDL1.

    public static byte[] EncodeManifest(DetailManifest manifest)
    {
        var runs = new List<(int Length, long Version)>();
        for (int tile = 0; tile < manifest.Context.TileCount;)
        {
            int end = tile + 1;
            while (end < manifest.Context.TileCount && manifest[end] == manifest[tile]) end++;
            runs.Add((end - tile, manifest[tile])); tile = end;
        }
        byte[] result = Header(1, manifest.Context, manifest.Sequence, 44 + runs.Count * 10);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40), runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(44 + i * 10), checked((ushort)runs[i].Length));
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(46 + i * 10), runs[i].Version);
        }
        return result;
    }

    public static DetailManifest DecodeManifest(ReadOnlySpan<byte> payload)
    {
        var (context, sequence) = ReadHeader(payload, 1, 44);
        int runs = BinaryPrimitives.ReadInt32LittleEndian(payload[40..]);
        if (runs <= 0 || runs > context.TileCount || payload.Length != 44 + runs * 10)
            throw new InvalidDataException("Invalid manifest run count.");
        var versions = new long[context.TileCount];
        int tile = 0;
        for (int i = 0; i < runs; i++)
        {
            int count = BinaryPrimitives.ReadUInt16LittleEndian(payload[(44 + i * 10)..]);
            long version = BinaryPrimitives.ReadInt64LittleEndian(payload[(46 + i * 10)..]);
            if (count <= 0 || count > versions.Length - tile || version <= 0 || version > sequence)
                throw new InvalidDataException("Invalid manifest run.");
            Array.Fill(versions, version, tile, count); tile += count;
        }
        if (tile != versions.Length) throw new InvalidDataException("Incomplete tile manifest.");
        return new(context, sequence, versions);
    }

    internal static byte[] EncodeChunk(DetailTransfer transfer, int offset, int chunkBytes = ChunkBytes)
    {
        if (chunkBytes is not (512 or ChunkBytes) || offset < 0 || offset >= transfer.Encoded.Length || offset % chunkBytes != 0)
            throw new InvalidDataException("Invalid chunk offset.");
        int length = Math.Min(chunkBytes, transfer.Encoded.Length - offset);
        byte[] result = Header(chunkBytes == 512 ? (byte)3 : (byte)2, transfer.Context, transfer.ReferenceSequence, ChunkHeaderBytes + length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40), transfer.Tile);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(44), transfer.Version);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(52), transfer.Encoded.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(56), offset);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(60), length);
        transfer.Digest.Span.CopyTo(result.AsSpan(64, 32));
        transfer.Encoded.Span.Slice(offset, length).CopyTo(result.AsSpan(ChunkHeaderBytes));
        return result;
    }

    public static DetailChunk DecodeChunk(ReadOnlySpan<byte> payload)
    {
        int chunkBytes = payload.Length > 4 && payload[4] == 3 ? 512 : ChunkBytes;
        var (context, sequence) = ReadHeader(payload, chunkBytes == 512 ? (byte)3 : (byte)2, ChunkHeaderBytes);
        int tile = BinaryPrimitives.ReadInt32LittleEndian(payload[40..]);
        long version = BinaryPrimitives.ReadInt64LittleEndian(payload[44..]);
        int total = BinaryPrimitives.ReadInt32LittleEndian(payload[52..]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(payload[56..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(payload[60..]);
        context.Tile(tile);
        if (version <= 0 || version > sequence || total is <= 0 or > MaxCompressedBytes || offset < 0 ||
            offset >= total || offset % chunkBytes != 0 || length != Math.Min(chunkBytes, total - offset) ||
            payload.Length != ChunkHeaderBytes + length)
            throw new InvalidDataException("Invalid detail chunk.");
        return new(context, sequence, tile, version, total, offset,
            payload.Slice(64, 32).ToArray(), payload[ChunkHeaderBytes..].ToArray(), chunkBytes);
    }

    public static byte[] DecodeRgba(ReadOnlySpan<byte> encoded, int expectedBytes)
    {
        if (encoded.Length is <= 0 or > MaxCompressedBytes || expectedBytes is <= 0 or > 128 * 128 * 4)
            throw new InvalidDataException("Invalid tile allocation.");
        using var input = new GuardedCompressedInput(encoded.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        byte[] rgba = new byte[expectedBytes];
        zlib.ReadExactly(rgba);
        if (zlib.ReadByte() != -1 || input.Consumed != encoded.Length)
            throw new InvalidDataException("Incomplete zlib member, trailing data or excess pixels.");
        return rgba;
    }

    // .NET 8 ZLibStream can return EOF for a truncated trailer after producing
    // all expected pixels. Keep the final eight bytes out of bulk read-ahead,
    // supply that tail byte-by-byte, then offer ONE guard byte. A complete member
    // ends without consuming the guard; an incomplete member consumes it/fails.
    // A suffixed stream ends with tail bytes still unread, even if an earlier
    // bulk read included part of the suffix. This verifies exact termination
    // without a global runtime switch or per-byte calls for the whole tile.
    private sealed class GuardedCompressedInput(byte[] data) : Stream
    {
        public int Consumed { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || Consumed > data.Length) return 0;
            int bulk = Math.Min(buffer.Length, data.Length - 8 - Consumed);
            if (bulk > 0)
            {
                data.AsSpan(Consumed, bulk).CopyTo(buffer); Consumed += bulk; return bulk;
            }
            buffer[0] = Consumed < data.Length ? data[Consumed] : (byte)0;
            Consumed++; return 1;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length + 1L;
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Header(byte kind, DetailContext context, long sequence, int length)
    {
        var result = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, Magic); result[4] = kind;
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(8), context.Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(16), context.Request);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24), context.Width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(28), context.Height);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(32), sequence);
        return result;
    }
    private static (DetailContext Context, long Sequence) ReadHeader(ReadOnlySpan<byte> payload, byte kind, int minimum)
    {
        if (payload.Length < minimum || BinaryPrimitives.ReadUInt32LittleEndian(payload) != Magic ||
            payload[4] != kind || payload[5] != 0 || payload[6] != 0 || payload[7] != 0)
            throw new InvalidDataException("Unknown detail record.");
        var context = new DetailContext(BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(payload[16..]), BinaryPrimitives.ReadInt32LittleEndian(payload[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[28..]));
        context.Validate();
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(payload[32..]);
        if (sequence <= 0) throw new InvalidDataException("Invalid source sequence.");
        return (context, sequence);
    }
}
