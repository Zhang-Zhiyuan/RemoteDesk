package com.remotedesk.agent;

import static org.junit.Assert.*;

import org.junit.Test;

public final class AndroidProjectionStateTest {
    @Test
    public void stoppedGrantPausesExactlyOnceAndFreshGrantResumes() {
        AndroidProjectionState state = new AndroidProjectionState();
        assertFalse(state.isPaused());
        state.started(1);
        assertTrue(state.stopped(1));
        assertTrue(state.isPaused());
        assertFalse(state.stopped(1));
        state.started(2);
        assertFalse(state.isPaused());
        assertFalse(state.stopped(1));
        assertTrue(state.stopped(2));
    }

    @Test
    public void staleCallbacksCannotPauseNewServiceOrNewGrant() {
        AndroidProjectionState state = new AndroidProjectionState();
        assertFalse(state.stopped(0));
        state.started(1);
        state.reset();
        assertFalse(state.stopped(1));
        assertFalse(state.isPaused());
        state.started(2);
        assertFalse(state.stopped(1));
        assertFalse(state.isPaused());
    }

    @Test
    public void stoppingServiceClearsPausedStatus() {
        AndroidProjectionState state = new AndroidProjectionState();
        state.started(1);
        state.stopped(1);
        state.reset();
        assertFalse(state.isPaused());
        assertFalse(state.stopped(1));
        assertThrows(IllegalArgumentException.class, () -> state.started(0));
    }
}
