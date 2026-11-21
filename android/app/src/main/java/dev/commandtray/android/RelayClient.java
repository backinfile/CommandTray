package dev.commandtray.android;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URI;
import android.net.Uri;
import java.nio.charset.StandardCharsets;
import org.json.JSONArray;
import org.json.JSONObject;

final class RelayClient {
    private static final int MAX_JSON = 16 * 1024 * 1024;
    private final AppSettings settings;

    RelayClient(AppSettings settings) {
        this.settings = settings;
    }

    JSONObject pair(String server, String name, String code) throws Exception {
        String address = AppSettings.normalizeServer(server);
        if (code.trim().isEmpty()) throw new IllegalArgumentException("请输入配对码");
        JSONObject body = new JSONObject().put("deviceName", AppSettings.checkedName(name)).put("pairingCode", code.trim());
        JSONObject response = json(address, null, "POST", "/v1/devices/pair", body);
        String deviceId = response.getString("deviceId");
        settings.savePairing(address, name, deviceId, response.getString("token"));
        return new JSONObject().put("deviceId", deviceId);
    }

    JSONArray devices() throws Exception {
        return authorized("GET", "/v1/devices", null).getJSONArray("devices");
    }

    JSONObject workspace() throws Exception {
        return authorized("GET", "/v1/workspace", null);
    }

    JSONObject saveWorkspace(long baseRevision, JSONObject document) throws Exception {
        return authorized("PUT", "/v1/workspace", new JSONObject().put("baseRevision", baseRevision).put("document", document));
    }

    JSONObject clipboard() throws Exception {
        return authorized("GET", "/v1/clipboard", null);
    }

    JSONObject sendClipboard(String text) throws Exception {
        return authorized("PUT", "/v1/clipboard", new JSONObject().put("text", text));
    }

    JSONArray files() throws Exception {
        return authorized("GET", "/v1/files", null).getJSONArray("files");
    }

    JSONObject upload(String name, String folder, InputStream source, long length) throws Exception {
        if (name.trim().isEmpty()) throw new IllegalArgumentException("文件名称不能为空");
        String path = "/v1/files?name=" + encode(name) + "&folder=" + encode(folder);
        HttpURLConnection connection = connect(settings.server(), settings.relayToken(), "POST", path);
        try {
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/octet-stream");
            if (length >= 0) connection.setFixedLengthStreamingMode(length);
            else connection.setChunkedStreamingMode(64 * 1024);
            try (OutputStream upload = connection.getOutputStream()) {
                copy(source, upload, Long.MAX_VALUE);
            }
            return readObject(connection);
        } finally {
            connection.disconnect();
        }
    }

    void download(String id, OutputStream destination, long limit) throws Exception {
        HttpURLConnection connection = connect(settings.server(), settings.relayToken(), "GET", "/v1/files/" + encode(id));
        connection.setRequestProperty("Accept", "application/octet-stream");
        try {
            requireSuccess(connection);
            try (InputStream body = connection.getInputStream()) {
                copy(body, destination, limit);
            }
        } finally {
            connection.disconnect();
        }
    }

    void deleteFile(String id) throws Exception {
        authorized("DELETE", "/v1/files/" + encode(id), null);
    }

    private JSONObject authorized(String method, String path, JSONObject body) throws Exception {
        return json(settings.server(), settings.relayToken(), method, path, body);
    }

    private JSONObject json(String server, String token, String method, String path, JSONObject body) throws Exception {
        HttpURLConnection connection = connect(server, token, method, path);
        try {
            if (body != null) {
                byte[] serialized = body.toString().getBytes(StandardCharsets.UTF_8);
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                connection.setDoOutput(true);
                connection.setFixedLengthStreamingMode(serialized.length);
                try (OutputStream output = connection.getOutputStream()) {
                    output.write(serialized);
                }
            }
            return readObject(connection);
        } finally {
            connection.disconnect();
        }
    }

    private static HttpURLConnection connect(String server, String token, String method, String path) throws Exception {
        if (server.isEmpty()) throw new IllegalStateException("请填写中转服务器地址");
        HttpURLConnection connection = (HttpURLConnection) new URI(server + path).toURL().openConnection();
        connection.setRequestMethod(method);
        connection.setConnectTimeout(10_000);
        connection.setReadTimeout(30_000);
        connection.setInstanceFollowRedirects(false);
        connection.setRequestProperty("Accept", "application/json");
        if (token != null) connection.setRequestProperty("Authorization", "Bearer " + token);
        return connection;
    }

    private static void requireSuccess(HttpURLConnection connection) throws Exception {
        int code = connection.getResponseCode();
        if (code >= 200 && code < 300) return;
        JSONObject detail = new JSONObject();
        InputStream body = connection.getErrorStream();
        if (body != null) {
            try (InputStream errors = body) {
                String data = readText(errors, MAX_JSON);
                if (!data.trim().isEmpty()) {
                    try { detail = new JSONObject(data); }
                    catch (org.json.JSONException ignored) {}
                }
            }
        }
        throw new RelayError(code, detail);
    }

    private static JSONObject readObject(HttpURLConnection connection) throws Exception {
        requireSuccess(connection);
        if (connection.getResponseCode() == 204) return new JSONObject();
        try (InputStream body = connection.getInputStream()) {
            String text = readText(body, MAX_JSON);
            return text.trim().isEmpty() ? new JSONObject() : new JSONObject(text);
        }
    }

    private static String encode(String text) {
        return Uri.encode(text);
    }

    private static String readText(InputStream source, int limit) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        copy(source, output, limit);
        return new String(output.toByteArray(), StandardCharsets.UTF_8);
    }

    private static void copy(InputStream input, OutputStream output, long limit) throws IOException {
        byte[] buffer = new byte[32 * 1024];
        long total = 0;
        for (int count; (count = input.read(buffer)) != -1;) {
            total += count;
            if (total > limit) throw new IOException("传输超过当前操作的大小上限");
            output.write(buffer, 0, count);
        }
    }

    static final class RelayError extends IOException {
        final int code;
        final JSONObject detail;

        RelayError(int code, JSONObject detail) {
            super("中转服务器返回 HTTP " + code);
            this.code = code;
            this.detail = detail;
        }
    }
}
