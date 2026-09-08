package com.remotedesk.agent;

import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public final class AndroidViewerH264DimensionsTest {
    @Test
    public void widthAndHeightArePublishedAsOnePair() {
        AndroidViewerH264Dimensions dimensions = new AndroidViewerH264Dimensions();
        assertFalse(dimensions.snapshot().isValid());

        dimensions.set(3840, 2160);
        AndroidViewerH264Dimensions.Snapshot snapshot = dimensions.snapshot();

        assertEquals(3840, snapshot.width);
        assertEquals(2160, snapshot.height);
        assertTrue(snapshot.isValid());
    }

    @Test
    public void replacementOwnerCannotObserveStaleOwnerDimensions() {
        AndroidViewerH264Dimensions staleOwner = new AndroidViewerH264Dimensions();
        staleOwner.set(1920, 1080);

        AndroidViewerH264Dimensions replacementOwner = new AndroidViewerH264Dimensions();

        assertTrue(staleOwner.snapshot().isValid());
        assertFalse(replacementOwner.snapshot().isValid());
    }

    @Test
    public void invalidFrameCannotOverwriteTheLastValidPair() {
        AndroidViewerH264Dimensions dimensions = new AndroidViewerH264Dimensions();
        dimensions.set(1280, 720);
        dimensions.set(0, 2160);

        AndroidViewerH264Dimensions.Snapshot snapshot = dimensions.snapshot();
        assertEquals(1280, snapshot.width);
        assertEquals(720, snapshot.height);
    }
}
