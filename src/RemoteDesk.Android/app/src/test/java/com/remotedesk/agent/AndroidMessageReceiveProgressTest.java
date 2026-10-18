package com.remotedesk.agent;

import static org.junit.Assert.*;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.security.GeneralSecurityException;
import java.util.Arrays;
import java.util.concurrent.atomic.AtomicLong;
import org.junit.Test;

public final class AndroidMessageReceiveProgressTest {
    private static final long SECOND = 1_000_000_000L;
    private static final long IDLE = AndroidViewerHeartbeat.INBOUND_TIMEOUT_NANOS;

    @Test public void headerAndStalledBodyDoNotKeepAConnectionAlive() {
        AtomicLong clock = new AtomicLong();
        AndroidMessageReceiveProgress progress = new AndroidMessageReceiveProgress(clock::get);
        progress.begin();
        assertFalse(progress.isMakingProgress(clock.get(), IDLE));
        progress.advance();
        clock.set(IDLE - 1);
        assertTrue(progress.isMakingProgress(clock.get(), IDLE));
        clock.incrementAndGet();
        assertFalse(progress.isMakingProgress(clock.get(), IDLE));
    }

    @Test public void continuousTrickleHasAHardDeadlineAndIsolatedConnectionLifetime() {
        AtomicLong clock = new AtomicLong();
        AndroidMessageReceiveProgress old = new AndroidMessageReceiveProgress(clock::get);
        AndroidMessageReceiveProgress current = new AndroidMessageReceiveProgress(clock::get);
        old.begin();
        current.begin();
        for (int i = 0; i < 90; i++) {
            old.advance();
            assertTrue(old.isMakingProgress(clock.get(), IDLE));
            assertFalse(current.isMakingProgress(clock.get(), IDLE));
            clock.addAndGet(SECOND);
        }
        old.advance();
        assertFalse(old.isMakingProgress(clock.get(), IDLE));
        old.end();
        old.begin();
        assertFalse(old.isMakingProgress(clock.get(), IDLE));
        old.advance();
        assertTrue(old.isMakingProgress(clock.get(), IDLE));
        old.end();
        assertFalse(old.isMakingProgress(clock.get(), IDLE));
    }

    @Test public void fragmentedEncryptedMessageKeepsGraceButNeverPublishesUnauthenticatedBytes() throws Exception {
        exerciseFragmentedMessage(false, false);
    }

    @Test public void invalidAuthenticationClearsReceiveGrace() throws Exception {
        exerciseFragmentedMessage(true, false);
    }

    @Test public void truncatedMessageClearsReceiveGrace() throws Exception {
        exerciseFragmentedMessage(false, true);
    }

    private void exerciseFragmentedMessage(boolean corrupt, boolean truncate) throws Exception {
        byte[] key = new byte[32];
        Arrays.fill(key, (byte)7);
        byte[] payload = new byte[64 * 1024];
        Arrays.fill(payload, (byte)91);
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        RemoteDeskTransport.writeMessage(output, RemoteDeskProtocol.MESSAGE_FRAME, payload,
            new RemoteDeskTransport.SecureSession(key, key), new Object());
        byte[] packet = output.toByteArray();
        if (corrupt) packet[packet.length - 1] ^= 1;
        if (truncate) packet = Arrays.copyOf(packet, packet.length - 1);
        AtomicLong clock = new AtomicLong();
        AndroidMessageReceiveProgress progress = new AndroidMessageReceiveProgress(clock::get);
        ByteArrayInputStream bytes = new ByteArrayInputStream(packet);
        AtomicLong calls = new AtomicLong();
        InputStream fragmented = new InputStream() {
            @Override public int read() { return bytes.read(); }
            @Override public int read(byte[] target, int offset, int length) {
                long call = calls.getAndIncrement();
                if (call < 3) assertFalse(progress.isMakingProgress(clock.get(), IDLE));
                else {
                    assertTrue(progress.isMakingProgress(clock.get(), IDLE));
                    // No complete authenticated message has arrived, but actual
                    // body reads continue beyond the ordinary 18-second timeout.
                    clock.addAndGet(SECOND);
                }
                return bytes.read(target, offset, Math.min(call < 2 ? 2 : 2048, length));
            }
        };
        RemoteDeskTransport.SecureSession session = new RemoteDeskTransport.SecureSession(key, key);
        try {
            if (corrupt) assertThrows(GeneralSecurityException.class, () -> RemoteDeskTransport.readMessage(fragmented, session, progress));
            else if (truncate) assertThrows(EOFException.class, () -> RemoteDeskTransport.readMessage(fragmented, session, progress));
            else assertArrayEquals(payload, RemoteDeskTransport.readMessage(fragmented, session, progress).payload);
            assertTrue(clock.get() > IDLE);
        } finally {
            assertFalse(progress.isMakingProgress(clock.get(), IDLE));
        }
    }

    @Test public void invalidLengthCannotStartGrace() {
        AndroidMessageReceiveProgress progress = new AndroidMessageReceiveProgress();
        byte[] header = {(byte)255, (byte)255, (byte)255, (byte)127};
        assertThrows(IOException.class, () -> RemoteDeskTransport.readMessage(new ByteArrayInputStream(header),
            new RemoteDeskTransport.SecureSession(new byte[32], new byte[32]), progress));
        assertFalse(progress.isMakingProgress(System.nanoTime(), IDLE));
    }
}
