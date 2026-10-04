package com.remotedesk.agent;

import com.jcraft.jsch.JSch;
import com.jcraft.jsch.Session;
import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidRelaySshAlgorithmPolicyTest {
    @Test public void defaultModernOrderIsPreservedWithoutNewEdDsaFamilies() {
        assertEquals("ecdsa-sha2-nistp256-cert-v01@openssh.com,ecdsa-sha2-nistp384-cert-v01@openssh.com," +
            "ecdsa-sha2-nistp521-cert-v01@openssh.com,rsa-sha2-512-cert-v01@openssh.com,rsa-sha2-256-cert-v01@openssh.com," +
            "ecdsa-sha2-nistp256,ecdsa-sha2-nistp384,ecdsa-sha2-nistp521,rsa-sha2-512,rsa-sha2-256",
            AndroidRelaySshAlgorithmPolicy.administratorHostKeyAlgorithms(JSch.getConfig("server_host_key")));
    }

    @Test public void edDsaCertificatesAndSha1CannotLeakIntoAdministratorProposal() {
        assertEquals("rsa-sha2-256,ecdsa-sha2-nistp521,rsa-sha2-512-cert-v01@openssh.com",
            AndroidRelaySshAlgorithmPolicy.administratorHostKeyAlgorithms(
                "ssh-ed25519,ssh-ed448,ssh-ed25519-cert-v01@openssh.com,ssh-ed448-cert-v01@openssh.com," +
                "ssh-rsa,ssh-rsa-cert-v01@openssh.com,ssh-dss,ssh-dss-cert-v01@openssh.com," +
                "rsa-sha2-256,ecdsa-sha2-nistp521,rsa-sha2-512-cert-v01@openssh.com"));
    }

    @Test public void firstUseAndPinnedLoginSharePolicyWithoutChangingGlobalOrTerminalConfiguration() throws Exception {
        String defaults = JSch.getConfig("server_host_key");
        JSch jsch = new JSch();
        Session firstUse = jsch.getSession("root", "owned.invalid", 22);
        Session pinnedLogin = jsch.getSession("admin", "owned.invalid", 2222);
        Session terminal = jsch.getSession("user", "owned.invalid", 22);
        firstUse.setConfig("StrictHostKeyChecking", "yes");
        pinnedLogin.setConfig("StrictHostKeyChecking", "yes");
        AndroidRelaySshAlgorithmPolicy.configureAdministratorSession(firstUse);
        AndroidRelaySshAlgorithmPolicy.configureAdministratorSession(pinnedLogin);
        assertEquals(firstUse.getConfig("server_host_key"), pinnedLogin.getConfig("server_host_key"));
        assertEquals(defaults, JSch.getConfig("server_host_key"));
        assertEquals(defaults, terminal.getConfig("server_host_key"));
        assertEquals("yes", firstUse.getConfig("StrictHostKeyChecking"));
        assertEquals("yes", pinnedLogin.getConfig("StrictHostKeyChecking"));
    }

    @Test public void policyNeverAddsAlgorithmsThatWereAbsentAndIsIdempotent() {
        String onlyRsa = "rsa-sha2-256,rsa-sha2-512";
        assertEquals(onlyRsa, AndroidRelaySshAlgorithmPolicy.administratorHostKeyAlgorithms(onlyRsa));
        assertEquals(onlyRsa, AndroidRelaySshAlgorithmPolicy.administratorHostKeyAlgorithms(" " + onlyRsa + ",rsa-sha2-256,,"));
        String filtered = AndroidRelaySshAlgorithmPolicy.administratorHostKeyAlgorithms(JSch.getConfig("server_host_key"));
        assertEquals(filtered, AndroidRelaySshAlgorithmPolicy.administratorHostKeyAlgorithms(filtered));
    }

    @Test public void unusableAlgorithmListsFailClosedInsteadOfRestoringUnsafeDefaults() {
        for (String defaults : new String[] { null, "", "ssh-ed25519,ssh-ed448", "ssh-rsa,ssh-dss", "unknown-algorithm" })
            assertThrows(IllegalArgumentException.class, () -> AndroidRelaySshAlgorithmPolicy.administratorHostKeyAlgorithms(defaults));
    }
}
