package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidViewerInputQueueTest {
    @Test
    public void mouseMovesUseLatestWinsWhileEdgesStayOrdered() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(8);

        assertTrue(queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_DOWN, 10, 10)));
        assertTrue(queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_MOVE, 20, 20)));
        assertTrue(queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_MOVE, 30, 40)));
        assertTrue(queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_UP, 30, 40)));

        assertEquals(3, queue.size());
        assertCommand(queue.poll(), RemoteDeskProtocol.INPUT_MOUSE_DOWN, 10, 10);
        assertCommand(queue.poll(), RemoteDeskProtocol.INPUT_MOUSE_MOVE, 30, 40);
        assertCommand(queue.poll(), RemoteDeskProtocol.INPUT_MOUSE_UP, 30, 40);
        assertNull(queue.poll());
    }

    @Test
    public void duplicateTailMouseMoveIsNotQueuedTwice() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(4);
        AndroidViewerInputQueue.Command move =
            command(RemoteDeskProtocol.INPUT_MOUSE_MOVE, 20, 30);

        assertTrue(queue.offer(move));
        assertFalse(queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_MOVE, 20, 30)));

        assertEquals(1, queue.size());
    }

    @Test
    public void fullQueueNeverDropsCompletedReliableGestureForNewPress() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(3);
        queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_DOWN, 1, 1));
        queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_UP, 1, 1));
        queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_DOWN, 2, 2));

        assertFalse(queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_DOWN, 3, 3)));

        assertEquals(3, queue.size());
        assertCommand(queue.poll(), RemoteDeskProtocol.INPUT_MOUSE_DOWN, 1, 1);
        assertCommand(queue.poll(), RemoteDeskProtocol.INPUT_MOUSE_UP, 1, 1);
        assertCommand(queue.poll(), RemoteDeskProtocol.INPUT_MOUSE_DOWN, 2, 2);
    }

    @Test
    public void fullReliableQueueStillAcceptsMouseUpFromReleaseReserve() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(1);
        assertTrue(queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_DOWN, 1, 1)));

        assertTrue(queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_UP, 2, 2)));

        assertEquals(2, queue.size());
        assertCommand(queue.poll(), RemoteDeskProtocol.INPUT_MOUSE_DOWN, 1, 1);
        assertCommand(queue.poll(), RemoteDeskProtocol.INPUT_MOUSE_UP, 2, 2);
    }

    @Test
    public void releaseReserveHasHardLimitWithoutEvictingReliableCommands() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(1);
        AndroidViewerInputQueue.Command keyDown =
            new AndroidViewerInputQueue.Command(
                RemoteDeskProtocol.INPUT_KEY_DOWN,
                RemoteDeskProtocol.MOUSE_NONE,
                0,
                0,
                42);
        assertTrue(queue.offer(keyDown));

        for (int index = 0; index < AndroidViewerInputQueue.RELEASE_RESERVE_CAPACITY; index++) {
            assertTrue(queue.offer(new AndroidViewerInputQueue.Command(
                RemoteDeskProtocol.INPUT_KEY_UP,
                RemoteDeskProtocol.MOUSE_NONE,
                0,
                0,
                index)));
        }

        assertEquals(
            1 + AndroidViewerInputQueue.RELEASE_RESERVE_CAPACITY,
            queue.size());
        assertFalse(queue.offer(new AndroidViewerInputQueue.Command(
            RemoteDeskProtocol.INPUT_KEY_UP,
            RemoteDeskProtocol.MOUSE_NONE,
            0,
            0,
            99)));
        assertEquals(
            1 + AndroidViewerInputQueue.RELEASE_RESERVE_CAPACITY,
            queue.size());
        assertEquals(RemoteDeskProtocol.INPUT_KEY_DOWN, queue.poll().kind);
        for (int index = 0; index < AndroidViewerInputQueue.RELEASE_RESERVE_CAPACITY; index++) {
            AndroidViewerInputQueue.Command release = queue.poll();
            assertEquals(RemoteDeskProtocol.INPUT_KEY_UP, release.kind);
            assertEquals(index, release.data);
        }
        assertNull(queue.poll());
    }

    @Test
    public void closeClearsQueueAndRejectsNewInput() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(4);
        queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_DOWN, 1, 1));

        queue.close();

        assertTrue(queue.isClosed());
        assertEquals(0, queue.size());
        assertFalse(queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_UP, 1, 1)));
    }

    @Test
    public void udpActivationCanDiscardOnlyQueuedTcpMoves() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(8);
        queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_DOWN, 10, 10));
        queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_MOVE, 20, 20));
        queue.offer(new AndroidViewerInputQueue.Command(
            RemoteDeskProtocol.INPUT_KEY_DOWN,
            RemoteDeskProtocol.MOUSE_NONE,
            0,
            0,
            65));

        queue.discardPendingMouseMoves();

        assertEquals(2, queue.size());
        assertEquals(RemoteDeskProtocol.INPUT_MOUSE_DOWN, queue.poll().kind);
        assertEquals(RemoteDeskProtocol.INPUT_KEY_DOWN, queue.poll().kind);
    }

    @Test
    public void capabilityRevocationCanDiscardEveryPendingCommand() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(8);
        queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_DOWN, 10, 10));
        queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_MOVE, 20, 20));
        queue.offer(command(RemoteDeskProtocol.INPUT_MOUSE_UP, 20, 20));

        queue.discardAll();

        assertEquals(0, queue.size());
        assertNull(queue.poll());
    }

    @Test
    public void commandCarriesCapabilityGenerationForDynamicFencing() {
        AndroidViewerInputQueue.Command command =
            new AndroidViewerInputQueue.Command(
                RemoteDeskProtocol.INPUT_MOUSE_MOVE,
                RemoteDeskProtocol.MOUSE_NONE,
                1,
                2,
                0,
                7L,
                11L);

        assertEquals(7L, command.mouseRouteGeneration);
        assertEquals(11L, command.inputCapabilityGeneration);
    }

    private static AndroidViewerInputQueue.Command command(int kind, int x, int y) {
        int button = kind == RemoteDeskProtocol.INPUT_MOUSE_MOVE
            ? RemoteDeskProtocol.MOUSE_NONE
            : RemoteDeskProtocol.MOUSE_LEFT;
        return new AndroidViewerInputQueue.Command(kind, button, x, y, 0);
    }

    private static void assertCommand(
        AndroidViewerInputQueue.Command command,
        int kind,
        int x,
        int y) {
        assertEquals(kind, command.kind);
        assertEquals(x, command.x);
        assertEquals(y, command.y);
    }
}
