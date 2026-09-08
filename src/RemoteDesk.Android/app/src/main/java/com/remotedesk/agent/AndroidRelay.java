package com.remotedesk.agent;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;

import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLHandshakeException;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/** Product relay transport. No direct-LAN fallback; pin verification precedes token writes. */
final class AndroidRelay {
    static final int PORT = 56567;
    static final int MAX_JSON = 65536;
    static final int TIMEOUT_MS = 12000;

    static final class IdentityFailure extends SecurityException {
        IdentityFailure(boolean certificate, Throwable cause) {
            super(certificate ? "中转 TLS 身份校验失败，请核实证书指纹。" : "中转访问密钥被拒绝，请检查中转配置。", cause);
        }
    }

    static final class Options {
        final String serverAddress, accessToken, tlsCertificateSha256, deviceId;
        final int port;
        final boolean publish;

        Options(String server, int port, String token, String pin, String deviceId, boolean publish) {
            this.serverAddress = server == null ? "" : server.trim();
            this.accessToken = token == null ? "" : token.trim();
            this.tlsCertificateSha256 = normalizePin(pin);
            if (this.serverAddress.isEmpty() || this.serverAddress.length() > 253 ||
                this.serverAddress.matches(".*[\\s/@\\\\].*") || port < 1 || port > 65535) {
                throw new IllegalArgumentException("中转服务器地址或端口无效。");
            }
            if (this.accessToken.length() < 32 || this.accessToken.length() > 4096) {
                throw new IllegalArgumentException("中转共享访问密钥无效。");
            }
            if (!this.tlsCertificateSha256.matches("[0-9A-F]{64}")) {
                throw new IllegalArgumentException("中转 TLS 指纹应为 64 位 SHA-256。");
            }
            try { this.deviceId = UUID.fromString(deviceId.trim()).toString(); }
            catch (RuntimeException ex) { throw new IllegalArgumentException("中转设备 ID 无效。"); }
            this.port = port;
            this.publish = publish;
        }

        Options target(String id) {
            return new Options(serverAddress, port, accessToken, tlsCertificateSha256, id, publish);
        }

        JSONObject json() throws Exception {
            return new JSONObject().put("serverAddress", serverAddress).put("port", port)
                .put("accessToken", accessToken).put("tlsCertificateSha256", tlsCertificateSha256)
                .put("deviceId", deviceId).put("publish", publish);
        }

        static Options parse(String text) throws Exception {
            if (text == null || text.length() > MAX_JSON) throw new IOException("中转配置无效。");
            JSONObject data = new JSONObject(text);
            return new Options(data.optString("serverAddress"), data.optInt("port", PORT),
                data.optString("accessToken"), data.optString("tlsCertificateSha256"),
                data.optString("deviceId"), data.optBoolean("publish", true));
        }

        @Override public String toString() { return "RelayOptions(" + serverAddress + ":" + port + ", <redacted>)"; }
    }

    static String normalizePin(String pin) {
        return (pin == null ? "" : pin).replaceAll("[:\\s-]", "").toUpperCase(java.util.Locale.ROOT);
    }

    static boolean certificateMatches(byte[] certificate, String pin) throws Exception {
        if (certificate == null || certificate.length == 0 || pin == null || !pin.matches("[0-9A-F]{64}")) return false;
        byte[] expected = new byte[32];
        for (int i = 0; i < 32; i++) expected[i] = (byte) Integer.parseInt(pin.substring(i * 2, i * 2 + 2), 16);
        return MessageDigest.isEqual(expected, MessageDigest.getInstance("SHA-256").digest(certificate));
    }

