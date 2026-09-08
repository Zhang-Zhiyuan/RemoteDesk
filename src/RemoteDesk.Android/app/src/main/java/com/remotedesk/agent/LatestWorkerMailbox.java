package com.remotedesk.agent;

import java.util.concurrent.Executor;
import java.util.concurrent.RejectedExecutionException;

/**
 * A capacity-one mailbox whose latest value is consumed on a supplied worker.
 *
 * <p>Only an undispatched value is replaceable. A value already owned by the
 * processor is never released by this class. Closing is non-blocking: pending
 * and future values are released, while an in-flight processor is allowed to
 * finish and must fence any externally visible result itself.</p>
 */
final class LatestWorkerMailbox<T> implements AutoCloseable {
    interface Processor<T> {
        void process(T value);
    }

    interface Releaser<T> {
        void release(T value);
    }

    interface FailureHandler {
        void onFailure(Throwable failure);
    }

    private final Executor executor;
    private final Processor<T> processor;
    private final Releaser<T> releaser;
    private final FailureHandler failureHandler;
    private T pending;
    private boolean workerScheduled;
    private boolean closed;

    LatestWorkerMailbox(
        Executor executor,
        Processor<T> processor,
        Releaser<T> releaser,
        FailureHandler failureHandler) {
        if (executor == null || processor == null || releaser == null ||
            failureHandler == null) {
            throw new IllegalArgumentException(
                "executor, processor, releaser and failureHandler are required");
        }

        this.executor = executor;
        this.processor = processor;
        this.releaser = releaser;
        this.failureHandler = failureHandler;
    }

    boolean offer(T value) {
        if (value == null) {
            throw new IllegalArgumentException("value is required");
        }

        T replaced = null;
        boolean scheduleWorker = false;
        boolean releaseOffered = false;
        synchronized (this) {
            if (closed) {
                releaseOffered = true;
            } else {
                replaced = pending;
                pending = value;
                if (!workerScheduled) {
                    workerScheduled = true;
                    scheduleWorker = true;
                }
            }
        }

        release(replaced);
        if (releaseOffered) {
            release(value);
            return false;
        }
        if (!scheduleWorker) {
            return true;
        }

        try {
            executor.execute(this::drain);
            return true;
        } catch (RejectedExecutionException ex) {
            T discarded;
            synchronized (this) {
                workerScheduled = false;
                discarded = pending;
                pending = null;
            }
            release(discarded);
            return false;
        }
    }

    private void drain() {
        while (true) {
            T value;
            synchronized (this) {
                if (closed) {
                    workerScheduled = false;
                    return;
                }
                value = pending;
                pending = null;
                if (value == null) {
                    workerScheduled = false;
                    return;
                }
            }

            try {
                processor.process(value);
            } catch (RuntimeException | OutOfMemoryError ex) {
                try {
                    failureHandler.onFailure(ex);
                } catch (RuntimeException | OutOfMemoryError ignored) {
                    // A diagnostic callback must not strand future mailbox work.
                }
            }
        }
    }

    synchronized int pendingCount() {
        return pending == null ? 0 : 1;
    }

    synchronized boolean isWorkerScheduled() {
        return workerScheduled;
    }

    @Override
    public void close() {
        T discarded;
        synchronized (this) {
            if (closed) {
                return;
            }
            closed = true;
            discarded = pending;
            pending = null;
        }
        release(discarded);
    }

    private void release(T value) {
        if (value != null) {
            releaser.release(value);
        }
    }
}
