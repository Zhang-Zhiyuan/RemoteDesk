package com.remotedesk.agent;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;

import org.junit.Test;

public final class AndroidCaptureGeometryPolicyTest {
    @Test
    public void vendorAlignmentSurvivesRepeatedDisplayRefreshGeometry() {
        AndroidCaptureGeometryPolicy policy =
            new AndroidCaptureGeometryPolicy();
        policy.configureEncoderAlignment(16, 32);

        assertArrayEquals(
            new int[] { 896, 1600 },
            policy.fitWithinMaxEdge(900, 1600, 1600));
        assertArrayEquals(
            new int[] { 896, 1600 },
            policy.fitWithinMaxEdge(900, 1600, 1600));
        assertArrayEquals(
            new int[] { 896, 1600 },
            policy.fitWithinMaxEdge(1800, 3200, 1600));
        assertEquals(16, policy.getWidthAlignment());
        assertEquals(32, policy.getHeightAlignment());
    }

    @Test
    public void newProjectionResetsCompatibilityAlignment() {
        AndroidCaptureGeometryPolicy policy =
            new AndroidCaptureGeometryPolicy();
        policy.configureEncoderAlignment(16, 32);
        policy.reset();

        assertArrayEquals(
            new int[] { 900, 1600 },
            policy.fitWithinMaxEdge(900, 1600, 1600));
        assertEquals(2, policy.getWidthAlignment());
        assertEquals(2, policy.getHeightAlignment());
    }
}
