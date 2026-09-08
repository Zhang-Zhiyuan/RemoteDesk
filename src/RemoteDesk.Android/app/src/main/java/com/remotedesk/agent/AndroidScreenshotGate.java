package com.remotedesk.agent;

// One valid request and one completed frame. A lost Binder reply must not stall capture forever.
final class AndroidScreenshotGate<T> {
    static final long INTERVAL_NANOS = 350_000_000L;
    static final long REQUEST_TIMEOUT_NANOS = 5_000_000_000L;

    static final class Request {
        final boolean recoveredFromTimeout;

        private Request(boolean recoveredFromTimeout) {
            this.recoveredFromTimeout = recoveredFromTimeout;
        }
    }

    private boolean closed;
    private boolean started;
    private Request pending;
    private long requestedAt;
    private T latest;

    static double frameBudgetMillis(int requestedFps) {
        return Math.max(1000d / Math.max(1, requestedFps), INTERVAL_NANOS / 1_000_000d);
    }

    synchronized Request begin(long now) {
        // Use elapsed differences: nanoTime may be negative or wrap around.
        if (closed || (started && now - requestedAt < INTERVAL_NANOS) ||
            (pending != null && now - requestedAt < REQUEST_TIMEOUT_NANOS)) return null;
        Request request = new Request(pending != null);
        pending = request;
        started = true;
        requestedAt = now;
        return request;
    }

    synchronized boolean isCurrent(Request request) {
        return !closed && request != null && pending == request;
    }

    synchronized boolean complete(Request request, T frame) {
        if (!isCurrent(request)) return false;
        pending = null;
        latest = frame;
        return true;
    }

    synchronized T poll() {
        T frame = latest;
        latest = null;
        return frame;
    }

    synchronized void close() {
        closed = true;
        pending = null;
        latest = null;
    }
}
