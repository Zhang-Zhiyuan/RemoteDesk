package com.remotedesk.agent;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.util.Arrays;

/** Narrow workaround for Android 8.0's software AVC decoder, not a transcoder. */
final class AndroidH264SpsCompatibility {
    private static final int MAX_SPS_BYTES = 4096;
    private final boolean enabled;
    private byte[] cachedSps;
    private byte[] cachedReplacement;
    private int rewrittenParameterSets;

    AndroidH264SpsCompatibility(String codecName, int sdkInt) {
        // Only this exact platform/codec was reproduced. Vendor codecs and
        // newer Android versions keep the original zero-copy input path.
        enabled = sdkInt == 26 && "OMX.google.h264.decoder".equals(codecName);
    }

    int rewrittenParameterSetCount() { return rewrittenParameterSets; }

    void putAccessUnit(ByteBuffer target, byte[] bytes) {
        if (!enabled) {
            target.put(bytes);
            return;
        }
        int copied = 0;
        int start = findStart(bytes, 0);
        while (start >= 0) {
            int header = start + (bytes[start + 2] == 1 ? 3 : 4);
            int next = findStart(bytes, header);
            int end = next < 0 ? bytes.length : next;
            if (header < end && (bytes[header] & 31) == 7 && end - header <= MAX_SPS_BYTES) {
                byte[] original = Arrays.copyOfRange(bytes, header, end);
                if (!Arrays.equals(original, cachedSps)) {
                    cachedSps = original;
                    cachedReplacement = rewriteSps(original);
                }
                if (cachedReplacement != null) {
                    target.put(bytes, copied, header - copied);
                    target.put(cachedReplacement);
                    copied = end;
                    rewrittenParameterSets++;
                }
            }
            start = next;
        }
        target.put(bytes, copied, bytes.length - copied);
    }

    private static int findStart(byte[] bytes, int from) {
        for (int i = from; i + 3 < bytes.length; i++) {
            if (bytes[i] == 0 && bytes[i + 1] == 0 &&
                (bytes[i + 2] == 1 || (bytes[i + 2] == 0 && bytes[i + 3] == 1))) return i;
        }
        return -1;
    }

    // Returns null for unsupported, dependent, interlaced or malformed SPS.
    // Only remove the final optional VUI restriction section of progressive,
    // zero-reference/zero-reorder streams. Color, timing, SPS IDs, PPS and all
    // coded slices are retained. No stream configuration is guessed.
    static byte[] rewriteSps(byte[] nal) {
        if (nal == null || nal.length < 4 || nal.length > MAX_SPS_BYTES ||
            (nal[0] & 0x9f) != 7) return null;
        try {
            byte[] rbsp = unescape(nal);
            Bits bits = new Bits(rbsp);
            bits.skip(8);
            int profile = bits.read(8);
            bits.skip(16);
            if (bits.ue() > 31) return null; // seq_parameter_set_id
            if (profile == 100) {
                int chroma = bits.ue();
                if (chroma > 3) return null;
                if (chroma == 3) bits.skip(1);
                if (bits.ue() != 0 || bits.ue() != 0) return null; // 8-bit only
                bits.skip(1);
                if (bits.flag()) {
                    for (int i = 0; i < (chroma == 3 ? 12 : 8); i++) {
                        if (bits.flag()) skipScalingList(bits, i < 6 ? 16 : 64);
                    }
                }
            } else if (profile != 66 && profile != 77 && profile != 88) return null;
            if (bits.ue() > 12) return null;
            int poc = bits.ue();
            if (poc == 0) {
                if (bits.ue() > 12) return null;
            } else if (poc == 1) {
                bits.skip(1);
                bits.se(); bits.se();
                int cycle = bits.ue();
                if (cycle > 255) return null;
                for (int i = 0; i < cycle; i++) bits.se();
            } else if (poc != 2) return null;
            if (bits.ue() != 0) return null; // max_num_ref_frames
            bits.skip(1);
            if (bits.ue() > 2047 || bits.ue() > 2047) return null;
            if (!bits.flag()) return null; // frame_mbs_only_flag
            bits.skip(1);
            if (bits.flag()) for (int i = 0; i < 4; i++) bits.ue();
            if (!bits.flag()) return null; // vui_parameters_present_flag
            if (bits.flag() && bits.read(8) == 255) bits.skip(32);
            if (bits.flag()) bits.skip(1);
            if (bits.flag()) {
                bits.skip(4);
                if (bits.flag()) bits.skip(24);
            }
            if (bits.flag()) { bits.ue(); bits.ue(); }
            if (bits.flag()) bits.skip(65);
            boolean nalHrd = bits.flag();
            if (nalHrd) skipHrd(bits);
            boolean vclHrd = bits.flag();
            if (vclHrd) skipHrd(bits);
            if (nalHrd || vclHrd) bits.skip(1);
            bits.skip(1); // pic_struct_present_flag
            int restrictionOffset = bits.position;
            if (!bits.flag()) return null;
            bits.skip(1);
            for (int i = 0; i < 4; i++) bits.ue();
            if (bits.ue() != 0 || bits.ue() > 1) return null;
            if (!bits.flag() || bits.remaining() > 7) return null; // rbsp_stop_one_bit
            while (bits.remaining() > 0) if (bits.flag()) return null;

            byte[] rewritten = new byte[(restrictionOffset + 2 + 7) / 8];
            for (int i = 0; i < restrictionOffset; i++) {
                if ((rbsp[i / 8] & (1 << (7 - i % 8))) != 0)
                    rewritten[i / 8] |= (byte)(1 << (7 - i % 8));
            }
            int stop = restrictionOffset + 1;
            rewritten[stop / 8] |= (byte)(1 << (7 - stop % 8));
            return escape(rewritten);
        } catch (IllegalArgumentException ignored) {
            return null;
        }
    }

