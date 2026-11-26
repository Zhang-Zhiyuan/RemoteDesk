package com.remotedesk.agent;

import java.net.InetAddress;
import java.util.Locale;

final class AndroidDirectSshTarget {
    final String host;
    final int port;

    AndroidDirectSshTarget(String address, int port) {
        if (address == null || port < 1 || port > 65535) throw new IllegalArgumentException("请填写有效的 SSH 地址和端口。");
        String value = address.trim();
        if (value.startsWith("[") && value.endsWith("]") && value.contains(":")) value = value.substring(1, value.length() - 1);
        if (value.contains(":")) {
            if (!value.matches("[0-9A-Fa-f:.]+(?:%[A-Za-z0-9_.-]+)?")) throw new IllegalArgumentException("IPv6 地址无效。");
            int zone = value.indexOf('%');
            String literal = zone < 0 ? value : value.substring(0, zone);
            try { value = InetAddress.getByName(literal).getHostAddress() + (zone < 0 ? "" : value.substring(zone)); }
            catch (java.net.UnknownHostException ex) { throw new IllegalArgumentException("IPv6 地址无效。"); }
        } else {
            if (!value.matches("[A-Za-z0-9][A-Za-z0-9._-]{0,252}")) throw new IllegalArgumentException("请填写 SSH 主机的 IP 地址或主机名。");
            value = value.toLowerCase(Locale.ROOT);
        }
        this.host = value; this.port = port;
    }

    String address() { return (host.contains(":") ? "[" + host + "]" : host) + ":" + port; }
    String alias() { return "direct-ssh/" + address(); }
}
