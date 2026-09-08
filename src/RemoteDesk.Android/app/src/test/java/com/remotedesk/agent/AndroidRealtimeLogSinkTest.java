package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

public final class AndroidRealtimeLogSinkTest {
    @Test
    public void slowWriterNeverBlocksCallerAndPendingLogsCollapseToLatest()
        throws Exception {
        List<String> written = Collections.synchronizedList(new ArrayList<>());
        CountDownLatch firstStarted = new CountDownLatch(1);
        CountDownLatch releaseFirst = new CountDownLatch(1);
        CountDownLatch twoWritten = new CountDownLatch(2);
        AndroidRealtimeLogSink sink = new AndroidRealtimeLogSink(message -> {
            written.add(message);
            if ("first".equals(message)) {
                firstStarted.countDown();
                try {
                    releaseFirst.await(5, TimeUnit.SECONDS);
                } catch (InterruptedException ex) {
                    Thread.currentThread().interrupt();
                }
            }
            twoWritten.countDown();
        });
        try {
            assertTrue(sink.offer("first"));
            assertTrue(firstStarted.await(2, TimeUnit.SECONDS));

            // These calls return while the writer remains blocked, proving
            // the UDP receive path never performs the disk write itself.
            assertTrue(sink.offer("stale"));
            assertTrue(sink.offer("latest"));
            assertEquals(1, sink.pendingCountForTests());
            assertEquals(1, sink.largestWorkerCountForTests());

            releaseFirst.countDown();
            assertTrue(twoWritten.await(2, TimeUnit.SECONDS));
            assertEquals(2, written.size());
            assertEquals("first", written.get(0));
            assertEquals("latest", written.get(1));
        } finally {
            releaseFirst.countDown();
            sink.close();
            assertTrue(sink.awaitStopped(2_000L));
        }
    }
}
