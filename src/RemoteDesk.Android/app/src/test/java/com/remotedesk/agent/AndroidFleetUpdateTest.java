package com.remotedesk.agent;

import org.json.JSONObject;
import org.junit.Test;
import java.nio.charset.StandardCharsets;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.Signature;
import java.util.Base64;
import static org.junit.Assert.assertThrows;

public final class AndroidFleetUpdateTest {
    @Test public void pinsPackagePlatformVersionAndDigestBeforeOpeningAnInstallerSession() throws Exception {
        KeyPairGenerator generator = KeyPairGenerator.getInstance("RSA");
        generator.initialize(2048);
        KeyPair publisher = generator.generateKeyPair();
        String hash = "a".repeat(64);
        Signature signature = Signature.getInstance("SHA256withRSA");
        signature.initSign(publisher.getPrivate());
        signature.update(("RemoteDesk/fleet/v1\n1.0.56\n20261120000000\nandroid\n5\n" + hash)
            .getBytes(StandardCharsets.US_ASCII));
        JSONObject ticket = new JSONObject().put("platform", "android").put("version", "1.0.56").put("size", 5)
            .put("sha256", hash).put("build", "20261120000000").put("signature", Base64.getEncoder().encodeToString(signature.sign()));
        AndroidFleetUpdate.verify(ticket, "1.0.55", publisher.getPublic());
        assertThrows(SecurityException.class, () -> AndroidFleetUpdate.verify(ticket, "1.0.56", publisher.getPublic()));
        assertThrows(SecurityException.class, () -> AndroidFleetUpdate.verify(ticket, "2.0.0", publisher.getPublic()));
        ticket.put("size", 6);
        assertThrows(SecurityException.class, () -> AndroidFleetUpdate.verify(ticket, "1.0.55", publisher.getPublic()));
        ticket.put("platform", "win-x64");
        assertThrows(SecurityException.class, () -> AndroidFleetUpdate.verify(ticket, "1.0.55", publisher.getPublic()));
    }
}
