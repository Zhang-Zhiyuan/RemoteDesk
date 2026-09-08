package com.remotedesk.agent;

import android.app.Activity;
import android.os.Bundle;
import android.widget.TextView;
import org.json.JSONArray;
import org.json.JSONObject;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

/** Isolated discovery checks: no AUTH, input injection, capture, or production preferences. */
public final class DiscoveryProbeActivity extends Activity {
    private final JSONArray checks = new JSONArray();
    private final JSONObject report = new JSONObject();
    private volatile AndroidLanDiscovery pending;
    private volatile boolean destroyed;
    private TextView status;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        status = new TextView(this); status.setText("RemoteDesk discovery test — no remote input"); setContentView(status);
        getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        String target = getIntent().getStringExtra("target");
        new Thread(() -> {
            try {
                report.put("scope","Actual Android discovery transport and JSON parser; no credentials or remote input");
                report.put("sdk",android.os.Build.VERSION.SDK_INT).put("checks",checks);
                parserChecks();
                try (DatagramSocket fixture = new DatagramSocket(new InetSocketAddress("127.0.0.9",40566))) {
                    fixture.setSoTimeout(250);
                    AtomicBoolean done = new AtomicBoolean();
                    Thread replies = new Thread(() -> {
                        while (!done.get() && !destroyed) try {
                            DatagramPacket request = new DatagramPacket(new byte[128],128); fixture.receive(request);
                            if (!RemoteDeskProtocol.DISCOVERY_REQUEST.equals(new String(request.getData(),0,request.getLength(),StandardCharsets.UTF_8))) continue;
                            byte[] reply = payload(45678,true).put("Address","203.0.113.99").toString().getBytes(StandardCharsets.UTF_8);
                            fixture.send(new DatagramPacket(reply,reply.length,request.getAddress(),request.getPort()));
                        } catch (java.net.SocketTimeoutException ignored) { }
                        catch (Exception ex) { if (!done.get()) break; }
                    },"DiscoveryFixture");
                    replies.start();
                    try {
                        pending = new AndroidLanDiscovery();
                        List<AndroidLanDevice> found = pending.scan(this,"127.0.0.9",Collections.emptyList());
                        check("UDP replies discover a custom TCP port",found.stream().anyMatch(d->d.host.equals("127.0.0.9") && d.port==45678 && d.listening));
                        check("Repeated UDP rounds deduplicate the same endpoint",found.stream().filter(d->d.port==45678).count()==1);
                        check("A JSON Address field cannot redirect the target",found.stream().noneMatch(d->d.host.equals("203.0.113.99")));
                    } finally { pending.close(); done.set(true); fixture.close(); replies.join(1500); }
                }
                tcpFallbackChecks();
                pending = new AndroidLanDiscovery();
                long started = android.os.SystemClock.elapsedRealtime();
                List<AndroidLanDevice> nearby = pending.scan(this,null,Collections.emptyList());
                check("Automatic interface/broadcast discovery is bounded",android.os.SystemClock.elapsedRealtime()-started<5500 && nearby.size()<=64);
                JSONArray nearbySummary = new JSONArray();
                for (AndroidLanDevice device : nearby) nearbySummary.put(new JSONObject().put("host",device.host).put("port",device.port).put("platform",device.platform).put("listening",device.listening));
                report.put("nearby",nearbySummary);
                if (target!=null && !target.isEmpty()) {
                    pending = new AndroidLanDiscovery();
                    List<AndroidLanDevice> hosts = pending.scan(this,target,Collections.emptyList());
                    JSONArray endpoints = new JSONArray();
                    for (AndroidLanDevice device : hosts) endpoints.put(new JSONObject().put("host",device.host).put("port",device.port).put("platform",device.platform).put("listening",device.listening).put("advertised",device.advertised));
                    report.put("explicitTarget",endpoints);
                    check("Explicitly authorized real target reports a listening RemoteDesk endpoint",hosts.stream().anyMatch(d->d.listening));
                }
                pending = new AndroidLanDiscovery(); pending.close();
                check("Cancellation precedes DNS and sockets",pending.scan(this,"do-not-resolve.invalid",Collections.emptyList()).isEmpty());
                report.put("completed",true); save();
                runOnUiThread(()->status.setText("Discovery checks passed: " + checks.length()));
            } catch (Exception | AssertionError error) {
                try { report.put("failure",error.getClass().getSimpleName()+": "+error.getMessage()); save(); }
                catch (Exception ignored) { }
                runOnUiThread(()->status.setText("Discovery test failed; inspect the app-private report."));
            } finally { if(pending!=null)pending.close(); }
        },"DiscoveryChecks").start();
    }

    private JSONObject payload(Object port,Object running) throws Exception {
        return new JSONObject().put("Type",RemoteDeskProtocol.DISCOVERY_RESPONSE_TYPE).put("MachineName","Discovery fixture")
            .put("Platform","Windows").put("Port",port).put("IsHostRunning",running);
    }
    private AndroidLanDevice parse(String text) throws Exception {
        byte[] bytes=text.getBytes(StandardCharsets.UTF_8);
        return AndroidLanDiscovery.parse(new DatagramPacket(bytes,bytes.length,InetAddress.getByName("127.0.0.9"),40566));
    }
    private void tcpFallbackChecks() throws Exception {
        // Different owned loopback alias: no UDP fixture, no production host
        // listener is replaced. Only RDK1 is sent; no authenticated session exists.
        try (java.net.ServerSocket server = new java.net.ServerSocket()) {
            server.bind(new InetSocketAddress("127.0.0.10",40567)); server.setSoTimeout(7000);
            java.util.concurrent.atomic.AtomicInteger received = new java.util.concurrent.atomic.AtomicInteger(-2);
            Thread banner = new Thread(() -> {
                try (java.net.Socket client = server.accept()) {
                    client.setSoTimeout(1500); client.getOutputStream().write(new byte[]{'R','D','K','1'});
                    received.set(client.getInputStream().read());
                } catch (Exception ignored) { }
            },"DiscoveryBannerFixture");
            banner.start();
            try {
                pending = new AndroidLanDiscovery();
                List<AndroidLanDevice> found = pending.scan(this,"127.0.0.10",Collections.emptyList());
                check("Blocked or absent UDP falls back to a product TCP port",found.stream().anyMatch(d->d.port==40567 && !d.advertised));
                banner.join(2000);
                check("TCP discovery closes without sending AUTH or other bytes",received.get()==-1);
            } finally { pending.close(); server.close(); banner.join(1500); }
        }
    }
    private void parserChecks() throws Exception {
        check("Custom advertised port is retained",parse(payload(45678,true).toString()).port==45678);
        check("Stopped host remains visibly stopped",!parse(payload(45678,false).toString()).listening);
        for(Object bad:new Object[]{0,-1,65536,123.5,"45678",true})
            check("Reject invalid port type/value: "+bad,parse(payload(bad,true).toString())==null);
        check("Reject boolean encoded as string",parse(payload(45678,"false").toString())==null);
        check("Reject other discovery protocols",parse(payload(45678,true).put("Type","Other.Discovery").toString())==null);
        check("Reject truncated JSON",parse("{\"Type\":")==null);
        check("Legacy host without running flag is accepted",parse(new JSONObject().put("Type",RemoteDeskProtocol.DISCOVERY_RESPONSE_TYPE).put("Port",12345).toString()).listening);
        StringBuilder nested = new StringBuilder();
        for (int i=0;i<1000;i++) nested.append("{\"x\":");
        nested.append('0'); for (int i=0;i<1000;i++) nested.append('}');
        check("Reject deeply nested payload before parsing",parse(nested.toString())==null);
        byte[] invalid={(byte)0xc3,(byte)0x28};
        check("Reject malformed UTF-8",AndroidLanDiscovery.parse(new DatagramPacket(invalid,invalid.length,InetAddress.getByName("127.0.0.9"),40566))==null);
    }
    private void check(String name,boolean passed) throws Exception {
        checks.put(new JSONObject().put("name",name).put("passed",passed)); save();
        if(!passed)throw new AssertionError(name);
    }
    private void save() throws Exception {
        Files.write(new java.io.File(getFilesDir(),"discovery-state.json").toPath(),report.toString(2).getBytes(StandardCharsets.UTF_8));
    }
    @Override public void onDestroy() { destroyed=true; if(pending!=null)pending.close(); super.onDestroy(); }
}
