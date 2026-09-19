package com.remotedesk.agent;

/** One background directory query at a time; cancelled pages never receive its result. */
final class AndroidReceivedFilesLoadState {
    private long generation;
    private long running;
    private boolean closed;
    private Runnable cancelRunning;

    long begin() { return begin(null); }

    synchronized long begin(Runnable cancellation) {
        if (closed || running != 0) return 0;
        running = ++generation;
        cancelRunning = cancellation;
        return running;
    }

    synchronized boolean finish(long request) {
        if (running != request || request == 0) return false;
        running = 0;
        cancelRunning = null;
        return !closed && generation == request;
    }

    synchronized boolean isCurrent(long request) {
        return !closed && request != 0 && generation == request;
    }

    void cancel() { cancel(0); }

    void cancel(long request) {
        Runnable cancellation;
        synchronized (this) {
            // A dismissed dialog's late callback must not cancel its replacement.
            if (request != 0 && request != generation) return;
            generation++;
            cancellation = cancelRunning;
            cancelRunning = null;
        }
        signalCancellation(cancellation);
    }

    void close() {
        Runnable cancellation;
        synchronized (this) {
            closed = true;
            generation++;
            cancellation = cancelRunning;
            cancelRunning = null;
        }
        signalCancellation(cancellation);
    }

    private static void signalCancellation(Runnable cancellation) {
        // Do not call provider/framework code while holding the state lock.
        // Cancellation is best-effort: the generation fence still drops results
        // if a vendor provider rejects or ignores its cancellation request.
        if (cancellation != null) {
            try { cancellation.run(); }
            catch (RuntimeException ignored) { }
        }
    }
}
