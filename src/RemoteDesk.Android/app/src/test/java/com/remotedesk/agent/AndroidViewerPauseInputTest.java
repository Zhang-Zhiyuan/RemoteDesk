package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.util.List;

public final class AndroidViewerPauseInputTest {
    @Test public void localPauseWinsOverCapabilitiesAndReconnectAvailability() {
        for (boolean info : new boolean[] {false, true}) {
            assertFalse(AndroidViewerInputCapabilityPolicy.canSendInput(info, RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL, true));
            assertFalse(AndroidViewerInputCapabilityPolicy.canSendInput(info, 0, true));
        }
        assertTrue(AndroidViewerInputCapabilityPolicy.canSendInput(true, RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL, false));
        assertFalse(AndroidViewerInputCapabilityPolicy.isCurrentCommand(true, 4, 6));
    }

    @Test public void pauseDiscardsQueuedTextAndReleasesOnlyActuallySentKeysInReverseOrder() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(16);
        AndroidViewerPressedInput pressed = new AndroidViewerPressedInput();
        queue.offerKeyboardBatch(AndroidViewerKeyboard.shortcut(0, 1, 0x11, 0x56));
        pressed.sent(queue.poll()); // Ctrl is on the wire; V and both key-ups are still queued.
        queue.releaseAll(40, 50);
        AndroidViewerInputQueue.Command barrier = queue.poll();
        assertTrue(barrier.releaseAll);
        assertNull(queue.poll());
        List<AndroidViewerInputQueue.Command> releases = pressed.releases(barrier.x, barrier.y);
        assertEquals(1, releases.size());
        assertEquals(RemoteDeskProtocol.INPUT_KEY_UP, releases.get(0).kind);
        assertEquals(0x11, releases.get(0).data);
        pressed.sent(releases.get(0));
        assertTrue(pressed.releases(0, 0).isEmpty());
    }

    @Test public void pendingReleaseSurvivesFastResumeAndCapabilityRefreshBeforeNewInput() {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(8);
        queue.offerKeyboardBatch(AndroidViewerKeyboard.text("旧", 0, 1));
        queue.releaseAll(4, 5);
        queue.discardAll();
        queue.discardAll();
        assertTrue(queue.offerKeyboardBatch(AndroidViewerKeyboard.text("新", 0, 3)));
        assertTrue(queue.poll().releaseAll);
        assertEquals('新', queue.poll().data);
        assertNull(queue.poll());
    }

    @Test public void inFlightPressCompletingAfterPauseIsIncludedInReleaseBarrier() throws Exception {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(8);
        AndroidViewerPressedInput pressed = new AndroidViewerPressedInput();
        queue.offer(new AndroidViewerInputQueue.Command(RemoteDeskProtocol.INPUT_MOUSE_DOWN, RemoteDeskProtocol.MOUSE_LEFT, 1, 2, 0));
        AndroidViewerInputQueue.Command inFlight = queue.take();
        queue.releaseAll(80, 90);
        pressed.sent(inFlight);
        queue.completeSend();
        AndroidViewerInputQueue.Command barrier = queue.take();
        List<AndroidViewerInputQueue.Command> releases = pressed.releases(barrier.x, barrier.y);
        assertEquals(1, releases.size());
        assertEquals(RemoteDeskProtocol.INPUT_MOUSE_UP, releases.get(0).kind);
        assertEquals(80, releases.get(0).x); assertEquals(90, releases.get(0).y);
        queue.completeSend();
        assertTrue(queue.awaitIdle(10));
    }

    @Test public void modifiersReleaseLastAndAlreadyReleasedKeysAreNotRepeated() {
        AndroidViewerPressedInput pressed = new AndroidViewerPressedInput();
        List<AndroidViewerInputQueue.Command> chord = AndroidViewerKeyboard.shortcut(0, 0, 0x11, 0x41);
        pressed.sent(chord.get(0)); pressed.sent(chord.get(1));
        List<AndroidViewerInputQueue.Command> releases = pressed.releases(0, 0);
        assertEquals(0x41, releases.get(0).data); assertEquals(0x11, releases.get(1).data);
        for (AndroidViewerInputQueue.Command command : releases) pressed.sent(command);
        assertTrue(pressed.releases(0, 0).isEmpty());
    }
}
