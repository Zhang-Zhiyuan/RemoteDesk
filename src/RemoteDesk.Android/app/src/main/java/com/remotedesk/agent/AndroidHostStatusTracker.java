package com.remotedesk.agent;

/** Cheap foreground-only change detection; unchanged polls do no UI/network work. */
final class AndroidHostStatusTracker {
    private int flags = -1;
    private String failure = "";

    boolean update(boolean serviceRunning, boolean hostRunning, boolean capturePaused,
            boolean accessibilityConnected, String startFailure) {
        int nextFlags = (serviceRunning ? 1 : 0) | (hostRunning ? 2 : 0) | (capturePaused ? 4 : 0)
            | (accessibilityConnected ? 8 : 0);
        String nextFailure = startFailure == null ? "" : startFailure;
        boolean changed = flags != nextFlags || !failure.equals(nextFailure);
        flags = nextFlags;
        failure = nextFailure;
        return changed;
    }
}
