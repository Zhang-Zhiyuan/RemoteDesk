package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import org.junit.Test;

public final class AndroidBoundedThreadShutdownTest {
    @Test
    public void gracefulStopJoinsWorkerWithinTheBound() throws Exception {
        AtomicBoolean running = new AtomicBoolean(true);
        Thread worker = new Thread(() -> {
            while (running.get()) {
                Thread.yield();
            }
        });
        worker.start();

        boolean stopped = AndroidBoundedThreadShutdown.stop(
            worker,
            new AndroidBoundedThreadShutdown.StopActions() {
                @Override
                public void requestGracefulStop() {
                    running.set(false);
                }

                @Override
                public void requestForcedStop() {
                    running.set(false);
                }
            },
            500L);

        assertTrue(stopped);
        assertFalse(worker.isAlive());
    }

    @Test
    public void currentThreadGuardNeverSelfJoins() throws Exception {
        CountDownLatch finished = new CountDownLatch(1);
        AtomicBoolean result = new AtomicBoolean(true);
        Thread[] holder = new Thread[1];
        holder[0] = new Thread(() -> {
            result.set(AndroidBoundedThreadShutdown.stop(
                holder[0],
                new AndroidBoundedThreadShutdown.StopActions() {
                    @Override
                    public void requestGracefulStop() {
                    }

                    @Override
                    public void requestForcedStop() {
                    }
                },
                500L));
            finished.countDown();
        });
        holder[0].start();

        assertTrue(finished.await(250L, TimeUnit.MILLISECONDS));
        assertFalse(result.get());
        holder[0].join(250L);
        assertFalse(holder[0].isAlive());
    }
}
