package com.remotedesk.agent;

import org.junit.Test;
import java.nio.charset.StandardCharsets;
import static org.junit.Assert.*;

public final class AndroidRelayEnrollmentTest {
    private static final String TOKEN = "test-only-access-token-not-production";
    private static final String ID = "6e5af07d-63ee-4f04-8d75-3c78be4a8ac9";
    private static final String PIN = "A1".repeat(32);

    private static AndroidRelayEnrollment.Draft draft(String server, int port) {
        return new AndroidRelayEnrollment.Draft(server, port, TOKEN, ID, true);
    }

    @Test public void firstSetupDoesNotRequireManualCertificate() {
        AndroidRelayEnrollment.Draft value = draft(" example.test ", 56567);
        assertEquals("example.test", value.server);
        assertEquals("", AndroidRelayEnrollment.knownPin(null, value, ""));
        assertFalse(value.toString().contains(TOKEN));
        assertThrows(IllegalArgumentException.class, () -> value.options(""));
    }

    @Test public void savedIdentityIsReusedWithoutTypingOrReenrollment() {
        AndroidRelay.Options saved = draft("example.test", 56567).options(PIN);
        assertEquals(PIN, AndroidRelayEnrollment.knownPin(saved, draft("EXAMPLE.test", 56567), ""));
        assertFalse(AndroidRelayEnrollment.replacesIdentity(saved, draft("example.test", 56567), PIN));
    }

    @Test public void anotherEndpointNeverInheritsTheOldIdentity() {
        AndroidRelay.Options saved = draft("example.test", 56567).options(PIN);
        assertEquals("", AndroidRelayEnrollment.knownPin(saved, draft("other.test", 56567), ""));
        assertEquals("", AndroidRelayEnrollment.knownPin(saved, draft("example.test", 56568), ""));
    }

    @Test public void changingAccessKeyOrPublishFlagDoesNotResetIdentity() {
        AndroidRelay.Options saved = draft("example.test", 56567).options(PIN);
        AndroidRelayEnrollment.Draft changed = new AndroidRelayEnrollment.Draft(
            "example.test", 56567, TOKEN + "-new", ID, false);
        assertEquals(PIN, AndroidRelayEnrollment.knownPin(saved, changed, ""));
        assertFalse(changed.options(PIN).publish);
    }

    @Test public void manualIdentityIsValidatedAndReplacementRequiresConfirmation() {
        AndroidRelayEnrollment.Draft draft = draft("example.test", 56567);
        AndroidRelay.Options saved = draft.options(PIN);
        String changed = AndroidRelayEnrollment.knownPin(saved, draft, "b2:".repeat(32));
        assertEquals("B2".repeat(32), changed);
        assertTrue(AndroidRelayEnrollment.replacesIdentity(saved, draft, changed));
        assertThrows(IllegalArgumentException.class, () -> AndroidRelayEnrollment.knownPin(saved, draft, "wrong"));
    }

    @Test public void whitespaceOnlyManualValueKeepsSavedIdentity() {
        AndroidRelayEnrollment.Draft draft = draft("example.test", 56567);
        assertEquals(PIN, AndroidRelayEnrollment.knownPin(draft.options(PIN), draft, " \t\n"));
    }

    @Test public void invalidSettingsFailBeforeNetworkDiscovery() {
        assertThrows(IllegalArgumentException.class, () -> draft("https://example.test", 56567));
        assertThrows(IllegalArgumentException.class, () -> draft("", 56567));
        assertThrows(IllegalArgumentException.class, () -> draft("example.test", 65536));
        assertThrows(IllegalArgumentException.class, () -> new AndroidRelayEnrollment.Draft("host", 56567, "short", ID, true));
        assertThrows(IllegalArgumentException.class, () -> new AndroidRelayEnrollment.Draft("host", 56567, TOKEN, "invalid", true));
    }

    @Test public void discoveredIdentityUsesTheExistingExactPinFormat() throws Exception {
        byte[] encoded = "abc".getBytes(StandardCharsets.UTF_8);
        String pin = AndroidRelayEnrollment.fingerprint(encoded);
        assertEquals(64, pin.length());
        assertTrue(AndroidRelay.certificateMatches(encoded, pin));
        assertFalse(AndroidRelay.certificateMatches("abd".getBytes(StandardCharsets.UTF_8), pin));
        assertThrows(java.security.cert.CertificateException.class, () -> AndroidRelayEnrollment.fingerprint(null));
        assertThrows(java.security.cert.CertificateException.class, () -> AndroidRelayEnrollment.fingerprint(new byte[0]));
    }
}
