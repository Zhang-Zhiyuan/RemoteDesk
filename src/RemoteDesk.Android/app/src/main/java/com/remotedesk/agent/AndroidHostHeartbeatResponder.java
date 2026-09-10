package com.remotedesk.agent;

/** Keeps a blocked video/Pong write off the input reader. One active + one pending reply. */
final class AndroidHostHeartbeatResponder implements AutoCloseable {
    interface Sender { void send() throws Exception; }

    private final Sender sender;
    private final Runnable onFailure;
    private final Object gate = new Object();
    private final Thread worker;
    private boolean pending;
    private boolean stopped;

    AndroidHostHeartbeatResponder(Sender sender, Runnable onFailure) {
        this.sender = java.util.Objects.requireNonNull(sender);
        this.onFailure = java.util.Objects.requireNonNull(onFailure);
        worker = new Thread(this::run, "RemoteDeskHostPong");
        worker.setDaemon(true);
        worker.start();
    }

    boolean request() {
        synchronized (gate) {
            if (stopped) return false;
            pending = true;
            gate.notifyAll();
            return true;
        }
    }

    private void run() {
        try {
            while (true) {
                synchronized (gate) {
                    while (!pending && !stopped) gate.wait();
                    if (stopped) return;
                    pending = false;
                }
                sender.send();
            }
        } catch (Exception error) {
            synchronized (gate) {
                if (stopped) return;
                stopped = true;
                pending = false;
            }
            // Session ownership stays with the caller. It closes this session's
            // socket to wake its reader; no unbounded task or retry is created.
            onFailure.run();
        }
    }

    @Override public void close() {
        synchronized (gate) {
            stopped = true;
            pending = false;
            gate.notifyAll();
        }
        worker.interrupt();
        if (Thread.currentThread() != worker) {
            try { worker.join(1000); }
            catch (InterruptedException error) { Thread.currentThread().interrupt(); }
        }
    }
}
