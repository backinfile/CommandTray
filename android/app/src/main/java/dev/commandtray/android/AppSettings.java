package dev.commandtray.android;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.SecureRandom;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import org.json.JSONObject;

final class AppSettings {
    static final int MCP_PORT = 58629;
    private static final String KEY_ALIAS = "commandtray.android.secrets";
    private final SharedPreferences values;

    AppSettings(Context context) {
        values = context.getSharedPreferences("commandtray", Context.MODE_PRIVATE);
    }

    synchronized String server() {
        return values.getString("server", "");
    }

    synchronized String deviceName() {
        return values.getString("name", "Android");
    }

    synchronized String deviceId() {
        return values.getString("deviceId", "");
    }

    synchronized boolean paired() {
        return values.contains("relayToken") && !server().isEmpty();
    }

    synchronized void saveSettings(String server, String name) throws Exception {
        String normalized = normalizeServer(server);
        SharedPreferences.Editor edit = values.edit().putString("server", normalized)
            .putString("name", checkedName(name));
        if (!normalized.equals(server())) {
            edit.remove("relayToken").remove("deviceId").remove("activeSpace");
        }
        save(edit);
    }

    synchronized void savePairing(String server, String name, String id, String token) throws Exception {
        if (id.trim().isEmpty() || token.trim().isEmpty()) {
            throw new IllegalStateException("中转服务器未返回配对信息");
        }
        String normalized = normalizeServer(server);
        SharedPreferences.Editor edit = values.edit();
        if (!normalized.equals(server())) edit.remove("activeSpace");
        save(edit.putString("server", normalized).putString("name", checkedName(name))
            .putString("deviceId", id).putString("relayToken", encrypt(token)));
    }

    synchronized void unpair() throws Exception {
        save(values.edit().remove("deviceId").remove("relayToken"));
    }

    synchronized String relayToken() throws Exception {
        String value = values.getString("relayToken", "");
        if (value.isEmpty()) throw new IllegalStateException("请先在设置中配对设备");
        return decrypt(value);
    }

    synchronized String activeSpace() {
        return values.getString("activeSpace", "");
    }

    synchronized void selectSpace(String name) {
        save(values.edit().putString("activeSpace", name));
    }

    synchronized boolean mcpEnabled() {
        return values.getBoolean("mcpEnabled", false);
    }

    synchronized void enableMcp(boolean enabled) throws Exception {
        SharedPreferences.Editor edit = values.edit().putBoolean("mcpEnabled", enabled);
        if (enabled && !values.contains("mcpToken")) {
            byte[] bytes = new byte[32];
            new SecureRandom().nextBytes(bytes);
            edit.putString("mcpToken", encrypt(Base64.encodeToString(bytes, Base64.NO_WRAP | Base64.NO_PADDING | Base64.URL_SAFE)));
        }
        save(edit);
    }

    synchronized String mcpToken() throws Exception {
        String value = values.getString("mcpToken", "");
        if (value.isEmpty()) throw new IllegalStateException("本地 MCP 未启用");
        return decrypt(value);
    }

    synchronized JSONObject status() throws Exception {
        return new JSONObject().put("server", server()).put("deviceName", deviceName())
            .put("deviceId", deviceId()).put("paired", paired()).put("mcpEnabled", mcpEnabled())
            .put("mcpUrl", "http://127.0.0.1:" + MCP_PORT + "/mcp");
    }

    static String normalizeServer(String input) {
        if (input.trim().isEmpty()) return "";
        try {
            URI uri = new URI(input.trim());
            String scheme = uri.getScheme();
            String host = uri.getHost();
            boolean local = "127.0.0.1".equals(host) || "localhost".equalsIgnoreCase(host);
            if (host == null || uri.getRawUserInfo() != null || uri.getRawQuery() != null
                || uri.getRawFragment() != null || uri.getPort() > 65535
                || (!"https".equalsIgnoreCase(scheme) && !(local && "http".equalsIgnoreCase(scheme)))) {
                throw new IllegalArgumentException();
            }
            return uri.toASCIIString().replaceFirst("/+$", "");
        } catch (Exception exception) {
            throw new IllegalArgumentException("请输入 HTTPS 服务器地址。HTTP 可用于本机回环地址。");
        }
    }

    static String checkedName(String name) {
        String value = name.trim();
        if (value.isEmpty() || value.length() > 120 || value.chars().anyMatch(Character::isISOControl)) throw new IllegalArgumentException("设备名称应为 1–120 个可显示字符");
        return value;
    }

    private void save(SharedPreferences.Editor edit) {
        if (!edit.commit()) throw new IllegalStateException("无法保存本机设置");
    }

    private static SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        SecretKey current = (SecretKey) store.getKey(KEY_ALIAS, null);
        if (current != null) return current;
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .build());
        return generator.generateKey();
    }

    private static String encrypt(String value) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] encrypted = cipher.doFinal(value.getBytes(StandardCharsets.UTF_8));
        return Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + "." + Base64.encodeToString(encrypted, Base64.NO_WRAP);
    }

    private static String decrypt(String value) throws Exception {
        String[] parts = value.split("\\.", 2);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)));
        return new String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), StandardCharsets.UTF_8);
    }
}
