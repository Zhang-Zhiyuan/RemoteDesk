package com.remotedesk.agent;

import org.junit.BeforeClass;
import org.junit.ClassRule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import java.io.File;
import java.io.FileInputStream;
import java.net.InetAddress;
import java.net.Socket;
import java.security.KeyStore;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicReference;
import javax.net.ssl.*;
import static org.junit.Assert.*;

/** Real JVM TLS sockets: no Android emulator, public host, or production credential. */
public final class AndroidRelayEnrollmentTlsTest {
    @ClassRule public static TemporaryFolder temporary = new TemporaryFolder();
    private static SSLContext serverContext;
    private static String expectedPin;

    @BeforeClass public static void certificate() throws Exception {
        File directory = temporary.newFolder("relay-test-identity");
        File store = new File(directory, "test-only.p12");
        String password = "fixture-only-password";
        String executable = System.getProperty("os.name").toLowerCase().contains("win") ? "keytool.exe" : "keytool";
        Process process = new ProcessBuilder(new File(System.getProperty("java.home"), "bin/" + executable).getPath(),
            "-genkeypair", "-alias", "relay", "-keyalg", "EC", "-groupname", "secp256r1",
            "-dname", "CN=RemoteDesk enrollment test", "-validity", "2", "-storetype", "PKCS12",
            "-keystore", store.getPath(), "-storepass", password, "-keypass", password, "-noprompt")
            .redirectErrorStream(true).redirectOutput(new File(directory, "keytool.log")).start();
        if (!process.waitFor(20, TimeUnit.SECONDS)) { process.destroyForcibly(); throw new AssertionError("Test certificate creation timed out"); }
        assertEquals("Could not create isolated test certificate", 0, process.exitValue());
        KeyStore keys = KeyStore.getInstance("PKCS12");
        try (FileInputStream input = new FileInputStream(store)) { keys.load(input, password.toCharArray()); }
        expectedPin = AndroidRelayEnrollment.fingerprint(keys.getCertificate("relay").getEncoded());
        KeyManagerFactory factory = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        factory.init(keys, password.toCharArray());
        serverContext = SSLContext.getInstance("TLS");
        serverContext.init(factory.getKeyManagers(), null, null);
    }

    private static final class Peer implements AutoCloseable {
        final SSLServerSocket server;
        final ExecutorService worker = Executors.newSingleThreadExecutor(r -> { Thread t = new Thread(r); t.setDaemon(true); return t; });
        final AtomicReference<Socket> accepted = new AtomicReference<>();
        final Future<Integer> received;
        Peer() throws Exception {
            server = (SSLServerSocket) serverContext.getServerSocketFactory().createServerSocket(0, 1, InetAddress.getByName("127.0.0.1"));
            server.setSoTimeout(5000);
            received = worker.submit(() -> {
                try (SSLSocket socket = (SSLSocket) server.accept()) {
                    accepted.set(socket);
                    socket.setSoTimeout(3000);
                    socket.startHandshake();
                    return socket.getInputStream().read();
                } catch (SSLException | java.net.SocketException rejected) { return -1; }
            });
        }
        int port() { return server.getLocalPort(); }
        public void close() throws Exception {
            server.close();
            Socket socket = accepted.get(); if (socket != null) socket.close();
            received.cancel(true); worker.shutdownNow();
        }
    }

    @Test public void discoveryClosesWithoutWritingAnyApplicationBytes() throws Exception {
        try (Peer peer = new Peer()) {
            AtomicReference<Socket> pending = new AtomicReference<>();
            assertEquals(expectedPin, AndroidRelayEnrollment.discover("127.0.0.1", peer.port(), pending::set));
            assertNull(pending.get());
            assertEquals(Integer.valueOf(-1), peer.received.get(5, TimeUnit.SECONDS));
        }
    }

    @Test public void acceptedIdentityReconnectsUsingStrictPinning() throws Exception {
        try (Peer peer = new Peer()) {
            AndroidRelay.Options options = new AndroidRelay.Options("127.0.0.1", peer.port(),
                "test-only-access-token-not-production", expectedPin,
                "6e5af07d-63ee-4f04-8d75-3c78be4a8ac9", false);
            try (SSLSocket socket = AndroidRelay.newSocket(options)) {
                AndroidRelay.connect(socket, options);
            }
            assertEquals(Integer.valueOf(-1), peer.received.get(5, TimeUnit.SECONDS));
        }
    }

    @Test public void changedIdentityIsRejectedBeforeApplicationData() throws Exception {
        try (Peer peer = new Peer()) {
            AndroidRelay.Options options = new AndroidRelay.Options("127.0.0.1", peer.port(),
                "test-only-access-token-not-production", "00".repeat(32),
                "6e5af07d-63ee-4f04-8d75-3c78be4a8ac9", false);
            try (SSLSocket socket = AndroidRelay.newSocket(options)) {
                assertThrows(AndroidRelay.IdentityFailure.class, () -> AndroidRelay.connect(socket, options));
            }
            assertEquals(Integer.valueOf(-1), peer.received.get(5, TimeUnit.SECONDS));
        }
    }

    @Test public void canceledDiscoveryCannotContinueConnecting() {
        assertThrows(java.io.IOException.class, () -> AndroidRelayEnrollment.discover("127.0.0.1", 56567,
            socket -> { if (socket != null) AndroidRelay.close(socket); }));
    }
}
