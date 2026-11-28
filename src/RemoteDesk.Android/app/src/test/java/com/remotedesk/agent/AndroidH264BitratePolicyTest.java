package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidH264BitratePolicyTest {
    private static final int RATE = 8_000_000;
    private static final long PERIOD = 250_000_000L;
    private static LowLatencyVideoProtocol.FeedbackV2 report(int i, int depth, int bytesPerInterval) {
        return new LowLatencyVideoProtocol.FeedbackV2(i * 250_000L, i * 200L, 0, 7,
                i * 200L, (long)i * bytesPerInterval, 0, 0, i * 7L, i * 7L, 0, 0, depth, 4, 0);
    }

    @Test public void verifiedDeliveryRestoresAndBoostsWithinCodecAndNetworkLimits() {
        AndroidH264BitratePolicy.UdpHeadroom policy = new AndroidH264BitratePolicy.UdpHeadroom();
        for (int i = 1; i <= 25; i++) {
            policy.observe(report(i - 1, 0, 300000), report(i, 0, 300000), i * PERIOD);
            if (i < 17) assertEquals(0, policy.request(i * PERIOD, RATE, RATE, 16_000_000));
        }
        assertEquals(8_800_000, policy.request(25 * PERIOD, RATE, RATE, 16_000_000));
        assertEquals(0, policy.request(25 * PERIOD + 1, RATE, RATE, 16_000_000));
        for (int i = 26; i <= 45; i++) policy.observe(report(i-1,0,300000),report(i,0,300000),i*PERIOD);
        assertEquals(9_000_000, policy.request(45 * PERIOD, 8_800_000, RATE, 9_000_000));
        policy.observe(report(45,0,300000), report(46,2,300000), 46 * PERIOD);
        assertEquals(RATE, policy.request(46 * PERIOD, 9_000_000, RATE, 16_000_000));
        assertEquals(0, policy.request(46 * PERIOD + 100, 9_000_000, RATE, 16_000_000));
        for (int i = 47; i <= 70; i++) policy.observe(report(i-1,0,300000),report(i,0,300000),i*PERIOD);
        assertEquals(0, policy.request(70 * PERIOD, RATE, RATE, 16_000_000));
        assertEquals(RATE, policy.request(75 * PERIOD, 8_800_000, RATE, 16_000_000));
    }

    @Test public void aQuietStreamDoesNotProveHeadroomAndRecoveryCanStartBelowNominal() {
        AndroidH264BitratePolicy.UdpHeadroom policy = new AndroidH264BitratePolicy.UdpHeadroom();
        for (int i = 1; i <= 25; i++) policy.observe(report(i-1,0,5000),report(i,0,5000),i*PERIOD);
        assertEquals(0, policy.request(25 * PERIOD, RATE, RATE, 16_000_000));
        policy = new AndroidH264BitratePolicy.UdpHeadroom();
        for (int i = 1; i <= 25; i++) policy.observe(report(i-1,0,200000),report(i,0,200000),i*PERIOD);
        assertEquals(4_400_000, policy.request(25 * PERIOD, 4_000_000, RATE, 16_000_000));
    }
    @Test public void staticScreenAndSlowEncoderKeepTheirQuality() {
        assertFalse(AndroidH264BitratePolicy.isNetworkBound(1, 30, 0.2, 100000, 8000000));
        assertFalse(AndroidH264BitratePolicy.isNetworkBound(10, 60, 0.3, 2000000, 8000000));
    }
    @Test public void SustainedSocketPressureReducesTheNetworkBoundStream() {
        assertTrue(AndroidH264BitratePolicy.isNetworkBound(9.5, 30, 102, 19000000, 16000000));
        assertTrue(AndroidH264BitratePolicy.isNetworkBound(20, 30, 25, 6000000, 8000000));
    }
    @Test public void highBitrateAloneAndHealthyCadenceAreNotCongestion() {
        assertFalse(AndroidH264BitratePolicy.isNetworkBound(30, 30, 0.2, 19000000, 8000000));
        assertFalse(AndroidH264BitratePolicy.isNetworkBound(30, 30, 20, 8000000, 8000000));
    }
    @Test public void invalidMetricsCannotLowerQuality() {
        assertFalse(AndroidH264BitratePolicy.isNetworkBound(Double.NaN, 30, 100, 1000000, 8000000));
        assertFalse(AndroidH264BitratePolicy.isNetworkBound(5, 30, Double.POSITIVE_INFINITY, 1000000, 8000000));
        assertFalse(AndroidH264BitratePolicy.isNetworkBound(5, 30, 100, 0, 8000000));
        assertFalse(AndroidH264BitratePolicy.isNetworkBound(5, 0, 100, 1000000, 8000000));
    }
}
