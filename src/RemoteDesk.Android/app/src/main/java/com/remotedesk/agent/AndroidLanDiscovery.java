package com.remotedesk.agent;

import android.content.Context;
import android.net.wifi.WifiManager;
import org.json.JSONObject;
import java.io.IOException;
import java.io.InputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.Enumeration;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

/** Active discovery uses an ephemeral socket; it never takes the host's UDP listener. */
final class AndroidLanDiscovery implements AutoCloseable {
    static final int[] DISCOVERY_PORTS = {56566, 40566};
    static final int[] COMPATIBLE_HOST_PORTS = {56565, 40565, 40567};
    static final int MAX_DEVICES = 64;
    private static final byte[] REQUEST = RemoteDeskProtocol.DISCOVERY_REQUEST.getBytes(StandardCharsets.UTF_8);
    private volatile boolean cancelled;
    private DatagramSocket udp;
    private Socket tcp;
    private WifiManager.MulticastLock multicast;

    List<AndroidLanDevice> scan(Context context, String target, List<AndroidConnectionHistory.Node> history) throws Exception {
        if (cancelled) return Collections.emptyList();
        LinkedHashSet<InetAddress> destinations = new LinkedHashSet<>();
        Set<String> localAddresses = new LinkedHashSet<>();
        Set<InetAddress> explicitAddresses = new LinkedHashSet<>();
        Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
        while (interfaces != null && interfaces.hasMoreElements()) {
            NetworkInterface network = interfaces.nextElement();
            if (!network.isUp()) continue;
            for (InterfaceAddress address : network.getInterfaceAddresses()) {
                localAddresses.add(address.getAddress().getHostAddress());
                if (!network.isLoopback() && address.getAddress() instanceof Inet4Address && address.getBroadcast() != null)
                    destinations.add(address.getBroadcast());
            }
        }
        if (target != null && !target.isEmpty()) {
            // Only an explicitly entered hostname may trigger DNS. Automatic
            // refresh does not resolve twenty old/offline names in the background.
            for (InetAddress address : InetAddress.getAllByName(target)) {
                if (!address.isAnyLocalAddress() && !address.isMulticastAddress()) explicitAddresses.add(address);
                if (explicitAddresses.size() >= 4) break;
            }
            destinations.clear(); destinations.addAll(explicitAddresses);
        } else {
            destinations.add(InetAddress.getByAddress(new byte[]{-1,-1,-1,-1}));
            for (AndroidConnectionHistory.Node node : history) {
                if (node.relay()) continue;
                InetAddress address = literalIpv4(node.host);
                if (address != null) destinations.add(address);
            }
        }
        if (cancelled) return Collections.emptyList();
        LinkedHashMap<String, AndroidLanDevice> devices = new LinkedHashMap<>();
        try {
            acquireMulticast(context);
            DatagramSocket socket = new DatagramSocket();
            synchronized (this) { udp = socket; if (cancelled) socket.close(); }
            if (cancelled) return Collections.emptyList();
            socket.setBroadcast(true); socket.setSoTimeout(120);
            long started = System.nanoTime();
            sendRequests(socket, destinations);
            boolean repeated = false;
            byte[] buffer = new byte[8193];
            while (!cancelled && System.nanoTime() - started < 1_800_000_000L) {
                if (!repeated && System.nanoTime() - started > 700_000_000L) {
                    repeated = true; sendRequests(socket, destinations);
                }
                DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                try { socket.receive(packet); }
                catch (SocketTimeoutException ignored) { continue; }
                catch (java.net.PortUnreachableException ignored) { continue; }
                if (cancelled) break;
                if (!explicitAddresses.isEmpty() && !explicitAddresses.contains(packet.getAddress())) continue;
                if (explicitAddresses.isEmpty() && (packet.getAddress().isLoopbackAddress() ||
                        localAddresses.contains(packet.getAddress().getHostAddress()))) continue;
                AndroidLanDevice device = parse(packet);
                if (device != null && (devices.containsKey(device.address()) || devices.size() < MAX_DEVICES))
                    devices.put(device.address(), device);
            }
            // No all-port/subnet TCP scan. A user-selected target can fall back
            // to the three product ports plus its own saved ports. Read only the
            // protocol banner: do not send AUTH, passwords or input, or take over.
            if (!cancelled && !explicitAddresses.isEmpty() && devices.values().stream().noneMatch(d -> d.listening)) {
                LinkedHashSet<Integer> ports = new LinkedHashSet<>();
                for (AndroidConnectionHistory.Node node : history)
                    if (!node.relay() && node.host.equals(target) && ports.size() < 4) ports.add(node.port);
                for (int port : COMPATIBLE_HOST_PORTS) ports.add(port);
                for (InetAddress address : explicitAddresses) for (int port : ports) {
                    if (cancelled || System.nanoTime() - started > 5_000_000_000L) break;
                    if (probe(address, port)) {
                        AndroidLanDevice device = new AndroidLanDevice(address.getHostAddress(), port, "", "RemoteDesk", true, false);
                        devices.put(device.address(), device);
                    }
                }
            }
            List<AndroidLanDevice> result = new ArrayList<>(devices.values());
            result.sort(Comparator.comparing((AndroidLanDevice d) -> !d.listening).thenComparing(d -> d.name).thenComparing(AndroidLanDevice::address));
            return result;
        } finally { releaseResources(); }
    }

