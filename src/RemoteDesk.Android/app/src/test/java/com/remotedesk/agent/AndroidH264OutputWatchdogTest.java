package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertThrows;
import static org.junit.Assert.assertTrue;

import java.util.concurrent.TimeUnit;

import org.junit.Test;

public final class AndroidH264OutputWatchdogTest {
    @Test
    public void productionWindowsAreConservativeAtThirtyFramesPerSecond() {
        assertEquals(4_000L, AndroidH264OutputWatchdog.STARTUP_KEY_FRAME_PROBE_MILLIS);
        assertEquals(8_000L, AndroidH264OutputWatchdog.STARTUP_TIMEOUT_MILLIS);
        assertEquals(6_000L, AndroidH264OutputWatchdog.STALLED_KEY_FRAME_PROBE_MILLIS);
        assertEquals(12_000L, AndroidH264OutputWatchdog.STALLED_TIMEOUT_MILLIS);

        assertTrue(
            AndroidH264OutputWatchdog.STARTUP_TIMEOUT_MILLIS *
                AndroidVideoStreamSettings.H264_TARGET_FPS >= 240_000L);
        assertTrue(
            AndroidH264OutputWatchdog.STALLED_TIMEOUT_MILLIS *
                AndroidVideoStreamSettings.H264_TARGET_FPS >= 360_000L);

        AndroidH264OutputWatchdog watchdog =
            new AndroidH264OutputWatchdog(TimeUnit.SECONDS.toNanos(1L));
        assertEquals(4_000L, watchdog.silenceMillis(TimeUnit.SECONDS.toNanos(5L)));
    }

    @Test
    public void startupRequestsOneKeyFrameThenFallsBackAtDeadline() {
        AndroidH264OutputWatchdog watchdog = testWatchdog(100L);

        assertEquals(AndroidH264OutputWatchdog.Action.None, watchdog.evaluate(139L));
        assertEquals(AndroidH264OutputWatchdog.Action.RequestKeyFrame, watchdog.evaluate(140L));
        assertEquals(AndroidH264OutputWatchdog.Action.None, watchdog.evaluate(179L));
        assertEquals(AndroidH264OutputWatchdog.Action.Fallback, watchdog.evaluate(180L));
        assertFalse(watchdog.hasProducedOutput());
    }

    @Test
    public void normalOutputMovesDeadlineAndStaticSilenceDoesNotTearDownStream() {
        AndroidH264OutputWatchdog watchdog = testWatchdog(100L);

        assertEquals(AndroidH264OutputWatchdog.Action.RequestKeyFrame, watchdog.evaluate(140L));
        assertTrue(watchdog.recordOutput(150L));
        assertTrue(watchdog.hasProducedOutput());
        assertFalse(watchdog.recordOutput(200L));

        assertEquals(AndroidH264OutputWatchdog.Action.None, watchdog.evaluate(259L));
        assertEquals(AndroidH264OutputWatchdog.Action.RequestKeyFrame, watchdog.evaluate(260L));
        assertEquals(AndroidH264OutputWatchdog.Action.None, watchdog.evaluate(319L));
        assertEquals(
            AndroidH264OutputWatchdog.Action.StaticSilence,
            watchdog.evaluate(320L));
        assertEquals(AndroidH264OutputWatchdog.Action.None, watchdog.evaluate(400L));
    }

    @Test
    public void outputAfterAProbeStartsAFreshStallWindow() {
        AndroidH264OutputWatchdog watchdog = testWatchdog(0L);
        watchdog.recordOutput(10L);

        assertEquals(AndroidH264OutputWatchdog.Action.RequestKeyFrame, watchdog.evaluate(70L));
        watchdog.recordOutput(75L);

        assertEquals(AndroidH264OutputWatchdog.Action.None, watchdog.evaluate(134L));
        assertEquals(AndroidH264OutputWatchdog.Action.RequestKeyFrame, watchdog.evaluate(135L));
    }

    @Test
    public void resumedOutputRearmsAStaticSilenceDiagnostic() {
        AndroidH264OutputWatchdog watchdog = testWatchdog(0L);
        watchdog.recordOutput(10L);

        assertEquals(
            AndroidH264OutputWatchdog.Action.StaticSilence,
            watchdog.evaluate(130L));
        assertEquals(AndroidH264OutputWatchdog.Action.None, watchdog.evaluate(140L));

        watchdog.recordOutput(150L);
        assertEquals(
            AndroidH264OutputWatchdog.Action.StaticSilence,
            watchdog.evaluate(270L));
    }

    @Test
    public void configuredRepeatCannotProveThatAStaticCompositorWillProduceFrames() {
        AndroidH264OutputWatchdog watchdog = testWatchdog(0L);
        watchdog.recordOutput(10L);

        assertEquals(
            AndroidH264OutputWatchdog.Action.RequestKeyFrame,
            watchdog.evaluate(70L));
        assertEquals(
            AndroidH264OutputWatchdog.Action.StaticSilence,
            watchdog.evaluate(130L));
        assertEquals(
            AndroidH264OutputWatchdog.Action.None,
            watchdog.evaluate(200L));
    }

    @Test
    public void invalidWindowsAreRejected() {
        assertThrows(
            IllegalArgumentException.class,
            () -> new AndroidH264OutputWatchdog(0L, 0L, 10L, 5L, 10L));
        assertThrows(
            IllegalArgumentException.class,
            () -> new AndroidH264OutputWatchdog(0L, 10L, 10L, 5L, 10L));
    }

    private static AndroidH264OutputWatchdog testWatchdog(long startedAtNanos) {
        return new AndroidH264OutputWatchdog(
            startedAtNanos,
            40L,
            80L,
            60L,
            120L);
    }
}
