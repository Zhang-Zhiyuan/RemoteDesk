package com.remotedesk.agent;

/** Rejects late pictures from a previous codec or selected screen. */
final class AndroidViewerPresentationEpoch {
    private long version;
    private int encoding;
    synchronized long accept(int nextEncoding) {
        if (encoding != nextEncoding) { encoding = nextEncoding; version++; }
        return version;
    }
    synchronized void invalidate() { encoding = 0; version++; }
    synchronized long version() { return version; }
    synchronized int encoding() { return encoding; }
    synchronized boolean current(long expected, int codec) { return version == expected && encoding == codec; }
}
