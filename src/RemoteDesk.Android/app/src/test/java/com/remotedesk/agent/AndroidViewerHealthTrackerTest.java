package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidViewerHealthTrackerTest {
    @Test
    public void firstSnapshotUsesNoRateSamplesAndPreservesAverages() {
        AndroidViewerHealthTracker tracker = new AndroidViewerHealthTracker();
        tracker.setVideoFormat(AndroidViewerHealthTracker.Codec.JPEG, 1280, 720);
        tracker.recordReceivedFrame(100_000, 2.0d, 1.0d);
        tracker.recordPresentedFrame();

        AndroidViewerHealthTracker.Snapshot snapshot = tracker.snapshot(
            1_000_000_000L,
            false,
            false,
            false);

        assertEquals(AndroidViewerHealthTracker.Codec.JPEG, snapshot.codec);
        assertEquals(1280, snapshot.width);
        assertEquals(720, snapshot.height);
        assertTrue(Double.isNaN(snapshot.presentedFramesPerSecond));
        assertTrue(Double.isNaN(snapshot.megabitsPerSecond));
        assertEquals(2.0d, snapshot.averageCaptureMillis, 0.001d);
        assertEquals(1.0d, snapshot.averageEncodeMillis, 0.001d);
    }

    @Test
    public void laterSnapshotCalculatesIntervalFpsAndBitrate() {
        AndroidViewerHealthTracker tracker = new AndroidViewerHealthTracker();
        tracker.start(1_000_000_000L);
        tracker.recordReceivedFrame(1_000_000, 4.0d, 2.0d);
        tracker.recordReceivedFrame(1_000_000, 6.0d, 4.0d);
        tracker.recordPresentedFrame();
        tracker.recordPresentedFrame();
        tracker.recordMouseAck(2_500L, 3L);

        AndroidViewerHealthTracker.Snapshot snapshot = tracker.snapshot(
            2_000_000_000L,
            true,
            true,
            true);

        assertEquals(2.0d, snapshot.presentedFramesPerSecond, 0.001d);
        assertEquals(16.0d, snapshot.megabitsPerSecond, 0.001d);
        assertEquals(5.0d, snapshot.averageCaptureMillis, 0.001d);
        assertEquals(3.0d, snapshot.averageEncodeMillis, 0.001d);
        assertEquals(2_500L, snapshot.mouseAckEwmaMicros);
        assertEquals(3L, snapshot.mouseAckSampleCount);
    }

    @Test
    public void startMakesTheFirstUiSnapshotAnActualRateWindow() {
        AndroidViewerHealthTracker tracker = new AndroidViewerHealthTracker();
        tracker.start(0L);
        tracker.recordReceivedFrame(125_000, 1.0d, 1.0d);
        tracker.recordPresentedFrame();

        AndroidViewerHealthTracker.Snapshot snapshot = tracker.snapshot(
            1_000_000_000L,
            false,
            false,
            true);

        assertEquals(1.0d, snapshot.presentedFramesPerSecond, 0.001d);
        assertEquals(1.0d, snapshot.megabitsPerSecond, 0.001d);
    }

    @Test
    public void repeatedSnapshotsRemainUnknownUntilAFrameSampleExists() {
        AndroidViewerHealthTracker tracker = new AndroidViewerHealthTracker();
        tracker.snapshot(1_000_000_000L, false, false, false);

        AndroidViewerHealthTracker.Snapshot snapshot = tracker.snapshot(
            2_000_000_000L,
            false,
            false,
            false);

        assertTrue(Double.isNaN(snapshot.presentedFramesPerSecond));
        assertTrue(Double.isNaN(snapshot.megabitsPerSecond));
    }

    @Test
    public void missingCodecTelemetryDoesNotReuseOldJpegCountsAsZeroFps() {
        AndroidViewerHealthTracker tracker = new AndroidViewerHealthTracker();
        tracker.start(0L);
        tracker.recordPresentedFrame();
        tracker.snapshot(1_000_000_000L, false, false, true);
        tracker.markPresentationTelemetryUnavailable();
        AndroidViewerHealthTracker.Snapshot snapshot = tracker.snapshot(
            2_000_000_000L, false, false, true);
        assertTrue(Double.isNaN(snapshot.presentedFramesPerSecond));
        assertTrue(AndroidViewerHealthFormatter.format(snapshot).contains("FPS —"));
        assertEquals(1L, snapshot.presentedFrameCount);
    }

    @Test
    public void actualRenderCallbackRestoresRateMeasurement() {
        AndroidViewerHealthTracker tracker = new AndroidViewerHealthTracker();
        tracker.start(0L);
        tracker.markPresentationTelemetryUnavailable();
        tracker.recordPresentedFrame();
        AndroidViewerHealthTracker.Snapshot snapshot = tracker.snapshot(
            1_000_000_000L, false, false, true);
        assertEquals(1.0d, snapshot.presentedFramesPerSecond, 0.001d);
    }
}
