package com.remotedesk.agent;

import java.net.Inet4Address;
import java.net.NetworkInterface;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.List;
import org.json.JSONArray;
import org.json.JSONObject;

/** Literal IPv4 hints only; never DNS-resolve or connect while listing devices. */
final class AndroidRelayAddresses {
    static final int MAX_ADDRESSES = 8;

    static boolean usable(String value) {
        if (value == null || value.length() < 7 || value.length() > 15) return false;
        String[] parts = value.split("\\.", -1);
        if (parts.length != 4) return false;
        int[] bytes = new int[4];
        for (int i = 0; i < 4; i++) {
            if (!parts[i].matches("0|[1-9][0-9]{0,2}")) return false;
            bytes[i] = Integer.parseInt(parts[i]);
            if (bytes[i] > 255) return false;
        }
        return bytes[0] > 0 && bytes[0] < 224 && bytes[0] != 127 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    static List<String> normalize(List<String> values) {
        List<String> result = new ArrayList<>();
        for (int i = 0; i < Math.min(32, values.size()) && result.size() < MAX_ADDRESSES; i++) {
            String value = values.get(i);
            if (usable(value) && !result.contains(value)) result.add(value);
        }
        return Collections.unmodifiableList(result);
    }

    static List<String> local() {
        List<String> found = new ArrayList<>();
        try {
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            int count = 0;
            while (interfaces != null && interfaces.hasMoreElements() && count++ < 64) {
                NetworkInterface network = interfaces.nextElement();
                try {
                    if (!network.isUp() || network.isLoopback()) continue;
                    for (java.net.InterfaceAddress address : network.getInterfaceAddresses()) {
                        if (address.getAddress() instanceof Inet4Address)
                            found.add(address.getAddress().getHostAddress());
                    }
                } catch (java.net.SocketException | SecurityException ignored) { }
            }
        } catch (java.net.SocketException | SecurityException ignored) { }
        Collections.sort(found);
        return normalize(found);
    }

    static JSONObject attach(JSONObject message, int port) throws Exception {
        return attach(message, port, local());
    }

    static JSONObject attach(JSONObject message, int port, List<String> addresses) throws Exception {
        return message.put("directAddresses", new JSONArray(normalize(addresses))).put("directPort", port);
    }

    static int port(JSONObject message) {
        Object value = message.opt("directPort");
        if (!(value instanceof Integer || value instanceof Long)) return 0;
        long port = ((Number) value).longValue();
        return port > 0 && port <= 65535 ? (int) port : 0;
    }

    static List<String> parse(JSONObject message) {
        JSONArray values = message.optJSONArray("directAddresses");
        if (port(message) == 0 || values == null) return Collections.emptyList();
        List<String> found = new ArrayList<>();
        for (int i = 0; i < Math.min(32, values.length()); i++) {
            Object value = values.opt(i);
            if (value instanceof String) found.add((String) value);
        }
        return normalize(found);
    }
}
