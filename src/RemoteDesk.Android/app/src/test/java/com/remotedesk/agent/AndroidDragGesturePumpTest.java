package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.junit.Test;

public final class AndroidDragGesturePumpTest {
    @Test public void pendingReleaseWaitsForPhysicalCompletionWithoutCanceling() throws Exception {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);
        pump.beginSession(1, 2);
        pump.end(1, 2);
        CountDownLatch started = new CountDownLatch(1), finished = new CountDownLatch(1);
        boolean[] passed = {false};
        Thread worker = new Thread(() -> {
            started.countDown();
            passed[0] = pump.awaitPendingRelease(1000);
            finished.countDown();
        });
        worker.start();
        assertTrue(started.await(1, TimeUnit.SECONDS));
        assertFalse(finished.await(40, TimeUnit.MILLISECONDS));
        dispatcher.completeNext();
        assertTrue(finished.await(1, TimeUnit.SECONDS));
        worker.join(1000);
        assertTrue(passed[0]);
        assertEquals(0, dispatcher.resetCount);
    }

    @Test public void heldDragDoesNotBlockOrReleaseForTyping() {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);
        pump.beginSession(1, 2);
        pump.move(3, 4);
        assertTrue(pump.awaitPendingRelease(0));
        assertTrue(pump.snapshot().acceptingInput);
        assertFalse(pump.snapshot().releasePending);
    }

    @Test public void timedOutReleaseDoesNotPretendThatClickWasApplied() {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);
        pump.beginSession(1, 2);
        pump.end(1, 2);
        assertFalse(pump.awaitPendingRelease(0));
        assertTrue(pump.snapshot().active);
        assertEquals(0, dispatcher.resetCount);
        dispatcher.completeNext();
        assertTrue(pump.awaitPendingRelease(0));
    }

    @Test public void canceledReleaseDoesNotAuthorizeFollowingText() throws Exception {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);
        pump.beginSession(1, 2);
        pump.end(1, 2);
        CountDownLatch started = new CountDownLatch(1);
        boolean[] passed = {true};
        Thread worker = new Thread(() -> { started.countDown(); passed[0] = pump.awaitPendingRelease(1000); });
        worker.start();
        assertTrue(started.await(1, TimeUnit.SECONDS));
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(1);
        while (worker.getState() != Thread.State.TIMED_WAITING && System.nanoTime() < deadline) Thread.yield();
        assertEquals(Thread.State.TIMED_WAITING, worker.getState());
        dispatcher.cancelNext();
        worker.join(1000);
        assertFalse(worker.isAlive());
        assertFalse(passed[0]);
    }

    @Test(expected = IllegalArgumentException.class) public void negativeReleaseWaitIsRejected() {
        new AndroidDragGesturePump(new FakeDispatcher()).awaitPendingRelease(-1);
    }

    @Test
    public void highFrequencyMovesCollapseToTheLatestPointAndNeverOverlap() {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);

        assertTrue(pump.beginSession(10, 20));
        for (int index = 1; index <= 1_000; index++) {
            assertTrue(pump.move(10 + index, 20 + index));
        }

        assertEquals(1, dispatcher.segments.size());
        assertEquals(1, dispatcher.maxInFlight);
        assertEquals(2, pump.snapshot().retainedPointCount());
        dispatcher.completeNext();

        assertEquals(2, dispatcher.segments.size());
        AndroidDragGesturePump.Segment latest = dispatcher.segments.get(1);
        assertEquals(1_010.0f, latest.endX, 0.0f);
        assertEquals(1_020.0f, latest.endY, 0.0f);
        assertTrue(latest.willContinue);
        assertEquals(1, dispatcher.maxInFlight);
    }

    @Test
    public void moveIsActuallyDispatchedBeforeMouseUpArrives() {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);

        assertTrue(pump.beginSession(10, 20));
        assertTrue(pump.move(100, 200));

        assertEquals(1, dispatcher.segments.size());
        assertTrue(dispatcher.segments.get(0).willContinue);
        assertTrue(pump.snapshot().acceptingInput);
    }

    @Test
    public void mouseUpWithoutMoveEndsTheContinuedStrokeAndPreservesClick() {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);

        assertTrue(pump.beginSession(50, 60));
        assertTrue(pump.end(50, 60));

        assertEquals(1, dispatcher.segments.size());
        AndroidDragGesturePump.Segment release = dispatcher.segments.get(0);
        assertTrue(release.first);
        assertFalse(release.willContinue);
        assertEquals(50.0f, release.startX, 0.0f);
        assertEquals(60.0f, release.startY, 0.0f);
        assertEquals(50.0f, release.endX, 0.0f);
        assertEquals(60.0f, release.endY, 0.0f);

        dispatcher.completeNext();
        assertFalse(pump.snapshot().active);
        assertEquals(0, dispatcher.inFlight);
    }

    @Test
    public void sessionCancelSeriallyTerminatesAnInFlightStroke() {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);

        assertTrue(pump.beginSession(1, 2));
        assertTrue(pump.move(30, 40));
        pump.cancel();
        assertFalse(pump.snapshot().acceptingInput);
        assertFalse(pump.beginSession(9, 9));

        dispatcher.completeNext();
        assertEquals(2, dispatcher.segments.size());
        assertFalse(dispatcher.segments.get(1).willContinue);
        dispatcher.completeNext();
        assertFalse(pump.snapshot().active);
    }

    @Test
    public void teardownWaitsForInFlightThenDispatchesAndCompletesFinalSegment()
        throws Exception {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);
        assertTrue(pump.beginSession(1, 2));
        assertTrue(pump.move(30, 40));
        CountDownLatch closed = new CountDownLatch(1);
        boolean[] graceful = { false };
        Thread closer = new Thread(() -> {
            graceful[0] = pump.cancelAndAwaitIdle(500L);
            closed.countDown();
        });
        closer.start();

        waitUntil(() -> !pump.snapshot().acceptingInput, 200L);
        dispatcher.completeNext();
        assertEquals(2, dispatcher.segments.size());
        assertFalse(dispatcher.segments.get(1).willContinue);
        dispatcher.completeNext();

        assertTrue(closed.await(250L, TimeUnit.MILLISECONDS));
        assertTrue(graceful[0]);
        assertFalse(pump.snapshot().active);
    }

    @Test
    public void downOnlyDisconnectClearsWithoutDispatchingATap() {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);
        assertTrue(pump.beginSession(50, 60));

        assertTrue(pump.cancelAndAwaitIdle(100L));

        assertEquals(0, dispatcher.segments.size());
        assertFalse(pump.snapshot().active);
    }

    @Test
    public void teardownTimeoutIsBoundedAndForceFencesLateCallback() throws Exception {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);
        assertTrue(pump.beginSession(1, 2));
        assertTrue(pump.move(30, 40));
        AndroidDragGesturePump.ResultCallback stale = dispatcher.removeNextCallback();
        long startedAt = System.nanoTime();

        assertFalse(pump.cancelAndAwaitIdle(40L));

        long elapsedMillis = TimeUnit.NANOSECONDS.toMillis(
            System.nanoTime() - startedAt);
        assertTrue(elapsedMillis < 250L);
        assertFalse(pump.snapshot().active);
        stale.onCompleted();
        assertFalse(pump.snapshot().active);
    }

    @Test
    public void staleCallbackFromRetiredGenerationCannotAdvanceReplacement() {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);

        assertTrue(pump.beginSession(1, 1));
        assertTrue(pump.move(3, 3));
        AndroidDragGesturePump.ResultCallback stale = dispatcher.removeNextCallback();
        pump.forceCancel();
        assertTrue(pump.beginSession(2, 2));
        assertTrue(pump.move(4, 4));
        long replacementGeneration = pump.snapshot().generation;

        stale.onCompleted();

        AndroidDragGesturePump.Snapshot snapshot = pump.snapshot();
        assertEquals(replacementGeneration, snapshot.generation);
        assertTrue(snapshot.active);
        assertTrue(snapshot.inFlight);
        assertEquals(1, dispatcher.inFlight);
    }

    @Test
    public void dispatchRejectionAndCallbackCancellationClearAllState() {
        FakeDispatcher rejected = new FakeDispatcher();
        rejected.acceptDispatch = false;
        AndroidDragGesturePump rejectedPump = new AndroidDragGesturePump(rejected);
        assertTrue(rejectedPump.beginSession(1, 1));
        assertFalse(rejectedPump.move(2, 2));
        assertFalse(rejectedPump.snapshot().active);
        assertEquals(1, rejected.resetCount);

        FakeDispatcher cancelled = new FakeDispatcher();
        AndroidDragGesturePump cancelledPump = new AndroidDragGesturePump(cancelled);
        assertTrue(cancelledPump.beginSession(1, 1));
        assertTrue(cancelledPump.move(2, 2));
        cancelled.cancelNext();
        assertFalse(cancelledPump.snapshot().active);
        assertEquals(1, cancelled.resetCount);
    }

    @Test
    public void oneThousandCompleteClicksRetainNoQueueOrGestureState() {
        FakeDispatcher dispatcher = new FakeDispatcher();
        AndroidDragGesturePump pump = new AndroidDragGesturePump(dispatcher);

        for (int index = 0; index < 1_000; index++) {
            assertTrue(pump.beginSession(index, index));
            assertTrue(pump.end(index, index));
            dispatcher.completeNext();
            AndroidDragGesturePump.Snapshot snapshot = pump.snapshot();
            assertFalse(snapshot.active);
            assertEquals(0, snapshot.retainedPointCount());
            assertEquals(0, dispatcher.inFlight);
            assertEquals(0, dispatcher.callbacks.size());
        }

        assertEquals(1_000, dispatcher.segments.size());
        assertEquals(1, dispatcher.maxInFlight);
    }

    private static final class FakeDispatcher implements AndroidDragGesturePump.Dispatcher {
        final List<AndroidDragGesturePump.Segment> segments = new ArrayList<>();
        final ArrayDeque<AndroidDragGesturePump.ResultCallback> callbacks =
            new ArrayDeque<>();
        boolean acceptDispatch = true;
        int inFlight;
        int maxInFlight;
        int resetCount;

        @Override
        public boolean dispatch(
            AndroidDragGesturePump.Segment segment,
            AndroidDragGesturePump.ResultCallback callback) {
            segments.add(segment);
            if (!acceptDispatch) {
                return false;
            }
            callbacks.addLast(callback);
            inFlight++;
            maxInFlight = Math.max(maxInFlight, inFlight);
            return true;
        }

        @Override
        public void reset(long retiredGeneration) {
            resetCount++;
            inFlight = 0;
            callbacks.clear();
        }

        void completeNext() {
            inFlight--;
            callbacks.removeFirst().onCompleted();
        }

        void cancelNext() {
            inFlight--;
            callbacks.removeFirst().onCancelled();
        }

        AndroidDragGesturePump.ResultCallback removeNextCallback() {
            inFlight--;
            return callbacks.removeFirst();
        }
    }

    private static void waitUntil(Check check, long timeoutMillis) throws Exception {
        long deadline = System.nanoTime() +
            TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
        while (!check.isTrue() && System.nanoTime() < deadline) {
            Thread.yield();
        }
        assertTrue(check.isTrue());
    }

    private interface Check {
        boolean isTrue();
    }
}
