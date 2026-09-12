package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidH264BitratePolicyTest {
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
