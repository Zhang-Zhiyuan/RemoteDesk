package com.remotedesk.agent;

/** One background directory query at a time; cancelled pages never receive its result. */
final class AndroidReceivedFilesLoadState {
    private long generation;
    private long running;
    private boolean closed;

    synchronized long begin() {
        if (closed || running != 0) return 0;
        running = ++generation;
        return running;
    }

    synchronized boolean finish(long request) {
        if (running != request || request == 0) return false;
        running = 0;
        return !closed && generation == request;
    }

    synchronized boolean isCurrent(long request) {
        return !closed && request != 0 && generation == request;
    }

    synchronized void cancel() { generation++; }

    synchronized void close() { closed = true; generation++; }
}
