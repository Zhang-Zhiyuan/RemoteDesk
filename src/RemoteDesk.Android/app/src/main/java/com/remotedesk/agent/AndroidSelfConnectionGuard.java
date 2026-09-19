package com.remotedesk.agent;

import java.net.InetAddress;
import java.net.Inet6Address;
import java.net.NetworkInterface;
import java.net.Socket;
import java.net.SocketException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Enumeration;
import java.util.List;

/** Viewer-only check. Relay host bridges legitimately connect to loopback. */
final class AndroidSelfConnectionGuard {
    static final String SELF_MESSAGE = "不能连接本机，请选择另一台设备。";
    static final String CHECK_FAILED_MESSAGE = "无法核验本机网络地址，已取消连接；请检查网络后重试。";

    static final class Rejected extends SecurityException {
        Rejected() { super(SELF_MESSAGE); }
    }
    static final class VerificationFailed extends SecurityException {
        VerificationFailed() { super(CHECK_FAILED_MESSAGE); }
    }

    private AndroidSelfConnectionGuard() { }

    interface AddressSource { List<InetAddress> read() throws SocketException; }

    static List<InetAddress> localAddresses() throws SocketException {
        List<InetAddress> result = new ArrayList<>();
        Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
        while (interfaces != null && interfaces.hasMoreElements()) {
            // Include every interface, not only the route selected for this socket.
            Enumeration<InetAddress> addresses = interfaces.nextElement().getInetAddresses();
            while (addresses.hasMoreElements()) result.add(addresses.nextElement());
        }
        if (result.isEmpty()) throw new SocketException("Local address enumeration unavailable");
        return result;
    }

    static void requireRemote(Socket connected, boolean allowIsolatedLoopbackFixture) {
        requireRemote(connected, allowIsolatedLoopbackFixture, AndroidSelfConnectionGuard::localAddresses);
    }

    static void requireResolvedRemote(InetAddress selected, boolean allowIsolatedLoopbackFixture) {
        // An unresolved endpoint is left to Socket.connect's normal DNS error handling.
        // This runs on the connection worker and is rechecked after connect.
        if (selected == null || allowIsolatedLoopbackFixture && isLoopback(selected)) return;
        if (isSelf(selected, java.util.Collections.emptyList())) throw new Rejected();
        requireNotOnLocalInterfaces(selected, AndroidSelfConnectionGuard::localAddresses);
    }

    static void requireRemote(Socket connected, boolean allowIsolatedLoopbackFixture, AddressSource addressSource) {
        InetAddress remote = connected.getInetAddress();
        InetAddress local = connected.getLocalAddress();
        if (remote == null || local == null || !connected.isConnected()) throw new VerificationFailed();
        // Only a separately compiled test APK may enable this. No intent or user setting.
        if (allowIsolatedLoopbackFixture && isLoopback(remote)) return;
        if (isSelf(remote, java.util.Collections.singletonList(local))) throw new Rejected();
        requireNotOnLocalInterfaces(remote, addressSource);
    }

    private static void requireNotOnLocalInterfaces(InetAddress remote, AddressSource addressSource) {
        try {
            List<InetAddress> addresses = addressSource.read();
            if (addresses == null || addresses.isEmpty()) throw new VerificationFailed();
            if (isSelf(remote, addresses)) throw new Rejected();
        } catch (SocketException | SecurityException failure) {
            if (failure instanceof Rejected) throw (Rejected) failure;
            throw new VerificationFailed();
        }
    }

    static boolean isSelf(InetAddress address, Iterable<InetAddress> localAddresses) {
        if (address == null) return true;
        byte[] remote = normalized(address);
        if (isLoopback(address) || allZero(remote)) return true;
        for (InetAddress local : localAddresses)
            if (local != null && Arrays.equals(remote, normalized(local)) && sameScope(address, local)) return true;
        return false;
    }

    private static boolean sameScope(InetAddress left, InetAddress right) {
        if (left instanceof Inet6Address && right instanceof Inet6Address && left.isLinkLocalAddress()) {
            int leftScope = ((Inet6Address) left).getScopeId(), rightScope = ((Inet6Address) right).getScopeId();
            // Equal link-local bytes on two explicitly different links are not the same endpoint.
            return leftScope == 0 || rightScope == 0 || leftScope == rightScope;
        }
        return true;
    }

    static boolean isLoopback(InetAddress address) {
        byte[] bytes = normalized(address);
        return address.isLoopbackAddress() || bytes.length == 4 && bytes[0] == 127;
    }

    private static byte[] normalized(InetAddress address) {
        byte[] bytes = address.getAddress();
        if (bytes.length == 16 && bytes[10] == (byte) 255 && bytes[11] == (byte) 255) {
            for (int i = 0; i < 10; i++) if (bytes[i] != 0) return bytes;
            return Arrays.copyOfRange(bytes, 12, 16);
        }
        return bytes;
    }

    private static boolean allZero(byte[] bytes) {
        for (byte value : bytes) if (value != 0) return false;
        return true;
    }

    static boolean sameDevice(String candidate, String localDeviceId) {
        String identity = AndroidConnectionHistory.deviceIdentity(candidate);
        return !identity.isEmpty() && identity.equals(AndroidConnectionHistory.deviceIdentity(localDeviceId));
    }

    static boolean knownSelf(AndroidConnectionHistory.Node node, boolean redirected, String localDeviceId) {
        return sameDevice(node.relay() ? node.relayDeviceId : redirected ? "" : node.deviceId, localDeviceId);
    }

    static void requireOtherDevice(String candidate, String localDeviceId) {
        if (sameDevice(candidate, localDeviceId)) throw new Rejected();
    }
}
