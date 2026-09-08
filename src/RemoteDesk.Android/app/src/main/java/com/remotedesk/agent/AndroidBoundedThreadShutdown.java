package com.remotedesk.agent;

final class AndroidBoundedThreadShutdown {
    interface StopActions {
        void requestGracefulStop();

        void requestForcedStop();
    }

    private AndroidBoundedThreadShutdown() {
    }

    static boolean stop(Thread worker, StopActions actions, long timeoutMillis) {
        if (worker == null || actions == null || timeoutMillis <= 0) {
            throw new IllegalArgumentException("worker, actions and a positive timeout are required");
        }

        actions.requestGracefulStop();
        if (Thread.currentThread() == worker) {
            return false;
        }

        long gracefulMillis = Math.max(1L, timeoutMillis / 2L);
        if (!join(worker, gracefulMillis)) {
            return false;
        }
        if (!worker.isAlive()) {
            return true;
        }

        actions.requestForcedStop();
        worker.interrupt();
        return join(worker, Math.max(1L, timeoutMillis - gracefulMillis)) &&
            !worker.isAlive();
    }

    private static boolean join(Thread worker, long timeoutMillis) {
        try {
            worker.join(timeoutMillis);
            return true;
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
            return false;
        }
    }
}
