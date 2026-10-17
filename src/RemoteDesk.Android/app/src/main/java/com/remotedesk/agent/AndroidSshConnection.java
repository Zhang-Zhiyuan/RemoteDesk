package com.remotedesk.agent;

import android.content.Context;
import com.jcraft.jsch.ChannelShell;
import com.jcraft.jsch.HostKey;
import com.jcraft.jsch.HostKeyRepository;
import com.jcraft.jsch.JSch;
import com.jcraft.jsch.Session;
import com.jcraft.jsch.SocketFactory;
import com.jcraft.jsch.UserInfo;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Socket;
import java.util.Arrays;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.net.ssl.SSLSocket;

/** One user-started SSH shell; no retry, forwarding, exec channel, or desktop access. */
final class AndroidSshConnection implements AutoCloseable {
    interface Listener {
        boolean confirmHostKey(String fingerprint) throws Exception;
        void status(String text);
        void output(byte[] bytes) throws Exception; // Backpressure: caller acknowledges parsing before returning.
        void ended(String text);
    }
    private static final ScheduledExecutorService DEADLINES = Executors.newSingleThreadScheduledExecutor(
        r -> AndroidRelay.thread("SshDeadline", r));
    private final Context context;
    private final Listener listener;
    private final AndroidSshHostKeys hostKeys;
    private final AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
    private final AtomicBoolean closed = new AtomicBoolean();
    private final ArrayBlockingQueue<byte[]> input = new ArrayBlockingQueue<>(16);
    private volatile Session session;
    private volatile ChannelShell shell;
    private volatile Thread writer;
    private volatile int columns = 80, rows = 24;
    private volatile boolean resizePending = true;
    private volatile String trustFailure;

    AndroidSshConnection(Context context, Listener listener) {
        this.context = context.getApplicationContext(); this.listener = listener;
        hostKeys = new AndroidSshHostKeys(this.context);
    }

    void start(AndroidRelay.Options target, String key, String username, byte[] password) {
        start(target, key, username, password, null);
    }
    void start(AndroidRelay.Options target, String key, String username, byte[] password, byte[] privateKey) {
        AndroidRelay.thread("SshConnect", () -> run(target, key, username, password, privateKey)).start();
    }

