package com.remotedesk.agent;

import android.app.Activity;
import android.os.Bundle;
import android.widget.ScrollView;
import android.widget.TextView;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import org.json.JSONObject;

/** Test-only app UID; production root passwords arrive only in a USB-forwarded memory stream. */
public final class RelayLoginProbeActivity extends Activity {
    private volatile ServerSocket listener;
    private volatile AndroidRelayAdminLogin.Operation operation;
    private AndroidRelayPanel panel;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        if (getIntent().getBooleanExtra("cancellationProbe", false)) {
            runCancellationProbe();
            return;
        }
        if (getIntent().getBooleanExtra("ui", false)) {
            ScrollView scroll = new ScrollView(this);
            panel = new AndroidRelayPanel(this, ignored -> { });
            scroll.addView(panel);
            setContentView(scroll);
            return;
        }
        TextView label = new TextView(this);
        label.setText("RemoteDesk SSH login test\nNo capture, input, deployment or password storage.");
        setContentView(label);
        new Thread(() -> {
            try (ServerSocket server = new ServerSocket(0, 1, InetAddress.getByName("127.0.0.1"))) {
                listener = server;
                server.setSoTimeout(60000);
                try (java.io.FileOutputStream ready = openFileOutput("relay-login-ready.json", MODE_PRIVATE)) {
                    ready.write(new JSONObject().put("port", server.getLocalPort()).toString().getBytes(StandardCharsets.UTF_8));
                }
                try (Socket control = server.accept()) {
                    control.setSoTimeout(60000);
                    DataInputStream input = new DataInputStream(control.getInputStream());
                    int length = input.readInt();
                    if (length < 1 || length > 65536) throw new IllegalArgumentException();
                    byte[] bytes = new byte[length]; input.readFully(bytes);
                    JSONObject request = new JSONObject(new String(bytes, StandardCharsets.UTF_8));
                    Arrays.fill(bytes, (byte) 0);
                    String host = request.getString("serverAddress");
                    if (!host.equals("8.138.5.232") && !host.equals("10.0.2.2") && !host.equals("127.0.0.1"))
                        throw new IllegalArgumentException("Unexpected authorized target");
                    byte[] password = request.getString("password").getBytes(StandardCharsets.UTF_8);
                    request.remove("password");
                    JSONObject result = new JSONObject().put("complete", false);
                    try {
                        operation = new AndroidRelayAdminLogin.Operation();
                        AndroidRelay.Options options = operation.login(getApplicationContext(), host,
                            request.getInt("sshPort"), "root", password, request.optString("sshIdentity", ""),
                            java.util.UUID.randomUUID().toString(), false);
                        int count = AndroidRelay.listDevices(this, options, ignored -> { }).size();
                        AndroidRelaySettings.save(this, options);
                        AndroidRelay.Options reloaded = AndroidRelaySettings.load(this);
                        if (reloaded == null || !reloaded.accessToken.equals(options.accessToken) ||
                                !reloaded.sshHostKeySha256.equals(options.sshHostKeySha256)) throw new IllegalStateException();
                        result.put("complete", true).put("server", host).put("port", options.port)
                            .put("onlineDevices", count).put("configurationReloaded", true)
                            .put("rootPasswordStored", reloaded.json().toString().toLowerCase().contains("password"));
                    } catch (AndroidRelayAdminLogin.LoginFailure error) {
                        result.put("failure", error.getMessage());
                    } catch (Exception error) { result.put("failureType", error.getClass().getSimpleName()); }
                    finally { Arrays.fill(password, (byte) 0); if (operation != null) operation.close(); }
                    byte[] reply = result.toString().getBytes(StandardCharsets.UTF_8);
                    DataOutputStream output = new DataOutputStream(control.getOutputStream());
                    output.writeInt(reply.length); output.write(reply); output.flush();
                    runOnUiThread(() -> label.setText("SSH login test complete; see non-secret result."));
                }
            } catch (Exception error) {
                runOnUiThread(() -> label.setText("Test stopped: " + error.getClass().getSimpleName()));
            } finally { deleteFile("relay-login-ready.json"); }
        }, "RelayLoginProbe").start();
    }

    /** Local callbacks only: no listener, SSH connection, saved relay or network request. */
    @SuppressWarnings("unchecked")
    private void runCancellationProbe() {
        JSONObject result = new JSONObject();
        AndroidRelayPanel fixture = null;
        try {
            result.put("runId", getIntent().getStringExtra("runId"));
            fixture = new AndroidRelayPanel(this, ignored -> { });
            java.lang.reflect.Method begin = AndroidRelayPanel.class.getDeclaredMethod("beginPendingOperation");
            begin.setAccessible(true);
            java.util.function.Consumer<Socket> directory =
                (java.util.function.Consumer<Socket>) begin.invoke(fixture);
            Socket directorySocket = new Socket();
            directory.accept(directorySocket);
            java.util.function.Consumer<Socket> login =
                (java.util.function.Consumer<Socket>) begin.invoke(fixture);
            AndroidRelayAdminLogin.Operation queuedLogin = new AndroidRelayAdminLogin.Operation();
            login.accept(queuedLogin);
            directory.accept(null); // A cancelled directory finishes after the next login was queued.
            result.put("newLoginSurvivedOldCleanup", !queuedLogin.isClosed());
            ((android.widget.Button) ViewerProbeApplication.field(fixture, "cancelSetupButton")).performClick();
            result.put("cancelButtonClosedQueuedLogin", queuedLogin.isClosed());
            result.put("previousDirectoryClosed", directorySocket.isClosed());

            Socket late = new Socket();
            directory.accept(late);
            result.put("lateDirectorySocketClosed", late.isClosed());

            java.util.function.Consumer<Socket> next =
                (java.util.function.Consumer<Socket>) begin.invoke(fixture);
            Socket nextSocket = new Socket();
            next.accept(nextSocket);
            java.lang.reflect.Field active = AndroidRelayPanel.class.getDeclaredField("active");
            active.setAccessible(true);
            active.set(fixture, true); // Model the visible state without starting its network ticker.
            fixture.active(false);
            result.put("leavingPageClosedCurrentSocket", nextSocket.isClosed());

            java.lang.reflect.Method failed = AndroidRelayPanel.class.getDeclaredMethod("showSetupFailure", String.class, boolean.class);
            failed.setAccessible(true);
            String warning = "登录已保存，但服务更新失败；请重启本机被控端。";
            failed.invoke(fixture, warning, true);
            java.lang.reflect.Method status = AndroidRelayPanel.class.getDeclaredMethod("setStatus", String.class);
            status.setAccessible(true);
            status.invoke(fixture, "在线设备 2 台");
            String actual = ((TextView) ViewerProbeApplication.field(fixture, "status")).getText().toString();
            result.put("savedFailureMessage", actual);
            result.put("savedFailureAccurateAfterDirectoryRefresh", actual.equals(warning + "\n在线设备 2 台"));
            boolean passed = true;
            for (String name : new String[] { "newLoginSurvivedOldCleanup", "cancelButtonClosedQueuedLogin",
                "previousDirectoryClosed", "lateDirectorySocketClosed", "leavingPageClosedCurrentSocket",
                "savedFailureAccurateAfterDirectoryRefresh" }) passed &= result.getBoolean(name);
            result.put("passed", passed);
        } catch (Exception failure) {
            try { result.put("passed", false).put("error", failure.toString()); }
            catch (Exception ignored) { }
        } finally {
            if (fixture != null) fixture.close();
        }
        try (java.io.FileOutputStream output = openFileOutput("relay-cancellation-probe.json", MODE_PRIVATE)) {
            output.write(result.toString(2).getBytes(StandardCharsets.UTF_8));
        } catch (Exception failure) { throw new RuntimeException(failure); }
        TextView label = new TextView(this);
        label.setText(result.toString());
        ScrollView scroll = new ScrollView(this);
        scroll.addView(label);
        setContentView(scroll);
    }
    @Override protected void onResume() { super.onResume(); if (panel != null) panel.active(true); }
    @Override protected void onPause() { if (panel != null) panel.active(false); super.onPause(); }
    @Override protected void onDestroy() {
        if (panel != null) panel.close();
        if (operation != null) operation.close();
        try { if (listener != null) listener.close(); } catch (Exception ignored) { }
        super.onDestroy();
    }
}
