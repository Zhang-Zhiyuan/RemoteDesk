package com.remotedesk.agent;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.DataInputStream;
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
    static final int LOOPBACK_BUFFER_BYTES = 16 * 1024;
    static final int COPY_BUFFER_BYTES = 16 * 1024;
    private static final java.util.concurrent.ScheduledThreadPoolExecutor DIRECTORY_DEADLINES = directoryDeadlines();

    private static java.util.concurrent.ScheduledThreadPoolExecutor directoryDeadlines() {
        java.util.concurrent.ScheduledThreadPoolExecutor timer = new java.util.concurrent.ScheduledThreadPoolExecutor(
            1, r -> thread("RelayDirectoryDeadline", r));
        timer.setRemoveOnCancelPolicy(true);
        return timer;
    }

    static int hostSendBufferBytes(java.net.InetAddress peer, int directBufferBytes) {
        return peer != null && peer.isLoopbackAddress() ? LOOPBACK_BUFFER_BYTES : directBufferBytes;
    }

    // Called only for our private 127.0.0.1 host adapter. Never shrink the
    // public TLS socket's receive window: that path needs WAN autotuning.
    static void configureLoopbackSocket(Socket socket) {
        try { socket.setReceiveBufferSize(LOOPBACK_BUFFER_BYTES); } catch (IOException | RuntimeException ignored) { }
        try { socket.setSendBufferSize(LOOPBACK_BUFFER_BYTES); } catch (IOException | RuntimeException ignored) { }
        try { socket.setTcpNoDelay(true); } catch (IOException | RuntimeException ignored) { }
    }

    static final class IdentityFailure extends SecurityException {
        IdentityFailure(boolean certificate, Throwable cause) {
            super(certificate ? "中转服务器身份与已保存的信息不符，请核实是否更换或重装过服务器；原配置未更改。" : "中转访问密钥被拒绝，请检查中转配置。", cause);
        }
    }

    static final class Options {
        final String serverAddress, accessToken, tlsCertificateSha256, deviceId;
        final int port;
        final boolean publish;

        Options(String server, int port, String token, String pin, String deviceId, boolean publish) {
            this.serverAddress = checkedServer(server, port);
            this.accessToken = checkedToken(token);
            this.tlsCertificateSha256 = normalizePin(pin);
            if (!this.tlsCertificateSha256.matches("[0-9A-F]{64}")) {
                throw new IllegalArgumentException("中转 TLS 指纹应为 64 位 SHA-256。");
            }
            this.deviceId = checkedDeviceId(deviceId);
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

    static String checkedServer(String server, int port) {
        String value = server == null ? "" : server.trim();
        if (value.isEmpty() || value.length() > 253 || value.matches(".*[\\s/@\\\\].*") || port < 1 || port > 65535)
            throw new IllegalArgumentException("中转服务器地址或端口无效。");
        return value;
    }

    static String checkedToken(String token) {
        String value = token == null ? "" : token.trim();
        if (value.length() < 32 || value.length() > 4096)
            throw new IllegalArgumentException("中转共享访问密钥无效。");
        return value;
    }

    static String checkedDeviceId(String deviceId) {
        try { return UUID.fromString(deviceId.trim()).toString(); }
        catch (RuntimeException ex) { throw new IllegalArgumentException("中转设备 ID 无效。"); }
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
    private static SSLContext tlsContext(Options options) throws Exception {
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
        return context;
    }

    static SSLSocket newSocket(Options options) throws Exception {
        return configureTlsSocket((SSLSocket) tlsContext(options).getSocketFactory().createSocket());
    }

    private static SSLSocket configureTlsSocket(SSLSocket socket) throws Exception {
        List<String> protocols = new ArrayList<>();
        for (String protocol : socket.getSupportedProtocols()) {
            if (protocol.equals("TLSv1.2") || protocol.equals("TLSv1.3")) protocols.add(protocol);
        }
        socket.setEnabledProtocols(protocols.toArray(new String[0]));
        socket.setTcpNoDelay(true);
        return socket;
    }

    static SSLSocket connectBoundSocket(Options options, android.net.Network network,
            AndroidRelayNetworkSelector.Dial dial, Consumer<Socket> configure) throws Exception {
        return connectBoundSocket(options.serverAddress, options.port, tlsContext(options), network, dial, configure);
    }

    // Shared socket plumbing only. Normal relay connections always enter via the
    // strict Options overload above; enrollment uses a separate, credential-free
    // observer and closes its socket before any trust decision or token write.
    static SSLSocket connectBoundSocket(String serverAddress, int port, SSLContext context,
            android.net.Network network, AndroidRelayNetworkSelector.Dial dial, Consumer<Socket> configure) throws Exception {
        java.net.InetAddress[] resolved = network == null
            ? java.net.InetAddress.getAllByName(serverAddress) : network.getAllByName(serverAddress);
        List<java.net.InetAddress> addresses = relayAddresses(resolved, network != null);
        return connectAddresses(addresses, dial, (address, tcpTimeout) -> {
            Socket raw = new Socket();
            SSLSocket socket = null;
            try {
                dial.add(raw);
                raw.setTcpNoDelay(true);
                configure.accept(raw);
                if (network != null) network.bindSocket(raw);
                dial.checkOpen();
                raw.connect(new InetSocketAddress(address, port), tcpTimeout);
                // Retain hostname/SNI and pinning for every DNS address. Failed
                // addresses never receive a relay token or application role.
                socket = (SSLSocket) context.getSocketFactory()
                    .createSocket(raw, serverAddress, port, true);
                dial.replace(raw, socket);
                configureTlsSocket(socket);
                socket.setSoTimeout(TIMEOUT_MS);
                startPinnedHandshake(socket);
                dial.checkOpen();
                return socket;
            } catch (Exception failure) {
                if (socket != null) dial.release(socket);
                dial.release(raw);
                throw failure;
            }
        });
    }

    @FunctionalInterface interface AddressConnector<T> {
        T connect(java.net.InetAddress address, int tcpTimeout) throws Exception;
    }

    static <T> T connectAddresses(List<java.net.InetAddress> addresses,
            AndroidRelayNetworkSelector.Dial dial, AddressConnector<T> connector) throws Exception {
        Exception failure = null;
        // A dead first AAAA/A record must not consume the whole dial. The outer
        // selector still enforces one deadline across DNS, TCP and all TLS attempts.
        int tcpTimeout = addresses.size() > 1 ? Math.min(TIMEOUT_MS, 2500) : TIMEOUT_MS;
        for (java.net.InetAddress address : addresses) {
            dial.checkOpen();
            try { return connector.connect(address, tcpTimeout); }
            catch (Exception next) { if (failure == null || next instanceof IdentityFailure) failure = next; }
        }
        throw failure == null ? new java.net.UnknownHostException("中转域名没有可用地址。") : failure;
    }

    static List<java.net.InetAddress> relayAddresses(java.net.InetAddress[] resolved, boolean alternative) throws IOException {
        List<java.net.InetAddress> addresses = new ArrayList<>();
        for (java.net.InetAddress address : resolved) {
            if (alternative) {
                // Optional cross-network probes are public IPv4 only, matching
                // desktop clients. Mixed public/private DNS keeps system routing.
                if (!(address instanceof java.net.Inet4Address)) continue;
                byte[] b = address.getAddress();
                int first = b[0] & 255, second = b[1] & 255;
                if (address.isAnyLocalAddress() || address.isLoopbackAddress() || address.isSiteLocalAddress() ||
                    address.isLinkLocalAddress() || first == 0 || first >= 224 || first == 100 && second >= 64 && second <= 127)
                    throw new IOException("内网中转地址沿用系统路由。");
            }
            if (!addresses.contains(address)) addresses.add(address);
        }
        // Retain a fallback from each family even if DNS returns many records
        // from the first family. No more than four addresses are tried per dial.
        List<java.net.InetAddress> ordered = new ArrayList<>();
        boolean ipv4 = !addresses.isEmpty() && addresses.get(0) instanceof java.net.Inet4Address;
        while (!addresses.isEmpty() && ordered.size() < 4) {
            int index = 0;
            for (int i = 0; i < addresses.size(); i++) {
                if ((addresses.get(i) instanceof java.net.Inet4Address) == ipv4) { index = i; break; }
            }
            ordered.add(addresses.remove(index));
            ipv4 = !ipv4;
        }
        return ordered;
    }

    static void connect(SSLSocket socket, Options options) throws Exception {
        socket.setSoTimeout(TIMEOUT_MS);
        socket.connect(new InetSocketAddress(options.serverAddress, options.port), TIMEOUT_MS);
        startPinnedHandshake(socket);
    }

    private static void startPinnedHandshake(SSLSocket socket) throws Exception {
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
        writeJsonPayload(output, value.toString().getBytes(StandardCharsets.UTF_8));
    }

    static void writeJsonPayload(OutputStream output, byte[] bytes) throws IOException {
        if (bytes.length < 1 || bytes.length > MAX_JSON) throw new IOException("中转握手消息过大。");
        // Keep the length and JSON in one TLS record/write instead of creating
        // an extra tiny packet for every relay heartbeat and handshake.
        byte[] frame = java.nio.ByteBuffer.allocate(4 + bytes.length).putInt(bytes.length).put(bytes).array();
        synchronized (output) {
            output.write(frame);
            output.flush();
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
        final List<String> directAddresses;
        final int directPort;
        Device(String id, String name, String platform, boolean busy) {
            this(id, name, platform, busy, java.util.Collections.emptyList(), 0);
        }
        Device(String id, String name, String platform, boolean busy, List<String> addresses, int port) {
            this.deviceId = id; this.name = name; this.platform = platform; this.busy = busy;
            this.directAddresses = AndroidRelayAddresses.normalize(addresses);
            this.directPort = port > 0 && port <= 65535 ? port : 0;
        }
        String addressDisplay() {
            List<String> values = new ArrayList<>();
            if (directPort > 0) for (String address : directAddresses) values.add(address + ":" + directPort);
            return values.isEmpty() ? "未上报 IP（仍可中转连接）" : String.join(" / ", values);
        }
    }

    static List<Device> listDevices(Options options) throws Exception {
        return listDevices(options, socket -> { });
    }

    static List<Device> listDevices(Options options, Consumer<Socket> socketChanged) throws Exception {
        return listDevices(null, options, socketChanged);
    }

    static List<Device> listDevices(android.content.Context context, Options options, Consumer<Socket> socketChanged) throws Exception {
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            socketChanged.accept(dial);
            ScheduledFuture<?> deadline = DIRECTORY_DEADLINES.schedule(dial::close, TIMEOUT_MS, TimeUnit.MILLISECONDS);
            try {
                List<Device> result = new ArrayList<>();
                Set<String> seen = new java.util.HashSet<>();
                int offset = 0;
                for (int page = 0; page < 16; page++) {
                    dial.checkOpen();
                    JSONObject response;
                    try (SSLSocket socket = connectDirectoryPage(dial,
                            pageDial -> AndroidRelayNetworkSelector.connect(context, options, pageDial))) {
                        try { response = exchange(socket, request(options, "directory").put("pageSize", 32).put("offset", offset)); }
                        finally { dial.release(socket); }
                    }
                    JSONArray devices = response.optJSONArray("devices");
                    if (devices == null || devices.length() > 512) throw new IOException("中转在线列表无效。");
                    for (int i = 0; i < devices.length(); i++) {
                        JSONObject item = devices.optJSONObject(i);
                        if (item == null) continue;
                        try {
                            String id = UUID.fromString(item.optString("deviceId")).toString();
                            if (seen.add(id)) result.add(new Device(id, bounded(item.optString("machineName", "未命名设备"), 120),
                                bounded(item.optString("platform", "未知"), 40), item.optBoolean("busy", false),
                                AndroidRelayAddresses.parse(item), AndroidRelayAddresses.port(item)));
                        } catch (IllegalArgumentException ignored) { }
                    }
                    dial.checkOpen();
                    if (result.size() > 512) throw new IOException("中转在线设备过多。");
                    if (!response.has("nextOffset") || response.isNull("nextOffset")) return result;
                    Object next = response.opt("nextOffset");
                    if (!(next instanceof Integer) || ((Integer) next) != offset + 32 || ((Integer) next) >= 512)
                        throw new IOException("中转目录分页无效。");
                    offset = (Integer) next;
                }
                throw new IOException("中转目录分页过多。");
            } finally { deadline.cancel(false); }
        } finally { socketChanged.accept(null); }
    }

    @FunctionalInterface interface DirectoryConnector<T extends Socket> {
        T connect(AndroidRelayNetworkSelector.Dial page) throws Exception;
    }

    static <T extends Socket> T connectDirectoryPage(AndroidRelayNetworkSelector.Dial directory,
            DirectoryConnector<T> connector) throws Exception {
        // The network race closes its own Dial after detaching its winner. The
        // directory deadline must survive that race and own every response/page.
        try (AndroidRelayNetworkSelector.Dial page = new AndroidRelayNetworkSelector.Dial()) {
            directory.add(page);
            try {
                T socket = connector.connect(page);
                directory.replace(page, socket); // Also closes a late winner if the outer request was canceled.
                return socket;
            } finally { directory.release(page); }
        }
    }

    private static String bounded(String text, int length) { return text.substring(0, Math.min(text.length(), length)); }
    static void close(Socket socket) { if (socket != null) try { socket.close(); } catch (IOException ignored) { } }

    /** Bounded, cancellable product registration and loopback host-data integration. */
    static final class HostConnector implements AutoCloseable {
        private final android.content.Context context;
        private final Options options;
        private final int localPort;
        private final String name;
        private final Consumer<String> status;
        private final java.util.function.Supplier<List<String>> addressProvider;
        private final AtomicBoolean stopped = new AtomicBoolean();
        private final Set<Socket> sockets = ConcurrentHashMap.newKeySet();
        private final Semaphore bridges = new Semaphore(8);
        private final ScheduledExecutorService timers = Executors.newSingleThreadScheduledExecutor(r -> thread("RelayHeartbeat", r));
        private final Thread worker;
        private volatile Runnable addressHeartbeat;
        private final AtomicBoolean addressRefreshQueued = new AtomicBoolean();

        HostConnector(Options options, int localPort, String name, Consumer<String> status) {
            this(null, options, localPort, name, status);
        }

        HostConnector(android.content.Context context, Options options, int localPort, String name, Consumer<String> status) {
            this(context, options, localPort, name, status, AndroidRelayAddresses::local);
        }

        HostConnector(android.content.Context context, Options options, int localPort, String name, Consumer<String> status,
                      java.util.function.Supplier<List<String>> addressProvider) {
            if (localPort < 1 || localPort > 65535) throw new IllegalArgumentException("本机端口无效。");
            this.options = options; this.localPort = localPort; this.name = bounded(name, 120); this.status = status;
            this.addressProvider = addressProvider;
            this.context = context == null ? null : context.getApplicationContext();
            worker = thread("RelayRegistration", this::run);
        }

        void start() { worker.start(); }

        boolean requestAddressRefresh() {
            if (stopped.get() || addressHeartbeat == null) return false;
            if (!addressRefreshQueued.compareAndSet(false, true)) return true;
            try {
                timers.execute(() -> {
                    try {
                        Runnable beat = addressHeartbeat;
                        if (!stopped.get() && beat != null) beat.run();
                    } finally { addressRefreshQueued.set(false); }
                });
                return true;
            } catch (java.util.concurrent.RejectedExecutionException ignored) {
                addressRefreshQueued.set(false);
                return false;
            }
        }

        private void track(Socket socket) throws IOException {
            sockets.add(socket);
            if (stopped.get()) { sockets.remove(socket); AndroidRelay.close(socket); throw new IOException("中转已停止。"); }
        }

        private void release(Socket socket) { sockets.remove(socket); AndroidRelay.close(socket); }
        private void report(String value) { try { status.accept(value); } catch (RuntimeException ignored) { } }

        private SSLSocket openConnectedSocket() throws Exception {
            try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
                track(dial);
                try {
                    SSLSocket socket = AndroidRelayNetworkSelector.connect(context, options, dial);
                    track(socket);
                    return socket;
                } finally { sockets.remove(dial); }
            }
        }

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
            SSLSocket socket = openConnectedSocket();
            ScheduledFuture<?> heartbeat = null;
            Set<Socket> connectionData = ConcurrentHashMap.newKeySet();
            AtomicBoolean controlClosed = new AtomicBoolean();
            try {
                track(socket);
                JSONObject registered = exchange(socket, AndroidRelayAddresses.attach(request(options, "host-control")
                    .put("deviceId", options.deviceId).put("machineName", name).put("platform", "Android"), localPort, addressProvider.get()));
                socket.setSoTimeout(45000);
                report("已上线到中转 " + options.serverAddress + ":" + options.port + " · 本地地址 " + socket.getLocalAddress().getHostAddress() +
                    (registered.optBoolean("addressReporting", false) ? " · IP / 端口自动更新已启用" : " · 服务器需更新才能显示 IP"));
                Runnable beat = () -> {
                    try { write(socket.getOutputStream(), AndroidRelayAddresses.attach(
                        new JSONObject().put("version", 1).put("type", "heartbeat"), localPort, addressProvider.get())); }
                    catch (Exception ex) { AndroidRelay.close(socket); }
                };
                addressHeartbeat = beat;
                heartbeat = timers.scheduleWithFixedDelay(beat, 10, 10, TimeUnit.SECONDS);
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
                addressHeartbeat = null;
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
                configureLoopbackSocket(local);
                local.connect(new InetSocketAddress("127.0.0.1", localPort), TIMEOUT_MS);
                remote = openConnectedSocket();
                track(remote); connectionData.add(remote);
                if (controlClosed.get()) return;
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
                byte[] buffer = new byte[COPY_BUFFER_BYTES];
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