    private void run(AndroidRelay.Options target, String key, String username, byte[] password, byte[] privateKey) {
        JSch jsch = new JSch();
        try {
            listener.status("正在核对 SSH 能力与设备身份……");
            ScheduledFuture<?> timeout = DEADLINES.schedule(dial::close, 20, TimeUnit.SECONDS);
            SSLSocket socket;
            try {
                boolean available = false;
                // A stale/forged entry must not open a desktop channel or probe SSH on an unadvertised device.
                for (AndroidRelay.Device device : AndroidRelay.listDevices(context, target, operation -> {
                    if (operation != null) try { dial.add(operation); } catch (IOException ex) { AndroidRelay.close(operation); }
                })) if (device.deviceId.equals(target.deviceId)) available = device.sshAvailable;
                if (!available) throw new IOException("目标未上线或未启用 SSH；请返回刷新在线列表。");
                checkOpen();
                socket = AndroidRelay.connectDirectoryPage(dial,
                    page -> AndroidRelayNetworkSelector.connect(context, target, page));
                socket.setSoTimeout(20000);
                AndroidSshProtocol.paired(AndroidRelay.exchange(socket,
                    AndroidRelay.request(target, "ssh-viewer").put("deviceId", target.deviceId)));
                AndroidSshProtocol.authenticate(socket.getInputStream(), socket.getOutputStream(), target.deviceId, key);
                key = null;
                dial.checkOpen();
            } finally { timeout.cancel(false); }
            checkOpen();
            socket.setSoTimeout(0);
            String alias = AndroidSshProtocol.alias(target);
            jsch.setHostKeyRepository(new HostKeyRepository() {
                @Override public int check(String host, byte[] keyBytes) {
                    try {
                        checkOpen();
                        if (!alias.equals(host)) return CHANGED;
                        String fingerprint = AndroidSshProtocol.fingerprint(keyBytes);
                        String saved = hostKeys.get(alias);
                        if (!saved.isEmpty()) {
                            if (saved.equals(fingerprint)) return OK;
                            trustFailure = "SSH 主机密钥已变化，已拒绝连接。请先在目标主机核实；不会自动覆盖已保存身份。";
                            return CHANGED;
                        }
                        if (!listener.confirmHostKey(fingerprint)) {
                            trustFailure = "未确认 SSH 主机身份，连接已取消。"; return NOT_INCLUDED;
                        }
                        checkOpen();
                        hostKeys.trustFirst(alias, fingerprint);
                        return OK;
                    } catch (Exception ex) {
                        trustFailure = "SSH 主机身份确认或保存未完成，连接已取消。"; return NOT_INCLUDED;
                    }
                }
                @Override public void add(HostKey key, UserInfo ui) { }
                @Override public void remove(String h, String t) { }
                @Override public void remove(String h, String t, byte[] k) { }
                @Override public String getKnownHostsRepositoryID() { return "RemoteDesk target SSH identities"; }
                @Override public HostKey[] getHostKey() { return new HostKey[0]; }
                @Override public HostKey[] getHostKey(String h, String t) { return new HostKey[0]; }
            });
            session = jsch.getSession(username, alias, 22);
            session.setHostKeyAlias(alias);
            session.setConfig("StrictHostKeyChecking", "yes");
            session.setConfig("PreferredAuthentications", privateKey == null ? "password" : "publickey");
            session.setConfig("NumberOfPasswordPrompts", "1");
            session.setSocketFactory(new SocketFactory() {
                @Override public Socket createSocket(String host, int port) throws IOException { checkOpen(); return socket; }
                @Override public InputStream getInputStream(Socket value) throws IOException { return value.getInputStream(); }
                @Override public OutputStream getOutputStream(Socket value) throws IOException { return value.getOutputStream(); }
            });
            if (privateKey == null) session.setPassword(password);
            else try { jsch.addIdentity("selected-target-key", privateKey, null, password.length == 0 ? null : password); }
            catch (com.jcraft.jsch.JSchException ex) {
                throw new IOException("所选 SSH 私钥无法解析或算法不受当前 Android 加密组件支持；请检查私钥格式与口令。", ex);
            }
            listener.status("正在验证 SSH 主机身份并登录目标账号……");
            try { session.connect(20000); }
            finally {
                Arrays.fill(password, (byte) 0); session.setPassword((byte[]) null);
                if (privateKey != null) Arrays.fill(privateKey, (byte) 0);
                jsch.removeAllIdentity();
            }
            checkOpen();
            session.setServerAliveInterval(15000);
            session.setServerAliveCountMax(2);
            shell = (ChannelShell) session.openChannel("shell");
            shell.setPty(true);
            shell.setPtyType("xterm-256color", columns, rows, 0, 0);
            InputStream output = shell.getInputStream();
            OutputStream terminalInput = shell.getOutputStream();
            shell.connect(15000);
            checkOpen();
            listener.status("SSH 已连接 · 独立通道，不接管桌面");
            writer = AndroidRelay.thread("SshInput", () -> writeInput(terminalInput));
            writer.start();
            byte[] buffer = new byte[8192];
            int count;
            while (!closed.get() && (count = output.read(buffer)) != -1)
                if (count > 0) listener.output(Arrays.copyOf(buffer, count));
            if (!closed.get()) listener.ended("SSH 会话已结束；不会自动重连或重放输入。");
        } catch (Exception ex) {
            if (!closed.get()) listener.ended(trustFailure != null ? trustFailure :
                ex instanceof AndroidRelay.IdentityFailure ? ex.getMessage() :
                ex instanceof IOException && ex.getMessage() != null && ex.getMessage().matches(".*[\\p{IsHan}].*") ? ex.getMessage() :
                ex instanceof com.jcraft.jsch.JSchException && String.valueOf(ex.getMessage()).contains("Algorithm negotiation fail") ?
                    "此 Android 与目标 SSH 服务没有共同可用的密钥或加密算法。请核对目标算法支持；不会关闭主机身份校验或自动启用旧弱算法。" :
                ex instanceof com.jcraft.jsch.JSchException && String.valueOf(ex.getMessage()).contains("Auth") ?
                    privateKey != null ? "SSH 私钥认证失败；请核对目标账号、私钥授权及此 Android 支持的密钥算法。" :
                        "SSH 账号或密码验证失败；请使用目标设备的 SSH 账号，不是中继服务器管理员账号。" :
                    "SSH 连接未完成或已断开，请检查网络与目标 SSH 服务后重试。");
        } finally {
            Arrays.fill(password, (byte) 0);
            if (privateKey != null) Arrays.fill(privateKey, (byte) 0);
            try { jsch.removeAllIdentity(); } catch (com.jcraft.jsch.JSchException ignored) { }
            close();
        }
    }

    void resize(int columns, int rows) {
        if (columns < 2 || columns > 500 || rows < 1 || rows > 300) return;
        this.columns = columns; this.rows = rows; resizePending = true;
    }

    boolean send(byte[] bytes) {
        synchronized (input) {
            if (closed.get() || shell == null || !shell.isConnected() || bytes.length == 0 || bytes.length > 8192) return false;
            byte[] copy = bytes.clone();
            if (input.offer(copy)) return true; // Never let congested SSH block the UI/IME thread.
            Arrays.fill(copy, (byte) 0); return false;
        }
    }

    private void writeInput(OutputStream stream) {
        try {
            while (!closed.get()) {
                if (resizePending) {
                    resizePending = false;
                    shell.setPtySize(columns, rows, 0, 0);
                }
                byte[] bytes = input.poll(100, TimeUnit.MILLISECONDS);
                if (bytes == null) continue;
                try { checkOpen(); stream.write(bytes); stream.flush(); }
                finally { Arrays.fill(bytes, (byte) 0); }
            }
        } catch (Exception ex) {
            if (!closed.get()) listener.ended("SSH 输入通道已断开；未发送的输入已丢弃。");
            close();
        }
    }

    private void checkOpen() throws IOException { if (closed.get()) throw new IOException("SSH 会话已关闭。"); dial.checkOpen(); }
    @Override public void close() {
        if (!closed.compareAndSet(false, true)) return;
        Thread currentWriter = writer;
        if (currentWriter != null) currentWriter.interrupt();
        synchronized (input) {
            byte[] bytes;
            while ((bytes = input.poll()) != null) Arrays.fill(bytes, (byte) 0);
        }
        // Conscrypt close() can send TLS close_notify. Even closing a socket must
        // not execute on the Activity/IME thread (NetworkOnMainThreadException).
        AndroidRelay.thread("SshClose", () -> {
            try { dial.close(); } catch (RuntimeException ignored) { }
            Session current = session;
            if (current != null) try { current.disconnect(); } catch (RuntimeException ignored) { }
        }).start();
    }
}
