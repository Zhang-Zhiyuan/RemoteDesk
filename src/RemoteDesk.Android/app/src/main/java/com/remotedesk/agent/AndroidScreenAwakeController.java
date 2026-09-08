package com.remotedesk.agent;

// Owns only this app's screen lock. It never changes the device's timeout or keyguard.
final class AndroidScreenAwakeController implements AutoCloseable {
    interface ScreenLock {
        void acquire();
        void release();
        boolean isHeld();
    }

    interface Factory {
        ScreenLock create();
    }

    private final Factory factory;
    private ScreenLock screenLock;

    AndroidScreenAwakeController(Factory factory) {
        this.factory = factory;
    }

    void setEnabled(boolean enabled) {
        if (!enabled) {
            close();
            return;
        }

        try {
            if (screenLock != null && screenLock.isHeld()) return;
            close();
            screenLock = factory.create();
            screenLock.acquire();
        } catch (RuntimeException ex) {
            close();
            throw ex;
        }
    }

    @Override
    public void close() {
        ScreenLock previous = screenLock;
        screenLock = null;
        if (previous == null) return;
        try {
            if (previous.isHeld()) previous.release();
        } catch (RuntimeException ignored) {
            // Still allow the other streaming resources to be released during teardown.
        }
    }
}
