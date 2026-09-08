package com.remotedesk.agent;

import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/** Keeps diagnostic disk I/O off UDP receive and other real-time threads. */
final class AndroidRealtimeLogSink implements AutoCloseable {
    interface Writer {
        void write(String message);
    }

    private final ThreadPoolExecutor executor;
    private final LatestWorkerMailbox<String> mailbox;

    AndroidRealtimeLogSink(Writer writer) {
        if (writer == null) {
            throw new IllegalArgumentException("writer is required");
        }

        executor = new ThreadPoolExecutor(
            1,
            1,
            0L,
            TimeUnit.MILLISECONDS,
            new ArrayBlockingQueue<>(1),
            runnable -> {
                Thread thread = new Thread(
                    runnable,
                    "RemoteDesk-Android-Realtime-Log");
                thread.setDaemon(true);
                return thread;
            },
            new ThreadPoolExecutor.AbortPolicy());
        mailbox = new LatestWorkerMailbox<>(
            executor,
            writer::write,
            ignored -> {
            },
            ignored -> {
                // Logging must remain best-effort and must never recursively
                // log a failure from its own writer.
            });
    }

    boolean offer(String message) {
        return message != null && !message.isEmpty() && mailbox.offer(message);
    }

    @Override
    public void close() {
        mailbox.close();
        executor.shutdownNow();
    }

    boolean awaitStopped(long timeoutMillis) {
        if (timeoutMillis < 0L) {
            throw new IllegalArgumentException("timeout must not be negative");
        }
        try {
            return executor.awaitTermination(timeoutMillis, TimeUnit.MILLISECONDS);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
            return false;
        }
    }

    int pendingCountForTests() {
        return mailbox.pendingCount();
    }

    int largestWorkerCountForTests() {
        return executor.getLargestPoolSize();
    }
}
