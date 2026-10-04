package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.channels.SocketChannel;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

public final class AndroidSocketOutputTest {
    @Test public void channelWriteDoesNotWaitForPeerToUnblockTheConcurrentRead() throws Exception {
        var executor = Executors.newFixedThreadPool(2);
        try (ServerSocket listener = new ServerSocket(0, 1, InetAddress.getLoopbackAddress());
             SocketChannel channel = SocketChannel.open(new InetSocketAddress(InetAddress.getLoopbackAddress(), listener.getLocalPort()));
             Socket peer = listener.accept()) {
            Socket socket = channel.socket();
            CountDownLatch reading = new CountDownLatch(1);
            var read = executor.submit(() -> { reading.countDown(); return socket.getInputStream().read(); });
            assertTrue(reading.await(1, TimeUnit.SECONDS));
            var write = executor.submit(() -> { AndroidSocketOutput.open(socket).write(new byte[] {0, 41, 42, 0}, 1, 2); return true; });
            peer.setSoTimeout(2000);
            assertEquals(41, peer.getInputStream().read()); assertEquals(42, peer.getInputStream().read());
            assertTrue(write.get(1, TimeUnit.SECONDS));
            assertFalse(read.isDone());
            peer.getOutputStream().write(43);
            assertEquals(43, (int)read.get(1, TimeUnit.SECONDS));
        } finally { executor.shutdownNow(); }
    }

    @Test public void plainSocketRetainsNativeOutputAndClosingAdapterClosesChannel() throws Exception {
        try (ServerSocket listener = new ServerSocket(0, 1, InetAddress.getLoopbackAddress());
             Socket socket = new Socket(InetAddress.getLoopbackAddress(), listener.getLocalPort());
             Socket peer = listener.accept()) {
            assertNull(socket.getChannel());
            AndroidSocketOutput.open(socket).write(17);
            peer.setSoTimeout(2000);
            assertEquals(17, peer.getInputStream().read());
        }
        try (ServerSocket listener = new ServerSocket(0, 1, InetAddress.getLoopbackAddress());
             SocketChannel channel = SocketChannel.open(new InetSocketAddress(InetAddress.getLoopbackAddress(), listener.getLocalPort()));
             Socket peer = listener.accept()) {
            AndroidSocketOutput.open(channel.socket()).close();
            assertFalse(channel.isOpen());
        }
    }
}
