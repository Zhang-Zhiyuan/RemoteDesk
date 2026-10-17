package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidSshLoginScopeTest {
    private static final String ID = "11111111-1111-4111-8111-111111111111";
    private static final String TOKEN = "owned-test-relay-token-never-a-password";
    private static final String PIN = "AB".repeat(32);
    private AndroidRelay.Options options(String server, int port, String token, String pin, boolean publish, int adminPort) {
        return new AndroidRelay.Options(server, port, token, pin, ID, publish, adminPort, "root", "");
    }
    @Test public void publicationAndAdminSettingsDoNotInterruptOutboundSsh() {
        AndroidRelay.Options original = options("relay.test", 56567, TOKEN, PIN, true, 22);
        assertTrue(AndroidRelaySettings.sameSshLogin(original, options("RELAY.TEST", 56567, TOKEN, PIN, false, 2222)));
    }
    @Test public void LogoutOrChangedRelayLoginEndsExistingSessions() {
        AndroidRelay.Options original = options("relay.test", 56567, TOKEN, PIN, true, 22);
        assertFalse(AndroidRelaySettings.sameSshLogin(original, null));
        assertFalse(AndroidRelaySettings.sameSshLogin(null, original));
        assertFalse(AndroidRelaySettings.sameSshLogin(original, options("another.test", 56567, TOKEN, PIN, true, 22)));
        assertFalse(AndroidRelaySettings.sameSshLogin(original, options("relay.test", 443, TOKEN, PIN, true, 22)));
        assertFalse(AndroidRelaySettings.sameSshLogin(original, options("relay.test", 56567, TOKEN + "renewed", PIN, true, 22)));
        assertFalse(AndroidRelaySettings.sameSshLogin(original, options("relay.test", 56567, TOKEN, "CD".repeat(32), true, 22)));
    }
}
