package com.remotedesk.agent;

import org.junit.Test;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import static org.junit.Assert.*;

public class AndroidReceivedFilesLoadStateTest {
    @Test public void repeatedTapsNeverQueueMultipleStorageScans() {
        var state = new AndroidReceivedFilesLoadState();
        long request = state.begin();
        assertTrue(request > 0);
        assertEquals(0, state.begin());
        assertTrue(state.finish(request));
        assertTrue(state.isCurrent(request));
        assertFalse(state.finish(request));
        assertTrue(state.begin() > request);
    }

    @Test public void cancellingOrLeavingPageDropsLateResultWithoutQueueingMoreWork() {
        var state = new AndroidReceivedFilesLoadState();
        long request = state.begin();
        state.cancel();
        assertFalse(state.isCurrent(request));
        assertEquals(0, state.begin());
        assertFalse(state.finish(request));
        assertTrue(state.begin() > request);
    }

    @Test public void alreadyPostedUiResultCannotReplaceANewerLoad() {
        var state = new AndroidReceivedFilesLoadState();
        long first = state.begin();
        assertTrue(state.finish(first));
        long second = state.begin();
        assertFalse(state.isCurrent(first));
        assertFalse(state.finish(first));
        assertEquals(0, state.begin());
        assertTrue(state.finish(second));
    }

    @Test public void destroyedActivityRejectsPendingAndFutureLoads() {
        var state = new AndroidReceivedFilesLoadState();
        long request = state.begin();
        state.close();
        assertFalse(state.finish(request));
        assertFalse(state.isCurrent(request));
        assertEquals(0, state.begin());
    }

    @Test public void cancellationReleasesBlockedStorageWorkAndAllowsRetryAfterCompletion() throws Exception {
        var state = new AndroidReceivedFilesLoadState();
        var started = new CountDownLatch(1);
        var cancellation = new CountDownLatch(1);
        var worker = Executors.newSingleThreadExecutor();
        long request = state.begin(cancellation::countDown);
        try {
            var pending = worker.submit(() -> {
                started.countDown();
                assertTrue("Cancellation was never forwarded to the storage query", cancellation.await(2, TimeUnit.SECONDS));
                return state.finish(request);
            });
            assertTrue(started.await(2, TimeUnit.SECONDS));
            state.cancel(request);
            assertFalse(pending.get(2, TimeUnit.SECONDS));
            assertTrue(state.begin() > request);
        } finally {
            cancellation.countDown();
            worker.shutdownNow();
            assertTrue(worker.awaitTermination(2, TimeUnit.SECONDS));
        }
    }

    @Test public void cancellationKeepsSlotUntilProviderReallyFinishes() {
        var state = new AndroidReceivedFilesLoadState();
        var signals = new AtomicInteger();
        long request = state.begin(signals::incrementAndGet);
        state.cancel(request);
        state.cancel(request);
        state.cancel();
        assertEquals(1, signals.get());
        assertEquals(0, state.begin());
        assertFalse(state.finish(request));
        assertTrue(state.begin() > request);
    }

    @Test public void lateDialogCancellationDoesNotCancelReplacementQuery() {
        var state = new AndroidReceivedFilesLoadState();
        var oldSignals = new AtomicInteger();
        var newSignals = new AtomicInteger();
        long first = state.begin(oldSignals::incrementAndGet);
        assertTrue(state.finish(first));
        long second = state.begin(newSignals::incrementAndGet);
        state.cancel(first);
        assertFalse(state.finish(first));
        assertTrue(state.isCurrent(second));
        assertEquals(0, oldSignals.get());
        assertEquals(0, newSignals.get());
        state.cancel(second);
        assertEquals(1, newSignals.get());
    }

    @Test public void cancellingFinishedButUndeliveredResultDoesNotContactProviderAgain() {
        var state = new AndroidReceivedFilesLoadState();
        var signals = new AtomicInteger();
        long request = state.begin(signals::incrementAndGet);
        assertTrue(state.finish(request));
        state.cancel(request);
        assertFalse(state.isCurrent(request));
        assertEquals(0, signals.get());
    }

    @Test public void closeSignalsOnceOutsideStateLockAndPreventsAnyRestart() {
        var state = new AndroidReceivedFilesLoadState();
        var signals = new AtomicInteger();
        var heldStateLock = new AtomicBoolean();
        long request = state.begin(() -> {
            heldStateLock.set(Thread.holdsLock(state));
            signals.incrementAndGet();
        });
        state.close();
        state.close();
        state.cancel();
        assertEquals(1, signals.get());
        assertFalse(heldStateLock.get());
        assertFalse(state.finish(request));
        assertEquals(0, state.begin());
    }

    @Test public void providerCancellationFailureStillInvalidatesLateResult() {
        var state = new AndroidReceivedFilesLoadState();
        long request = state.begin(() -> { throw new IllegalStateException("synthetic vendor cancellation failure"); });
        state.cancel(request);
        assertFalse(state.isCurrent(request));
        assertEquals(0, state.begin());
        assertFalse(state.finish(request));
        assertTrue(state.begin() > request);
    }
}
