package com.remotedesk.agent;

import android.app.Activity;
import android.os.Bundle;
import android.widget.TextView;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;
import org.json.JSONArray;
import org.json.JSONObject;

/** Owned runtime sockets only; no capture, input, saved credentials or real clipboard. */
public final class Ipv6ProbeActivity extends Activity {
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        TextView text = new TextView(this); text.setText("IPv4 / IPv6 encrypted transport test"); setContentView(text);
        new Thread(() -> {
            JSONObject report = new JSONObject(); JSONArray cases = new JSONArray();
            try {
                for (String address : new String[]{"127.0.0.1", "::1"}) {
                    long start = System.nanoTime();
                    try (ServerSocket listener = new ServerSocket()) {
                        RemoteDeskHostServer.bindListener(listener, 0);
                        listener.setSoTimeout(5000);
                        byte[] payload = ("双栈中文 clipboard / file chunk " + address).getBytes(StandardCharsets.UTF_8);
                        FutureTask<Boolean> receive = new FutureTask<>(() -> {
                            try (Socket peer = listener.accept()) {
                                peer.setSoTimeout(5000);
                                RemoteDeskTransport.SecureSession session = RemoteDeskTransport.authenticateServer(
                                    peer.getInputStream(), peer.getOutputStream(), "isolated-ipv6-fixture");
                                if (session == null) throw new IllegalStateException("Authentication failed");
                                RemoteDeskTransport.ProtocolMessage message = RemoteDeskTransport.readMessage(peer.getInputStream(), session);
                                RemoteDeskTransport.writeMessage(peer.getOutputStream(), RemoteDeskProtocol.MESSAGE_CONTROL,
                                    message.payload, session, new Object());
                                return Arrays.equals(payload, message.payload);
                            }
                        });
                        Thread server = new Thread(receive, "IPv6-owned-server"); server.start();
                        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
                                Socket client = AndroidTcpConnector.connect(address, listener.getLocalPort(), 5000, dial, socket -> {})) {
                            client.setSoTimeout(5000);
                            RemoteDeskTransport.SecureSession session = RemoteDeskTransport.authenticateClient(
                                client.getInputStream(), client.getOutputStream(), "isolated-ipv6-fixture");
                            RemoteDeskTransport.writeMessage(client.getOutputStream(), RemoteDeskProtocol.MESSAGE_CONTROL,
                                payload, session, new Object());
                            if (!Arrays.equals(payload, RemoteDeskTransport.readMessage(client.getInputStream(), session).payload)
                                    || !receive.get(6, TimeUnit.SECONDS)) throw new IllegalStateException("Encrypted echo mismatch");
                        } finally { server.join(6000); }
                        cases.put(new JSONObject().put("address", address).put("passed", true)
                            .put("authenticationAndEchoMs", (System.nanoTime()-start)/1e6));
                    }
                }
                report.put("passed", true).put("cases", cases);
            } catch (Exception failure) {
                try { report.put("passed", false).put("cases", cases).put("error", failure.toString()); } catch (Exception ignored) { }
            }
            try (java.io.FileOutputStream output = openFileOutput("ipv6-probe.json", MODE_PRIVATE)) {
                output.write(report.toString().getBytes(StandardCharsets.UTF_8));
            } catch (Exception ignored) { }
            runOnUiThread(() -> text.setText(report.toString()));
        }, "IPv6-owned-probe").start();
    }
}
