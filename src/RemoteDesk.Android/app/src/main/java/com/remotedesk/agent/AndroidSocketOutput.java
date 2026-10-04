package com.remotedesk.agent;

import java.io.IOException;
import java.io.OutputStream;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.channels.SocketChannel;

/** Keep channel-backed sockets full duplex on Android's SocketAdaptor. */
final class AndroidSocketOutput {
    private AndroidSocketOutput() { }

    static OutputStream open(Socket socket) throws IOException {
        SocketChannel channel = socket.getChannel();
        if (channel == null) return socket.getOutputStream();
        // Channels.newOutputStream / SocketAdaptor's default output holds the
        // channel's blockingLock while writing. On Android API 30 its
        // InputStream holds that same lock while reading, starving control and
        // file writes behind a continuously receiving viewer. Direct channel
        // writes use its separate write lock; session writers remain serialized
        // by their existing encryption/write lock. Dialing is already blocking.
        return new OutputStream() {
            @Override public void write(int value) throws IOException { write(new byte[] {(byte)value}); }
            @Override public void write(byte[] bytes, int offset, int length) throws IOException {
                ByteBuffer pending = ByteBuffer.wrap(bytes, offset, length);
                while (pending.hasRemaining()) channel.write(pending);
            }
            @Override public void close() throws IOException { socket.close(); }
        };
    }
}
