package com.remotedesk.agent;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.UUID;

/** Bounded, versioned history model. Serialize ONLY into encrypted local storage. */
final class AndroidConnectionHistory {
    static final int LIMIT = 20;
    private static final int MAX_ENCODED_LENGTH = 1024 * 1024;
    private final List<Node> nodes = new ArrayList<>();

    static final class Node {
        final String id, host, relayDeviceId, password, relayConfiguration, name, remark, deviceId;
        final int port;
        final long connectedAt;
        final boolean autoPort;

        Node(String id, String host, int port, String relayDeviceId, String password,
             String relayConfiguration, String name, String remark, long connectedAt) {
            this(id, host, port, relayDeviceId, password, relayConfiguration, name, remark, connectedAt, "");
        }
        Node(String id, String host, int port, String relayDeviceId, String password,
             String relayConfiguration, String name, String remark, long connectedAt, String deviceId) {
            this(id, host, port, relayDeviceId, password, relayConfiguration, name, remark, connectedAt, deviceId, true);
        }
        Node(String id, String host, int port, String relayDeviceId, String password,
             String relayConfiguration, String name, String remark, long connectedAt, String deviceId, boolean autoPort) {
            this.id = UUID.fromString(id).toString();
            this.host = normalizeHost(host);
            if (port < 1 || port > 65535) throw new IllegalArgumentException("Invalid port");
            this.port = port;
            this.relayDeviceId = relayDeviceId.isEmpty() ? "" : UUID.fromString(relayDeviceId).toString();
            this.password = bounded(password, 4096);
            this.relayConfiguration = bounded(relayConfiguration, 8192);
            if (password.isEmpty() || relayDeviceId.isEmpty() != relayConfiguration.isEmpty())
                throw new IllegalArgumentException("Incomplete connection");
            this.name = bounded(name, 128);
            this.remark = bounded(remark.trim(), 64);
            this.connectedAt = Math.max(0, connectedAt);
            this.deviceId = deviceIdentity(deviceId);
            this.autoPort = autoPort;
        }

        boolean relay() { return !relayDeviceId.isEmpty(); }
        String address() { return (host.contains(":") ? "[" + host + "]" : host) + ":" + port; }
        String title() { return !remark.isEmpty() ? remark : !name.isEmpty() ? name : address(); }
        String key() { return host + "\n" + port + "\n" + relayDeviceId; }
        Node withRemark(String value) {
            return new Node(id, host, port, relayDeviceId, password, relayConfiguration, name, value, connectedAt, deviceId, autoPort);
        }
        @Override public String toString() { return "SavedConnection(<redacted>)"; }
    }

    List<Node> entries() { return Collections.unmodifiableList(new ArrayList<>(nodes)); }
    Node find(String id) {
        for (Node node : nodes) if (node.id.equals(id)) return node;
        return null;
    }

    Node remember(String previousId, String host, int port, String relayDeviceId, String password,
                  String relayConfiguration, String name, long now) {
        return remember(previousId, host, port, relayDeviceId, password, relayConfiguration, name, now, "");
    }
    Node remember(String previousId, String host, int port, String relayDeviceId, String password,
                  String relayConfiguration, String name, long now, String deviceId) {
        // A node deleted while its viewer connects must not reappear on reconnect.
        if (!previousId.isEmpty() && find(previousId) == null) return null;
        Node fresh = new Node(UUID.randomUUID().toString(), host, port, relayDeviceId,
            password, relayConfiguration, name, "", now, deviceId);
        Node existing = previousId.isEmpty() ? null : find(previousId);
        if (existing != null && identityConflicts(existing.deviceId, fresh.deviceId)) existing = null;
        if (existing == null && !fresh.deviceId.isEmpty()) for (Node candidate : nodes) {
            if (fresh.deviceId.equals(candidate.deviceId)) { existing = candidate; break; }
        }
        if (existing == null) for (Node candidate : nodes) {
            if (sameMachine(candidate, fresh)) { existing = candidate; break; }
        }
        if (existing != null) {
            fresh = new Node(existing.id, fresh.host, port, fresh.relayDeviceId, password,
                relayConfiguration, name.isEmpty() ? existing.name : name, existing.remark, now,
                fresh.deviceId.isEmpty() ? existing.deviceId : fresh.deviceId, existing.autoPort);
        }
        if (fresh.remark.isEmpty()) for (Node candidate : nodes) {
            if (sameMachine(candidate, fresh) && !candidate.remark.isEmpty()) {
                fresh = fresh.withRemark(candidate.remark); break;
            }
        }
        Node remembered = fresh;
        nodes.removeIf(node -> node.id.equals(remembered.id) || sameMachine(node, remembered));
        nodes.add(0, fresh);
        while (nodes.size() > LIMIT) nodes.remove(nodes.size() - 1);
        return fresh;
    }

