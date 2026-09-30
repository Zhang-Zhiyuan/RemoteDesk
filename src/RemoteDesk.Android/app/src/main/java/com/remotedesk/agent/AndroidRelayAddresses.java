package com.remotedesk.agent;

import java.net.Inet6Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.List;
import org.json.JSONArray;
import org.json.JSONObject;

/** Literal unicast hints only; never DNS-resolve or connect while listing devices. */
final class AndroidRelayAddresses {
    static final int MAX_ADDRESSES = 8;

    static boolean usable(String value) {
        return normalizeAddress(value) != null;
    }

    static String normalizeAddress(String value) {
        if (usableIpv4(value)) return value;
        if (value == null || value.length() > 39 || !value.contains(":") || !value.matches("[0-9a-fA-F:]+")) return null;
        try {
            // The strict numeric grammar above prevents name-service lookups.
            InetAddress address = InetAddress.getByName(value);
            if (!(address instanceof Inet6Address) || address.isAnyLocalAddress() || address.isLoopbackAddress() ||
                    address.isLinkLocalAddress() || address.isMulticastAddress() || address.isSiteLocalAddress()) return null;
            byte[] bytes = address.getAddress();
            boolean compatible = true;
            for (int i = 0; i < 12; i++) compatible &= bytes[i] == 0;
            if (compatible) return null;
            return canonicalIpv6(bytes);
        } catch (java.net.UnknownHostException ignored) { return null; }
    }

    private static String canonicalIpv6(byte[] bytes) {
        int[] words = new int[8];
        for (int i = 0; i < 8; i++) words[i] = ((bytes[2*i] & 255) << 8) | (bytes[2*i+1] & 255);
        int best = -1, length = 1;
        for (int i = 0; i < 8;) {
            if (words[i] != 0) { i++; continue; }
            int start = i;
            while (i < 8 && words[i] == 0) i++;
            if (i - start > length) { best = start; length = i - start; }
        }
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < 8;) {
            if (i == best) { result.append("::"); i += length; }
            else {
                if (result.length() > 0 && result.charAt(result.length()-1) != ':') result.append(':');
                result.append(Integer.toHexString(words[i++]));
            }
        }
        return result.toString();
    }

    static String endpoint(String address, int port) {
        return address.contains(":") ? "[" + address + "]:" + port : address + ":" + port;
    }

    private static boolean usableIpv4(String value) {
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
        for (int i = 0; i < Math.min(32, values.size()); i++) {
            String value = normalizeAddress(values.get(i));
            if (value != null && !result.contains(value)) result.add(value);
        }
        List<String> bounded = new ArrayList<>(result.subList(0, Math.min(MAX_ADDRESSES, result.size())));
        if (bounded.size() == MAX_ADDRESSES) {
            boolean ipv6 = bounded.get(0).contains(":");
            if (bounded.stream().allMatch(value -> value.contains(":") == ipv6))
                for (String value : result) if (value.contains(":") != ipv6) {
                    bounded.set(MAX_ADDRESSES - 1, value); break;
                }
        }
        return Collections.unmodifiableList(bounded);
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
                        String value = localAddress(address.getAddress());
                        if (value != null) found.add(value);
                    }
                } catch (java.net.SocketException | SecurityException ignored) { }
            }
        } catch (java.net.SocketException | SecurityException ignored) { }
        Collections.sort(found);
        return normalize(found);
    }

    static String localAddress(InetAddress address) {
        // Some Android interfaces attach a scope even to a global IPv6 address.
        // Strip our own local scope only; scopes received from peers stay invalid.
        return normalizeAddress(address.getHostAddress().split("%", 2)[0]);
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
