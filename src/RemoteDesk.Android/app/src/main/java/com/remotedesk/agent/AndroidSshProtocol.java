package com.remotedesk.agent;

import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.Base64;
import java.util.UUID;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;
import org.json.JSONArray;
import org.json.JSONObject;

/** SSH is a separate relay service. No desktop negotiation or takeover is permitted here. */
final class AndroidSshProtocol {
    private AndroidSshProtocol() { }

    static boolean available(JSONObject directory, JSONObject device) {
        if (!Boolean.TRUE.equals(directory.opt("sshTunneling"))) return false;
        JSONArray services = device.optJSONArray("services");
        if (services == null) return false; // Legacy directory means desktop only.
        for (int i = 0; i < services.length(); i++) if ("ssh".equals(services.opt(i))) return true;
        return false;
    }

    static boolean desktopAvailable(JSONObject device) {
        if (!device.has("services")) return true;
        JSONArray services = device.optJSONArray("services");
        if (services == null) return false;
        for (int i = 0; i < services.length(); i++) if ("desktop".equals(services.opt(i))) return true;
        return false;
    }

    static void paired(JSONObject response) throws IOException {
        if (!Boolean.TRUE.equals(response.opt("ok")) || !"paired".equals(response.optString("type")) ||
                !"ssh".equals(response.optString("service")))
            throw new IOException("中继未建立独立 SSH 通道，请刷新在线列表并检查主机 SSH 设置。");
    }

    static byte[] proof(byte[] key, String role, String id, byte[] nonce) throws Exception {
        if ((!"server".equals(role) && !"client".equals(role)) || nonce.length != 32 || key.length != 32 ||
                !UUID.fromString(id).toString().equals(id)) throw new IOException("SSH 身份消息无效。");
        Mac hmac = Mac.getInstance("HmacSHA256");
        hmac.init(new SecretKeySpec(key, "HmacSHA256"));
        hmac.update(("RemoteDesk SSH gate v1\0" + role + "\0" + id).getBytes(StandardCharsets.UTF_8));
        return hmac.doFinal(nonce);
    }

    static void authenticate(InputStream input, OutputStream output, String id, String deviceKey) throws Exception {
        if (deviceKey == null || deviceKey.isEmpty() || deviceKey.length() > 4096)
            throw new IOException("请填写此设备的设备密钥。");
        byte[] secret = deviceKey.getBytes(StandardCharsets.UTF_8);
        byte[] key = MessageDigest.getInstance("SHA-256").digest(secret);
        Arrays.fill(secret, (byte) 0);
        try {
            byte[] hello = new byte[104];
            new DataInputStream(input).readFully(hello);
            if (hello[0] != 'R' || hello[1] != 'D' || hello[2] != 'S' || hello[3] != '1' ||
                    !id.equals(new String(hello, 36, 36, StandardCharsets.US_ASCII)))
                throw new IOException("SSH 设备身份不符，已拒绝连接。");
            byte[] nonce = Arrays.copyOfRange(hello, 4, 36);
            if (!MessageDigest.isEqual(proof(key, "server", id, nonce), Arrays.copyOfRange(hello, 72, 104)))
                throw new IOException("SSH 设备密钥不正确或身份不符，已拒绝连接。");
            byte[] client = proof(key, "client", id, nonce);
            try { output.write(client); output.flush(); } finally { Arrays.fill(client, (byte) 0); }
            int accepted = input.read();
            if (accepted == 2) throw new IOException("目标主机的 SSH 服务不可达，请在目标主机检查 SSH 设置。");
            if (accepted != 1) throw new IOException("目标主机拒绝 SSH 设备认证。");
        } finally { Arrays.fill(key, (byte) 0); }
    }

    static String alias(AndroidRelay.Options target) {
        return "remotedesk-" + target.tlsCertificateSha256.toLowerCase(java.util.Locale.ROOT) + "-" + target.deviceId + ":ssh";
    }

    static String fingerprint(byte[] hostKey) throws Exception {
        return "SHA256:" + Base64.getEncoder().withoutPadding().encodeToString(
            MessageDigest.getInstance("SHA-256").digest(hostKey));
    }

    static byte[] readPrivateKey(InputStream input) throws IOException {
        byte[] buffer = new byte[131073];
        try {
            int offset = 0, count;
            while (offset < buffer.length && (count = input.read(buffer, offset, buffer.length - offset)) != -1) {
                if (count == 0) { int value = input.read(); if (value == -1) break; buffer[offset++] = (byte) value; }
                else offset += count;
            }
            if (offset == 0 || offset > 131072) throw new IOException("SSH 私钥文件须为 1 字节至 128 KiB。");
            return Arrays.copyOf(buffer, offset);
        } finally { Arrays.fill(buffer, (byte) 0); }
    }
}
