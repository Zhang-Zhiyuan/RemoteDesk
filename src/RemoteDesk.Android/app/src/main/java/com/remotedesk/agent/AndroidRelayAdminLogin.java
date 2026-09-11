package com.remotedesk.agent;

import android.content.Context;
import com.jcraft.jsch.ChannelExec;
import com.jcraft.jsch.HostKey;
import com.jcraft.jsch.HostKeyRepository;
import com.jcraft.jsch.JSch;
import com.jcraft.jsch.JSchException;
import com.jcraft.jsch.Session;
import com.jcraft.jsch.UserInfo;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.Base64;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;
import org.json.JSONObject;

/** SSH is used only to read enrollment data; it never carries a remote desktop. */
final class AndroidRelayAdminLogin {
    private static final int LIMIT = 65536;
    private static final ScheduledExecutorService DEADLINES = Executors.newSingleThreadScheduledExecutor(
        r -> AndroidRelay.thread("RelayLoginDeadline", r));

    static final class LoginFailure extends Exception {
        LoginFailure(String message) { super(message); }
    }

    static String username(String value) throws LoginFailure {
        String result = value == null ? "" : value.trim();
        if (!result.matches("[a-zA-Z_][a-zA-Z0-9_.-]{0,63}"))
            throw new LoginFailure("服务器管理员账号无效；默认使用 root。");
        return result;
    }

    static String fingerprint(byte[] key) throws Exception {
        return "SHA256:" + Base64.getEncoder().withoutPadding().encodeToString(
            MessageDigest.getInstance("SHA-256").digest(key));
    }

    static String normalizeIdentity(String value) {
        String result = value == null ? "" : value.trim();
        if (result.isEmpty()) return "";
        if (result.regionMatches(true, 0, "SHA256:", 0, 7)) result = result.substring(7);
        try {
            byte[] bytes = Base64.getDecoder().decode(result);
            if (bytes.length == 32) return "SHA256:" + Base64.getEncoder().withoutPadding().encodeToString(bytes);
        } catch (IllegalArgumentException ignored) { }
        throw new IllegalArgumentException("已保存的 SSH 服务器身份无效；原配置未更改。");
    }

    static String knownIdentity(AndroidRelay.Options saved, String server, int sshPort) {
        return saved != null && saved.serverAddress.equalsIgnoreCase(server.trim()) && saved.sshPort == sshPort
            ? saved.sshHostKeySha256 : "";
    }

    static AndroidRelay.Options parseResponse(String text, String server, int sshPort,
            String username, String identity, String deviceId, boolean publish) throws LoginFailure {
        if (text == null || text.length() > LIMIT) throw new LoginFailure("服务器返回的配置过大，原配置未更改。");
        try {
            JSONObject response = new JSONObject(text);
            String error = response.optString("errorCode");
            if (error.equals("not_configured")) throw new LoginFailure("服务器尚未安装中继，请先在 Windows 端使用“部署 / 更新服务器”。");
            if (error.equals("permission_denied")) throw new LoginFailure("此账号不能读取中继配置，请使用 root 或有 sudo 权限的管理员。");
            if (!error.isEmpty() || !(response.get("version") instanceof Integer) || response.getInt("version") != 1 ||
                    !(response.get("port") instanceof Integer) || !(response.get("accessToken") instanceof String) ||
                    !(response.get("tlsCertificateSha256") instanceof String)) throw new IllegalArgumentException();
            return new AndroidRelay.Options(server, response.getInt("port"), response.getString("accessToken"),
                response.getString("tlsCertificateSha256"), deviceId, publish, sshPort, username, identity);
        } catch (LoginFailure error) { throw error; }
        catch (Exception error) { throw new LoginFailure("服务器中继配置无效；未保存，请检查服务器安装状态。"); }
    }

    static String command(byte[] source, String username) {
        String code = Base64.getEncoder().encodeToString(source);
        String command = "python3 -c \"exec(__import__('base64').b64decode('" + code + "'))\"";
        return username.equals("root") ? command : "sudo -k -S -p '' -- " + command;
    }

    /** Socket-shaped cancellation handle, owned by the existing panel lifecycle. */
    static final class Operation extends Socket {
        private final AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
        private volatile boolean cancelled;
        private volatile Session session;

        @Override public void close() {
            cancelled = true;
            dial.close();
            Session current = session;
            if (current != null) current.disconnect();
        }

        @Override public boolean isClosed() { return cancelled; }

        private void checkOpen() throws LoginFailure {
            if (cancelled) throw new LoginFailure("服务器登录已取消或超时，原配置未更改。");
        }

