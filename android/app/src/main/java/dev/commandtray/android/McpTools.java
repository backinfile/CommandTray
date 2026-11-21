package dev.commandtray.android;

import android.util.Base64;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import org.json.JSONArray;
import org.json.JSONObject;

final class McpTools {
    private static final int MAX_MCP_FILE = 4 * 1024 * 1024;
    private final CommandTrayApp app;

    McpTools(CommandTrayApp app) {
        this.app = app;
    }

    JSONArray list() throws Exception {
        JSONArray tools = new JSONArray();
        tools.put(tool("settings_read", "Read native Android settings and pairing status.", "", ""));
        tools.put(tool("settings_update", "Update the relay URL and local device name.", "server deviceName", "server deviceName"));
        tools.put(tool("mcp_disable", "Disable the local Android MCP listener.", "", ""));
        tools.put(tool("device_pair", "Pair the Android app with a relay.", "server deviceName pairingCode", "server deviceName pairingCode"));
        tools.put(tool("device_forget", "Remove the locally stored pairing token.", "", ""));
        tools.put(tool("devices_list", "List devices paired with the relay.", "", ""));
        tools.put(tool("workspace_read", "Read the local workspace, boards, SSH profiles and pending conflict.", "", ""));
        tools.put(tool("workspace_pull", "Pull the relay workspace without overwriting pending local changes.", "", ""));
        tools.put(tool("workspace_push", "Push pending edits and retain conflicting versions.", "", ""));
        tools.put(tool("workspace_resolve", "Resolve a conflict with choice local or remote.", "choice", "choice"));
        tools.put(tool("space_create", "Create a workspace space.", "name", "name"));
        tools.put(tool("space_select", "Select the active space in the Android app.", "spaceId", "spaceId"));
        tools.put(tool("space_rename", "Rename a workspace space.", "spaceId name", "spaceId name"));
        tools.put(tool("component_create", "Create a note or todo component.", "spaceId kind title", "spaceId kind"));
        tools.put(tool("note_update", "Set the text of a note component.", "spaceId itemId text", "spaceId itemId text"));
        tools.put(tool("task_update", "Add a task if taskId is omitted, or update its text/completion.", "spaceId itemId taskId text completed:boolean", "spaceId itemId"));
        tools.put(tool("task_delete", "Delete an existing task.", "spaceId itemId taskId", "spaceId itemId taskId"));
        tools.put(tool("clipboard_send", "Send text or use the foreground Android clipboard when text is omitted.", "text", ""));
        tools.put(tool("clipboard_read", "Read relay clipboard text.", "", ""));
        tools.put(tool("clipboard_receive", "Read relay text and copy it while CommandTray has window focus.", "", ""));
        tools.put(tool("files_list", "List shared files.", "", ""));
        tools.put(tool("file_upload", "Upload base64 file content up to 4 MiB.", "name folder base64", "name base64"));
        tools.put(tool("file_download", "Read base64 content for a shared file up to 4 MiB.", "id", "id"));
        tools.put(tool("file_delete", "Delete a shared file.", "id", "id"));
        return tools;
    }

    Object call(String name, JSONObject args) throws Exception {
        synchronized (app.workLock) {
            String space = args.optString("spaceId");
            String item = args.optString("itemId");
            return switch (name) {
                case "settings_read" -> app.settings.status().put("mcpRunning", app.mcpRunning());
                case "settings_update" -> {
                    app.settings.saveSettings(args.getString("server"), args.getString("deviceName"));
                    yield app.settings.status();
                }
                case "mcp_disable" -> {
                    app.setMcpEnabled(false);
                    yield ok();
                }
                case "device_pair" -> app.relay.pair(args.getString("server"), args.getString("deviceName"), args.getString("pairingCode"));
                case "device_forget" -> {
                    app.settings.unpair();
                    yield ok();
                }
                case "devices_list" -> new JSONObject().put("devices", app.relay.devices());
                case "workspace_read" -> app.workspace.state().put("selectedSpaceId", app.settings.activeSpace());
                case "workspace_pull" -> app.workspace.pull();
                case "workspace_push" -> app.workspace.push();
                case "workspace_resolve" -> app.workspace.resolve(args.getString("choice"));
                case "space_create" -> app.workspace.createSpace(args.getString("name"));
                case "space_select" -> {
                    WorkspaceStore.space(app.workspace.state(), space);
                    app.settings.selectSpace(space);
                    yield new JSONObject().put("selectedSpaceId", space);
                }
                case "space_rename" -> app.workspace.renameSpace(space, args.getString("name"));
                case "component_create" -> app.workspace.createItem(space, args.getString("kind"), args.optString("title"));
                case "note_update" -> app.workspace.writeNote(space, item, args.getString("text"));
                case "task_update" -> app.workspace.writeTask(space, item, args.optString("taskId"),
                    args.has("text") ? args.getString("text") : null,
                    args.has("completed") ? args.getBoolean("completed") : null);
                case "task_delete" -> app.workspace.removeTask(space, item, args.getString("taskId"));
                case "clipboard_send" -> app.relay.sendClipboard(args.has("text") ? args.getString("text") : app.readClipboard());
                case "clipboard_read" -> app.relay.clipboard();
                case "clipboard_receive" -> {
                    JSONObject clip = app.relay.clipboard();
                    app.writeClipboard(clip.optString("text"));
                    yield clip;
                }
                case "files_list" -> new JSONObject().put("files", app.relay.files());
                case "file_upload" -> upload(args);
                case "file_download" -> download(args.getString("id"));
                case "file_delete" -> {
                    app.relay.deleteFile(args.getString("id"));
                    yield ok();
                }
                default -> throw new IllegalArgumentException("未知 MCP 操作");
            };
        }
    }

    private JSONObject upload(JSONObject args) throws Exception {
        String encoded = args.getString("base64");
        if (encoded.length() > ((MAX_MCP_FILE + 2) / 3) * 4) throw new IllegalArgumentException("MCP 文件超过 4 MiB");
        byte[] bytes = Base64.decode(encoded, Base64.NO_WRAP);
        if (bytes.length > MAX_MCP_FILE) throw new IllegalArgumentException("MCP 文件超过 4 MiB");
        return app.relay.upload(args.getString("name"), args.optString("folder"), new ByteArrayInputStream(bytes), bytes.length);
    }

    private JSONObject download(String id) throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        app.relay.download(id, bytes, MAX_MCP_FILE);
        return new JSONObject().put("id", id).put("size", bytes.size()).put("base64", Base64.encodeToString(bytes.toByteArray(), Base64.NO_WRAP));
    }

    private static JSONObject ok() throws Exception {
        return new JSONObject().put("ok", true);
    }

    private static JSONObject tool(String name, String description, String fields, String mandatory) throws Exception {
        JSONObject properties = new JSONObject();
        JSONArray requiredFields = new JSONArray();
        if (!fields.isEmpty()) {
            for (String field : fields.split(" ")) {
                String[] parts = field.split(":", 2);
                properties.put(parts[0], new JSONObject().put("type", parts.length > 1 ? parts[1] : "string"));
            }
        }
        if (!mandatory.isEmpty()) {
            for (String field : mandatory.split(" ")) requiredFields.put(field);
        }
        return new JSONObject().put("name", name).put("description", description)
            .put("inputSchema", new JSONObject().put("type", "object").put("properties", properties).put("required", requiredFields)
                .put("additionalProperties", false));
    }
}
