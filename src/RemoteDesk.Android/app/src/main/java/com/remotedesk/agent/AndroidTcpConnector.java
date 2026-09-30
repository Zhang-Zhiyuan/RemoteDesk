package com.remotedesk.agent;

import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.channels.SelectionKey;
import java.nio.channels.Selector;
import java.nio.channels.SocketChannel;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.function.Consumer;

/** Bounded TCP-only family race on the viewer worker; no extra threads or authentication. */
final class AndroidTcpConnector {
    static Socket connect(String host, int port, int timeoutMillis, AndroidRelayNetworkSelector.Dial dial,
            Consumer<Socket> configure) throws Exception {
        long deadline = System.nanoTime() + timeoutMillis * 1_000_000L;
        return connect(InetAddress.getAllByName(host), port, deadline, dial, configure);
    }

    static Socket connect(InetAddress[] resolved, int port, long deadline, AndroidRelayNetworkSelector.Dial dial,
            Consumer<Socket> configure) throws Exception {
        List<InetAddress> addresses = AndroidRelay.relayAddresses(resolved, false);
        List<SocketChannel> owned = new ArrayList<>();
        Socket selected = null;
        IOException failure = null;
        SocketChannel winner = null;
        try {
            try (Selector selector = Selector.open()) {
                int next = 0;
                long startNext = 0;
                while (winner == null) {
                    dial.checkOpen();
                    long now = System.nanoTime();
                    if (now >= deadline) throw new SocketTimeoutException("连接超时，请检查地址、端口和防火墙。");
                    if (next < addresses.size() && (now >= startNext || selector.keys().isEmpty())) {
                        InetAddress address = addresses.get(next++);
                        SocketChannel channel = SocketChannel.open();
                        owned.add(channel);
                        Socket socket = channel.socket();
                        dial.add(socket);
                        try {
                            configure.accept(socket);
                            channel.configureBlocking(false);
                            if (channel.connect(new InetSocketAddress(address, port))) { winner = channel; break; }
                            channel.register(selector, SelectionKey.OP_CONNECT);
                            startNext = now + 250_000_000L;
                        } catch (IOException error) { failure = error; dial.release(socket); startNext = 0; }
                    }
                    if (next == addresses.size() && selector.keys().isEmpty())
                        throw failure == null ? new IOException("目标没有可用的 IPv4 / IPv6 地址。") : failure;
                    long remaining = deadline - System.nanoTime();
                    if (next < addresses.size()) remaining = Math.min(remaining, startNext - System.nanoTime());
                    selector.select(Math.max(1, Math.min(100, remaining / 1_000_000L)));
                    Iterator<SelectionKey> ready = selector.selectedKeys().iterator();
                    while (ready.hasNext()) {
                        SelectionKey key = ready.next(); ready.remove();
                        SocketChannel channel = (SocketChannel) key.channel();
                        if (!key.isValid()) continue;
                        try {
                            if (channel.finishConnect()) { winner = channel; break; }
                        } catch (IOException error) {
                            failure = error; key.cancel(); dial.release(channel.socket()); startNext = 0;
                        }
                    }
                    if (winner == null) selector.selectNow(); // Retire cancelled keys before checking exhaustion.
                }
            }
            dial.checkOpen();
            winner.configureBlocking(true);
            selected = dial.take(winner.socket());
            return selected;
        } finally {
            for (SocketChannel channel : owned) if (channel.socket() != selected) dial.release(channel.socket());
        }
    }
}
