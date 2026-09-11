package com.remotedesk.agent;

import java.net.Socket;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.Collections;
import java.util.function.Consumer;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/** First-use identity discovery. Never receives or sends a relay access token. */
final class AndroidRelayEnrollment {
    private AndroidRelayEnrollment() { }

    static final class Draft {
        final String server, token, deviceId;
        final int port;
        final boolean publish;

        Draft(String server, int port, String token, String deviceId, boolean publish) {
            this.server = AndroidRelay.checkedServer(server, port);
            this.token = AndroidRelay.checkedToken(token);
            this.deviceId = AndroidRelay.checkedDeviceId(deviceId);
            this.port = port;
            this.publish = publish;
        }

        AndroidRelay.Options options(String pin) {
            return new AndroidRelay.Options(server, port, token, pin, deviceId, publish);
        }

        @Override public String toString() { return "RelayDraft(" + server + ":" + port + ", <redacted>)"; }
    }

    static boolean sameEndpoint(AndroidRelay.Options saved, Draft draft) {
        return saved != null && saved.port == draft.port && saved.serverAddress.equalsIgnoreCase(draft.server);
    }

    static String knownPin(AndroidRelay.Options saved, Draft draft, String optionalPin) {
        String manual = AndroidRelay.normalizePin(optionalPin);
        if (!manual.isEmpty()) return draft.options(manual).tlsCertificateSha256;
        return sameEndpoint(saved, draft) ? saved.tlsCertificateSha256 : "";
    }

    static boolean replacesIdentity(AndroidRelay.Options saved, Draft draft, String pin) {
        return sameEndpoint(saved, draft) && !saved.tlsCertificateSha256.equals(pin);
    }

    static String fingerprint(byte[] encoded) throws Exception {
        if (encoded == null || encoded.length == 0) throw new CertificateException("服务器未提供身份信息。");
        byte[] hash = MessageDigest.getInstance("SHA-256").digest(encoded);
        StringBuilder result = new StringBuilder(64);
        for (byte value : hash) result.append(String.format(java.util.Locale.ROOT, "%02X", value & 255));
        return result.toString();
    }

    // Deliberate TOFU probe: only observe the peer, never authenticate it or
    // expose this SSLContext/socket to application requests. The UI must obtain
    // explicit first-use trust, then reconnect through the strict pinned path.
    @android.annotation.SuppressLint("CustomX509TrustManager")
    static String discover(String server, int port, Consumer<Socket> pending) throws Exception {
        String host = AndroidRelay.checkedServer(server, port);
        SSLContext observer = SSLContext.getInstance("TLS");
        observer.init(null, new TrustManager[] {new X509TrustManager() {
            public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
                throw new CertificateException("不接受客户端证书认证。");
            }
            public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
                if (chain == null || chain.length == 0) throw new CertificateException("服务器未提供身份信息。");
                // Handshake proves possession only; first-use trust is a separate UI decision.
            }
        }}, null);
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            pending.accept(dial);
            try (SSLSocket socket = new AndroidRelayNetworkSelector.Selector(System::nanoTime).connect(
                    "enroll/" + host + ":" + port, Collections.emptyList(), dial,
                    (path, owner) -> AndroidRelay.connectBoundSocket(host, port, observer, null, owner, ignored -> { }))) {
                pending.accept(socket);
                return fingerprint(socket.getSession().getPeerCertificates()[0].getEncoded());
            }
        } finally { pending.accept(null); }
    }
}
