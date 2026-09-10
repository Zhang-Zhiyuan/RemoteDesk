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
                    if (value.optBoolean("addressReportTest", false)) {
                        JSONObject result = addressReportProbe(options);
                        byte[] reply = result.toString().getBytes(StandardCharsets.UTF_8);
                        DataOutputStream output = new DataOutputStream(control.getOutputStream());
                        output.writeInt(reply.length); output.write(reply); output.flush();
                        runOnUiThread(() -> label.setText("RemoteDesk IP report test passed"));
                        return;
                    }
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

    private JSONObject addressReportProbe(AndroidRelay.Options configuration) throws Exception {
        AndroidRelay.Options options = configuration.target(java.util.UUID.randomUUID().toString());
        java.util.List<String> actual = AndroidRelayAddresses.local();
        if (actual.isEmpty()) throw new IllegalStateException("No local IPv4 address");
        java.util.concurrent.atomic.AtomicReference<java.util.List<String>> addresses =
            new java.util.concurrent.atomic.AtomicReference<>(actual);
        try (ServerSocket echo = new ServerSocket(0, 1, InetAddress.getByName("127.0.0.1"));
             AndroidRelay.HostConnector host = new AndroidRelay.HostConnector(getApplicationContext(), options,
                 echo.getLocalPort(), "Owned Android address probe", status -> { }, addresses::get)) {
            echo.setSoTimeout(30000);
            Thread echoThread = AndroidRelay.thread("AddressProbeEcho", () -> {
                try (Socket socket = echo.accept()) {
                    socket.setSoTimeout(30000);
                    byte[] buffer = new byte[4096];
                    for (int count; (count = socket.getInputStream().read(buffer)) > 0;)
                        socket.getOutputStream().write(buffer, 0, count);
                } catch (Exception ignored) { }
            });
            echoThread.start();
            host.start();
            awaitAddresses(options, actual, echo.getLocalPort());
            JSONArray stages = new JSONArray();
            try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
                 javax.net.ssl.SSLSocket viewer = AndroidRelayNetworkSelector.connect(getApplicationContext(), options, dial)) {
                AndroidRelay.exchange(viewer, AndroidRelay.request(options, "viewer").put("deviceId", options.deviceId));
                for (java.util.List<String> value : java.util.List.of(java.util.Collections.<String>emptyList(), actual)) {
                    addresses.set(value);
                    if (!host.requestAddressRefresh()) throw new IllegalStateException("Host not active");
                    awaitAddresses(options, value, echo.getLocalPort());
                    byte[] data = "address-change-中文".getBytes(StandardCharsets.UTF_8);
                    viewer.getOutputStream().write(data);
                    byte[] reply = new byte[data.length];
                    new DataInputStream(viewer.getInputStream()).readFully(reply);
                    if (!java.util.Arrays.equals(data, reply)) throw new IllegalStateException("Session interrupted");
                    stages.put(new JSONObject().put("advertisedCount", value.size()).put("sameSessionEcho", true));
                }
            }
            echoThread.join(1000);
            return new JSONObject().put("complete", true).put("platform", "Android")
                .put("api", android.os.Build.VERSION.SDK_INT).put("localAddresses", new JSONArray(actual))
                .put("customPort", echo.getLocalPort()).put("stages", stages)
                .put("scope", "Public relay; native product host/directory; simulated address removal/restoration. No NIC changes, screen or OS input.");
        }
    }

    private void awaitAddresses(AndroidRelay.Options options, java.util.List<String> expected, int port) throws Exception {
        for (int retry = 0; retry < 30; retry++) {
            for (AndroidRelay.Device device : AndroidRelay.listDevices(getApplicationContext(), options, socket -> { })) {
                if (device.deviceId.equals(options.deviceId) && device.directAddresses.equals(expected) &&
                    device.directPort == (expected.isEmpty() ? 0 : port)) return;
            }
            Thread.sleep(200);
        }
        throw new IllegalStateException("Address record did not update");
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
