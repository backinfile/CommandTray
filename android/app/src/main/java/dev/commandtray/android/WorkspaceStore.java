package dev.commandtray.android;

import android.content.Context;
import android.util.AtomicFile;
import android.util.Base64;

import java.io.File;
import java.io.FileOutputStream;
import java.io.FileNotFoundException;
import java.security.MessageDigest;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.UUID;
import org.json.JSONArray;
import org.json.JSONObject;

final class WorkspaceStore {
    private final File directory;
    private final RelayClient relay;
    private final AppSettings settings;

    WorkspaceStore(Context context, RelayClient relay, AppSettings settings) {
        this.directory = context.getFilesDir();
        this.relay = relay;
        this.settings = settings;
    }

    synchronized JSONObject state() throws Exception {
        try {
            return new JSONObject(new String(file().readFully(), StandardCharsets.UTF_8));
        } catch (FileNotFoundException missing) {
            return new JSONObject().put("revision", 0).put("document", emptyDocument()).put("dirty", false);
        }
    }

    synchronized JSONObject pull() throws Exception {
        JSONObject current = state();
        JSONObject incoming = relay.workspace();
        if (current.getBoolean("dirty")) {
            if (current.getLong("revision") != incoming.getLong("revision")) {
                current.put("remote", incoming);
                write(current);
            }
            return current;
        }
        return saveRemote(incoming);
    }

    synchronized JSONObject push() throws Exception {
        JSONObject current = state();
        if (!current.getBoolean("dirty")) return pull();
        try {
            return saveRemote(relay.saveWorkspace(current.getLong("revision"), current.getJSONObject("document")));
        } catch (RelayClient.RelayError error) {
            if (error.code != 409) throw error;
            JSONObject incoming = error.detail.has("document") && error.detail.has("revision") ? error.detail : relay.workspace();
            current.put("remote", incoming);
            return write(current);
        }
    }

    synchronized JSONObject resolve(String choice) throws Exception {
        JSONObject current = state();
        JSONObject remote = current.optJSONObject("remote");
        if (remote == null) throw new IllegalStateException("当前没有同步冲突");
        if ("remote".equals(choice)) return saveRemote(remote);
        if (!"local".equals(choice)) throw new IllegalArgumentException("未知的冲突处理方式");
        current.put("revision", remote.getLong("revision"));
        current.remove("remote");
        return write(current);
    }

    synchronized JSONObject renameSpace(String spaceId, String name) throws Exception {
        if (name.trim().isEmpty()) throw new IllegalArgumentException("空间名称不能为空");
        JSONObject current = state();
        space(current, spaceId).put("name", name.trim());
        return saveEdit(current);
    }

    synchronized JSONObject createSpace(String name) throws Exception {
        if (name.trim().isEmpty()) throw new IllegalArgumentException("空间名称不能为空");
        JSONObject current = state();
        JSONObject space = new JSONObject().put("id", uuid()).put("name", name.trim()).put("items", new JSONArray());
        JSONArray entries = spaces(current);
        entries.put(space);
        if (entries.length() == 1) current.getJSONObject("document").put("activeSpaceId", space.getString("id"));
        saveEdit(current);
        return space;
    }

    synchronized JSONObject createItem(String spaceId, String kind, String title) throws Exception {
        if (!"note".equals(kind) && !"todo".equals(kind)) throw new IllegalArgumentException("手机端支持新建便签和待办");
        JSONObject current = state();
        JSONObject data = new JSONObject();
        if ("note".equals(kind)) data.put("text", "");
        else data.put("tasks", new JSONArray());
        JSONObject item = new JSONObject().put("id", uuid()).put("kind", kind).put("title", title).put("x", 0).put("y", 0)
            .put("width", 320).put("height", 260).put("data", data);
        space(current, spaceId).getJSONArray("items").put(item);
        saveEdit(current);
        return item;
    }

    synchronized JSONObject writeNote(String spaceId, String itemId, String text) throws Exception {
        JSONObject current = state();
        itemData(current, spaceId, itemId, "note").put("text", text);
        return saveEdit(current);
    }

