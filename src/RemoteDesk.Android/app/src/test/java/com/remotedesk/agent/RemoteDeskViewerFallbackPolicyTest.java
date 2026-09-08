package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class RemoteDeskViewerFallbackPolicyTest {
    @Test
    public void jpegFallbackFailureClosesOnlyTheCurrentMatchingGeneration() {
        assertTrue(RemoteDeskViewerActivity.shouldCloseOwnerAfterJpegFallbackFailure(
            7L,
            7L,
            true));
        assertFalse(RemoteDeskViewerActivity.shouldCloseOwnerAfterJpegFallbackFailure(
            7L,
            8L,
            true));
        assertFalse(RemoteDeskViewerActivity.shouldCloseOwnerAfterJpegFallbackFailure(
            7L,
            7L,
            false));
        assertFalse(RemoteDeskViewerActivity.shouldCloseOwnerAfterJpegFallbackFailure(
            0L,
            0L,
            true));
    }
}
