package com.remotedesk.agent;

import android.app.Activity;
import android.os.Bundle;
import android.widget.TextView;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import org.json.JSONArray;
import org.json.JSONObject;

/** One bounded, USB-forwarded request in the app UID. No screen, input or stored credentials. */
public final class RelayNetworkProbeActivity extends Activity {
    private ServerSocket listener;
    private volatile boolean finished;
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        TextView label = new TextView(this);
        label.setText("RemoteDesk network test\nTLS and directory only; no screen capture or input.");
        setContentView(label);
        new Thread(() -> {
            try (ServerSocket server = new ServerSocket(0, 1, InetAddress.getByName("127.0.0.1"))) {
                listener = server;
                server.setSoTimeout(45000);
                JSONObject ready = new JSONObject().put("port", server.getLocalPort());
                try (java.io.FileOutputStream output = openFileOutput("relay-path-ready.json", MODE_PRIVATE)) {
                    output.write(ready.toString().getBytes(StandardCharsets.UTF_8));
                }
                try (Socket control = server.accept()) {
                    control.setSoTimeout(45000);
                    DataInputStream input = new DataInputStream(control.getInputStream());
                    int length = input.readInt();
                    if (length < 1 || length > 65536) throw new IllegalArgumentException("Invalid test request");
                    byte[] bytes = new byte[length]; input.readFully(bytes);
                    JSONObject value = new JSONObject(new String(bytes, StandardCharsets.UTF_8));
                    if (!"8.138.5.232".equals(value.getString("serverAddress")))
                        throw new IllegalArgumentException("Unexpected authorized relay");
                    AndroidRelay.Options options = new AndroidRelay.Options(value.getString("serverAddress"),
                        value.getInt("port"), value.getString("accessToken"), value.getString("tlsCertificateSha256"),
                        value.getString("deviceId"), false);
                    JSONArray measurements = new JSONArray();
                    for (int round = 0; round < 3; round++) {
                        long start = System.nanoTime();
                        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
                             javax.net.ssl.SSLSocket socket = AndroidRelayNetworkSelector.connect(getApplicationContext(), options, dial)) {
                            double tlsMs = (System.nanoTime() - start) / 1e6;
                            JSONObject directory = AndroidRelay.exchange(socket, AndroidRelay.request(options, "directory"));
                            measurements.put(new JSONObject().put("round", round).put("tlsMs", tlsMs)
                                .put("directoryTotalMs", (System.nanoTime() - start) / 1e6)
                                .put("localAddress", socket.getLocalAddress().getHostAddress())
                                .put("deviceCount", directory.getJSONArray("devices").length()));
                        }
                    }
                    android.net.ConnectivityManager manager = (android.net.ConnectivityManager)getSystemService(CONNECTIVITY_SERVICE);
                    android.net.Network active = manager.getActiveNetwork();
                    JSONObject bound = new JSONObject();
                    if (active != null) {
                        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
                            long start = System.nanoTime();
                            javax.net.ssl.SSLSocket socket = AndroidRelay.connectBoundSocket(options, active, dial, raw -> { });
                            bound.put("ok", true).put("tlsMs", (System.nanoTime() - start) / 1e6)
                                .put("localAddress", socket.getLocalAddress().getHostAddress());
                        } catch (Exception failure) { bound.put("ok", false).put("failure", failure.getClass().getSimpleName()); }
                    }
                    JSONObject cancellation = singleNetworkCancellationProbe();
                    JSONObject result = new JSONObject().put("complete", cancellation.getBoolean("ok")).put("uid", android.os.Process.myUid())
                        .put("api", android.os.Build.VERSION.SDK_INT).put("measurements", measurements)
                        .put("activeNetworkBindingProbe", bound)
                        .put("singleNetworkTimeoutProbe", cancellation)
                        .put("scope", "Real app UID, product TLS pinning and directory; no host takeover, screen/input, or video latency measurement");
                    byte[] reply = result.toString().getBytes(StandardCharsets.UTF_8);
                    DataOutputStream output = new DataOutputStream(control.getOutputStream());
                    output.writeInt(reply.length); output.write(reply); output.flush();
                    runOnUiThread(() -> label.setText("RemoteDesk network test passed (3 rounds)"));
                }
            } catch (Exception failure) {
                String safe = failure.getClass().getSimpleName();
                runOnUiThread(() -> label.setText("Network test: " + safe));
            } finally {
                finished = true;
                deleteFile("relay-path-ready.json");
            }
        }, "RelayNetworkProbe").start();
    }

    private JSONObject singleNetworkCancellationProbe() throws Exception {
        Socket pending = new Socket();
        java.util.concurrent.CountDownLatch release = new java.util.concurrent.CountDownLatch(1);
        java.util.concurrent.CountDownLatch done = new java.util.concurrent.CountDownLatch(1);
        long started = System.nanoTime();
        boolean timedOut = false;
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            try {
                new AndroidRelayNetworkSelector.Selector(System::nanoTime, 150).connect("timeout-fixture", java.util.List.of(), dial,
                    (path, owner) -> {
                        owner.add(pending);
                        try {
                            while (release.getCount() != 0) {
                                try { release.await(); } catch (InterruptedException ignored) { }
                            }
                            owner.checkOpen();
                            return pending;
                        } finally { done.countDown(); }
                    });
            } catch (java.net.SocketTimeoutException expected) { timedOut = true; }
            double ms = (System.nanoTime() - started) / 1e6;
            return new JSONObject().put("ok", timedOut && pending.isClosed() && ms < 1500)
                .put("timeoutMs", ms).put("pendingSocketClosed", pending.isClosed());
        } finally {
            release.countDown();
            done.await(2, java.util.concurrent.TimeUnit.SECONDS);
            pending.close();
        }
    }

    @Override public void onDestroy() {
        if (!finished && listener != null) try { listener.close(); } catch (Exception ignored) { }
        super.onDestroy();
    }
}
