package com.remotedesk.agent;

final class AndroidDragGesturePump {
    interface Dispatcher {
        boolean dispatch(Segment segment, ResultCallback callback);

        default void reset(long retiredGeneration) {
        }
    }

    interface ResultCallback {
        void onCompleted();

        void onCancelled();

        void onRejected();
    }

    static final class Segment {
        final long generation;
        final long sequence;
        final boolean first;
        final boolean willContinue;
        final float startX;
        final float startY;
        final float endX;
        final float endY;

        Segment(
            long generation,
            long sequence,
            boolean first,
            boolean willContinue,
            float startX,
            float startY,
            float endX,
            float endY) {
            this.generation = generation;
            this.sequence = sequence;
            this.first = first;
            this.willContinue = willContinue;
            this.startX = startX;
            this.startY = startY;
            this.endX = endX;
            this.endY = endY;
        }
    }

    static final class Snapshot {
        final long generation;
        final boolean active;
        final boolean acceptingInput;
        final boolean inFlight;
        final boolean hasPendingPoint;
        final boolean releasePending;

        Snapshot(
            long generation,
            boolean active,
            boolean acceptingInput,
            boolean inFlight,
            boolean hasPendingPoint,
            boolean releasePending) {
            this.generation = generation;
            this.active = active;
            this.acceptingInput = acceptingInput;
            this.inFlight = inFlight;
            this.hasPendingPoint = hasPendingPoint;
            this.releasePending = releasePending;
        }

        int retainedPointCount() {
            if (!active) {
                return 0;
            }
            return hasPendingPoint ? 2 : 1;
        }
    }

    private final Object lock = new Object();
    private final Dispatcher dispatcher;

    private long generation;
    private long nextSequence;
    private boolean active;
    private boolean acceptingInput;
    private boolean inFlight;
    private boolean hasPendingPoint;
    private boolean releasePending;
    private boolean strokeStarted;
    private float currentX;
    private float currentY;
    private float pendingX;
    private float pendingY;
    private long inFlightSequence;

    AndroidDragGesturePump(Dispatcher dispatcher) {
        if (dispatcher == null) {
            throw new IllegalArgumentException("dispatcher is required");
        }
        this.dispatcher = dispatcher;
    }

    boolean beginSession(float x, float y) {
        synchronized (lock) {
            if (active) {
                return false;
            }
            generation = nextGeneration(generation);
            active = true;
            acceptingInput = true;
            hasPendingPoint = false;
            releasePending = false;
            strokeStarted = false;
            currentX = x;
            currentY = y;
            inFlight = false;
            inFlightSequence = 0;
        }
        return true;
    }

    boolean move(float x, float y) {
        Segment segment = null;
        synchronized (lock) {
            if (!active || !acceptingInput) {
                return false;
            }
            float latestX = hasPendingPoint ? pendingX : currentX;
            float latestY = hasPendingPoint ? pendingY : currentY;
            if (samePoint(latestX, latestY, x, y)) {
                return true;
            }
            pendingX = x;
            pendingY = y;
            hasPendingPoint = true;
            if (!inFlight) {
                segment = preparePendingSegmentLocked(true, !hasDispatchedSegmentLocked());
            }
        }
        return segment == null || dispatch(segment);
    }

    boolean end(float x, float y) {
        Segment segment = null;
        synchronized (lock) {
            if (!active || !acceptingInput) {
                return false;
            }
            acceptingInput = false;
            pendingX = x;
            pendingY = y;
            hasPendingPoint = true;
            releasePending = true;
            if (!inFlight) {
                segment = preparePendingSegmentLocked(
                    false,
                    !hasDispatchedSegmentLocked());
            }
        }
        return segment == null || dispatch(segment);
    }

    void cancel() {
        Segment segment = null;
        synchronized (lock) {
            if (!active) {
                return;
            }
            if (!strokeStarted) {
                clearLocked();
                return;
            }
            acceptingInput = false;
            pendingX = currentX;
            pendingY = currentY;
            hasPendingPoint = true;
            releasePending = true;
            if (!inFlight) {
                segment = preparePendingSegmentLocked(
                    false,
                    !hasDispatchedSegmentLocked());
            }
        }
        if (segment != null) {
            dispatch(segment);
        }
    }

    void forceCancel() {
        long retiredGeneration;
        synchronized (lock) {
            if (!active) {
                return;
            }
            retiredGeneration = generation;
            clearLocked();
            generation = nextGeneration(generation);
        }
        dispatcher.reset(retiredGeneration);
    }

