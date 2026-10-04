package com.remotedesk.agent;

import java.net.Socket;
import java.nio.charset.StandardCharsets;
import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidRelayPendingSocketTest {
    @Test public void lateDirectoryCleanupCannotOrphanQueuedAdministratorLogin() {
        AndroidRelayPendingSocket directory = new AndroidRelayPendingSocket();
        AndroidRelayPendingSocket login = new AndroidRelayPendingSocket();
        Socket directorySocket = new Socket();
        AndroidRelayAdminLogin.Operation operation = new AndroidRelayAdminLogin.Operation();
        directory.accept(directorySocket);
        directory.close();
        login.accept(operation);

        // listDevices invokes socketChanged(null) from its finally block,
        // potentially after the UI has queued a new administrator login.
        directory.accept(null);
        assertFalse(operation.isClosed());
        login.close();
        assertTrue(directorySocket.isClosed());
        assertTrue(operation.isClosed());

        byte[] password = "public-cancel-fixture".getBytes(StandardCharsets.UTF_8);
        assertThrows(AndroidRelayAdminLogin.LoginFailure.class, () -> operation.login(
            null, "never-connect.invalid", 22, "root", password, "",
            "11111111-1111-4111-8111-111111111111", true));
        assertArrayEquals(new byte[password.length], password);
    }

    @Test public void socketRegisteredAfterCancellationIsImmediatelyClosed() {
        AndroidRelayPendingSocket operation = new AndroidRelayPendingSocket();
        operation.close();
        Socket late = new Socket();
        operation.accept(late);
        operation.accept(null);
        assertTrue(late.isClosed());
    }

    @Test public void lateNonNullCallbackCannotReplaceOrCloseCurrentOperation() {
        AndroidRelayPendingSocket previous = new AndroidRelayPendingSocket();
        AndroidRelayPendingSocket current = new AndroidRelayPendingSocket();
        Socket currentSocket = new Socket(), lateSocket = new Socket();
        previous.close();
        current.accept(currentSocket);
        previous.accept(lateSocket);
        assertTrue(lateSocket.isClosed());
        assertFalse(currentSocket.isClosed());
        current.close();
        assertTrue(currentSocket.isClosed());
    }

    @Test public void replacingSocketDisposesOnlyThePreviousOwnerAndCloseIsIdempotent() {
        AndroidRelayPendingSocket operation = new AndroidRelayPendingSocket();
        Socket first = new Socket(), second = new Socket();
        operation.accept(first);
        operation.accept(first);
        assertFalse(first.isClosed());
        operation.accept(second);
        assertTrue(first.isClosed());
        assertFalse(second.isClosed());
        operation.accept(null);
        assertTrue(second.isClosed());
        operation.close();
        operation.close();
    }
}
