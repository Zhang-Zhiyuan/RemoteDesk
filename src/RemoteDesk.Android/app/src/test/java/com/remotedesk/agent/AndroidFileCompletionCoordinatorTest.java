package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;

public final class AndroidFileCompletionCoordinatorTest {
    @Test
    public void slowCompletionDoesNotBlockCallerAndQueueAndThreadStayBounded()
        throws Exception {
        AtomicLong currentGeneration = new AtomicLong(1L);
        List<String> published = Collections.synchronizedList(new ArrayList<>());
        CountDownLatch slowStarted = new CountDownLatch(1);
        CountDownLatch releaseSlow = new CountDownLatch(1);
        CountDownLatch publishedAll = new CountDownLatch(
            AndroidFileCompletionCoordinator.QUEUE_CAPACITY + 1);
        AndroidFileCompletionCoordinator coordinator = coordinator(
            1L,
            currentGeneration,
            published,
            publishedAll);
        try {
            assertTrue(coordinator.offer(cancellation -> {
                slowStarted.countDown();
                releaseSlow.await(5, TimeUnit.SECONDS);
                return "slow";
            }));
            assertTrue(slowStarted.await(2, TimeUnit.SECONDS));

            // The authenticated read pump can immediately process the next
            // Ping while publication is blocked on slow storage.
            for (int index = 0;
                 index < AndroidFileCompletionCoordinator.QUEUE_CAPACITY;
                 index++) {
                int queuedIndex = index;
                assertTrue(coordinator.offer(
                    cancellation -> "queued-" + queuedIndex));
            }
            assertFalse(coordinator.offer(cancellation -> "must-be-rejected"));
            assertEquals(
                AndroidFileCompletionCoordinator.QUEUE_CAPACITY,
                coordinator.queuedCompletionCountForTests());
            assertEquals(1, coordinator.largestWorkerCountForTests());

            releaseSlow.countDown();
            assertTrue(publishedAll.await(5, TimeUnit.SECONDS));
            assertEquals(
                AndroidFileCompletionCoordinator.QUEUE_CAPACITY + 1,
                published.size());
            assertEquals("slow", published.get(0));
            assertEquals(
                "queued-" +
                    (AndroidFileCompletionCoordinator.QUEUE_CAPACITY - 1),
                published.get(published.size() - 1));
        } finally {
            releaseSlow.countDown();
            coordinator.close();
            assertTrue(coordinator.awaitStopped(2_000L));
        }
    }

    @Test
    public void disconnectInterruptsSlowCompletionAndSuppressesStatus() throws Exception {
        AtomicLong currentGeneration = new AtomicLong(7L);
        List<String> published = Collections.synchronizedList(new ArrayList<>());
        CountDownLatch started = new CountDownLatch(1);
        CountDownLatch cleaned = new CountDownLatch(1);
        AndroidFileCompletionCoordinator coordinator = coordinator(
            7L,
            currentGeneration,
            published,
            new CountDownLatch(0));

        assertTrue(coordinator.offer(cancellation -> {
            started.countDown();
            try {
                while (!cancellation.isCancelled()) {
                    Thread.sleep(50L);
                }
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
            } finally {
                cleaned.countDown();
            }
            return "stale";
        }));
        assertTrue(started.await(2, TimeUnit.SECONDS));

        currentGeneration.set(0L);
        coordinator.close();

        assertTrue(cleaned.await(2, TimeUnit.SECONDS));
        assertTrue(coordinator.awaitStopped(2_000L));
        assertTrue(published.isEmpty());
    }

    @Test
    public void disconnectDiscardsQueuedCompletionBeforeWorkerRuns()
        throws Exception {
        AtomicLong currentGeneration = new AtomicLong(8L);
        List<String> published = Collections.synchronizedList(new ArrayList<>());
        CountDownLatch firstStarted = new CountDownLatch(1);
        CountDownLatch releaseFirst = new CountDownLatch(1);
        CountDownLatch queuedDiscarded = new CountDownLatch(1);
        AndroidFileCompletionCoordinator coordinator = coordinator(
            8L,
            currentGeneration,
            published,
            new CountDownLatch(0));

        assertTrue(coordinator.offer(cancellation -> {
            firstStarted.countDown();
            releaseFirst.await(5, TimeUnit.SECONDS);
            return "first";
        }));
        assertTrue(firstStarted.await(2, TimeUnit.SECONDS));
        assertTrue(coordinator.offer(
            cancellation -> "must-not-run",
            queuedDiscarded::countDown));

        currentGeneration.set(0L);
        coordinator.close();
        releaseFirst.countDown();

        assertTrue(queuedDiscarded.await(2, TimeUnit.SECONDS));
        assertTrue(coordinator.awaitStopped(2_000L));
        assertTrue(published.isEmpty());
    }

    @Test
    public void reconnectGenerationFencesOldStatusAndAllowsNewOwner() throws Exception {
        AtomicLong currentGeneration = new AtomicLong(11L);
        List<String> published = Collections.synchronizedList(new ArrayList<>());
        CountDownLatch oldStarted = new CountDownLatch(1);
        CountDownLatch releaseOld = new CountDownLatch(1);
        AndroidFileCompletionCoordinator oldCoordinator = coordinator(
            11L,
            currentGeneration,
            published,
            new CountDownLatch(0));
        assertTrue(oldCoordinator.offer(cancellation -> {
            oldStarted.countDown();
            releaseOld.await(5, TimeUnit.SECONDS);
            return "old";
        }));
        assertTrue(oldStarted.await(2, TimeUnit.SECONDS));

        currentGeneration.set(12L);
        releaseOld.countDown();
        oldCoordinator.close();
        assertTrue(oldCoordinator.awaitStopped(2_000L));

        CountDownLatch newPublished = new CountDownLatch(1);
        AndroidFileCompletionCoordinator newCoordinator = coordinator(
            12L,
            currentGeneration,
            published,
            newPublished);
        try {
            assertTrue(newCoordinator.offer(cancellation -> "new"));
            assertTrue(newPublished.await(2, TimeUnit.SECONDS));
            assertEquals(1, published.size());
            assertEquals("new", published.get(0));
        } finally {
            newCoordinator.close();
            assertTrue(newCoordinator.awaitStopped(2_000L));
        }
    }

    private static AndroidFileCompletionCoordinator coordinator(
        long generation,
        AtomicLong currentGeneration,
        List<String> published,
        CountDownLatch publishLatch) {
        return new AndroidFileCompletionCoordinator(
            generation,
            new AndroidFileCompletionCoordinator.Owner() {
                @Override
                public boolean isCurrent(long expectedGeneration) {
                    return currentGeneration.get() == expectedGeneration;
                }

                @Override
                public void publish(
                    long expectedGeneration,
                    String message,
                    Throwable failure) {
                    if (failure == null) {
                        published.add(message);
                    } else {
                        published.add("error:" + failure.getClass().getSimpleName());
                    }
                    publishLatch.countDown();
                }
            });
    }
}
