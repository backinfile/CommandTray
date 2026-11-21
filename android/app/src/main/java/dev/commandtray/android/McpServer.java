package dev.commandtray.android;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.Closeable;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

final class McpServer implements Closeable {
    private static final String PROTOCOL = "2025-11-25";
    private static final int MAX_BODY = 6 * 1024 * 1024;
    private final ServerSocket listener;
    private final ThreadPoolExecutor requests = new ThreadPoolExecutor(2, 4, 30, TimeUnit.SECONDS,
        new ArrayBlockingQueue<>(8));
    private final McpTools tools;
    private final byte[] authorization;

    McpServer(McpTools tools, String token) throws Exception {
        this.tools = tools;
        this.authorization = ("Bearer " + token).getBytes(StandardCharsets.UTF_8);
        listener = new ServerSocket();
        try {
            listener.setReuseAddress(true);
            listener.bind(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), AppSettings.MCP_PORT), 16);
        } catch (IOException error) {
            listener.close();
            throw error;
        }
        Thread acceptor = new Thread(this::accept, "CommandTray-MCP");
        acceptor.setDaemon(true);
        acceptor.start();
    }

    private void accept() {
        while (!listener.isClosed()) {
            try {
                Socket socket = listener.accept();
                try {
                    requests.execute(() -> handle(socket));
                } catch (RejectedExecutionException error) {
                    socket.close();
                }
            } catch (IOException error) {
                if (!listener.isClosed()) close();
            }
        }
    }

    private void handle(Socket socket) {
        try (Socket request = socket) {
            request.setSoTimeout(5000);
            InputStream input = new BufferedInputStream(request.getInputStream());
            OutputStream output = request.getOutputStream();
            try {
                String[] line = readLine(input, 4096).split(" ", -1);
                if (line.length != 3 || !"HTTP/1.1".equals(line[2])) throw new BadRequest();
                Map<String, String> headers = readHeaders(input);
                if (!validHost(headers.get("host")) || !validOrigin(headers.get("origin"))) {
                    reply(output, 403, "Forbidden", "", "");
                    return;
                }
                if (!"/mcp".equals(line[1])) {
                    reply(output, 404, "Not Found", "", "");
                    return;
                }
                String supplied = headers.getOrDefault("authorization", "");
                if (!MessageDigest.isEqual(authorization, supplied.getBytes(StandardCharsets.UTF_8))) {
                    reply(output, 401, "Unauthorized", "", "WWW-Authenticate: Bearer\r\n");
                    return;
                }
                if (!"POST".equals(line[0])) {
                    reply(output, 405, "Method Not Allowed", "", "Allow: POST\r\n");
                    return;
                }
                String protocolVersion = headers.get("mcp-protocol-version");
                if (protocolVersion != null && !supported(protocolVersion)) {
                    json(output, 400, "Bad Request", failure(JSONObject.NULL, -32600, "Unsupported protocol version"));
                    return;
                }
                if (headers.containsKey("transfer-encoding")) throw new BadRequest();
                int length;
                try { length = Integer.parseInt(headers.getOrDefault("content-length", "-1")); }
                catch (NumberFormatException error) { throw new BadRequest(); }
                if (length < 0) throw new BadRequest();
                if (length > MAX_BODY) {
                    reply(output, 413, "Content Too Large", "", "");
                    return;
                }
                String mediaType = headers.getOrDefault("content-type", "");
                if (!"application/json".equalsIgnoreCase(mediaType.split(";", 2)[0].trim())) {
                    reply(output, 415, "Unsupported Media Type", "", "");
                    return;
                }
                byte[] body = new byte[length];
                int offset = 0;
                while (offset < length) {
                    int count = input.read(body, offset, length - offset);
                    if (count < 0) throw new BadRequest();
                    offset += count;
                }
                JSONObject message;
                try { message = new JSONObject(new String(body, StandardCharsets.UTF_8)); }
                catch (JSONException error) {
                    json(output, 400, "Bad Request", failure(JSONObject.NULL, -32700, "Invalid JSON object"));
                    return;
                }
                dispatch(output, message);
            } catch (BadRequest error) {
                reply(output, 400, "Bad Request", "", "");
            }
        } catch (IOException | JSONException ignored) {
            // Client disconnected or sent an incomplete request.
        }
    }

    private void dispatch(OutputStream output, JSONObject message) throws IOException, JSONException {
        Object id = message.has("id") ? message.opt("id") : JSONObject.NULL;
        String method = message.optString("method");
        if (!"2.0".equals(message.optString("jsonrpc")) || method.isEmpty()) {
            json(output, 400, "Bad Request", failure(id, -32600, "Invalid request"));
            return;
        }
        if (!message.has("id") && method.startsWith("notifications/")) {
            reply(output, 202, "Accepted", "", "");
            return;
        }
        if (!(id instanceof String) && !(id instanceof Number)) {
            json(output, 400, "Bad Request", failure(JSONObject.NULL, -32600, "Request ID is required"));
            return;
        }
        JSONObject params = message.optJSONObject("params");
        if (params == null) params = new JSONObject();
        JSONObject result;
        switch (method) {
            case "initialize" -> {
                String offered = params.optString("protocolVersion", PROTOCOL);
                String version = supported(offered) ? offered : PROTOCOL;
                result = new JSONObject().put("protocolVersion", version)
                    .put("capabilities", new JSONObject().put("tools", new JSONObject()))
                    .put("serverInfo", new JSONObject().put("name", "CommandTray Android").put("version", "1.0.0"));
            }
            case "ping" -> result = new JSONObject();
            case "tools/list" -> {
                try { result = new JSONObject().put("tools", tools.list()); }
                catch (Exception error) {
                    json(output, 200, "OK", failure(id, -32603, "Cannot list tools"));
                    return;
                }
            }
            case "tools/call" -> {
                try {
                    JSONObject argument = params.optJSONObject("arguments");
                    Object data = tools.call(params.getString("name"), argument == null ? new JSONObject() : argument);
                    result = new JSONObject().put("structuredContent", data)
                        .put("content", new JSONArray().put(new JSONObject().put("type", "text").put("text", data.toString())));
                } catch (Exception error) {
                    String safeMessage = error.getMessage();
                    if (safeMessage == null || error instanceof IOException && !(error instanceof RelayClient.RelayError)) {
                        safeMessage = "操作未完成，请检查本机设置或中转连接";
                    }
                    result = new JSONObject().put("isError", true)
                        .put("content", new JSONArray().put(new JSONObject().put("type", "text").put("text", safeMessage)));
                }
            }
            default -> {
                json(output, 200, "OK", failure(id, -32601, "Method not found"));
                return;
            }
        }
        json(output, 200, "OK", resultMessage(id).put("result", result));
    }

    private static boolean supported(String version) {
        return PROTOCOL.equals(version) || "2025-06-18".equals(version) || "2025-03-26".equals(version);
    }

    private static boolean validHost(String host) {
        return ("127.0.0.1:" + AppSettings.MCP_PORT).equals(host)
            || ("localhost:" + AppSettings.MCP_PORT).equalsIgnoreCase(host);
    }

    private static boolean validOrigin(String origin) {
        if (origin == null) return true;
        return ("http://127.0.0.1:" + AppSettings.MCP_PORT).equals(origin)
            || ("http://localhost:" + AppSettings.MCP_PORT).equalsIgnoreCase(origin);
    }

    private static Map<String, String> readHeaders(InputStream input) throws IOException, BadRequest {
        Map<String, String> headers = new HashMap<>();
        int size = 0;
        for (;;) {
            String line = readLine(input, 4096);
            if (line.isEmpty()) return headers;
            size += line.length() + 2;
            int separator = line.indexOf(':');
            if (size > 16_384 || separator < 1) throw new BadRequest();
            String key = line.substring(0, separator).toLowerCase(Locale.ROOT);
            if (!key.matches("[a-z0-9!#$%&'*+.^_~-]+") || headers.putIfAbsent(key, line.substring(separator + 1).trim()) != null) {
                throw new BadRequest();
            }
        }
    }

    private static String readLine(InputStream input, int limit) throws IOException, BadRequest {
        ByteArrayOutputStream line = new ByteArrayOutputStream();
        for (;;) {
            int value = input.read();
            if (value == '\r' && input.read() == '\n') return new String(line.toByteArray(), StandardCharsets.US_ASCII);
            if (value < 0 || value == '\r' || value == '\n' || (value < 32 && value != '\t') || value > 126 || line.size() == limit) {
                throw new BadRequest();
            }
            line.write(value);
        }
    }

    private static JSONObject failure(Object id, int code, String message) throws JSONException {
        return resultMessage(id).put("error", new JSONObject().put("code", code).put("message", message));
    }

    private static JSONObject resultMessage(Object id) throws JSONException {
        return new JSONObject().put("jsonrpc", "2.0").put("id", id);
    }

    private static void json(OutputStream output, int code, String reason, JSONObject value) throws IOException {
        reply(output, code, reason, value.toString(), "");
    }

    private static void reply(OutputStream output, int code, String reason, String body, String otherHeaders) throws IOException {
        byte[] data = body.getBytes(StandardCharsets.UTF_8);
        String headers = "HTTP/1.1 " + code + " " + reason + "\r\nContent-Length: " + data.length
            + "\r\nContent-Type: application/json; charset=utf-8\r\nConnection: close\r\n"
            + "Cache-Control: no-store\r\n" + otherHeaders + "\r\n";
        output.write(headers.getBytes(StandardCharsets.US_ASCII));
        output.write(data);
        output.flush();
    }

    @Override
    public void close() {
        try { listener.close(); }
        catch (IOException ignored) {}
        requests.shutdown();
    }

    private static final class BadRequest extends Exception {}
}
