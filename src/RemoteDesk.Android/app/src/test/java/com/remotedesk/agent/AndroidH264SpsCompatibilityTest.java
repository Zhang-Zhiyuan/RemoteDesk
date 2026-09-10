package com.remotedesk.agent;

import static org.junit.Assert.*;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.Random;
import org.junit.Test;

public final class AndroidH264SpsCompatibilityTest {
    // Actual 1080p Jetson and x264 SPS; original / SPS-only compatibility pair.
    private static final byte[] JETSON = hex("67640028ac2cb01e0089f966a0202028000003000800000301e478442350");
    private static final byte[] JETSON_FIXED = hex("67640028ac2cb01e0089f966a0202028000003000800000301e420");
    private static final byte[] X264 = hex("6742c028dc0780227e5840000003004000000f23c60ce0");
    private static final byte[] X264_FIXED = hex("6742c028dc0780227e5840000003004000000f21");

    @Test public void capturedJetsonSpsRetainsEveryColorAndTimingBit() {
        assertArrayEquals(JETSON_FIXED, AndroidH264SpsCompatibility.rewriteSps(JETSON));
    }

    @Test public void capturedBaselineX264SpsIsAlsoCompatible() {
        assertArrayEquals(X264_FIXED, AndroidH264SpsCompatibility.rewriteSps(X264));
    }

    @Test public void noRestrictionMeansNoRewrite() {
        assertNull(AndroidH264SpsCompatibility.rewriteSps(JETSON_FIXED));
        assertNull(AndroidH264SpsCompatibility.rewriteSps(X264_FIXED));
    }

    @Test public void onlyReproducedPlatformAndCodecCanRewrite() {
        byte[] input = join(hex("000001"), JETSON, hex("0000000165b8041234"));
        for (int sdk : new int[] {25, 27, 28, 29, 30, 36}) {
            var filter = new AndroidH264SpsCompatibility("OMX.google.h264.decoder", sdk);
            assertArrayEquals(input, transform(filter, input));
            assertEquals(0, filter.rewrittenParameterSetCount());
        }
        for (String codec : new String[] {null, "c2.android.avc.decoder", "OMX.qcom.video.decoder.avc",
                "OMX.google.h264.decoder.vendor", "omx.google.h264.decoder"}) {
            var filter = new AndroidH264SpsCompatibility(codec, 26);
            assertArrayEquals(input, transform(filter, input));
            assertEquals(0, filter.rewrittenParameterSetCount());
        }
    }

    @Test public void accessUnitPreservesAudPpsSeiSlicesAndMixedPrefixes() {
        byte[] before = hex("000000010950000001");
        byte[] after = hex("0000000168ee3cb00000010605040000030100000165b8040027fd09cf");
        byte[] input = join(before, JETSON, after);
        byte[] original = input.clone();
        var filter = legacy();
        for (int i = 0; i < 3; i++)
            assertArrayEquals(join(before, JETSON_FIXED, after), transform(filter, input));
        assertArrayEquals(original, input);
        assertEquals(3, filter.rewrittenParameterSetCount());
    }

    @Test public void changingSpsNeverReusesPreviousReplacement() {
        var filter = legacy();
        byte[] prefix = hex("00000001");
        assertArrayEquals(join(prefix, JETSON_FIXED), transform(filter, join(prefix, JETSON)));
        assertArrayEquals(join(prefix, X264_FIXED), transform(filter, join(prefix, X264)));
        byte[] dependent = syntheticSps(1, 0, 1, true, false);
        assertArrayEquals(join(prefix, dependent), transform(filter, join(prefix, dependent)));
        assertArrayEquals(join(prefix, JETSON_FIXED), transform(filter, join(prefix, JETSON)));
        assertEquals(3, filter.rewrittenParameterSetCount());
    }

    @Test public void emptyAndNonSpsAccessUnitsAreUnchanged() {
        for (byte[] input : new byte[][] {new byte[0], hex("000001"), hex("00000001"),
                hex("00000165b804000003001122"), hex("123456"), hex("00000167000000")})
            assertArrayEquals(input, transform(legacy(), input));
    }

    @Test public void multipleSpsInOneAccessUnitAreHandledWithoutDroppingOtherNals() {
        byte[] gap = hex("00000168ee3c8000000001");
        byte[] input = join(hex("000001"), JETSON, gap, X264, hex("00000165b82233"));
        assertArrayEquals(join(hex("000001"), JETSON_FIXED, gap, X264_FIXED, hex("00000165b82233")),
            transform(legacy(), input));
    }

    @Test public void referencesReorderingInterlaceAndLargerBufferingAreNotChanged() {
        assertNull(AndroidH264SpsCompatibility.rewriteSps(syntheticSps(1, 0, 1, true, false)));
        assertNull(AndroidH264SpsCompatibility.rewriteSps(syntheticSps(0, 1, 1, true, false)));
        assertNull(AndroidH264SpsCompatibility.rewriteSps(syntheticSps(0, 0, 2, true, false)));
        assertNull(AndroidH264SpsCompatibility.rewriteSps(syntheticSps(0, 0, 1, false, false)));
        assertNotNull(AndroidH264SpsCompatibility.rewriteSps(syntheticSps(0, 0, 1, true, false)));
    }

