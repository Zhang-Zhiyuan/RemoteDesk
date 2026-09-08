package com.remotedesk.agent;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.nio.charset.StandardCharsets;
import java.security.Key;
import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

final class AndroidPasswordStore {
    private static final String KEY_ALIAS = "RemoteDeskAgentPassword";
    private static final String PREF_PASSWORD_ENCRYPTED = "password.keystore.v1";
    private static final String PREF_VIEWER_PASSWORD_ENCRYPTED =
        "viewer-password.keystore.v1";
    private static final String TRANSFORMATION = "AES/GCM/NoPadding";
    private static final int GCM_TAG_BITS = 128;

    private AndroidPasswordStore() {
    }

    static String load(Context context) {
        return loadValue(context, PREF_PASSWORD_ENCRYPTED, true);
    }

    static String loadViewer(Context context) {
        return loadValue(context, PREF_VIEWER_PASSWORD_ENCRYPTED, false);
    }

    static String loadHistory(Context context) throws Exception {
        String encrypted = preferences(context).getString("viewer-history.keystore.v1", "");
        if (encrypted == null || encrypted.isEmpty()) return "";
        String value = decrypt(encrypted);
        // Do not silently replace an unreadable history with an empty one.
        if (value.isEmpty()) throw new java.io.IOException("无法读取历史连接");
        return value;
    }

    static void saveHistory(Context context, String value) throws Exception {
        if (!preferences(context).edit().putString("viewer-history.keystore.v1", encrypt(value)).commit())
            throw new java.io.IOException("无法保存历史连接");
    }

    static String loadRelay(Context context) {
        return loadValue(context, "relay-options.keystore.v1", false);
    }

    static String loadUnlockPin(Context context) {
        return loadValue(context, "unlock-pin.keystore.v1", false);
    }

    static boolean hasUnlockPin(Context context) {
        return preferences(context).contains("unlock-pin.keystore.v1");
    }

    static void saveUnlockPin(Context context, String pin) throws Exception {
        if (pin != null && !pin.isEmpty() && !AndroidPinUnlockPolicy.validPin(pin))
            throw new IllegalArgumentException("仅支持 4 至 16 位数字 PIN");
        SharedPreferences.Editor editor = preferences(context).edit();
        if (pin == null || pin.isEmpty()) editor.remove("unlock-pin.keystore.v1");
        else editor.putString("unlock-pin.keystore.v1", encrypt(pin));
        // The UI must not report success before this one-time credential is
        // durable; an immediate update/restart must not lose the saved setting.
        if (!editor.commit()) throw new java.io.IOException("无法持久保存自动解锁设置");
    }

    static void saveRelay(Context context, String configuration) throws Exception {
        saveValue(context, "relay-options.keystore.v1", configuration, false);
    }

    private static String loadValue(
        Context context,
        String preferenceName,
        boolean migrateLegacyHostPassword) {
        SharedPreferences preferences = preferences(context);
        String encryptedPassword = preferences.getString(preferenceName, "");
        if (encryptedPassword != null && !encryptedPassword.isEmpty()) {
            try {
                return decrypt(encryptedPassword);
            } catch (Exception ignored) {
                return "";
            }
        }

        if (!migrateLegacyHostPassword) {
            return "";
        }

        String legacyPassword = preferences.getString(RemoteDeskForegroundService.PREF_PASSWORD, "");
        if (legacyPassword == null || legacyPassword.isEmpty()) {
            return "";
        }

        try {
            save(context, legacyPassword);
        } catch (Exception ignored) {
        }

        return legacyPassword;
    }

    static void save(Context context, String password) throws Exception {
        saveValue(context, PREF_PASSWORD_ENCRYPTED, password, true);
    }

    static void saveViewer(Context context, String password) throws Exception {
        saveValue(context, PREF_VIEWER_PASSWORD_ENCRYPTED, password, false);
    }

    private static void saveValue(
        Context context,
        String preferenceName,
        String password,
        boolean removeLegacyHostPassword) throws Exception {
        SharedPreferences.Editor editor = preferences(context).edit();
        if (removeLegacyHostPassword) {
            editor.remove(RemoteDeskForegroundService.PREF_PASSWORD);
        }
        if (password == null || password.trim().isEmpty()) {
            editor.remove(preferenceName).apply();
            return;
        }

        editor.putString(preferenceName, encrypt(password)).apply();
    }

    private static SharedPreferences preferences(Context context) {
        return context.getApplicationContext().getSharedPreferences(
            RemoteDeskForegroundService.PREFS_NAME,
            Context.MODE_PRIVATE);
    }

    private static String encrypt(String password) throws Exception {
        Cipher cipher = Cipher.getInstance(TRANSFORMATION);
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey());
        byte[] cipherText = cipher.doFinal(password.getBytes(StandardCharsets.UTF_8));
        return Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + ":" +
            Base64.encodeToString(cipherText, Base64.NO_WRAP);
    }

    private static String decrypt(String encryptedPassword) throws Exception {
        int separatorIndex = encryptedPassword.indexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= encryptedPassword.length() - 1) {
            return "";
        }

        byte[] iv = Base64.decode(encryptedPassword.substring(0, separatorIndex), Base64.NO_WRAP);
        byte[] cipherText = Base64.decode(encryptedPassword.substring(separatorIndex + 1), Base64.NO_WRAP);
        Cipher cipher = Cipher.getInstance(TRANSFORMATION);
        cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(), new GCMParameterSpec(GCM_TAG_BITS, iv));
        return new String(cipher.doFinal(cipherText), StandardCharsets.UTF_8);
    }

    // First-use callers must not generate two keys for the same alias: replacing
    // the first key would make a concurrently saved password unreadable.
    private static synchronized SecretKey getOrCreateKey() throws Exception {
        KeyStore keyStore = KeyStore.getInstance("AndroidKeyStore");
        keyStore.load(null);
        Key key = keyStore.getKey(KEY_ALIAS, null);
        if (key instanceof SecretKey) {
            return (SecretKey) key;
        }

        KeyGenerator keyGenerator = KeyGenerator.getInstance(
            KeyProperties.KEY_ALGORITHM_AES,
            "AndroidKeyStore");
        KeyGenParameterSpec spec = new KeyGenParameterSpec.Builder(
            KEY_ALIAS,
            KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setRandomizedEncryptionRequired(true)
            .build();
        keyGenerator.init(spec);
        return keyGenerator.generateKey();
    }
}
