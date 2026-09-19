package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidFileTransferLivenessTest {
    private static final long SECOND = 1_000_000_000L;

    @Test public void normalViewingPickerAndConfirmationKeepEighteenSecondTimeout() {
        var tracker = new AndroidFileTransferLiveness();
        assertFalse(tracker.hasInboundTimedOut(18 * SECOND - 1, 0));
        assertTrue(tracker.hasInboundTimedOut(18 * SECOND, 0));
    }

    @Test public void uploadAndReceiptWaitHaveBoundedSilenceAllowance() {
        var tracker = new AndroidFileTransferLiveness();
        try (var lease = tracker.begin()) {
            assertFalse(tracker.hasInboundTimedOut(25 * SECOND, 0));
            assertFalse(tracker.hasInboundTimedOut(150 * SECOND - 1, 0));
            assertTrue(tracker.hasInboundTimedOut(150 * SECOND, 0));
            assertFalse(tracker.hasInboundTimedOut(150 * SECOND, 140 * SECOND));
            assertTrue(tracker.hasInboundTimedOut(290 * SECOND, 140 * SECOND));
        }
        assertTrue(tracker.hasInboundTimedOut(25 * SECOND, 0));
    }

    @Test public void failedOrCancelledSenderRestoresNormalDeadline() {
        var tracker = new AndroidFileTransferLiveness();
        assertThrows(java.io.IOException.class, () -> {
            try (var lease = tracker.begin()) { throw new java.io.IOException("cancelled"); }
        });
        assertTrue(tracker.hasInboundTimedOut(18 * SECOND, 0));
    }

    @Test public void oldFinallyCannotClearANewUploadOrAnotherConnection() {
        var firstOwner = new AndroidFileTransferLiveness();
        var old = firstOwner.begin();
        old.close();
        try (var current = firstOwner.begin()) {
            var secondOwner = new AndroidFileTransferLiveness();
            try (var other = secondOwner.begin()) {
                old.close();
                assertFalse(firstOwner.hasInboundTimedOut(25 * SECOND, 0));
                assertFalse(secondOwner.hasInboundTimedOut(25 * SECOND, 0));
            }
            assertTrue(secondOwner.hasInboundTimedOut(25 * SECOND, 0));
            assertFalse(firstOwner.hasInboundTimedOut(25 * SECOND, 0));
        }
    }

    @Test public void overlappingUploadsCannotContinuouslyRenewAllowance() {
        var tracker = new AndroidFileTransferLiveness();
        try (var lease = tracker.begin()) {
            assertThrows(IllegalStateException.class, tracker::begin);
            assertTrue(tracker.hasInboundTimedOut(150 * SECOND, 0));
            assertFalse(tracker.hasInboundTimedOut(0, SECOND));
        }
    }

    @Test public void oldPeerWithoutReceiptKeepsBoundedTailDrainAfterLocalCompletion() {
        var tracker = new AndroidFileTransferLiveness();
        try (var lease = tracker.begin()) { lease.completedWithoutReceipt(25 * SECOND); }
        assertFalse(tracker.hasInboundTimedOut(26 * SECOND, 0));
        assertFalse(tracker.hasInboundTimedOut(149 * SECOND, 0));
        assertTrue(tracker.hasInboundTimedOut(150 * SECOND, 0));
        // An early Pong is not a file receipt and cannot prematurely drop the allowance.
        assertFalse(tracker.hasInboundTimedOut(90 * SECOND, 30 * SECOND));
        assertFalse(tracker.hasInboundTimedOut(174 * SECOND, 30 * SECOND));
        // Even incoming messages cannot extend the tail lease beyond 150 seconds.
        assertTrue(tracker.hasInboundTimedOut(175 * SECOND, 150 * SECOND));
    }

    @Test public void legacyDrainIsNotInheritedByNewConnectionAndStaleLeaseCannotRenewIt() {
        var tracker = new AndroidFileTransferLiveness();
        var old = tracker.begin();
        old.completedWithoutReceipt(25 * SECOND); old.close();
        assertTrue(new AndroidFileTransferLiveness().hasInboundTimedOut(20 * SECOND, 0));
        old.completedWithoutReceipt(140 * SECOND);
        assertTrue(tracker.hasInboundTimedOut(176 * SECOND, 150 * SECOND));
        try (var next = tracker.begin()) { /* Failed next sender: no legacy completion. */ }
        old.close();
        assertTrue(tracker.hasInboundTimedOut(50 * SECOND, 30 * SECOND));
    }
}