    private void sendRequests(DatagramSocket socket, Set<InetAddress> addresses) {
        int sent = 0;
        for (InetAddress address : addresses) for (int port : DISCOVERY_PORTS) {
            if (cancelled || ++sent > 80) return;
            try { socket.send(new DatagramPacket(REQUEST, REQUEST.length, address, port)); }
            catch (IOException ignored) { /* One absent/VPN route must not block other interfaces. */ }
        }
    }

    private boolean probe(InetAddress address, int port) {
        try (Socket socket = new Socket()) {
            synchronized (this) { tcp = socket; if (cancelled) socket.close(); }
            if (cancelled) return false;
            socket.connect(new InetSocketAddress(address, port), 250);
            socket.setSoTimeout(250);
            return readBanner(socket.getInputStream());
        } catch (IOException ignored) { return false; }
        finally { synchronized (this) { tcp = null; } }
    }

    static boolean readBanner(InputStream input) throws IOException {
        for (byte expected : new byte[]{'R','D','K','1'}) if (input.read() != expected) return false;
        return true;
    }

    static AndroidLanDevice parse(DatagramPacket packet) {
        if (packet.getLength() < 2 || packet.getLength() > 8192) return null;
        try {
            String json = StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(packet.getData(), packet.getOffset(), packet.getLength())).toString();
            if (!safeJsonEnvelope(json)) return null;
            JSONObject value = new JSONObject(json);
            if (!RemoteDeskProtocol.DISCOVERY_RESPONSE_TYPE.equals(value.opt("Type"))) return null;
            Object port = value.opt("Port"), running = value.opt("IsHostRunning");
            if (!(port instanceof Integer || port instanceof Long)) return null;
            long number = ((Number) port).longValue();
            if (number < 1 || number > 65535 || (running != null && !(running instanceof Boolean))) return null;
            if (packet.getAddress().isAnyLocalAddress() || packet.getAddress().isMulticastAddress()) return null;
            // Never trust an Address/Host field in unauthenticated JSON. The
            // reply's network source and its advertised TCP port form the target.
            return new AndroidLanDevice(packet.getAddress().getHostAddress(), (int) number,
                value.opt("MachineName") instanceof String ? (String) value.opt("MachineName") : "",
                value.opt("Platform") instanceof String ? (String) value.opt("Platform") : "",
                running == null || (Boolean) running, true,
                value.opt("DeviceId") instanceof String ? (String)value.opt("DeviceId") : "");
        } catch (Exception ignored) { return null; }
    }

    static boolean safeJsonEnvelope(String json) {
        if (json == null || json.length() > 8192 || !json.trim().startsWith("{")) return false;
        int depth = 0; boolean quoted = false, escaped = false;
        for (int i = 0; i < json.length(); i++) {
            char c = json.charAt(i);
            if (quoted) {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
            } else if (c == '"') quoted = true;
            else if (c == '{' || c == '[') { if (++depth > 8) return false; }
            else if (c == '}' || c == ']') { if (--depth < 0) return false; }
        }
        // JSONObject has no nesting limit on older Android releases. Reject a
        // tiny but deeply nested hostile datagram before it can overflow a stack.
        return depth == 0 && !quoted;
    }

    static InetAddress literalIpv4(String host) {
        if (host == null) return null;
        String[] parts = host.split("\\.", -1);
        if (parts.length != 4) return null;
        byte[] bytes = new byte[4];
        try {
            for (int i = 0; i < 4; i++) {
                if (!parts[i].matches("[0-9]{1,3}")) return null;
                int value = Integer.parseInt(parts[i]);
                if (value > 255) return null;
                bytes[i] = (byte) value;
            }
            InetAddress result = InetAddress.getByAddress(bytes);
            return result.isAnyLocalAddress() || result.isMulticastAddress() || host.equals("255.255.255.255") ? null : result;
        } catch (Exception ignored) { return null; }
    }

    private synchronized void acquireMulticast(Context context) {
        if (cancelled) return;
        try {
            WifiManager wifi = (WifiManager) context.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wifi != null) {
                multicast = wifi.createMulticastLock("RemoteDeskActiveDiscovery");
                multicast.setReferenceCounted(false); multicast.acquire();
            }
        } catch (RuntimeException ignored) { /* Directed replies can still work. */ }
    }

    private synchronized void releaseResources() {
        if (udp != null) { udp.close(); udp = null; }
        if (tcp != null) { try { tcp.close(); } catch (IOException ignored) { } tcp = null; }
        if (multicast != null) {
            try { if (multicast.isHeld()) multicast.release(); } catch (RuntimeException ignored) { }
            multicast = null;
        }
    }
    @Override public synchronized void close() { cancelled = true; releaseResources(); }
}
