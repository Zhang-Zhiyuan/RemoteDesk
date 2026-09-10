package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.IOException;
import java.net.Socket;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;

public final class AndroidRelayNetworkSelectorTest {
    private static final class TestSocket extends Socket {
        final CountDownLatch closed = new CountDownLatch(1);
        @Override public synchronized void close() { closed.countDown(); }
        @Override public boolean isClosed() { return closed.getCount() == 0; }
    }

    @Test public void meteredUnvalidatedRestrictedOrVpnAlternativesAreNotUsed() {
        assertTrue(AndroidRelayNetworkSelector.allowAlternative(false, true, true, true, true, true));
        assertFalse(AndroidRelayNetworkSelector.allowAlternative(true, true, true, true, true, true));
        assertFalse(AndroidRelayNetworkSelector.allowAlternative(false, false, true, true, true, true));
        assertFalse(AndroidRelayNetworkSelector.allowAlternative(false, true, false, true, true, true));
        assertFalse(AndroidRelayNetworkSelector.allowAlternative(false, true, true, false, true, true));
        assertFalse(AndroidRelayNetworkSelector.allowAlternative(false, true, true, true, false, true));
        assertFalse(AndroidRelayNetworkSelector.allowAlternative(false, true, true, true, true, false));
    }

    @Test public void fastTrustedPathWinsAndAllOtherSocketsClose() throws Exception {
        AndroidRelayNetworkSelector.Selector selector = new AndroidRelayNetworkSelector.Selector(System::nanoTime);
        TestSocket winner = new TestSocket(), loser = new TestSocket();
        CountDownLatch started = new CountDownLatch(1);
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            Socket result = selector.connect("relay", List.of("wifi"), dial, (path, owner) -> {
                if (path.equals("default")) {
                    owner.add(loser); started.countDown();
                    loser.closed.await(3, TimeUnit.SECONDS);
                    return loser;
                }
                assertTrue(started.await(2, TimeUnit.SECONDS));
                owner.add(winner); return winner;
            });
            assertSame(winner, result);
            assertTrue(loser.closed.await(2, TimeUnit.SECONDS));
            assertFalse(winner.isClosed());
            winner.close();
        }
    }

    @Test public void badCertificateCannotBeatAValidFallback() throws Exception {
        AndroidRelayNetworkSelector.Selector selector = new AndroidRelayNetworkSelector.Selector(System::nanoTime);
        CountDownLatch rejectedPathAttempted = new CountDownLatch(1);
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            Socket result = selector.connect("relay", List.of("wifi"), dial, (path, owner) -> {
                if (path.equals("wifi")) {
                    rejectedPathAttempted.countDown();
                    throw new AndroidRelay.IdentityFailure(true, null);
                }
                assertTrue(rejectedPathAttempted.await(2, TimeUnit.SECONDS));
                TestSocket socket = new TestSocket(); owner.add(socket); return socket;
            });
            result.close();
        }
    }

    @Test public void allFailuresKeepIdentityError() throws Exception {
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            assertThrows(AndroidRelay.IdentityFailure.class, () ->
                new AndroidRelayNetworkSelector.Selector(System::nanoTime).connect("relay", List.of("wifi"), dial,
                    (path, owner) -> { throw path.equals("wifi") ? new AndroidRelay.IdentityFailure(true, null) : new IOException("offline"); }));
        }
    }

    @Test public void closingPendingHandleCancelsAllCandidates() throws Exception {
        AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
        TestSocket one = new TestSocket(), two = new TestSocket();
        dial.add(one); dial.add(two); dial.close();
        assertTrue(one.isClosed()); assertTrue(two.isClosed());
        TestSocket late = new TestSocket();
        assertThrows(IOException.class, () -> dial.add(late));
        assertTrue(late.isClosed());
        assertThrows(IOException.class, () -> dial.take(one));
    }

    @Test public void wrappingTlsTransfersUnderlyingSocketOwnership() throws Exception {
        AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
        TestSocket raw = new TestSocket(), tls = new TestSocket();
        dial.add(raw); dial.replace(raw, tls); dial.take(tls); dial.close();
        assertFalse(raw.isClosed()); assertFalse(tls.isClosed());
        raw.close(); tls.close();
    }

    @Test public void cachedWinnerAvoidsOtherHandshakesButExpires() throws Exception {
        AtomicLong now = new AtomicLong();
        AndroidRelayNetworkSelector.Selector selector = new AndroidRelayNetworkSelector.Selector(now::get);
        List<String> calls = new CopyOnWriteArrayList<>();
        AndroidRelayNetworkSelector.Connector<TestSocket> connect = (path, owner) -> {
            calls.add(path);
            TestSocket socket = new TestSocket(); owner.add(socket);
            if (path.equals("default")) {
                socket.closed.await(3, TimeUnit.SECONDS);
                owner.checkOpen();
            }
            return socket;
        };
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            selector.connect("relay", List.of("wifi"), dial, connect).close();
        }
        calls.clear(); now.set(TimeUnit.SECONDS.toNanos(30));
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            selector.connect("relay", List.of("wifi"), dial, connect).close();
        }
        assertEquals(List.of("wifi"), calls);
        calls.clear(); now.set(TimeUnit.SECONDS.toNanos(61));
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            selector.connect("relay", List.of("wifi"), dial, connect).close();
        }
        assertTrue(calls.contains("default")); assertTrue(calls.contains("wifi"));
    }

    @Test public void noAlternativeKeepsOneNormalConnection() throws Exception {
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            List<String> calls = new CopyOnWriteArrayList<>();
            Socket result = new AndroidRelayNetworkSelector.Selector(System::nanoTime).connect("relay", List.of(), dial,
                (path, owner) -> { calls.add(path); TestSocket socket = new TestSocket(); owner.add(socket); return socket; });
            assertEquals(List.of("default"), calls);
            result.close();
        }
    }

    @Test public void singleNetworkHasAnOverallDeadlineEvenWhenDnsIgnoresCancellation() throws Exception {
        AndroidRelayNetworkSelector.Selector selector = new AndroidRelayNetworkSelector.Selector(System::nanoTime, 100);
        CountDownLatch release = new CountDownLatch(1), finished = new CountDownLatch(1);
        TestSocket pending = new TestSocket();
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            assertThrows(java.net.SocketTimeoutException.class, () -> selector.connect("relay", List.of(), dial,
                (path, owner) -> {
                    owner.add(pending);
                    try {
                        while (release.getCount() != 0) {
                            try { release.await(); } catch (InterruptedException ignored) { }
                        }
                        owner.checkOpen();
                        return pending;
                    } finally { finished.countDown(); }
                }));
            assertTrue(pending.isClosed());
            assertTrue(dial.isClosed());
        } finally {
            release.countDown();
            assertTrue(finished.await(2, TimeUnit.SECONDS));
        }
    }

    @Test public void cancellingSingleNetworkDialReturnsWithoutWaitingForDns() throws Exception {
        AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
        CountDownLatch started = new CountDownLatch(1), release = new CountDownLatch(1), finished = new CountDownLatch(1);
        java.util.concurrent.ExecutorService caller = java.util.concurrent.Executors.newSingleThreadExecutor();
        try {
            java.util.concurrent.Future<?> call = caller.submit(() -> {
                assertThrows(IOException.class, () -> new AndroidRelayNetworkSelector.Selector(System::nanoTime)
                    .connect("relay", List.of(), dial, (path, owner) -> {
                        started.countDown();
                        try {
                            while (release.getCount() != 0) {
                                try { release.await(); } catch (InterruptedException ignored) { }
                            }
                            owner.checkOpen();
                            TestSocket late = new TestSocket(); owner.add(late); return late;
                        } finally { finished.countDown(); }
                    }));
            });
            assertTrue(started.await(2, TimeUnit.SECONDS));
            dial.close();
            call.get(2, TimeUnit.SECONDS);
        } finally {
            dial.close(); release.countDown(); caller.shutdownNow();
            assertTrue(finished.await(2, TimeUnit.SECONDS));
        }
    }

    @Test public void rejectedWorkClosesPendingSocketsAndReportsRetryableFailure() throws Exception {
        java.util.concurrent.ExecutorService unavailable = java.util.concurrent.Executors.newSingleThreadExecutor();
        unavailable.shutdownNow();
        TestSocket pending = new TestSocket();
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            dial.add(pending);
            IOException failure = assertThrows(IOException.class, () ->
                new AndroidRelayNetworkSelector.Selector(System::nanoTime, 1000, unavailable)
                    .connect("relay", List.of(), dial, (path, owner) -> { fail("executor rejected work"); return pending; }));
            assertTrue(failure.getCause() instanceof java.util.concurrent.RejectedExecutionException);
            assertTrue(pending.isClosed());
            assertTrue(dial.isClosed());
        }
    }
}