    @Test public void optionalVuiAndHrdFieldsAreRetained() {
        byte[] rewritten = AndroidH264SpsCompatibility.rewriteSps(syntheticSps(0, 0, 1, true, true));
        assertNotNull(rewritten);
        assertNull(AndroidH264SpsCompatibility.rewriteSps(rewritten));
    }

    @Test public void truncatedAndMalformedSpsFailClosed() {
        assertNull(AndroidH264SpsCompatibility.rewriteSps(null));
        assertNull(AndroidH264SpsCompatibility.rewriteSps(new byte[4097]));
        for (byte[] valid : new byte[][] {JETSON, X264})
            for (int n = 0; n < valid.length; n++)
                assertNull("truncated at " + n, AndroidH264SpsCompatibility.rewriteSps(Arrays.copyOf(valid, n)));
        byte[] unsupported = JETSON.clone(); unsupported[1] = 110;
        assertNull(AndroidH264SpsCompatibility.rewriteSps(unsupported));
        byte[] forbidden = JETSON.clone(); forbidden[0] |= (byte)128;
        assertNull(AndroidH264SpsCompatibility.rewriteSps(forbidden));
        byte[] badEscape = JETSON.clone(); badEscape[19] = (byte)255;
        assertNull(AndroidH264SpsCompatibility.rewriteSps(badEscape));
        byte[] badTail = X264.clone(); badTail[badTail.length - 1] |= 1;
        assertNull(AndroidH264SpsCompatibility.rewriteSps(badTail));
        assertNull(AndroidH264SpsCompatibility.rewriteSps(join(hex("67420028"), new byte[100])));
    }

    @Test public void boundedMalformedInputsNeverCrashOrGrow() {
        Random random = new Random(20260909);
        for (int i = 0; i < 2000; i++) {
            byte[] nal = new byte[random.nextInt(512)];
            random.nextBytes(nal);
            if (nal.length > 0) nal[0] = 0x67;
            byte[] replacement = AndroidH264SpsCompatibility.rewriteSps(nal);
            if (replacement != null) assertTrue(replacement.length <= nal.length);
            byte[] input = join(hex("00000001"), nal);
            assertTrue(transform(legacy(), input).length <= input.length);
        }
    }

    private static AndroidH264SpsCompatibility legacy() {
        return new AndroidH264SpsCompatibility("OMX.google.h264.decoder", 26);
    }
    private static byte[] transform(AndroidH264SpsCompatibility filter, byte[] input) {
        ByteBuffer target = ByteBuffer.allocate(input.length);
        filter.putAccessUnit(target, input);
        return Arrays.copyOf(target.array(), target.position());
    }
    private static byte[] hex(String text) {
        byte[] bytes = new byte[text.length() / 2];
        for (int i = 0; i < bytes.length; i++) bytes[i] = (byte)Integer.parseInt(text.substring(i * 2, i * 2 + 2), 16);
        return bytes;
    }
    private static byte[] join(byte[]... values) {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        for (byte[] value : values) output.write(value, 0, value.length);
        return output.toByteArray();
    }
    private static byte[] syntheticSps(int refs, int reorder, int buffering, boolean progressive, boolean hrd) {
        Writer out = new Writer();
        out.value(0x67420028, 32); out.ue(0); out.ue(0); out.ue(2); out.ue(refs);
        out.value(0, 1); out.ue(119); out.ue(67); out.value(progressive ? 1 : 0, 1);
        if (!progressive) out.value(0, 1);
        out.value(1, 1); out.value(0, 1); out.value(1, 1); // direct, crop, VUI
        out.value(1, 1); out.value(255, 8); out.value(1, 16); out.value(1, 16);
        out.value(1, 1); out.value(0, 1); // overscan
        out.value(1, 1); out.value(5, 3); out.value(0, 1); out.value(1, 1);
        out.value(1, 8); out.value(1, 8); out.value(1, 8); // BT.709
        out.value(1, 1); out.ue(0); out.ue(0); out.value(0, 1); // chroma, timing absent
        out.value(hrd ? 1 : 0, 1);
        if (hrd) { out.ue(0); out.value(0, 8); out.ue(0); out.ue(0); out.value(1, 1); out.value(0, 20); }
        out.value(0, 1); if (hrd) out.value(1, 1); // VCL HRD, low-delay
        out.value(0, 1); out.value(1, 1); out.value(1, 1); // pic_struct, restriction, motion
        for (int i = 0; i < 4; i++) out.ue(0);
        out.ue(reorder); out.ue(buffering); out.value(1, 1);
        return out.finish();
    }
    private static final class Writer {
        StringBuilder bits = new StringBuilder();
        void value(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.append((value >>> i) & 1); }
        void ue(int value) {
            String code = Integer.toBinaryString(value + 1);
            for (int i = 1; i < code.length(); i++) bits.append('0');
            bits.append(code);
        }
        byte[] finish() {
            while (bits.length() % 8 != 0) bits.append('0');
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            int zeroes = 0;
            for (int i = 0; i < bits.length(); i += 8) {
                int value = Integer.parseInt(bits.substring(i, i + 8), 2);
                if (zeroes >= 2 && value <= 3) { output.write(3); zeroes = 0; }
                output.write(value); zeroes = value == 0 ? zeroes + 1 : 0;
            }
            return output.toByteArray();
        }
    }
}
