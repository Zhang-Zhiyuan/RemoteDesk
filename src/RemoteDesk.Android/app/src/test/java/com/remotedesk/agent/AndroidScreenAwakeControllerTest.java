package com.remotedesk.agent;

import static org.junit.Assert.*;

import org.junit.Test;

public final class AndroidScreenAwakeControllerTest {
    @Test
    public void inactivePresenceDoesNotAcquireAndRepeatedStartDoesNotLeak() {
        FakeLock lock = new FakeLock();
        AndroidScreenAwakeController controller = new AndroidScreenAwakeController(() -> lock);
        controller.setEnabled(false);
        assertEquals(0, lock.acquired);
        controller.setEnabled(true);
        controller.setEnabled(true);
        assertEquals(1, lock.acquired);
        controller.setEnabled(false);
        controller.close();
        assertEquals(1, lock.released);
        assertFalse(lock.held);
    }

    @Test
    public void newSessionOrTogglingCanAcquireAgain() {
        FakeLock lock = new FakeLock();
        AndroidScreenAwakeController controller = new AndroidScreenAwakeController(() -> lock);
        controller.setEnabled(true);
        controller.close();
        controller.setEnabled(true);
        assertEquals(2, lock.acquired);
        assertTrue(lock.held);
        controller.close();
        assertEquals(2, lock.released);
    }

    @Test
    public void failedAcquireReleasesPartialOwnershipAndCanRetry() {
        FakeLock lock = new FakeLock();
        lock.failAcquire = true;
        AndroidScreenAwakeController controller = new AndroidScreenAwakeController(() -> lock);
        assertThrows(IllegalStateException.class, () -> controller.setEnabled(true));
        assertFalse(lock.held);
        assertEquals(1, lock.released);
        lock.failAcquire = false;
        controller.setEnabled(true);
        assertTrue(lock.held);
        controller.close();
    }

    @Test
    public void failingFactoryCanRetryAndTeardownIsSafe() {
        FakeLock lock = new FakeLock();
        int[] attempts = {0};
        AndroidScreenAwakeController controller = new AndroidScreenAwakeController(() -> {
            if (++attempts[0] == 1) throw new IllegalStateException("unavailable");
            return lock;
        });
        assertThrows(IllegalStateException.class, () -> controller.setEnabled(true));
        controller.close();
        controller.setEnabled(true);
        assertTrue(lock.held);
        controller.close();
    }

    private static final class FakeLock implements AndroidScreenAwakeController.ScreenLock {
        boolean held;
        boolean failAcquire;
        int acquired;
        int released;
        public void acquire() {
            held = true;
            acquired++;
            if (failAcquire) throw new IllegalStateException("partial acquire");
        }
        public void release() { held = false; released++; }
        public boolean isHeld() { return held; }
    }
}
