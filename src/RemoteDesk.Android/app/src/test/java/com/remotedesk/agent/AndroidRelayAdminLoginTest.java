package com.remotedesk.agent;

import java.nio.charset.StandardCharsets;
import java.util.Base64;
import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidRelayAdminLoginTest {
    private static final String DEVICE = "e864106e-a411-4707-9065-746c85eb7201";
    private static final String TOKEN = "synthetic-only-relay-access-token-1234567890";
    private static final String PIN = "AB".repeat(32);
    private static final String SSH = "SHA256:" + Base64.getEncoder().withoutPadding().encodeToString(new byte[32]);

    private static JSONObject response() throws Exception {
        return new JSONObject().put("version", 1).put("port", 56567).put("accessToken", TOKEN).put("tlsCertificateSha256", PIN);
    }

    private static AndroidRelay.Options parse(String value) throws Exception {
        return AndroidRelayAdminLogin.parseResponse(value, "relay.test", 2222, "root", SSH, DEVICE, false);
    }

    @Test public void sshLoginObtainsInternalKeyAndPortWithoutChangingDeviceIdentity() throws Exception {
        AndroidRelay.Options options = parse(response().toString());
        assertEquals(TOKEN, options.accessToken);
        assertEquals(56567, options.port);
        assertEquals(2222, options.sshPort);
        assertEquals(SSH, options.sshHostKeySha256);
        assertEquals(DEVICE, options.deviceId);
        assertFalse(options.publish);
        assertEquals("root", options.adminUsername);
        assertFalse(options.toString().contains(TOKEN));
    }

    @Test public void oldEncryptedConfigurationStillLoadsWithoutAdministratorPassword() throws Exception {
        JSONObject old = response().put("serverAddress", "relay.test").put("deviceId", DEVICE);
        AndroidRelay.Options options = AndroidRelay.Options.parse(old.toString());
        assertEquals(22, options.sshPort);
        assertEquals("root", options.adminUsername);
        assertEquals("", options.sshHostKeySha256);
        assertEquals(TOKEN, options.accessToken);
        assertFalse(options.json().toString().toLowerCase().contains("password"));
    }

    @Test public void metadataRoundTripAndTargetSelectionPreserveOnlyNonPasswordSshSettings() throws Exception {
        AndroidRelay.Options options = parse(response().toString());
        AndroidRelay.Options loaded = AndroidRelay.Options.parse(options.json().toString()).target("f9fe781c-c565-46f3-8a4c-1ff880e2bf18");
        assertEquals(2222, loaded.sshPort);
        assertEquals(SSH, loaded.sshHostKeySha256);
        assertEquals(TOKEN, loaded.accessToken);
        for (java.lang.reflect.Field field : AndroidRelay.Options.class.getDeclaredFields())
            assertFalse(field.getName().toLowerCase().contains("password"));
    }

    @Test public void savedSshIdentityIsScopedToHostAndSshPort() throws Exception {
        AndroidRelay.Options options = parse(response().toString());
        assertEquals(SSH, AndroidRelayAdminLogin.knownIdentity(options, "RELAY.test", 2222));
        assertEquals("", AndroidRelayAdminLogin.knownIdentity(options, "other.test", 2222));
        assertEquals("", AndroidRelayAdminLogin.knownIdentity(options, "relay.test", 22));
    }

    @Test public void fingerprintMatchesOpenSshFormat() throws Exception {
        String fingerprint = AndroidRelayAdminLogin.fingerprint("abc".getBytes(StandardCharsets.UTF_8));
        assertEquals("SHA256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0", fingerprint);
        assertEquals(fingerprint, AndroidRelayAdminLogin.normalizeIdentity(fingerprint + "="));
        assertThrows(IllegalArgumentException.class, () -> AndroidRelayAdminLogin.normalizeIdentity("incorrect"));
    }

    @Test public void errorsIdentifyServerLoginWithoutLeakingReturnedSecrets() {
        for (String code : new String[] {"not_configured", "permission_denied", "untrusted-server-error"}) {
            AndroidRelayAdminLogin.LoginFailure error = assertThrows(AndroidRelayAdminLogin.LoginFailure.class,
                () -> parse("{\"errorCode\":\"" + code + "\",\"password\":\"never-display-this\"}"));
            assertFalse(error.getMessage().contains("never-display-this"));
            assertFalse(error.getMessage().contains("untrusted-server-error"));
            if (code.equals("not_configured")) assertTrue(error.getMessage().contains("部署 / 更新服务器"));
        }
    }

    @Test public void invalidResponsesCannotBecomeStoredConfiguration() throws Exception {
        for (String value : new String[] {"null", "[]", "{}", "untrusted".repeat(9000),
            response().put("version", 2).toString(), response().put("port", "56567").toString(),
            response().put("port", 0).toString(), response().put("accessToken", "device-key").toString(),
            response().put("tlsCertificateSha256", "incorrect").toString()}) {
            assertThrows(AndroidRelayAdminLogin.LoginFailure.class, () -> parse(value));
        }
    }

    @Test public void commandContainsOnlyBundledReaderNotAnyPassword() {
        byte[] source = "print('owned')".getBytes(StandardCharsets.UTF_8);
        assertTrue(AndroidRelayAdminLogin.command(source, "root").startsWith("python3 -c "));
        assertTrue(AndroidRelayAdminLogin.command(source, "admin").startsWith("sudo -k -S -p '' -- python3 -c "));
        assertFalse(AndroidRelayAdminLogin.command(source, "root").contains("accessToken"));
        assertFalse(AndroidRelayAdminLogin.command(source, "root").contains("systemctl"));
    }

    @Test public void cancelledLoginDoesNotReadAssetsOrConnectAndErasesPasswordBytes() {
        AndroidRelayAdminLogin.Operation operation = new AndroidRelayAdminLogin.Operation();
        byte[] password = "owned-test-root-password".getBytes(StandardCharsets.UTF_8);
        operation.close();
        assertThrows(AndroidRelayAdminLogin.LoginFailure.class,
            () -> operation.login(null, "relay.test", 22, "root", password, "", DEVICE, true));
        assertArrayEquals(new byte[password.length], password);
        assertTrue(operation.isClosed());
    }
}