        AndroidRelay.Options login(Context context, String server, int sshPort, String adminName,
                byte[] password, String expectedIdentity, String deviceId, boolean publish) throws LoginFailure {
            java.util.concurrent.ScheduledFuture<?> timeout = DEADLINES.schedule(this::close, 40, TimeUnit.SECONDS);
            AtomicBoolean mismatch = new AtomicBoolean();
            AtomicReference<String> observed = new AtomicReference<>();
            ChannelExec channel = null;
            try {
                checkOpen();
                String host = AndroidRelay.checkedServer(server, sshPort);
                String admin = username(adminName);
                String expected = normalizeIdentity(expectedIdentity);
                if (password == null || password.length == 0 || password.length > 16384)
                    throw new LoginFailure("请输入服务器 root / 管理员密码；不是设备密钥。");
                for (byte value : password) if (value == 0 || value == '\n' || value == '\r')
                    throw new LoginFailure("服务器密码不能包含换行或空字符。");
                JSch client = new JSch();
                client.setHostKeyRepository(new HostKeyRepository() {
                    public int check(String peer, byte[] key) {
                        try {
                            String actual = fingerprint(key);
                            String previous = observed.get();
                            if ((!expected.isEmpty() && !expected.equals(actual)) || (previous != null && !previous.equals(actual))) {
                                mismatch.set(true); return CHANGED;
                            }
                            observed.set(actual);
                            return OK; // First-use trust is scoped to this explicit administrator login.
                        } catch (Exception failure) { mismatch.set(true); return CHANGED; }
                    }
                    public void add(HostKey key, UserInfo info) { }
                    public void remove(String host, String type) { }
                    public void remove(String host, String type, byte[] key) { }
                    public String getKnownHostsRepositoryID() { return "RemoteDesk server login"; }
                    public HostKey[] getHostKey() { return new HostKey[0]; }
                    public HostKey[] getHostKey(String host, String type) { return new HostKey[0]; }
                });
                session = client.getSession(admin, host, sshPort);
                session.setConfig("StrictHostKeyChecking", "yes");
                session.setConfig("PreferredAuthentications", "password");
                session.setConfig("NumberOfPasswordPrompts", "1");
                session.setSocketFactory(new com.jcraft.jsch.SocketFactory() {
                    public Socket createSocket(String host, int port) throws IOException {
                        Socket socket = new Socket();
                        dial.add(socket);
                        socket.connect(new InetSocketAddress(host, port), 12000);
                        socket.setSoTimeout(12000);
                        socket.setTcpNoDelay(true);
                        dial.checkOpen();
                        return socket;
                    }
                    public InputStream getInputStream(Socket socket) throws IOException { return socket.getInputStream(); }
                    public OutputStream getOutputStream(Socket socket) throws IOException { return socket.getOutputStream(); }
                });
                session.setPassword(password);
                session.setTimeout(12000);
                session.connect(12000);
                checkOpen();
                if (observed.get() == null) throw new LoginFailure("未取得服务器 SSH 身份，登录已停止。");
                byte[] source;
                try (InputStream stream = context.getAssets().open("read_remotedesk_relay_config.py")) {
                    source = readBounded(stream);
                }
                channel = (ChannelExec) session.openChannel("exec");
                channel.setCommand(command(source, admin));
                channel.setPty(false);
                channel.setErrStream(new OutputStream() { @Override public void write(int value) { } });
                InputStream stdout = channel.getInputStream();
                OutputStream stdin = channel.getOutputStream();
                channel.connect(12000);
                if (!admin.equals("root")) { stdin.write(password); stdin.write('\n'); stdin.flush(); }
                stdin.close();
                String response = new String(readBounded(stdout), StandardCharsets.UTF_8);
                while (!channel.isClosed()) { checkOpen(); Thread.sleep(10); }
                checkOpen();
                if (channel.getExitStatus() != 0)
                    throw new LoginFailure("无法读取中继配置；请检查 root / sudo 权限及服务器 Python 3。");
                return parseResponse(response, host, sshPort, admin, observed.get(), deviceId, publish);
            } catch (LoginFailure error) { throw error; }
            catch (JSchException error) {
                if (mismatch.get()) throw new LoginFailure("服务器 SSH 身份已变化，未发送 root 密码；请先核实服务器是否更换或重装。");
                checkOpen();
                if (error.getMessage() != null && error.getMessage().startsWith("Auth fail"))
                    throw new LoginFailure("服务器 root / 管理员密码错误，或 SSH 禁止密码登录。");
                throw new LoginFailure("无法登录服务器 SSH；请检查服务器地址、SSH 端口和网络。原配置未更改。");
            } catch (Exception error) {
                checkOpen();
                throw new LoginFailure("服务器登录未完成；请检查网络和中继安装状态。原配置未更改。");
            } finally {
                if (password != null) Arrays.fill(password, (byte) 0);
                if (channel != null) channel.disconnect();
                timeout.cancel(false);
                close();
            }
        }
    }

    private static byte[] readBounded(InputStream stream) throws IOException {
        ByteArrayOutputStream result = new ByteArrayOutputStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = stream.read(buffer)) != -1) {
            if (result.size() + count > LIMIT) throw new IOException("Bounded SSH response exceeded");
            result.write(buffer, 0, count);
        }
        return result.toByteArray();
    }
}
