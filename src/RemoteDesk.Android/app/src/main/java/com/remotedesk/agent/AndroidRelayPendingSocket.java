package com.remotedesk.agent;

import java.net.Socket;
import java.util.function.Consumer;

/** Each network operation owns its callbacks; late cleanup cannot replace another operation's socket. */
final class AndroidRelayPendingSocket implements Consumer<Socket>, AutoCloseable {
    private Socket current;
    private boolean closed;

    @Override public void accept(Socket socket) {
        Socket dispose;
        synchronized (this) {
            if (closed) dispose = socket;
            else {
                dispose = current == socket ? null : current;
                current = socket;
            }
        }
        AndroidRelay.close(dispose);
    }

    @Override public void close() {
        Socket dispose;
        synchronized (this) {
            closed = true;
            dispose = current;
            current = null;
        }
        AndroidRelay.close(dispose);
    }
}
