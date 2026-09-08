package com.remotedesk.agent;

import java.util.List;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Runs expensive file publication away from the authenticated input/read pump.
 * One worker and a bounded queue cap both threads and retained work for each
 * connection generation.
 */
final class AndroidFileCompletionCoordinator implements AutoCloseable {
    interface CancellationSignal {
        boolean isCancelled();
    }

    interface Operation {
        String complete(CancellationSignal cancellationSignal) throws Exception;
    }

    interface Owner {
        boolean isCurrent(long generation);

        void publish(
            long generation,
            String message,
            Throwable failure);
    }

    // The desktop paste workflow permits a batch of up to 32 files and sends
    // the next START immediately after COMPLETE. Keep the queue bounded while
    // allowing that supported batch to drain on one publication worker.
    static final int QUEUE_CAPACITY = 32;

    private final long generation;
    private final Owner owner;
    private final AtomicBoolean closed = new AtomicBoolean();
    private final ThreadPoolExecutor executor;

    AndroidFileCompletionCoordinator(long generation, Owner owner) {
        if (generation <= 0L || owner == null) {
            throw new IllegalArgumentException("generation and owner are required");
        }

        this.generation = generation;
        this.owner = owner;
        executor = new ThreadPoolExecutor(
            1,
            1,
            0L,
            TimeUnit.MILLISECONDS,
            new ArrayBlockingQueue<>(QUEUE_CAPACITY),
            runnable -> {
                Thread thread = new Thread(
                    runnable,
                    "RemoteDesk-Android-File-Finalizer-" + generation);
                thread.setDaemon(true);
                return thread;
            },
            new ThreadPoolExecutor.AbortPolicy());
    }

    boolean offer(Operation operation) {
        return offer(operation, () -> {
        });
    }

    boolean offer(Operation operation, Runnable discardPending) {
        if (operation == null || closed.get()) {
            runDiscard(discardPending);
            return false;
        }

        PendingCompletion pending =
            new PendingCompletion(operation, discardPending);
        try {
            executor.execute(pending);
            return true;
        } catch (RejectedExecutionException ex) {
            pending.discard();
            return false;
        }
    }

    private void runOperation(Operation operation) {
        String message = null;
        Throwable failure = null;
        try {
            message = operation.complete(this::isCancelled);
        } catch (Exception ex) {
            failure = ex;
        }

        if (isCancelled()) {
            return;
        }

        try {
            owner.publish(generation, message, failure);
        } catch (RuntimeException ex) {
            AndroidSessionLog.error(
                "Android file completion callback failed.",
                ex);
        }
    }

    private boolean isCancelled() {
        if (closed.get() || Thread.currentThread().isInterrupted()) {
            return true;
        }
        try {
            return !owner.isCurrent(generation);
        } catch (RuntimeException ex) {
            return true;
        }
    }

    @Override
    public void close() {
        if (closed.compareAndSet(false, true)) {
            List<Runnable> pending = executor.shutdownNow();
            for (Runnable runnable : pending) {
                if (runnable instanceof PendingCompletion) {
                    ((PendingCompletion) runnable).discard();
                }
            }
        }
    }

    private static void runDiscard(Runnable discardPending) {
        if (discardPending == null) {
            return;
        }
        try {
            discardPending.run();
        } catch (RuntimeException ignored) {
            // Cancellation cleanup is best-effort and must not strand the
            // authenticated read pump while the session is tearing down.
        }
    }

    boolean awaitStopped(long timeoutMillis) {
        if (timeoutMillis < 0L) {
            throw new IllegalArgumentException("timeout must not be negative");
        }
        try {
            return executor.awaitTermination(timeoutMillis, TimeUnit.MILLISECONDS);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
            return false;
        }
    }

    int largestWorkerCountForTests() {
        return executor.getLargestPoolSize();
    }

    int queuedCompletionCountForTests() {
        return executor.getQueue().size();
    }

    private final class PendingCompletion implements Runnable {
        private final Operation operation;
        private final Runnable discardPending;
        private final AtomicBoolean claimed = new AtomicBoolean();

        PendingCompletion(Operation operation, Runnable discardPending) {
            this.operation = operation;
            this.discardPending = discardPending;
        }

        @Override
        public void run() {
            if (claimed.compareAndSet(false, true)) {
                runOperation(operation);
            }
        }

        void discard() {
            if (claimed.compareAndSet(false, true)) {
                runDiscard(discardPending);
            }
        }
    }
}
