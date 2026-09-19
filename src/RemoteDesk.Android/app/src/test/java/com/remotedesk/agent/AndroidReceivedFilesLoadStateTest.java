package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidReceivedFilesLoadStateTest {
    @Test public void repeatedTapsNeverQueueMultipleStorageScans() {
        var state = new AndroidReceivedFilesLoadState();
        long request = state.begin();
        assertTrue(request > 0);
        assertEquals(0, state.begin());
        assertTrue(state.finish(request));
        assertTrue(state.isCurrent(request));
        assertFalse(state.finish(request));
        assertTrue(state.begin() > request);
    }

    @Test public void cancellingOrLeavingPageDropsLateResultWithoutQueueingMoreWork() {
        var state = new AndroidReceivedFilesLoadState();
        long request = state.begin();
        state.cancel();
        assertFalse(state.isCurrent(request));
        assertEquals(0, state.begin());
        assertFalse(state.finish(request));
        assertTrue(state.begin() > request);
    }

    @Test public void alreadyPostedUiResultCannotReplaceANewerLoad() {
        var state = new AndroidReceivedFilesLoadState();
        long first = state.begin();
        assertTrue(state.finish(first));
        long second = state.begin();
        assertFalse(state.isCurrent(first));
        assertFalse(state.finish(first));
        assertEquals(0, state.begin());
        assertTrue(state.finish(second));
    }

    @Test public void destroyedActivityRejectsPendingAndFutureLoads() {
        var state = new AndroidReceivedFilesLoadState();
        long request = state.begin();
        state.close();
        assertFalse(state.finish(request));
        assertFalse(state.isCurrent(request));
        assertEquals(0, state.begin());
    }
}
