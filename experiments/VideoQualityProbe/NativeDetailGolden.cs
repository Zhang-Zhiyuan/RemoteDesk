using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RemoteDesk.NativeDetail;

internal static class NativeDetailGolden
{
    internal static void Write(string output)
    {
        if (File.Exists(output)) throw new IOException("Preserving interop vectors.");
        var cache = new DetailCache(); var lines = new List<string>(); long now = 0;
        void Emit(string operation, string data, Func<string> action)
        {
            now += 10;
            string result;
            try { result = action(); }
            catch (InvalidDataException) { result = "invalid"; }
            string pixels = string.Join("\n", cache.Patches.OrderBy(patch => patch.Tile)
                .Select(patch => $"{patch.Tile}|{patch.Version}|{Convert.ToHexString(SHA256.HashData(patch.Rgba.Span))}"));
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(pixels)));
            lines.Add($"{operation}\t{now}\t{data}\t{result}\t{fingerprint}\t{cache.PendingBytes}");
        }
        void Reset(DetailContext context, DetailRect rect) => Emit("reset",
            $"{context.Epoch},{context.Request},{context.Width},{context.Height},{rect.X},{rect.Y},{rect.Width},{rect.Height}",
            () => { cache.Reset(context, rect); return "reset"; });
        void Frame(DetailManifest manifest)
        {
            byte[] packet = DetailWire.EncodeManifest(manifest);
            Emit("frame", Convert.ToBase64String(packet), () => cache.Present(DetailWire.DecodeManifest(packet)) ? "presented" : "rejected");
        }
        void Chunk(byte[] payload) => Emit("chunk", Convert.ToBase64String(payload), () => cache.Receive(payload, now).ToString());
        DetailTransfer Tile(DetailManifest manifest, int tile, bool random = false)
        {
            var rect = manifest.Context.Tile(tile); var pixels = new byte[rect.Width * rect.Height * 4];
            if (random) new Random(99 + tile).NextBytes(pixels); else Array.Fill(pixels, (byte)255);
            return DetailTransfer.Encode(manifest, tile, pixels);
        }
        DetailManifest All(DetailContext context, long sequence = 1) =>
            new(context, sequence, Enumerable.Repeat(1L, context.TileCount).ToArray());

        var c = new DetailContext(1, 1, 257, 129); Reset(c, new(0, 0, 257, 129));
        var initial = All(c); Frame(initial); var old = Tile(initial, 0); Chunk(old.Chunk(0));
        Frame(All(c, 2));
        Frame(new(c, 3, [2, 1, 1, 1, 1, 1])); Chunk(old.Chunk(0));
        var future = new DetailManifest(c, 4, [3, 1, 1, 1, 1, 1]); var futureTile = Tile(future, 0);
        Chunk(futureTile.Chunk(0)); Frame(future); Chunk(futureTile.Chunk(0));
        byte[] broken = futureTile.Chunk(0); broken[0] ^= 1; Chunk(broken);
        Emit("input", "-", () => { cache.Interaction(); return "ok"; }); Chunk(futureTile.Chunk(0));
        var fifth = new DetailManifest(c, 5, [3, 1, 1, 1, 1, 1]); Frame(fifth); Chunk(Tile(fifth, 0).Chunk(0));
        Frame(initial); // Replayed base must not roll state back.
        Emit("off", "-", () => { cache.Disable(); return "ok"; }); Chunk(old.Chunk(0));
        Reset(c, new(0, 0, 257, 129)); // Reusing the request token is invalid.

        c = new(1, 2, 384, 128); Reset(c, new(0, 0, 128, 128)); initial = All(c); Frame(initial);
        Chunk(Tile(initial, 2).Chunk(0));
        var noisy = Tile(initial, 0, true); Chunk(noisy.Chunk(0)); Chunk(noisy.Chunk(0));
        broken = noisy.Chunk(0); broken[^1] ^= 1; Chunk(broken);
        for (int offset = (noisy.Encoded.Length - 1) / 4096 * 4096; offset >= 0; offset -= 4096) Chunk(noisy.Chunk(offset));
        Frame(new(c, 2, [2, 1, 1])); Chunk(noisy.Chunk(0));
        Frame(new(c, 3, [1, 1, 1])); // Invalid rollback, no partial commit.
        c = new(1, 3, 384, 128); Reset(c, new(0, 0, 384, 128)); initial = All(c); Frame(initial);
        var a = Tile(initial, 0, true); var b = Tile(initial, 1, true); var d = Tile(initial, 2, true);
        Chunk(a.Chunk(0)); Chunk(b.Chunk(0)); Chunk(d.Chunk(0));
        broken = a.Chunk(0); BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(52), int.MaxValue); Chunk(broken);
        now += 2000; Chunk(d.Chunk(0)); // Expire both older assemblies.
        Emit("input", "-", () => { cache.Interaction(); return "ok"; }); Chunk(d.Chunk(4096));
        var fresh = All(c, 2); Frame(fresh); var newA = Tile(fresh, 0, true); Chunk(newA.Chunk(0));
        Chunk(a.Chunk(4096)); // Old reference cannot kill the new assembly.
        for (int offset = 4096; offset < newA.Encoded.Length; offset += 4096) Chunk(newA.Chunk(offset));

        c = new(2, 1, 128, 128); Reset(c, new(0, 0, 128, 128)); initial = All(c); Frame(initial);
        byte[] template = Tile(initial, 0).Chunk(0);
        void EncodedChunk(byte[] compressed)
        {
            if (compressed.Length > 4096) throw new IOException("Test payload must fit one chunk.");
            var record = new byte[96 + compressed.Length]; template.AsSpan(0, 96).CopyTo(record);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(52), compressed.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(60), compressed.Length);
            SHA256.HashData(compressed).CopyTo(record, 64); compressed.CopyTo(record, 96); Chunk(record);
        }
        byte[] validCompressed = template[96..];
        for (int missing = 1; missing <= 5; missing++) EncodedChunk(validCompressed[..^missing]);
        EncodedChunk([..validCompressed, 0]); EncodedChunk([..validCompressed, ..validCompressed]);
        using var outputBytes = new MemoryStream();
        using (var zlib = new ZLibStream(outputBytes, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(new byte[65537]);
        byte[] tooManyPixels = outputBytes.ToArray();
        var bomb = new byte[96 + tooManyPixels.Length]; template.AsSpan(0, 96).CopyTo(bomb);
        BinaryPrimitives.WriteInt32LittleEndian(bomb.AsSpan(52), tooManyPixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bomb.AsSpan(60), tooManyPixels.Length);
        SHA256.HashData(tooManyPixels).CopyTo(bomb, 64); tooManyPixels.CopyTo(bomb, 96); Chunk(bomb);
        broken = template[..^1]; Chunk(broken);
        Chunk([..template, 0]);
        broken = (byte[])template.Clone(); broken[5] = 1; Chunk(broken);
        byte[] invalidManifest = [..DetailWire.EncodeManifest(initial), 0];
        Emit("frame", Convert.ToBase64String(invalidManifest), () => cache.Present(DetailWire.DecodeManifest(invalidManifest)) ? "presented" : "rejected");
        c = new(2, 2, 1, 1); Reset(c, new(0, 0, 1, 1)); initial = All(c); Frame(initial); Chunk(Tile(initial, 0).Chunk(0));
        c = new(3, 1, 128, 128); Reset(c, new(0, 0, 128, 128)); initial = All(c); Frame(initial);
        var small = Tile(initial, 0, true);
        Chunk(small.Chunk(0, 512)); Chunk(small.Chunk(0)); // mixed layouts cannot share an assembly
        broken = small.Chunk(512, 512); broken[4] = 2; Chunk(broken);
        for (int offset = (small.Encoded.Length - 1) / 512 * 512; offset >= 512; offset -= 512)
            Chunk(small.Chunk(offset, 512));
        Chunk(small.Chunk(0, 512)); // exact duplicate after out-of-order completion
        File.WriteAllLines(output, lines);
    }
}
