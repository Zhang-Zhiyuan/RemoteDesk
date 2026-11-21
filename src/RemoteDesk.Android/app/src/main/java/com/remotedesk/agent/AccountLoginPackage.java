package com.remotedesk.agent;

import org.json.JSONException;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.Locale;

final class AccountLoginPackage {
    static final int LIMIT = 2 * 1024 * 1024;
    static final String IMPORT_HINT = "在 Windows 的 API 切换器中，打开对应客户端的“官方账号”，选择“导入登录包”，输入迁移密码后按需切换账号。";
    private static final String INVALID = "请选择 API 切换器从“官方账号”导出的完整 .asxaccount 登录包。";

    static byte[] read(String name, InputStream input) throws IOException {
        if (name == null || !name.toLowerCase(Locale.ROOT).endsWith(".asxaccount") || input == null) throw new IOException(INVALID);
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = input.read(chunk)) != -1) {
            if (count > LIMIT - buffer.size()) throw new IOException("登录包超过 2 MiB。");
            buffer.write(chunk, 0, count);
        }
        byte[] bytes = buffer.toByteArray();
        try {
            org.json.JSONTokener reader = new org.json.JSONTokener(new String(bytes, StandardCharsets.UTF_8));
            Object value = reader.nextValue();
            if (!(value instanceof JSONObject) || reader.nextClean() != 0) throw new IOException(INVALID);
            JSONObject root = (JSONObject) value;
            if (!"api-switcher-account-login".equals(root.get("format"))) throw new IOException(INVALID);
            Object version = root.get("version");
            JSONObject cipher = root.getJSONObject("cipher");
            if (Integer.valueOf(2).equals(version) && "none".equals(cipher.get("name")))
                throw new IOException("这个登录包没有设置迁移密码。请在 API 切换器中重新导出，设置至少 8 个字符的迁移密码后再发送。");
            JSONObject kdf = root.getJSONObject("kdf");
            Object iterations = kdf.get("iterations");
            if (Integer.valueOf(1).equals(version)
                    && "zlib".equals(root.get("compression")) && "PBKDF2HMAC-SHA256".equals(kdf.get("name"))
                    && iterations instanceof Integer && (int) iterations >= 100_000 && (int) iterations <= 2_000_000
                    && decodedLength(kdf.get("salt")) == 16
                    && "AES-256-GCM".equals(cipher.get("name"))
                    && decodedLength(cipher.get("nonce")) == 12
                    && decodedLength(root.get("payload")) > 16) return bytes;
        } catch (JSONException | IllegalArgumentException | StackOverflowError ignored) {
            // Android's JSONTokener also recurses on malformed, deeply nested input.
        }
        throw new IOException(INVALID);
    }

    private static int decodedLength(Object field) {
        if (!(field instanceof String)) return -1;
        String encoded = (String) field;
        // API Switcher requires standard padded Base64. Java also accepts missing padding by default.
        if (encoded.length() % 4 != 0) return -1;
        return Base64.getDecoder().decode(encoded).length;
    }
}
