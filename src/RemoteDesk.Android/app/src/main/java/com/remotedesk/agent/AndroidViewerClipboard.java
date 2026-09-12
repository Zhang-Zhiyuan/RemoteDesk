package com.remotedesk.agent;

import java.util.concurrent.CountDownLatch;

// One request per physical connection: legacy peers do not echo request IDs.
final class AndroidViewerClipboard {
    static final long TIMEOUT_MILLIS = 8000;
    static final long RELAY_TIMEOUT_MILLIS = 30000;
    private Request pending;
    private Request latest;

    synchronized Request begin(boolean read, long localRevision, long nowMillis) {
        return begin(read, localRevision, nowMillis, false);
    }

    synchronized Request begin(boolean read, long localRevision, long nowMillis, boolean slowRelayReply) {
        if (pending != null) return null;
        pending = latest = new Request(read, localRevision,
            nowMillis + (slowRelayReply ? RELAY_TIMEOUT_MILLIS : TIMEOUT_MILLIS));
        return pending;
    }

    synchronized boolean receive(boolean textReply, boolean success, String text, long nowMillis) {
        Request request = pending;
        if (request == null || (textReply && !request.read) || (!textReply && request.read && success)) return false;
        request.success = success;
        request.text = text == null ? "" : text;
        request.completed.countDown();
        if (nowMillis >= request.deadlineMillis) pending = null;
        return true;
    }

    synchronized void finish(Request request, boolean sent) {
        // Do not assign a late response to a subsequent operation after timeout.
        if (pending == request && (!sent || request.completed.getCount() == 0)) pending = null;
    }

    synchronized boolean canApply(Request request, long localRevision, long nowMillis) {
        return latest == request && request.localRevision == localRevision && nowMillis < request.deadlineMillis;
    }

    static final class Request {
        final boolean read;
        final long localRevision, deadlineMillis;
        final CountDownLatch completed = new CountDownLatch(1);
        volatile boolean success;
        volatile String text = "";
        Request(boolean read, long revision, long deadline) {
            this.read = read; localRevision = revision; deadlineMillis = deadline;
        }
    }
}
