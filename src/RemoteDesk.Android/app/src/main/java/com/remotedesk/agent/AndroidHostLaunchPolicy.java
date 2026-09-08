package com.remotedesk.agent;

/** One-shot navigation after an explicit, successful local host start. */
final class AndroidHostLaunchPolicy {
    static final long START_TIMEOUT_MILLIS = 10_000;
    enum Action { NONE, WAIT, RETURN_TO_DESKTOP, TIMED_OUT }

    private boolean pending;
    private long startedAtMillis;

    void begin(long nowMillis) {
        startedAtMillis = nowMillis;
        pending = true;
    }

    boolean isPending() { return pending; }

    void cancel() { pending = false; }

    Action check(long nowMillis, boolean hostRunning, boolean activityReady) {
        if (!pending) return Action.NONE;
        if (nowMillis < startedAtMillis || nowMillis - startedAtMillis >= START_TIMEOUT_MILLIS) {
            pending = false;
            return Action.TIMED_OUT;
        }
        if (!hostRunning || !activityReady) return Action.WAIT;
        pending = false;
        return Action.RETURN_TO_DESKTOP;
    }
}
