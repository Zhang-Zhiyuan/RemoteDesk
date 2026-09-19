package com.remotedesk.agent;

import java.util.ArrayDeque;
import java.util.concurrent.Executor;
import java.util.concurrent.RejectedExecutionException;
import java.util.function.BooleanSupplier;

/** Bounded per-connection work on the host's existing background executor. */
final class AndroidClipboardSnapshotQueue implements AutoCloseable {
    interface Operation { void run() throws Exception; }
    static final int MAX_PENDING = 4;
    private final Executor executor;
    private final BooleanSupplier current;
    private final Runnable failure;
    private final ArrayDeque<Operation> pending = new ArrayDeque<>();
    private boolean active;
    private boolean closed;

    AndroidClipboardSnapshotQueue(Executor executor, BooleanSupplier current, Runnable failure) {
        this.executor = executor;
        this.current = current;
        this.failure = failure;
    }

    synchronized boolean offer(Operation operation) {
        if (closed || !current.getAsBoolean() || pending.size() >= MAX_PENDING) return false;
        pending.addLast(operation);
        if (!active) {
            active = true;
            try { executor.execute(this::drain); }
            catch (RejectedExecutionException error) {
                active = false;
                close();
                return false;
            }
        }
        return true;
    }

    private void drain() {
        try {
            while (true) {
                Operation operation;
                synchronized (this) {
                    if (closed || !current.getAsBoolean()) { close(); active = false; return; }
                    operation = pending.pollFirst();
                    if (operation == null) { active = false; return; }
                }
                operation.run();
            }
        } catch (Exception error) {
            boolean notify;
            synchronized (this) { notify = !closed && current.getAsBoolean(); close(); active = false; }
            if (notify) failure.run();
        }
    }

    @Override public synchronized void close() {
        closed = true;
        pending.clear();
    }
}