    synchronized JSONObject writeTask(String spaceId, String itemId, String taskId, String text, Boolean completed) throws Exception {
        JSONObject current = state();
        JSONObject list = itemData(current, spaceId, itemId, "todo");
        JSONArray tasks = list.optJSONArray("tasks");
        if (tasks == null) {
            tasks = new JSONArray();
            list.put("tasks", tasks);
        }
        JSONObject task;
        if (taskId.isEmpty()) {
            if (text == null || text.trim().isEmpty()) throw new IllegalArgumentException("待办内容不能为空");
            task = new JSONObject().put("id", uuid()).put("completed", false);
            tasks.put(task);
        } else {
            task = find(tasks, taskId);
        }
        if (text != null) task.put("text", text);
        if (completed != null) task.put("completed", completed);
        saveEdit(current);
        return task;
    }

    synchronized JSONObject removeTask(String spaceId, String itemId, String taskId) throws Exception {
        JSONObject current = state();
        JSONArray tasks = itemData(current, spaceId, itemId, "todo").getJSONArray("tasks");
        for (int index = 0; index < tasks.length(); index++) {
            JSONObject task = tasks.getJSONObject(index);
            if (taskId.equals(task.optString("id"))) {
                tasks.remove(index);
                return saveEdit(current);
            }
        }
        throw new IllegalArgumentException("找不到待办");
    }

    static JSONArray spaces(JSONObject state) throws Exception {
        JSONObject document = state.getJSONObject("document");
        JSONArray spaces = document.optJSONArray("spaces");
        if (spaces == null) {
            spaces = new JSONArray();
            document.put("spaces", spaces);
        }
        return spaces;
    }

    static JSONObject space(JSONObject state, String id) throws Exception {
        return find(spaces(state), id);
    }

    private static JSONObject itemData(JSONObject state, String spaceId, String itemId, String kind) throws Exception {
        JSONObject item = find(space(state, spaceId).getJSONArray("items"), itemId);
        if (!kind.equals(item.optString("kind"))) throw new IllegalArgumentException("组件类型不匹配");
        JSONObject data = item.optJSONObject("data");
        if (data == null) {
            data = new JSONObject();
            item.put("data", data);
        }
        return data;
    }

    private static JSONObject find(JSONArray entries, String id) throws Exception {
        for (int index = 0; index < entries.length(); index++) {
            JSONObject entry = entries.optJSONObject(index);
            if (entry != null && id.equals(entry.optString("id"))) return entry;
        }
        throw new IllegalArgumentException("找不到指定记录");
    }

    private JSONObject saveRemote(JSONObject response) throws Exception {
        JSONObject document = response.optJSONObject("document");
        if (document == null) document = emptyDocument();
        return write(new JSONObject().put("revision", response.getLong("revision")).put("document", document).put("dirty", false));
    }

    private JSONObject saveEdit(JSONObject current) throws Exception {
        current.put("dirty", true);
        return write(current);
    }

    private JSONObject write(JSONObject value) throws Exception {
        AtomicFile file = file();
        FileOutputStream output = file.startWrite();
        try {
            output.write(value.toString().getBytes(StandardCharsets.UTF_8));
            file.finishWrite(output);
            return value;
        } catch (IOException error) {
            file.failWrite(output);
            throw error;
        }
    }

    private AtomicFile file() throws Exception {
        byte[] hash = MessageDigest.getInstance("SHA-256").digest(settings.server().getBytes(StandardCharsets.UTF_8));
        String key = Base64.encodeToString(hash, Base64.URL_SAFE | Base64.NO_WRAP | Base64.NO_PADDING);
        return new AtomicFile(new File(directory, "workspace-" + key + ".json"));
    }

    private static JSONObject emptyDocument() throws Exception {
        return new JSONObject().put("schemaVersion", 1).put("activeSpaceId", JSONObject.NULL)
            .put("spaces", new JSONArray()).put("sshHosts", new JSONArray());
    }

    private static String uuid() {
        return UUID.randomUUID().toString();
    }
}
