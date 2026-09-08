package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import org.junit.Test;

public final class AndroidViewerClosePolicyTest {
    @Test
    public void uiCloseSignalsAndSchedulesWithoutRunningHeavyTeardownInline()
        throws Exception {
        AtomicBoolean closed = new AtomicBoolean();
        AtomicBoolean teardownRan = new AtomicBoolean();
        CountDownLatch release = new CountDownLatch(1);
        Runnable[] scheduled = new Runnable[1];
        AndroidViewerClosePolicy.Owner owner = new AndroidViewerClosePolicy.Owner() {
            @Override
            public boolean signalClose() {
                return closed.compareAndSet(false, true);
            }

            @Override
            public void teardown() {
                try {
                    release.await(5, TimeUnit.SECONDS);
                } catch (InterruptedException ex) {
                    Thread.currentThread().interrupt();
                }
                teardownRan.set(true);
            }
        };

        long startedAt = System.nanoTime();
        assertTrue(AndroidViewerClosePolicy.closeFromUi(owner, work -> {
            scheduled[0] = work;
            return true;
        }));

        assertTrue(TimeUnit.NANOSECONDS.toMillis(
            System.nanoTime() - startedAt) < 100L);
        assertTrue(closed.get());
        assertFalse(teardownRan.get());
        assertEquals(1, scheduled.length);
        release.countDown();
    }

    @Test
    public void duplicateUiCloseNeitherWaitsNorSchedulesAgain() {
        AtomicBoolean closed = new AtomicBoolean(true);
        int[] scheduled = { 0 };
        AndroidViewerClosePolicy.Owner owner = new AndroidViewerClosePolicy.Owner() {
            @Override
            public boolean signalClose() {
                return closed.compareAndSet(false, true);
            }

            @Override
            public void teardown() {
            }
        };

        assertFalse(AndroidViewerClosePolicy.closeFromUi(owner, work -> {
            scheduled[0]++;
            return true;
        }));
        assertEquals(0, scheduled[0]);
    }
}
