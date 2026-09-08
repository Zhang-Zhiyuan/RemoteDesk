package com.remotedesk.agent;

import java.util.ArrayList;
import java.util.List;
import java.util.LinkedHashMap;

/** Discovery is a hint, not authenticated device identity. Never stores a password. */
final class AndroidLanDevice {
    final String host, name, platform, deviceId;
    final int port;
    final boolean listening, advertised;

    AndroidLanDevice(String host, int port, String name, String platform, boolean listening, boolean advertised) {
        this(host, port, name, platform, listening, advertised, "");
    }
    AndroidLanDevice(String host, int port, String name, String platform, boolean listening, boolean advertised, String deviceId) {
        this.host = AndroidConnectionHistory.normalizeHost(host);
        if (port < 1 || port > 65535) throw new IllegalArgumentException("Invalid advertised port");
        this.port = port;
        this.name = label(name, this.host);
        this.platform = label(platform, "RemoteDesk");
        this.listening = listening; this.advertised = advertised;
        this.deviceId = AndroidConnectionHistory.deviceIdentity(deviceId);
    }

    String address() { return formatAddress(host, port); }
    String description() { return name + " · " + platform + "\n" + address() + (listening ? " · 可连接" : " · 被控未启动"); }
    static String formatAddress(String host, int port) { return (host.contains(":") ? "[" + host + "]" : host) + ":" + port; }

    static List<AndroidLanDevice> collapseAliases(List<AndroidLanDevice> devices) {
        LinkedHashMap<String, AndroidLanDevice> unique = new LinkedHashMap<>();
        for (AndroidLanDevice device : devices) {
            String key = device.deviceId.isEmpty() ? "ep:" + device.address() : "id:" + device.deviceId;
            AndroidLanDevice previous = unique.get(key);
            if (previous == null || !previous.listening && device.listening) unique.put(key, device);
        }
        return new ArrayList<>(unique.values());
    }

    static boolean explicitPort(String value) {
        String text = value == null ? "" : value.trim();
        if (text.startsWith("[")) {
            int end = text.indexOf(']');
            return end > 0 && text.length() > end + 2 && text.charAt(end + 1) == ':';
        }
        int colon = text.indexOf(':');
        return colon > 0 && colon == text.lastIndexOf(':') && colon < text.length() - 1;
    }

    static AndroidConnectionHistory.Node exact(List<AndroidConnectionHistory.Node> nodes, AndroidLanDevice device) {
        for (AndroidConnectionHistory.Node node : nodes)
            if (!node.relay() && node.host.equals(device.host) && node.port == device.port) return node;
        return null;
    }

    static List<AndroidLanDevice> candidates(AndroidConnectionHistory.Node node, List<AndroidLanDevice> discovered) {
        List<AndroidLanDevice> exact = new ArrayList<>(), sameAddress = new ArrayList<>(), sameName = new ArrayList<>();
        if (node.relay()) return exact;
        List<AndroidLanDevice> sameIdentity = new ArrayList<>();
        for (AndroidLanDevice device : discovered) {
            if (!device.listening) continue;
            if (!node.deviceId.isEmpty() && node.deviceId.equals(device.deviceId)) sameIdentity.add(device);
            if (node.host.equals(device.host)) {
                if (node.port == device.port) exact.add(device);
                else sameAddress.add(device);
            } else if (device.advertised && !node.name.isEmpty() && node.name.equalsIgnoreCase(device.name)) {
                sameName.add(device);
            }
        }
        // The saved endpoint always wins. Names are not unique; the UI must ask
        // before changing an address, even when there is only one name match.
        return !sameIdentity.isEmpty() ? sameIdentity : !exact.isEmpty() ? exact : !sameAddress.isEmpty() ? sameAddress : sameName;
    }

    static String label(String value, String fallback) {
        if (value == null) return fallback;
        StringBuilder clean = new StringBuilder();
        for (int i = 0; i < value.length() && clean.length() < 96; i++) {
            char c = value.charAt(i);
            if (!Character.isISOControl(c) && Character.getType(c) != Character.FORMAT) clean.append(c);
        }
        String text = clean.toString().trim();
        return text.isEmpty() ? fallback : text;
    }
}
