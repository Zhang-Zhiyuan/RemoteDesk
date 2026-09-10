package com.remotedesk.agent;

import static org.junit.Assert.*;

import java.io.IOException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.Test;

public final class AndroidHostHeartbeatResponderTest {
    @Test public void blockedWriterDoesNotBlockRequestsAndKeepsOnlyOnePendingReply() throws Exception {
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
        CountDownLatch second = new CountDownLatch(1), finish = new CountDownLatch(1);
        AtomicInteger sends = new AtomicInteger(), failures = new AtomicInteger();
        try (AndroidHostHeartbeatResponder responder = new AndroidHostHeartbeatResponder(() -> {
            if (sends.incrementAndGet() == 1) {
                entered.countDown();
                release.await();
            } else {
                second.countDown();
                finish.await();
            }
        }, failures::incrementAndGet)) {
            assertTrue(responder.request());
            assertTrue(entered.await(2, TimeUnit.SECONDS));
            CountDownLatch requested = new CountDownLatch(1);
            Thread reader = new Thread(() -> {
                for (int i = 0; i < 10_000; i++) responder.request();
                requested.countDown();
            });
            reader.start();
            assertTrue(requested.await(2, TimeUnit.SECONDS));
            assertEquals(1, sends.get());
            release.countDown();
            assertTrue(second.await(2, TimeUnit.SECONDS));
            assertEquals(2, sends.get());
        }
        assertEquals(0, failures.get());
    }

    @Test public void closeDiscardsPendingReplyAndRejectsFurtherRequests() throws Exception {
        CountDownLatch entered = new CountDownLatch(1);
        AtomicInteger sends = new AtomicInteger(), failures = new AtomicInteger();
        AndroidHostHeartbeatResponder responder = new AndroidHostHeartbeatResponder(() -> {
            sends.incrementAndGet(); entered.countDown(); new CountDownLatch(1).await();
        }, failures::incrementAndGet);
        try {
            responder.request();
            assertTrue(entered.await(2, TimeUnit.SECONDS));
            responder.request();
        } finally { responder.close(); }
        assertFalse(responder.request());
        assertEquals(1, sends.get());
        assertEquals(0, failures.get());
    }

    @Test public void failedWriteClosesOwningSessionOnce() throws Exception {
        CountDownLatch failed = new CountDownLatch(1);
        AtomicInteger failures = new AtomicInteger();
        try (AndroidHostHeartbeatResponder responder = new AndroidHostHeartbeatResponder(
                () -> { throw new IOException("synthetic write failure"); },
                () -> { failures.incrementAndGet(); failed.countDown(); })) {
            responder.request();
            assertTrue(failed.await(2, TimeUnit.SECONDS));
            assertFalse(responder.request());
            assertEquals(1, failures.get());
        }
    }

    @Test public void idleCloseDoesNotSendAnything() {
        AtomicInteger sends = new AtomicInteger(), failures = new AtomicInteger();
        AndroidHostHeartbeatResponder responder = new AndroidHostHeartbeatResponder(
            sends::incrementAndGet, failures::incrementAndGet);
        responder.close();
        assertFalse(responder.request());
        assertEquals(0, sends.get());
        assertEquals(0, failures.get());
    }
}
