package com.remotedesk.agent;

import static org.junit.Assert.*;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.Test;

public final class AndroidClipboardSnapshotQueueTest {
    @Test public void requestsUseOneBoundedBackgroundDrainAndPreserveOrder() {
        ArrayDeque<Runnable> scheduled = new ArrayDeque<>();
        List<Integer> reads = new ArrayList<>();
        AndroidClipboardSnapshotQueue queue = new AndroidClipboardSnapshotQueue(scheduled::add, () -> true, () -> fail());
        for (int i = 0; i < AndroidClipboardSnapshotQueue.MAX_PENDING; i++) {
            int value = i;
            assertTrue(queue.offer(() -> reads.add(value)));
        }
        assertFalse(queue.offer(() -> fail()));
        assertTrue(reads.isEmpty());
        assertEquals(1, scheduled.size());
        scheduled.removeFirst().run();
        assertEquals(List.of(0, 1, 2, 3), reads);
        assertTrue(queue.offer(() -> reads.add(4)));
        scheduled.removeFirst().run();
        assertEquals(List.of(0, 1, 2, 3, 4), reads);
    }

    @Test public void disconnectedOwnerDiscardsQueuedReads() {
        ArrayDeque<Runnable> scheduled = new ArrayDeque<>();
        AtomicBoolean current = new AtomicBoolean(true);
        AndroidClipboardSnapshotQueue queue = new AndroidClipboardSnapshotQueue(scheduled::add, current::get, () -> fail());
        assertTrue(queue.offer(() -> fail("Old owner must not read the clipboard")));
        current.set(false);
        scheduled.removeFirst().run();
        assertFalse(queue.offer(() -> fail()));
    }

    @Test public void closeDiscardsPendingReadsAndRejectsNewWork() {
        ArrayDeque<Runnable> scheduled = new ArrayDeque<>();
        AndroidClipboardSnapshotQueue queue = new AndroidClipboardSnapshotQueue(scheduled::add, () -> true, () -> fail());
        queue.offer(() -> fail());
        queue.close();
        scheduled.removeFirst().run();
        assertFalse(queue.offer(() -> fail()));
    }

    @Test public void rejectedExecutorDoesNotLeaveAnUnboundedOrStrandedQueue() {
        AndroidClipboardSnapshotQueue queue = new AndroidClipboardSnapshotQueue(
            runnable -> { throw new RejectedExecutionException(); }, () -> true, () -> fail());
        assertFalse(queue.offer(() -> fail()));
        assertFalse(queue.offer(() -> fail()));
    }

    @Test public void writeFailureSignalsOnlyItsCurrentOwnerOnce() {
        ArrayDeque<Runnable> scheduled = new ArrayDeque<>();
        AtomicInteger failures = new AtomicInteger();
        AndroidClipboardSnapshotQueue queue = new AndroidClipboardSnapshotQueue(scheduled::add, () -> true, failures::incrementAndGet);
        queue.offer(() -> { throw new java.io.IOException("closed socket"); });
        queue.offer(() -> fail());
        scheduled.removeFirst().run();
        assertEquals(1, failures.get());
        assertFalse(queue.offer(() -> fail()));
    }

    @Test public void slowClipboardReadDoesNotBlockInputThreadOrTeardown() throws Exception {
        ExecutorService executor = Executors.newSingleThreadExecutor();
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
        AndroidClipboardSnapshotQueue queue = new AndroidClipboardSnapshotQueue(executor, () -> true, () -> fail());
        try {
            assertTrue(queue.offer(() -> { entered.countDown(); assertTrue(release.await(2, TimeUnit.SECONDS)); }));
            assertTrue(entered.await(1, TimeUnit.SECONDS));
            assertTrue(queue.offer(() -> fail("Pending read must be discarded during teardown")));
            queue.close();
            assertFalse(queue.offer(() -> fail()));
            release.countDown();
        } finally {
            release.countDown();
            executor.shutdown();
            assertTrue(executor.awaitTermination(3, TimeUnit.SECONDS));
        }
    }
}
