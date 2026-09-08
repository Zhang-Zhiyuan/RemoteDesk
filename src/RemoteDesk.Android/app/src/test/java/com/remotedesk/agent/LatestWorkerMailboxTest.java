package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.Queue;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executor;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import org.junit.Test;

public final class LatestWorkerMailboxTest {
    @Test
    public void workerConsumesOnlyLatestUndispatchedValue() {
        ManualExecutor executor = new ManualExecutor();
        List<String> processed = new ArrayList<>();
        List<String> released = new ArrayList<>();
        LatestWorkerMailbox<String> mailbox = new LatestWorkerMailbox<>(
            executor,
            processed::add,
            released::add,
            ignored -> {
            });

        assertTrue(mailbox.offer("frame-1"));
        assertTrue(mailbox.offer("frame-2"));
        assertEquals(List.of("frame-1"), released);
        assertEquals(1, mailbox.pendingCount());
        assertTrue(mailbox.isWorkerScheduled());

        executor.runNext();

        assertEquals(List.of("frame-2"), processed);
        assertEquals(0, mailbox.pendingCount());
        assertFalse(mailbox.isWorkerScheduled());
    }

    @Test
    public void closeIsNonBlockingAndReleasesPendingAndFutureValues() {
        ManualExecutor executor = new ManualExecutor();
        List<String> processed = new ArrayList<>();
        List<String> released = new ArrayList<>();
        LatestWorkerMailbox<String> mailbox = new LatestWorkerMailbox<>(
            executor,
            processed::add,
            released::add,
            ignored -> {
            });
        mailbox.offer("frame-1");

        mailbox.close();
        assertFalse(mailbox.offer("frame-2"));
        executor.runNext();

        assertEquals(List.of("frame-1", "frame-2"), released);
        assertTrue(processed.isEmpty());
        assertFalse(mailbox.isWorkerScheduled());
    }

    @Test
    public void processorFailureDoesNotStrandLaterWork() {
        ManualExecutor executor = new ManualExecutor();
        List<String> processed = new ArrayList<>();
        List<String> failures = new ArrayList<>();
        LatestWorkerMailbox<String> mailbox = new LatestWorkerMailbox<>(
            executor,
            value -> {
                if ("bad".equals(value)) {
                    throw new IllegalStateException("decode failed");
                }
                processed.add(value);
            },
            ignored -> {
            },
            failure -> failures.add(failure.getMessage()));

        mailbox.offer("bad");
        executor.runNext();
        mailbox.offer("good");
        executor.runNext();

        assertEquals(List.of("decode failed"), failures);
        assertEquals(List.of("good"), processed);
    }

    @Test
    public void valuesOfferedDuringProcessingRemainCapacityOne() {
        ManualExecutor executor = new ManualExecutor();
        List<String> processed = new ArrayList<>();
        List<String> released = new ArrayList<>();
        AtomicReference<LatestWorkerMailbox<String>> mailboxReference =
            new AtomicReference<>();
        LatestWorkerMailbox<String> mailbox = new LatestWorkerMailbox<>(
            executor,
            value -> {
                processed.add(value);
                if ("frame-1".equals(value)) {
                    mailboxReference.get().offer("frame-2");
                    mailboxReference.get().offer("frame-3");
                }
            },
            released::add,
            ignored -> {
            });
        mailboxReference.set(mailbox);

        mailbox.offer("frame-1");
        executor.runNext();

        assertEquals(List.of("frame-1", "frame-3"), processed);
        assertEquals(List.of("frame-2"), released);
        assertFalse(mailbox.isWorkerScheduled());
    }

    @Test
    public void closeDuringProcessingDoesNotReleaseProcessorOwnedValue() {
        ManualExecutor executor = new ManualExecutor();
        List<String> processed = new ArrayList<>();
        List<String> released = new ArrayList<>();
        AtomicReference<LatestWorkerMailbox<String>> mailboxReference =
            new AtomicReference<>();
        LatestWorkerMailbox<String> mailbox = new LatestWorkerMailbox<>(
            executor,
            value -> {
                mailboxReference.get().close();
                processed.add(value);
            },
            released::add,
            ignored -> {
            });
        mailboxReference.set(mailbox);

        mailbox.offer("active-frame");
        executor.runNext();

        assertEquals(List.of("active-frame"), processed);
        assertTrue(released.isEmpty());
        assertFalse(mailbox.offer("late-frame"));
        assertEquals(List.of("late-frame"), released);
    }

    @Test
    public void rejectedWorkerReleasesLatestValueAndCanRetry() {
        RejectOnceExecutor executor = new RejectOnceExecutor();
        List<String> processed = new ArrayList<>();
        List<String> released = new ArrayList<>();
        LatestWorkerMailbox<String> mailbox = new LatestWorkerMailbox<>(
            executor,
            processed::add,
            released::add,
            ignored -> {
            });

        assertFalse(mailbox.offer("frame-1"));
        assertEquals(List.of("frame-1"), released);
        assertFalse(mailbox.isWorkerScheduled());
        assertTrue(mailbox.offer("frame-2"));

        assertEquals(List.of("frame-2"), processed);
    }

    @Test
    public void saturatedBoundedExecutorDoesNotStrandMailbox() throws Exception {
        ThreadPoolExecutor executor = new ThreadPoolExecutor(
            1,
            1,
            0L,
            TimeUnit.MILLISECONDS,
            new ArrayBlockingQueue<>(1));
        CountDownLatch activeStarted = new CountDownLatch(1);
        CountDownLatch releaseActive = new CountDownLatch(1);
        CountDownLatch queuedWorkFinished = new CountDownLatch(1);
        CountDownLatch frameProcessed = new CountDownLatch(1);
        List<String> released = new ArrayList<>();
        try {
            executor.execute(() -> {
                activeStarted.countDown();
                try {
                    releaseActive.await();
                } catch (InterruptedException ex) {
                    Thread.currentThread().interrupt();
                }
            });
            assertTrue(activeStarted.await(2, TimeUnit.SECONDS));
            executor.execute(queuedWorkFinished::countDown);

            LatestWorkerMailbox<String> mailbox = new LatestWorkerMailbox<>(
                executor,
                ignored -> frameProcessed.countDown(),
                released::add,
                ignored -> {
                });
            assertFalse(mailbox.offer("rejected-frame"));
            assertEquals(List.of("rejected-frame"), released);
            assertFalse(mailbox.isWorkerScheduled());

            releaseActive.countDown();
            assertTrue(queuedWorkFinished.await(2, TimeUnit.SECONDS));
            assertTrue(mailbox.offer("retry-frame"));
            assertTrue(frameProcessed.await(2, TimeUnit.SECONDS));
            CountDownLatch drainFinished = new CountDownLatch(1);
            executor.execute(drainFinished::countDown);
            assertTrue(drainFinished.await(2, TimeUnit.SECONDS));
            assertFalse(mailbox.isWorkerScheduled());
        } finally {
            releaseActive.countDown();
            executor.shutdownNow();
            assertTrue(executor.awaitTermination(2, TimeUnit.SECONDS));
        }
    }

    private static final class ManualExecutor implements Executor {
        private final Queue<Runnable> work = new ArrayDeque<>();

        @Override
        public void execute(Runnable command) {
            work.add(command);
        }

        void runNext() {
            Runnable next = work.remove();
            next.run();
        }
    }

    private static final class RejectOnceExecutor implements Executor {
        private boolean rejected;

        @Override
        public void execute(Runnable command) {
            if (!rejected) {
                rejected = true;
                throw new RejectedExecutionException("busy");
            }
            command.run();
        }
    }
}
