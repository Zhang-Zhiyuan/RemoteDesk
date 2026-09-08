package com.remotedesk.agent;

import java.io.IOException;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.SocketException;

final class AndroidDiscoverySockets {
    private static final int BIND_ATTEMPTS = 6;
    private static final long BIND_RETRY_DELAY_MILLIS = 120;

    private AndroidDiscoverySockets() {
    }

    static DatagramSocket openBoundDiscoverySocket() throws IOException {
        IOException lastError = null;
        for (int attempt = 1; attempt <= BIND_ATTEMPTS; attempt++) {
            DatagramSocket socket = null;
            try {
                socket = new DatagramSocket(null);
                socket.setReuseAddress(true);
                socket.bind(new InetSocketAddress(InetAddress.getByName("0.0.0.0"), RemoteDeskProtocol.DISCOVERY_PORT));
                return socket;
            } catch (IOException ex) {
                closeQuietly(socket);
                lastError = ex;
                if (attempt < BIND_ATTEMPTS) {
                    sleepBeforeRetry();
                }
            }
        }

        throw new SocketException("RemoteDesk discovery port bind failed after retries: " +
            (lastError == null ? "unknown error" : lastError.getMessage()));
    }

    static int getBindAttemptCount() {
        return BIND_ATTEMPTS;
    }

    static long getBindRetryDelayMillis() {
        return BIND_RETRY_DELAY_MILLIS;
    }

    private static void sleepBeforeRetry() throws IOException {
        try {
            Thread.sleep(BIND_RETRY_DELAY_MILLIS);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
            throw new IOException("Interrupted while waiting to retry discovery bind.", ex);
        }
    }

    private static void closeQuietly(DatagramSocket socket) {
        if (socket != null) {
            socket.close();
        }
    }
}
