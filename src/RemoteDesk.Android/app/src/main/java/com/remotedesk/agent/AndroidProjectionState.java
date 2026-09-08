package com.remotedesk.agent;

final class AndroidProjectionState {
    private long activeGeneration;
    private boolean paused;

    synchronized void started(long generation) {
        if (generation == 0L) throw new IllegalArgumentException("Missing projection generation");
        activeGeneration = generation;
        paused = false;
    }

    synchronized boolean stopped(long generation) {
        if (generation == 0L || generation != activeGeneration) return false;
        activeGeneration = 0L;
        paused = true;
        return true;
    }

    synchronized boolean isPaused() {
        return paused;
    }

    synchronized void reset() {
        activeGeneration = 0L;
        paused = false;
    }
}