    boolean cancelAndAwaitIdle(long timeoutMillis) {
        if (timeoutMillis < 0) {
            throw new IllegalArgumentException("timeout must not be negative");
        }
        cancel();
        long deadline = System.nanoTime() + timeoutMillis * 1_000_000L;
        synchronized (lock) {
            while (active) {
                long remainingNanos = deadline - System.nanoTime();
                if (remainingNanos <= 0) {
                    break;
                }
                try {
                    long waitMillis = Math.max(1L, remainingNanos / 1_000_000L);
                    lock.wait(waitMillis);
                } catch (InterruptedException ex) {
                    Thread.currentThread().interrupt();
                    break;
                }
            }
            if (!active) {
                return true;
            }
        }
        forceCancel();
        return false;
    }

    Snapshot snapshot() {
        synchronized (lock) {
            return new Snapshot(
                generation,
                active,
                acceptingInput,
                inFlight,
                hasPendingPoint,
                releasePending);
        }
    }

    /** Wait only for an already requested release; never cancel a held drag. */
    boolean awaitPendingRelease(long timeoutMillis) {
        if (timeoutMillis < 0) throw new IllegalArgumentException("timeout must not be negative");
        long deadline = System.nanoTime() + timeoutMillis * 1_000_000L;
        synchronized (lock) {
            if (!active || !releasePending) return true;
            long expectedGeneration = generation;
            while (active && generation == expectedGeneration) {
                long remaining = deadline - System.nanoTime();
                if (remaining <= 0) return false;
                try { lock.wait(Math.max(1L, remaining / 1_000_000L)); }
                catch (InterruptedException error) {
                    Thread.currentThread().interrupt();
                    return false;
                }
            }
            return !active && generation == expectedGeneration;
        }
    }

    private Segment preparePendingSegmentLocked(
        boolean willContinue,
        boolean first) {
        float endX = pendingX;
        float endY = pendingY;
        hasPendingPoint = false;
        releasePending = !willContinue;
        return prepareSegmentLocked(first, willContinue, endX, endY);
    }

    private Segment prepareSegmentLocked(
        boolean first,
        boolean willContinue,
        float endX,
        float endY) {
        long sequence = nextGeneration(nextSequence);
        nextSequence = sequence;
        Segment segment = new Segment(
            generation,
            sequence,
            first,
            willContinue,
            currentX,
            currentY,
            endX,
            endY);
        currentX = endX;
        currentY = endY;
        strokeStarted = true;
        inFlight = true;
        inFlightSequence = sequence;
        return segment;
    }

    private boolean dispatch(Segment segment) {
        ResultCallback callback = new ResultCallback() {
            @Override
            public void onCompleted() {
                finishDispatch(segment, true);
            }

            @Override
            public void onCancelled() {
                finishDispatch(segment, false);
            }

            @Override
            public void onRejected() {
                finishDispatch(segment, false);
            }
        };
        boolean accepted;
        try {
            accepted = dispatcher.dispatch(segment, callback);
        } catch (RuntimeException ignored) {
            accepted = false;
        }
        if (!accepted) {
            finishDispatch(segment, false);
        }
        return accepted;
    }

    private void finishDispatch(Segment completed, boolean successful) {
        Segment next = null;
        long retiredGeneration = 0;
        synchronized (lock) {
            if (!active ||
                generation != completed.generation ||
                !inFlight ||
                inFlightSequence != completed.sequence) {
                return;
            }
            inFlight = false;
            if (!successful) {
                retiredGeneration = generation;
                clearLocked();
                generation = nextGeneration(generation);
            } else if (!completed.willContinue) {
                clearLocked();
            } else if (releasePending) {
                next = preparePendingSegmentLocked(false, false);
            } else if (hasPendingPoint) {
                next = preparePendingSegmentLocked(true, false);
            }
        }
        if (retiredGeneration != 0) {
            dispatcher.reset(retiredGeneration);
        }
        if (next != null) {
            dispatch(next);
        }
    }

    private void clearLocked() {
        active = false;
        acceptingInput = false;
        inFlight = false;
        hasPendingPoint = false;
        releasePending = false;
        strokeStarted = false;
        currentX = 0;
        currentY = 0;
        pendingX = 0;
        pendingY = 0;
        inFlightSequence = 0;
        lock.notifyAll();
    }

    private boolean hasDispatchedSegmentLocked() {
        return strokeStarted;
    }

    private static boolean samePoint(float firstX, float firstY, float secondX, float secondY) {
        return Float.compare(firstX, secondX) == 0 &&
            Float.compare(firstY, secondY) == 0;
    }

    private static long nextGeneration(long current) {
        return current == Long.MAX_VALUE ? 1L : current + 1L;
    }
}
