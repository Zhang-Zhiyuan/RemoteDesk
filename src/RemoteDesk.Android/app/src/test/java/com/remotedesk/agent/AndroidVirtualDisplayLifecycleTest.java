package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.fail;

import org.junit.Test;

public final class AndroidVirtualDisplayLifecycleTest {
    @Test
    public void jpegVideoJpegUsesOneVirtualDisplay() {
        AndroidVirtualDisplayLifecycle lifecycle = startWithJpeg();

        lifecycle.recordSurfaceAttached(AndroidVirtualDisplayLifecycle.Output.Video);
        lifecycle.recordSurfaceAttached(AndroidVirtualDisplayLifecycle.Output.Jpeg);

        assertEquals(1, lifecycle.getCreateCount());
        assertFalse(lifecycle.shouldCreateVirtualDisplay());
        assertEquals(
            AndroidVirtualDisplayLifecycle.Output.Jpeg,
            lifecycle.getOutput());
    }

    @Test
    public void h264StartupFailureReturnsToJpegWithoutRecreation() {
        AndroidVirtualDisplayLifecycle lifecycle = startWithJpeg();

        lifecycle.recordSurfaceAttached(AndroidVirtualDisplayLifecycle.Output.Video);
        // Encoder startup failed after its input Surface was attached. close()
        // restores the JPEG ImageReader Surface before releasing that input.
        lifecycle.recordSurfaceAttached(AndroidVirtualDisplayLifecycle.Output.Jpeg);

        assertEquals(1, lifecycle.getCreateCount());
        assertEquals(
            AndroidVirtualDisplayLifecycle.Output.Jpeg,
            lifecycle.getOutput());
    }

    @Test
    public void resizeKeepsTheExistingVirtualDisplay() {
        AndroidVirtualDisplayLifecycle lifecycle = startWithJpeg();

        assertTrue(lifecycle.needsResize(720, 1280, 420));
        lifecycle.recordResize(720, 1280, 420);
        lifecycle.recordSurfaceAttached(AndroidVirtualDisplayLifecycle.Output.Video);

        assertEquals(1, lifecycle.getCreateCount());
        assertEquals(720, lifecycle.getWidth());
        assertEquals(1280, lifecycle.getHeight());
        assertEquals(420, lifecycle.getDensityDpi());
        assertEquals(
            AndroidVirtualDisplayLifecycle.Output.Video,
            lifecycle.getOutput());
    }

    @Test
    public void clearAllowsExactlyOneDisplayForTheNextProjection() {
        AndroidVirtualDisplayLifecycle lifecycle = startWithJpeg();
        lifecycle.clearProjection();

        assertFalse(lifecycle.isDisplayCreated());
        assertFalse(lifecycle.shouldCreateVirtualDisplay());
        assertEquals(
            AndroidVirtualDisplayLifecycle.Output.None,
            lifecycle.getOutput());

        lifecycle.beginProjection();
        assertTrue(lifecycle.shouldCreateVirtualDisplay());
        lifecycle.recordVirtualDisplayCreated(
            900,
            1600,
            440,
            AndroidVirtualDisplayLifecycle.Output.Jpeg);
        assertEquals(1, lifecycle.getCreateCount());
    }

    @Test
    public void secondCreateInOneProjectionIsRejected() {
        AndroidVirtualDisplayLifecycle lifecycle = startWithJpeg();

        try {
            lifecycle.recordVirtualDisplayCreated(
                900,
                1600,
                440,
                AndroidVirtualDisplayLifecycle.Output.Video);
            fail("A second VirtualDisplay must be rejected.");
        } catch (IllegalStateException expected) {
            assertEquals(1, lifecycle.getCreateCount());
        }
    }

    @Test
    public void lateStopFromOldProjectionCannotOwnNewProjection() {
        AndroidVirtualDisplayLifecycle lifecycle =
            new AndroidVirtualDisplayLifecycle();
        long first = lifecycle.beginProjection();
        lifecycle.recordVirtualDisplayCreated(
            900,
            1600,
            440,
            AndroidVirtualDisplayLifecycle.Output.Jpeg);
        lifecycle.clearProjection();

        long second = lifecycle.beginProjection();
        lifecycle.recordVirtualDisplayCreated(
            900,
            1600,
            440,
            AndroidVirtualDisplayLifecycle.Output.Jpeg);

        assertFalse(lifecycle.isCurrentProjection(first));
        assertTrue(lifecycle.isCurrentProjection(second));
        assertEquals(second, lifecycle.getProjectionGeneration());
    }

    @Test
    public void intentionalClearInvalidatesLateStopCallback() {
        AndroidVirtualDisplayLifecycle lifecycle =
            new AndroidVirtualDisplayLifecycle();
        long generation = lifecycle.beginProjection();
        lifecycle.recordVirtualDisplayCreated(
            900,
            1600,
            440,
            AndroidVirtualDisplayLifecycle.Output.Jpeg);

        lifecycle.clearProjection();

        assertFalse(lifecycle.isCurrentProjection(generation));
        assertEquals(0L, lifecycle.getProjectionGeneration());
    }

    @Test
    public void capturedContentResizeIsScopedToCurrentProjection() {
        AndroidVirtualDisplayLifecycle lifecycle =
            new AndroidVirtualDisplayLifecycle();
        long first = lifecycle.beginProjection();

        assertTrue(lifecycle.recordCapturedContentResize(first, 721, 1281));
        assertFalse(lifecycle.recordCapturedContentResize(first, 721, 1281));
        assertEquals(721, lifecycle.getCapturedContentWidth());
        assertEquals(1281, lifecycle.getCapturedContentHeight());

        lifecycle.clearProjection();
        long second = lifecycle.beginProjection();
        assertFalse(lifecycle.recordCapturedContentResize(first, 900, 1600));
        assertFalse(lifecycle.recordCapturedContentResize(second, 0, 1600));
        assertFalse(lifecycle.hasCapturedContentSize());
        assertTrue(lifecycle.recordCapturedContentResize(second, 900, 1600));
    }

    private static AndroidVirtualDisplayLifecycle startWithJpeg() {
        AndroidVirtualDisplayLifecycle lifecycle =
            new AndroidVirtualDisplayLifecycle();
        lifecycle.beginProjection();
        lifecycle.recordVirtualDisplayCreated(
            900,
            1600,
            440,
            AndroidVirtualDisplayLifecycle.Output.Jpeg);
        return lifecycle;
    }
}
