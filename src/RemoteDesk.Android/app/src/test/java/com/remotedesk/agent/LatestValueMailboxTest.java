package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;

import java.util.ArrayList;
import java.util.List;

import org.junit.Test;

public final class LatestValueMailboxTest {
    @Test
    public void newerValueReplacesAndReleasesUndisplayedValue() {
        List<String> released = new ArrayList<>();
        LatestValueMailbox<String> mailbox = new LatestValueMailbox<>(released::add);

        assertTrue(mailbox.offer("frame-1"));
        assertFalse(mailbox.offer("frame-2"));

        assertEquals(List.of("frame-1"), released);
        assertEquals(1, mailbox.pendingCount());
        assertTrue(mailbox.isDispatchScheduled());
        assertEquals("frame-2", mailbox.pollLatestForDispatch());
        assertEquals(0, mailbox.pendingCount());
        assertFalse(mailbox.isDispatchScheduled());
    }

    @Test
    public void pollingAllowsExactlyOneNewUiDispatch() {
        LatestValueMailbox<String> mailbox = new LatestValueMailbox<>(ignored -> {
        });

        assertTrue(mailbox.offer("frame-1"));
        assertEquals("frame-1", mailbox.pollLatestForDispatch());
        assertTrue(mailbox.offer("frame-2"));
        assertFalse(mailbox.offer("frame-3"));
        assertEquals("frame-3", mailbox.pollLatestForDispatch());
    }

    @Test
    public void closeReleasesPendingAndFutureValues() {
        List<String> released = new ArrayList<>();
        LatestValueMailbox<String> mailbox = new LatestValueMailbox<>(released::add);
        mailbox.offer("frame-1");

        mailbox.close();

        assertEquals(List.of("frame-1"), released);
        assertFalse(mailbox.offer("frame-2"));
        assertEquals(List.of("frame-1", "frame-2"), released);
        assertNull(mailbox.pollLatestForDispatch());
    }
}
