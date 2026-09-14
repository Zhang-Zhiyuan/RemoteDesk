import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.security.MessageDigest;
import java.util.*;
import java.util.zip.DataFormatException;
import java.util.zip.Inflater;

// Plain Java receiver parity probe. No Android framework, UI, permissions or
// production endpoints. Running on a desktop JVM is not an Android device test.
public final class NativeDetailReceiver {
    private static void check(boolean value) { if (!value) throw new IllegalArgumentException("Invalid NDL1 data"); }
    private static final class Context {
        final long epoch, request; final int width, height;
        Context(long e, long r, int w, int h) {
            check(e > 0 && r > 0 && w > 0 && w <= 8192 && h > 0 && h <= 8192 && (long)w * h <= 16777216);
            epoch = e; request = r; width = w; height = h; check(count() <= 2048);
        }
        int count() { return ((width + 127) / 128) * ((height + 127) / 128); }
        int[] rect(int tile) {
            check(tile >= 0 && tile < count()); int x = tile % ((width + 127) / 128) * 128, y = tile / ((width + 127) / 128) * 128;
            return new int[] { x, y, Math.min(128, width - x), Math.min(128, height - y) };
        }
        boolean same(Context other) { return other != null && epoch == other.epoch && request == other.request && width == other.width && height == other.height; }
    }
    private static final class Header {
        final Context context; final long sequence;
        Header(byte[] data, int kind, int minimum) {
            check(data.length >= minimum && data[0] == 'N' && data[1] == 'D' && data[2] == 'L' && data[3] == '1' &&
                data[4] == kind && data[5] == 0 && data[6] == 0 && data[7] == 0);
            ByteBuffer b = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN);
            context = new Context(b.getLong(8), b.getLong(16), b.getInt(24), b.getInt(28)); sequence = b.getLong(32); check(sequence > 0);
        }
    }
    private static final class Manifest {
        final Header header; final long[] versions;
        Manifest(byte[] data) {
            header = new Header(data, 1, 44); ByteBuffer b = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN);
            int runs = b.getInt(40); check(runs > 0 && runs <= header.context.count() && data.length == 44 + runs * 10);
            versions = new long[header.context.count()]; int tile = 0;
            for (int i = 0; i < runs; i++) {
                int count = b.getShort(44 + i * 10) & 65535; long version = b.getLong(46 + i * 10);
                check(count > 0 && count <= versions.length - tile && version > 0 && version <= header.sequence);
                Arrays.fill(versions, tile, tile + count, version); tile += count;
            }
            check(tile == versions.length);
        }
    }
    private static final class Chunk {
        final Header header; final int tile, total, offset, chunkBytes; final long version; final byte[] digest, data;
        Chunk(byte[] input) {
            check(input.length >= 96 && (input[4] == 2 || input[4] == 3));
            chunkBytes = input[4] == 2 ? 4096 : 512;
            header = new Header(input, input[4], 96); ByteBuffer b = ByteBuffer.wrap(input).order(ByteOrder.LITTLE_ENDIAN);
            tile = b.getInt(40); version = b.getLong(44); total = b.getInt(52); offset = b.getInt(56); int size = b.getInt(60);
            header.context.rect(tile);
            check(version > 0 && version <= header.sequence && total > 0 && total <= 96 * 1024 && offset >= 0 && offset < total && offset % chunkBytes == 0);
            check(size == Math.min(chunkBytes, total - offset) && input.length == 96 + size);
            digest = Arrays.copyOfRange(input, 64, 96); data = Arrays.copyOfRange(input, 96, input.length);
        }
    }
    private static final class Assembly {
        final Chunk header; final long started; final byte[] data; final boolean[] received;
        Assembly(Chunk h, long now) { header = h; started = now; data = new byte[h.total]; received = new boolean[(h.total + h.chunkBytes - 1) / h.chunkBytes]; }
        boolean matches(Chunk h) { return header.header.sequence == h.header.sequence && header.version == h.version &&
            header.total == h.total && header.chunkBytes == h.chunkBytes && MessageDigest.isEqual(header.digest, h.digest); }
    }
    private static final class Patch {
        final long version; final byte[] rgba;
        Patch(long v, byte[] pixels) { version = v; rgba = pixels; }
    }
    private static final class Cache {
        Context context; Manifest displayed; boolean enabled; int[] viewport;
        long minimum, now;
        final Map<Integer, Assembly> assemblies = new HashMap<>();
        final LinkedHashMap<Integer, Patch> patches = new LinkedHashMap<>();
        void clear() { assemblies.clear(); patches.clear(); }
        String reset(long[] p) {
            check(p.length == 8);
            for (int i = 2; i < p.length; i++) check(p[i] >= 0 && p[i] <= Integer.MAX_VALUE);
            Context next = new Context(p[0], p[1], (int)p[2], (int)p[3]);
            check(p[4] >= 0 && p[5] >= 0 && p[6] > 0 && p[7] > 0 && p[4] + p[6] <= next.width && p[5] + p[7] <= next.height);
            check(context == null || next.epoch > context.epoch || (next.epoch == context.epoch && next.request > context.request));
            clear(); context = next; viewport = new int[] {(int)p[4], (int)p[5], (int)p[6], (int)p[7]}; enabled = true; displayed = null; minimum = 0;
            return "reset";
        }
        String present(byte[] data) {
            Manifest m = new Manifest(data);
            if (!enabled || !m.header.context.same(context) || (displayed != null && m.header.sequence <= displayed.header.sequence)) return "rejected";
            if (displayed != null) for (int tile = 0; tile < context.count(); tile++) if (m.versions[tile] < displayed.versions[tile]) return "rejected";
            patches.entrySet().removeIf(entry -> entry.getValue().version != m.versions[entry.getKey()]);
            assemblies.entrySet().removeIf(entry -> entry.getValue().header.version != m.versions[entry.getKey()]);
            displayed = m; return "presented";
        }
        String off() { clear(); enabled = false; displayed = null; return "ok"; }
        String interaction() {
            if (displayed != null && displayed.header.sequence == Long.MAX_VALUE) return off();
            clear(); minimum = displayed == null ? 1 : displayed.header.sequence + 1; return "ok";
        }
        String receive(byte[] bytes, long time) throws Exception {
            check(time >= now); now = time;
            assemblies.entrySet().removeIf(entry -> time - entry.getValue().started > 1500);
            if (!enabled || displayed == null) return "Inactive";
            Chunk c;
            try { c = new Chunk(bytes); } catch (IllegalArgumentException ex) { return "Invalid"; }
            if (!c.header.context.same(context) || c.header.sequence < minimum) return "Stale";
            if (c.header.sequence > displayed.header.sequence) return "Future";
            if (c.version != displayed.versions[c.tile]) return "Stale";
            int[] r = context.rect(c.tile);
            if (!(r[0] < viewport[0] + viewport[2] && viewport[0] < r[0] + r[2] && r[1] < viewport[1] + viewport[3] && viewport[1] < r[1] + r[3])) return "OutsideViewport";
            if (patches.containsKey(c.tile)) return "Duplicate";
            Assembly a = assemblies.get(c.tile);
            if (a == null) {
                if (assemblies.size() >= 2) return "Limited";
                a = new Assembly(c, time); assemblies.put(c.tile, a);
            }
            if (!a.matches(c)) {
                if (c.header.sequence < a.header.header.sequence) return "Stale";
                if (c.header.sequence > a.header.header.sequence && c.offset == 0) { a = new Assembly(c, time); assemblies.put(c.tile, a); }
                else return "Invalid";
            }
            int index = c.offset / c.chunkBytes;
            if (a.received[index]) {
                if (Arrays.equals(Arrays.copyOfRange(a.data, c.offset, c.offset + c.data.length), c.data)) return "Duplicate";
                assemblies.remove(c.tile); return "Invalid";
            }
            System.arraycopy(c.data, 0, a.data, c.offset, c.data.length); a.received[index] = true;
            for (boolean received : a.received) if (!received) return "Partial";
            assemblies.remove(c.tile);
            if (!MessageDigest.isEqual(hash(a.data), c.digest)) return "Invalid";
            byte[] rgba;
            try { rgba = inflate(a.data, r[2] * r[3] * 4); } catch (IllegalArgumentException | DataFormatException ex) { return "Invalid"; }
            while (patches.size() >= 64) patches.remove(patches.keySet().iterator().next());
            patches.put(c.tile, new Patch(c.version, rgba)); return "Applied";
        }
        int pendingBytes() { int bytes = 0; for (Assembly a : assemblies.values()) bytes += a.data.length; return bytes; }
        String fingerprint() throws Exception {
            StringBuilder text = new StringBuilder();
            for (Map.Entry<Integer, Patch> entry : new TreeMap<>(patches).entrySet()) {
                if (text.length() != 0) text.append('\n');
                text.append(entry.getKey()).append('|').append(entry.getValue().version).append('|').append(hex(hash(entry.getValue().rgba)));
            }
            return hex(hash(text.toString().getBytes(StandardCharsets.US_ASCII)));
        }
    }
    private static byte[] inflate(byte[] compressed, int expected) throws DataFormatException {
        check(expected > 0 && expected <= 65536 && compressed.length <= 96 * 1024);
        Inflater inflater = new Inflater();
        try {
            inflater.setInput(compressed); byte[] result = new byte[expected + 1]; int count = 0;
            while (!inflater.finished() && count < result.length) {
                int read = inflater.inflate(result, count, result.length - count);
                if (read == 0) break;
                count += read;
            }
            check(count == expected && inflater.finished() && inflater.getRemaining() == 0); return Arrays.copyOf(result, expected);
        } finally { inflater.end(); }
    }
    private static byte[] hash(byte[] bytes) throws Exception { return MessageDigest.getInstance("SHA-256").digest(bytes); }
    private static String hex(byte[] bytes) {
        StringBuilder text = new StringBuilder(); for (byte value : bytes) text.append(String.format(Locale.ROOT, "%02X", value & 255)); return text.toString();
    }
    public static void main(String[] args) throws Exception {
        if (args.length != 1) throw new IllegalArgumentException("Usage: NativeDetailReceiver <interop-vectors.txt>");
        Cache cache = new Cache(); int count = 0;
        for (String line : Files.readAllLines(Paths.get(args[0]), StandardCharsets.UTF_8)) {
            if (line.startsWith("\uFEFF")) line = line.substring(1);
            String[] row = line.split("\t", -1); check(row.length == 6); long now = Long.parseLong(row[1]); String result;
            try {
                switch (row[0]) {
                    case "reset": result = cache.reset(Arrays.stream(row[2].split(",")).mapToLong(Long::parseLong).toArray()); break;
                    case "frame": result = cache.present(Base64.getDecoder().decode(row[2])); break;
                    case "chunk": result = cache.receive(Base64.getDecoder().decode(row[2]), now); break;
                    case "input": result = cache.interaction(); break;
                    case "off": result = cache.off(); break;
                    default: throw new IOException("Unknown test operation");
                }
            } catch (IllegalArgumentException ex) { result = "invalid"; }
            count++;
            if (!result.equals(row[3]) || !cache.fingerprint().equals(row[4]) || cache.pendingBytes() != Integer.parseInt(row[5]))
                throw new IOException("Event " + count + " " + row[0] + ": " + result + " != " + row[3] + ", pixel/memory state mismatch");
        }
        check(count > 50);
        System.out.println("{\"passed\":true,\"events\":" + count + ",\"runtime\":\"Java\",\"scope\":\"NDL1 wire/state/RGBA parity on desktop JVM, not Android UI or device qualification\"}");
    }
}
