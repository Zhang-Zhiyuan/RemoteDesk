package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.junit.Test;

public final class AndroidHostStopPolicyTest {
    @Test
    public void mainThreadStopOnlySignalsAndNeverRunsBlockingWorkerClose()
        throws Exception {
        CountDownLatch blocker = new CountDownLatch(1);
        boolean[] signalled = { false };
        boolean[] udpSignalled = { false };
        boolean[] workersClosed = { false };
        AndroidHostStopPolicy.Session session = new AndroidHostStopPolicy.Session() {
            @Override
            public void signalStopped() {
                signalled[0] = true;
            }

            @Override
            public void signalUdpClose() {
                udpSignalled[0] = true;
            }

            @Override
            public void closeWorkers() {
                try {
                    blocker.await(5, TimeUnit.SECONDS);
                } catch (InterruptedException ex) {
                    Thread.currentThread().interrupt();
                }
                workersClosed[0] = true;
            }
        };
        long startedAt = System.nanoTime();

        AndroidHostStopPolicy.signalFromMainThread(session);

        assertTrue(TimeUnit.NANOSECONDS.toMillis(
            System.nanoTime() - startedAt) < 100L);
        assertTrue(signalled[0]);
        assertTrue(udpSignalled[0]);
        assertFalse(workersClosed[0]);
        blocker.countDown();
    }
}
