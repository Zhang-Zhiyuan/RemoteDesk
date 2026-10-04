package com.remotedesk.agent;

import com.jcraft.jsch.Session;
import java.util.ArrayList;

/** Preserve existing administrator host-key selection when BC enables EdDSA. */
final class AndroidRelaySshAlgorithmPolicy {
    private static final String CERTIFICATE_SUFFIX = "-cert-v01@openssh.com";

    private AndroidRelaySshAlgorithmPolicy() { }

    static void configureAdministratorSession(Session session) {
        // This applies identically on first enrollment and pinned re-login.
        // Never alter global JSch configuration or the independent SSH terminal.
        session.setConfig("server_host_key", administratorHostKeyAlgorithms(session.getConfig("server_host_key")));
    }

    static String administratorHostKeyAlgorithms(String defaults) {
        ArrayList<String> algorithms = new ArrayList<>();
        if (defaults != null) for (String entry : defaults.split(",")) {
            String algorithm = entry.trim();
            String base = algorithm.endsWith(CERTIFICATE_SUFFIX)
                ? algorithm.substring(0, algorithm.length() - CERTIFICATE_SUFFIX.length()) : algorithm;
            // These are the modern host-key families available to the previous
            // Android administrator login. Preserve their original preference
            // order, including certificates; never add RSA/SHA-1 or DSA.
            if (base.equals("ecdsa-sha2-nistp256") || base.equals("ecdsa-sha2-nistp384") ||
                    base.equals("ecdsa-sha2-nistp521") || base.equals("rsa-sha2-512") || base.equals("rsa-sha2-256"))
                if (!algorithms.contains(algorithm)) algorithms.add(algorithm);
        }
        if (algorithms.isEmpty()) throw new IllegalArgumentException("管理员 SSH 没有可用的现代兼容主机密钥算法。");
        return String.join(",", algorithms);
    }
}
