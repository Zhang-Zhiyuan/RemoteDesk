package com.remotedesk.agent;

final class LatestValueMailbox<T> implements AutoCloseable {
    interface Releaser<T> {
        void release(T value);
    }

    private final Releaser<T> releaser;
    private T pending;
    private boolean dispatchScheduled;
    private boolean closed;

    LatestValueMailbox(Releaser<T> releaser) {
        if (releaser == null) {
            throw new IllegalArgumentException("releaser is required");
        }

        this.releaser = releaser;
    }

    boolean offer(T value) {
        if (value == null) {
            throw new IllegalArgumentException("value is required");
        }

        T replaced = null;
        boolean shouldSchedule = false;
        boolean releaseOffered = false;
        synchronized (this) {
            if (closed) {
                releaseOffered = true;
            } else {
                replaced = pending;
                pending = value;
                if (!dispatchScheduled) {
                    dispatchScheduled = true;
                    shouldSchedule = true;
                }
            }
        }

        release(replaced);
        if (releaseOffered) {
            release(value);
        }
        return shouldSchedule;
    }

    T pollLatestForDispatch() {
        synchronized (this) {
            T latest = pending;
            pending = null;
            dispatchScheduled = false;
            return latest;
        }
    }

    synchronized int pendingCount() {
        return pending == null ? 0 : 1;
    }

    synchronized boolean isDispatchScheduled() {
        return dispatchScheduled;
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
            dispatchScheduled = false;
        }

        release(discarded);
    }

    private void release(T value) {
        if (value != null) {
            releaser.release(value);
        }
    }
}
