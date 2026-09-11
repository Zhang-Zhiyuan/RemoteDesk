package com.remotedesk.agent;

import org.junit.Test;
import java.net.Socket;
import java.io.IOException;
import java.util.List;
import static org.junit.Assert.*;

public final class AndroidRelayDirectoryLifetimeTest {
    @Test public void successfulDialDoesNotCancelTheDirectoryOrLaterPages() throws Exception {
        try (AndroidRelayNetworkSelector.Dial directory = new AndroidRelayNetworkSelector.Dial()) {
            for (int pageNumber = 0; pageNumber < 2; pageNumber++) {
                Socket socket = AndroidRelay.connectDirectoryPage(directory, page ->
                    new AndroidRelayNetworkSelector.Selector(System::nanoTime).connect("directory", List.of(), page,
                        (path, owner) -> { Socket value = new Socket(); owner.add(value); return value; }));
                assertFalse(directory.isClosed());
                assertFalse(socket.isClosed());
                directory.release(socket);
                assertTrue(socket.isClosed());
            }
        }
    }

    @Test public void cancelBetweenHandshakeAndPromotionClosesLateWinner() throws Exception {
        Socket winner = new Socket();
        try (AndroidRelayNetworkSelector.Dial directory = new AndroidRelayNetworkSelector.Dial()) {
            assertThrows(IOException.class, () -> AndroidRelay.connectDirectoryPage(directory, page -> {
                page.close(); directory.close(); return winner;
            }));
            assertTrue(winner.isClosed());
        }
    }

    @Test public void directoryCancellationStillClosesTheResponseSocket() throws Exception {
        try (AndroidRelayNetworkSelector.Dial directory = new AndroidRelayNetworkSelector.Dial()) {
            Socket socket = AndroidRelay.connectDirectoryPage(directory, page -> { page.close(); return new Socket(); });
            directory.close();
            assertTrue(socket.isClosed());
        }
    }

    @Test public void failedPageClosesItsCandidatesButPreservesOuterDeadline() throws Exception {
        Socket pending = new Socket();
        try (AndroidRelayNetworkSelector.Dial directory = new AndroidRelayNetworkSelector.Dial()) {
            assertThrows(IOException.class, () -> AndroidRelay.connectDirectoryPage(directory, page -> {
                page.add(pending); throw new IOException("fixture failure");
            }));
            assertTrue(pending.isClosed());
            assertFalse(directory.isClosed());
        }
    }
}