    // A private self-signed relay is trusted by an exact, user-provided SHA-256
    // leaf-certificate pin, checked before any access token is transmitted.
    @android.annotation.SuppressLint("CustomX509TrustManager")
    static SSLSocket newSocket(Options options) throws Exception {
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, new TrustManager[] { new X509TrustManager() {
            public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            public void checkClientTrusted(X509Certificate[] chain, String type) throws CertificateException {
                throw new CertificateException("不接受客户端证书认证。");
            }
            public void checkServerTrusted(X509Certificate[] chain, String type) throws CertificateException {
                try {
                    if (chain == null || chain.length == 0 || !certificateMatches(chain[0].getEncoded(), options.tlsCertificateSha256)) {
                        throw new CertificateException("中转 TLS 指纹不匹配。");
                    }
                } catch (CertificateException ex) { throw ex; }
                catch (Exception ex) { throw new CertificateException("中转 TLS 身份校验失败。"); }
            }
        } }, null);
        SSLSocket socket = (SSLSocket) context.getSocketFactory().createSocket();
        List<String> protocols = new ArrayList<>();
        for (String protocol : socket.getSupportedProtocols()) {
            if (protocol.equals("TLSv1.2") || protocol.equals("TLSv1.3")) protocols.add(protocol);
        }
        socket.setEnabledProtocols(protocols.toArray(new String[0]));
        socket.setTcpNoDelay(true);
        return socket;
    }

    static void connect(SSLSocket socket, Options options) throws Exception {
        socket.setSoTimeout(TIMEOUT_MS);
        socket.connect(new InetSocketAddress(options.serverAddress, options.port), TIMEOUT_MS);
        try { socket.startHandshake(); }
        catch (SSLHandshakeException ex) {
            if (isIdentityFailure(ex)) throw new IdentityFailure(true, ex);
            throw ex; // A network EOF during TLS negotiation is retryable, not a changed identity.
        }
    }

    static boolean isIdentityFailure(Throwable failure) {
        for (int depth = 0; failure != null && depth < 16; depth++, failure = failure.getCause()) {
            if (failure instanceof CertificateException) return true;
        }
        return false;
    }

    static void connectViewer(SSLSocket socket, Options options) throws Exception {
        connect(socket, options);
        JSONObject request = request(options, "viewer").put("deviceId", options.deviceId);
        exchange(socket, request);
    }

    static JSONObject request(Options options, String role) throws Exception {
        return new JSONObject().put("version", 1).put("role", role).put("token", options.accessToken);
    }

    static void write(OutputStream output, JSONObject value) throws Exception {
        byte[] bytes = value.toString().getBytes(StandardCharsets.UTF_8);
        if (bytes.length < 1 || bytes.length > MAX_JSON) throw new IOException("中转握手消息过大。");
        synchronized (output) {
            DataOutputStream data = new DataOutputStream(output);
            data.writeInt(bytes.length);
            data.write(bytes);
            data.flush();
        }
    }

    static JSONObject read(InputStream input) throws Exception {
        DataInputStream data = new DataInputStream(input);
        int length = data.readInt();
        if (length < 1 || length > MAX_JSON) throw new IOException("中转握手消息长度无效。");
        byte[] bytes = new byte[length];
        data.readFully(bytes);
        try { return new JSONObject(new String(bytes, StandardCharsets.UTF_8)); }
        catch (Exception ex) { throw new IOException("中转握手消息无效。"); }
    }

    static JSONObject exchange(Socket socket, JSONObject request) throws Exception {
        write(socket.getOutputStream(), request);
        JSONObject result = read(socket.getInputStream());
        if (!Boolean.TRUE.equals(result.opt("ok"))) {
            if (result.optString("error").contains("访问密钥")) throw new IdentityFailure(false, null);
            throw new IOException("中转拒绝连接：目标可能已离线，请刷新设备列表。");
        }
        return result;
    }

    static final class Device {
        final String deviceId, name, platform;
        final boolean busy;
        Device(String id, String name, String platform, boolean busy) {
            this.deviceId = id; this.name = name; this.platform = platform; this.busy = busy;
        }
    }

    static List<Device> listDevices(Options options) throws Exception {
        return listDevices(options, socket -> { });
    }

    static List<Device> listDevices(Options options, Consumer<Socket> socketChanged) throws Exception {
        try (SSLSocket socket = newSocket(options)) {
            socketChanged.accept(socket);
            connect(socket, options);
            JSONArray devices = exchange(socket, request(options, "directory")).optJSONArray("devices");
            if (devices == null || devices.length() > 512) throw new IOException("中转在线列表无效。");
            List<Device> result = new ArrayList<>();
            Set<String> seen = new java.util.HashSet<>();
            for (int i = 0; i < devices.length(); i++) {
                JSONObject item = devices.optJSONObject(i);
                if (item == null) continue;
                try {
                    String id = UUID.fromString(item.optString("deviceId")).toString();
                    if (seen.add(id)) result.add(new Device(id, bounded(item.optString("machineName", "未命名设备"), 120),
                        bounded(item.optString("platform", "未知"), 40), item.optBoolean("busy", false)));
                } catch (IllegalArgumentException ignored) { }
            }
            return result;
        } finally { socketChanged.accept(null); }
    }

    private static String bounded(String text, int length) { return text.substring(0, Math.min(text.length(), length)); }
    static void close(Socket socket) { if (socket != null) try { socket.close(); } catch (IOException ignored) { } }

    /** Bounded, cancellable product registration and loopback host-data integration. */
    static final class HostConnector implements AutoCloseable {
        private final Options options;
        private final int localPort;
        private final String name;
        private final Consumer<String> status;
        private final AtomicBoolean stopped = new AtomicBoolean();
        private final Set<Socket> sockets = ConcurrentHashMap.newKeySet();
        private final Semaphore bridges = new Semaphore(8);
        private final ScheduledExecutorService timers = Executors.newSingleThreadScheduledExecutor(r -> thread("RelayHeartbeat", r));
        private final Thread worker;

        HostConnector(Options options, int localPort, String name, Consumer<String> status) {
            if (localPort < 1 || localPort > 65535) throw new IllegalArgumentException("本机端口无效。");
            this.options = options; this.localPort = localPort; this.name = bounded(name, 120); this.status = status;
            worker = thread("RelayRegistration", this::run);
        }

        void start() { worker.start(); }

        private void track(Socket socket) throws IOException {
            sockets.add(socket);
            if (stopped.get()) { sockets.remove(socket); AndroidRelay.close(socket); throw new IOException("中转已停止。"); }
        }

        private void release(Socket socket) { sockets.remove(socket); AndroidRelay.close(socket); }
        private void report(String value) { try { status.accept(value); } catch (RuntimeException ignored) { } }

        private void run() {
            int failures = 0;
            long[] delays = {1000, 2000, 5000, 10000, 20000};
            try {
                while (!stopped.get()) {
                    try { control(); failures = 0; }
                    catch (SecurityException ex) { report(ex.getMessage()); return; }
                    catch (Exception ex) {
                        if (stopped.get()) break;
                        long delay = delays[Math.min(failures++, delays.length - 1)];
                        report("中转离线，" + delay / 1000 + " 秒后重试。");
                        try { Thread.sleep(delay); } catch (InterruptedException interrupted) { break; }
                    }
                }
            } finally { timers.shutdownNow(); }
        }

        private void control() throws Exception {
            SSLSocket socket = newSocket(options);
            ScheduledFuture<?> heartbeat = null;
            Set<Socket> connectionData = ConcurrentHashMap.newKeySet();
            AtomicBoolean controlClosed = new AtomicBoolean();
            try {
                track(socket);
                connect(socket, options);
                exchange(socket, request(options, "host-control").put("deviceId", options.deviceId)
                    .put("machineName", name).put("platform", "Android"));
                socket.setSoTimeout(45000);
                report("已上线到中转 " + options.serverAddress + ":" + options.port);
                heartbeat = timers.scheduleWithFixedDelay(() -> {
                    try { write(socket.getOutputStream(), new JSONObject().put("version", 1).put("type", "heartbeat")); }
                    catch (Exception ex) { AndroidRelay.close(socket); }
                }, 10, 10, TimeUnit.SECONDS);
                while (!stopped.get()) {
                    JSONObject message = read(socket.getInputStream());
                    if (!"open".equals(message.optString("type"))) continue;
                    String id;
                    try { id = UUID.fromString(message.optString("sessionId")).toString(); }
                    catch (IllegalArgumentException invalid) { continue; }
                    if (!bridges.tryAcquire()) continue;
                    thread("RelayHostData", () -> {
                        try { data(id, connectionData, controlClosed); }
                        finally { bridges.release(); }
                    }).start();
                }
            } finally {
                controlClosed.set(true);
                if (heartbeat != null) heartbeat.cancel(true);
                for (Socket dataSocket : connectionData) AndroidRelay.close(dataSocket);
                release(socket);
            }
        }

        private void data(String sessionId, Set<Socket> connectionData, AtomicBoolean controlClosed) {
            Socket local = new Socket();
            SSLSocket remote = null;
            try {
                track(local); connectionData.add(local);
                if (controlClosed.get()) return;
                local.setTcpNoDelay(true);
                local.connect(new InetSocketAddress("127.0.0.1", localPort), TIMEOUT_MS);
                remote = newSocket(options);
                track(remote); connectionData.add(remote);
                if (controlClosed.get()) return;
                connect(remote, options);
                exchange(remote, request(options, "host-data").put("deviceId", options.deviceId).put("sessionId", sessionId));
                remote.setSoTimeout(0);
                SSLSocket peer = remote;
                Thread upstream = thread("RelayHostUpstream", () -> pump(local, peer));
                upstream.start();
                pump(peer, local);
                upstream.join(1000);
            } catch (Exception ex) {
                if (!stopped.get() && !controlClosed.get()) report("中转会话未建立或已结束，请确认录屏与被控端正在运行。");
            } finally {
                connectionData.remove(local); release(local);
                if (remote != null) { connectionData.remove(remote); release(remote); }
            }
        }

        private static void pump(Socket from, Socket to) {
            try {
                byte[] buffer = new byte[65536];
                InputStream input = from.getInputStream();
                OutputStream output = to.getOutputStream();
                for (int count; (count = input.read(buffer)) != -1;) { output.write(buffer, 0, count); output.flush(); }
            } catch (IOException ignored) { }
            finally { AndroidRelay.close(from); AndroidRelay.close(to); }
        }

        @Override public void close() {
            if (!stopped.compareAndSet(false, true)) return;
            timers.shutdownNow();
            worker.interrupt();
            for (Socket socket : sockets) AndroidRelay.close(socket);
            report("中转注册已停止。");
        }
    }

    static Thread thread(String name, Runnable work) {
        Thread thread = new Thread(work, "RemoteDesk" + name);
        thread.setDaemon(true);
        return thread;
    }
}