    private static void skipScalingList(Bits bits, int count) {
        int last = 8, next = 8;
        for (int i = 0; i < count; i++) {
            if (next != 0) next = (int)(((long)last + bits.se() + 256) & 255);
            if (next != 0) last = next;
        }
    }

    private static void skipHrd(Bits bits) {
        int count = bits.ue();
        if (count > 31) throw new IllegalArgumentException("Invalid HRD count");
        bits.skip(8);
        for (int i = 0; i <= count; i++) { bits.ue(); bits.ue(); bits.skip(1); }
        bits.skip(20);
    }

    private static byte[] unescape(byte[] nal) {
        ByteArrayOutputStream output = new ByteArrayOutputStream(nal.length);
        int zeroes = 0;
        for (int i = 0; i < nal.length; i++) {
            int value = nal[i] & 255;
            if (zeroes >= 2 && value == 3) {
                if (i + 1 == nal.length || (nal[i + 1] & 255) > 3)
                    throw new IllegalArgumentException("Invalid emulation prevention");
                zeroes = 0;
                continue;
            }
            output.write(value);
            zeroes = value == 0 ? zeroes + 1 : 0;
        }
        return output.toByteArray();
    }

    private static byte[] escape(byte[] rbsp) {
        ByteArrayOutputStream output = new ByteArrayOutputStream(rbsp.length + 8);
        int zeroes = 0;
        for (byte next : rbsp) {
            int value = next & 255;
            if (zeroes >= 2 && value <= 3) { output.write(3); zeroes = 0; }
            output.write(value);
            zeroes = value == 0 ? zeroes + 1 : 0;
        }
        return output.toByteArray();
    }

    private static final class Bits {
        final byte[] bytes;
        int position;
        Bits(byte[] bytes) { this.bytes = bytes; }
        int remaining() { return bytes.length * 8 - position; }
        void skip(int count) {
            if (count < 0 || count > remaining()) throw new IllegalArgumentException("Truncated SPS");
            position += count;
        }
        int read(int count) {
            if (count < 0 || count > 31 || count > remaining()) throw new IllegalArgumentException("Truncated SPS");
            int value = 0;
            for (int i = 0; i < count; i++, position++)
                value = (value << 1) | ((bytes[position / 8] >> (7 - position % 8)) & 1);
            return value;
        }
        boolean flag() { return read(1) != 0; }
        int ue() {
            int zeroes = 0;
            while (!flag()) if (++zeroes > 30) throw new IllegalArgumentException("Oversized SPS integer");
            return ((1 << zeroes) - 1) + read(zeroes);
        }
        int se() {
            int value = ue();
            return (value & 1) == 0 ? -(value / 2) : value / 2 + 1;
        }
    }
}
