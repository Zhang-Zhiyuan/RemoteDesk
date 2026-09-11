package com.remotedesk.agent;

import java.util.ArrayDeque;
import java.util.Iterator;
import java.util.List;

final class AndroidViewerInputQueue implements AutoCloseable {
    static final int RELEASE_RESERVE_CAPACITY = 16;

    private final int capacity;
    private final int hardCapacity;
    private int activeReleaseLimit;
    private final ArrayDeque<Command> commands = new ArrayDeque<>();
    private boolean closed;
    private int inFlight;

    AndroidViewerInputQueue(int capacity) {
        if (capacity <= 0 || capacity > Integer.MAX_VALUE - RELEASE_RESERVE_CAPACITY) {
            throw new IllegalArgumentException("capacity must be positive");
        }

        this.capacity = capacity;
        hardCapacity = capacity + RELEASE_RESERVE_CAPACITY;
        activeReleaseLimit = hardCapacity;
    }

    synchronized boolean offer(Command command) {
        if (command == null || closed) {
            return false;
        }

        if (command.kind == RemoteDeskProtocol.INPUT_MOUSE_MOVE) {
            Command latest = commands.peekLast();
            if (latest != null &&
                latest.kind == RemoteDeskProtocol.INPUT_MOUSE_MOVE &&
                latest.samePayload(command)) {
                return false;
            }

            removePendingMouseMoves();
        }

        if (commands.size() >= capacity) {
            removeOldestMouseMove();
        }

        if (commands.size() >= capacity &&
            (!isRelease(command) || commands.size() >= activeReleaseLimit)) {
            return false;
        }

        commands.addLast(command);
        notifyAll();
        return true;
    }

    synchronized void discardPendingMouseMoves() {
        removePendingMouseMoves();
    }

    synchronized boolean offerKeyboardBatch(List<Command> batch) {
        if (closed || batch == null || batch.isEmpty() || batch.size() > AndroidViewerKeyboard.MAX_TEXT_LENGTH * 2) return false;
        for (Command command : batch) {
            if (command == null || (command.kind != RemoteDeskProtocol.INPUT_KEY_DOWN &&
                command.kind != RemoteDeskProtocol.INPUT_KEY_UP && command.kind != RemoteDeskProtocol.INPUT_TEXT)) return false;
        }
        // Never enqueue half a shortcut (e.g. Ctrl without its release) or half
        // an IME commit. The caller retains its draft when the queue is busy.
        if (commands.size() + batch.size() > capacity && !commands.isEmpty()) return false;
        // One IME commit may use up to 256 commands (128 newlines). It may
        // expand only an empty queue, not stack extra commits behind a backlog.
        activeReleaseLimit = Math.max(hardCapacity, batch.size() + RELEASE_RESERVE_CAPACITY);
        commands.addAll(batch);
        notifyAll();
        return true;
    }

    synchronized void discardAll() {
        commands.clear();
        activeReleaseLimit = hardCapacity;
        notifyAll();
    }

    synchronized Command take() throws InterruptedException {
        while (commands.isEmpty() && !closed) {
            wait();
        }

        Command command = poll();
        if (command != null) inFlight++;
        return command;
    }

    synchronized void completeSend() {
        if (inFlight > 0) inFlight--;
        notifyAll();
    }

    synchronized boolean awaitIdle(long timeoutMillis) throws InterruptedException {
        long deadline = System.nanoTime() + java.util.concurrent.TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
        while (!closed && (!commands.isEmpty() || inFlight != 0)) {
            long remaining = deadline - System.nanoTime();
            if (remaining <= 0) return false;
            java.util.concurrent.TimeUnit.NANOSECONDS.timedWait(this, remaining);
        }
        return !closed;
    }

    synchronized Command poll() {
        Command result = commands.pollFirst();
        if (commands.size() <= capacity) activeReleaseLimit = hardCapacity;
        return result;
    }

    synchronized int size() {
        return commands.size();
    }

    synchronized boolean isClosed() {
        return closed;
    }

    @Override
    public synchronized void close() {
        if (closed) {
            return;
        }

        closed = true;
        commands.clear();
        notifyAll();
    }

    private void removePendingMouseMoves() {
        Iterator<Command> iterator = commands.iterator();
        while (iterator.hasNext()) {
            if (iterator.next().kind == RemoteDeskProtocol.INPUT_MOUSE_MOVE) {
                iterator.remove();
            }
        }
    }

    private boolean removeOldestMouseMove() {
        Iterator<Command> iterator = commands.iterator();
        while (iterator.hasNext()) {
            if (iterator.next().kind == RemoteDeskProtocol.INPUT_MOUSE_MOVE) {
                iterator.remove();
                return true;
            }
        }

        return false;
    }

    private static boolean isRelease(Command command) {
        return command.kind == RemoteDeskProtocol.INPUT_MOUSE_UP ||
            command.kind == RemoteDeskProtocol.INPUT_KEY_UP;
    }

    static final class Command {
        final int kind;
        final int button;
        final int x;
        final int y;
        final int data;
        final long mouseRouteGeneration;
        final long inputCapabilityGeneration;

        Command(int kind, int button, int x, int y, int data) {
            this(kind, button, x, y, data, 0L, 0L);
        }

        Command(int kind, int button, int x, int y, int data, long mouseRouteGeneration) {
            this(kind, button, x, y, data, mouseRouteGeneration, 0L);
        }

        Command(
            int kind,
            int button,
            int x,
            int y,
            int data,
            long mouseRouteGeneration,
            long inputCapabilityGeneration) {
            this.kind = kind;
            this.button = button;
            this.x = x;
            this.y = y;
            this.data = data;
            this.mouseRouteGeneration = mouseRouteGeneration;
            this.inputCapabilityGeneration = inputCapabilityGeneration;
        }

        private boolean samePayload(Command other) {
            return kind == other.kind &&
                button == other.button &&
                x == other.x &&
                y == other.y &&
                data == other.data;
        }
    }
}