    boolean rename(String id, String remark) {
        for (int i = 0; i < nodes.size(); i++) {
            if (nodes.get(i).id.equals(id)) { nodes.set(i, nodes.get(i).withRemark(remark)); return true; }
        }
        return false;
    }
    boolean remove(String id) { return nodes.removeIf(node -> node.id.equals(id)); }

    void autoPort(String id, boolean enabled) {
        for (int i = 0; i < nodes.size(); i++) {
            Node n = nodes.get(i);
            if (n.id.equals(id)) nodes.set(i, new Node(n.id, n.host, n.port, n.relayDeviceId, n.password,
                n.relayConfiguration, n.name, n.remark, n.connectedAt, n.deviceId, enabled));
        }
    }

    String encode() throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(bytes)) {
            out.writeInt(2); out.writeInt(nodes.size());
            for (Node node : nodes) {
                out.writeUTF(node.id); out.writeUTF(node.host); out.writeInt(node.port);
                out.writeUTF(node.relayDeviceId); out.writeUTF(node.password); out.writeUTF(node.relayConfiguration);
                out.writeUTF(node.name); out.writeUTF(node.remark); out.writeLong(node.connectedAt);
                out.writeUTF(node.deviceId);
                out.writeBoolean(node.autoPort);
            }
        }
        String encoded = Base64.getEncoder().encodeToString(bytes.toByteArray());
        if (encoded.length() > MAX_ENCODED_LENGTH) throw new IOException("History too large");
        return encoded;
    }

    static AndroidConnectionHistory decode(String encoded) throws IOException {
        AndroidConnectionHistory result = new AndroidConnectionHistory();
        if (encoded.isEmpty()) return result;
        if (encoded.length() > MAX_ENCODED_LENGTH) throw new IOException("History too large");
        try (DataInputStream in = new DataInputStream(new ByteArrayInputStream(Base64.getDecoder().decode(encoded)))) {
            int version = in.readInt();
            if (version != 1 && version != 2) throw new IOException("Unsupported history version");
            int count = in.readInt();
            if (count < 0 || count > LIMIT) throw new IOException("Invalid history count");
            for (int i = 0; i < count; i++) {
                Node node = new Node(in.readUTF(), in.readUTF(), in.readInt(), in.readUTF(),
                    in.readUTF(), in.readUTF(), in.readUTF(), in.readUTF(), in.readLong(), version == 2 ? in.readUTF() : "", version == 1 || in.readBoolean());
                for (Node existing : result.nodes) {
                    if (existing.id.equals(node.id) || (existing.key().equals(node.key()) && !identityConflicts(existing.deviceId, node.deviceId)))
                        throw new IOException("Duplicate history entry");
                }
                result.nodes.add(node);
            }
            if (in.read() != -1) throw new IOException("Unexpected history data");
            return result;
        } catch (IllegalArgumentException ex) { throw new IOException("Invalid history data"); }
    }

    static String deviceIdentity(String value) {
        if (value == null || !value.matches("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")) return "";
        UUID id = UUID.fromString(value);
        return id.getMostSignificantBits() == 0 && id.getLeastSignificantBits() == 0 ? "" : id.toString();
    }

    static boolean identityConflicts(String first, String second) {
        String left = deviceIdentity(first), right = deviceIdentity(second);
        return !left.isEmpty() && !right.isEmpty() && !left.equals(right);
    }

    private static boolean sameMachine(Node first, Node second) {
        return !identityConflicts(first.deviceId, second.deviceId) &&
            ((!first.deviceId.isEmpty() && first.deviceId.equals(second.deviceId)) || first.key().equals(second.key()));
    }

    static String normalizeHost(String value) {
        String host = value.trim();
        if (host.startsWith("[") && host.endsWith("]")) host = host.substring(1, host.length() - 1);
        if (host.isEmpty() || host.length() > 253 || host.matches(".*[\\s/@\\\\\\[\\]].*"))
            throw new IllegalArgumentException("Invalid host");
        // DNS is case-insensitive, but an IPv6 interface/zone name need not be.
        int zone = host.indexOf('%');
        if (zone >= 0) return host.substring(0, zone).toLowerCase(Locale.ROOT) + host.substring(zone);
        if (!host.contains(":") && host.endsWith(".")) host = host.substring(0, host.length() - 1);
        if (host.isEmpty()) throw new IllegalArgumentException("Invalid host");
        return host.toLowerCase(Locale.ROOT);
    }

    private static String bounded(String value, int length) {
        if (value == null || value.length() > length) throw new IllegalArgumentException("Field too long");
        return value;
    }
}
