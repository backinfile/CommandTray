package dev.commandtray.android;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.InputStream;
import java.io.OutputStream;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.function.Consumer;
import org.json.JSONArray;
import org.json.JSONObject;

public final class MainActivity extends Activity {
    private static final int PICK_UPLOAD = 10;
    private static final int PICK_DOWNLOAD = 11;
    private static final int SPACE = 0;
    private static final int FILES = 1;
    private static final int CLIPBOARD = 2;
    private static final int SETTINGS = 3;
    private final ExecutorService work = Executors.newSingleThreadExecutor();
    private CommandTrayApp app;
    private LinearLayout page;
    private TextView header;
    private Button[] navigation;
    private int selection;
    private JSONObject workspace;
    private JSONArray files = new JSONArray();
    private String activeSpace = "";
    private String pendingDownload;
    private String uploadFolder = "";
    private int pendingRequests;

    @Override
    public void onCreate(Bundle saved) {
        super.onCreate(saved);
        app = (CommandTrayApp) getApplication();
        activeSpace = app.settings.activeSpace();
        if (saved != null) {
            selection = saved.getInt("selection");
            activeSpace = saved.getString("activeSpace", "");
            pendingDownload = saved.getString("pendingDownload");
            uploadFolder = saved.getString("uploadFolder", "");
        }
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Color.rgb(250, 250, 250));
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            view.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(),
                insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
            return insets;
        });
        header = text("CommandTray", 24);
        header.setPadding(dp(16), dp(12), dp(16), dp(8));
        root.addView(header);
        LinearLayout tabs = row();
        navigation = new Button[4];
        String[] labels = {"空间", "文件", "剪贴板", "设置"};
        for (int i = 0; i < labels.length; i++) {
            int target = i;
            Button button = button(labels[i], () -> switchPage(target));
            button.setSingleLine();
            button.setTextSize(13);
            button.setMinimumWidth(0);
            button.setPadding(0, 0, 0, 0);
            navigation[i] = button;
            tabs.addView(button, new LinearLayout.LayoutParams(0, -2, 1));
        }
        root.addView(tabs);
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        page = column();
        page.setPadding(dp(16), dp(8), dp(16), dp(24));
        scroll.addView(page, new ScrollView.LayoutParams(-1, -2));
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        setContentView(root);
        root.requestApplyInsets();
        switchPage(selection);
    }

    @Override
    protected void onSaveInstanceState(Bundle state) {
        state.putInt("selection", selection);
        state.putString("activeSpace", activeSpace);
        state.putString("pendingDownload", pendingDownload);
        state.putString("uploadFolder", uploadFolder);
        super.onSaveInstanceState(state);
    }

    @Override
    public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        app.setClipboardFocused(focused);
    }

    @Override
    protected void onPause() {
        app.setClipboardFocused(false);
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        if (hasWindowFocus()) app.setClipboardFocused(false);
        work.shutdown();
        super.onDestroy();
    }

    private void switchPage(int target) {
        selection = target;
        for (int i = 0; i < navigation.length; i++) navigation[i].setEnabled(i != target);
        render();
        if (target == SPACE) {
            run(app.workspace::state, opened -> {
                String selected = app.settings.activeSpace();
                if (!selected.isEmpty()) activeSpace = selected;
                showWorkspace(opened);
            });
        } else if (target == FILES && app.settings.paired()) {
            run(app.relay::files, this::showFiles);
        }
    }

    private void render() {
        page.removeAllViews();
        if (selection == SPACE) renderWorkspace();
        else if (selection == FILES) renderFiles();
        else if (selection == CLIPBOARD) renderClipboard();
        else renderSettings();
    }

    private void renderSettings() {
        section("中转服务器");
        EditText server = field("服务器地址", app.settings.server(), InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        EditText name = field("设备名称", app.settings.deviceName(), InputType.TYPE_CLASS_TEXT);
        EditText code = field("配对码", "", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        page.addView(server);
        page.addView(name);
        page.addView(code);
        hint(app.settings.paired() ? "已配对" : "未配对");
        page.addView(actions(
            button("保存", () -> {
                String url = value(server);
                String deviceName = value(name);
                run(() -> {
                    app.settings.saveSettings(url, deviceName);
                    return app.settings.paired();
                }, ignored -> {
                    workspace = null;
                    files = new JSONArray();
                    notice("设置已保存");
                    renderIf(SETTINGS);
                });
            }),
            button("配对", () -> {
                String url = value(server);
                String deviceName = value(name);
                String pairing = value(code);
                run(() -> app.relay.pair(url, deviceName, pairing), ignored -> {
                    workspace = null;
                    files = new JSONArray();
                    notice("配对成功");
                    renderIf(SETTINGS);
                });
            })
        ));
        if (app.settings.paired()) {
            page.addView(actions(
                button("已配对设备", () -> run(app.relay::devices, this::showDevices)),
                button("解除本机配对", () -> confirm("解除本机配对？", () -> run(() -> {
                    app.settings.unpair();
                    return null;
                }, ignored -> renderIf(SETTINGS))))
            ));
        }
        section("本地 MCP");
        CheckBox enable = new CheckBox(this);
        enable.setText("启用本地 MCP");
        enable.setChecked(app.settings.mcpEnabled());
        enable.setOnCheckedChangeListener((view, checked) -> run(() -> {
            app.setMcpEnabled(checked);
            return checked;
        }, ignored -> renderIf(SETTINGS)));
        page.addView(enable);
        if (!app.mcpError().isEmpty()) hint(app.mcpError());
        if (app.mcpRunning()) {
            hint("http://127.0.0.1:" + AppSettings.MCP_PORT + "/mcp");
            page.addView(button("复制连接配置", () -> run(() ->
                new JSONObject().put("url", "http://127.0.0.1:" + AppSettings.MCP_PORT + "/mcp")
                    .put("headers", new JSONObject().put("Authorization", "Bearer " + app.settings.mcpToken())).toString(2),
                config -> {
                    try { app.writeClipboard(config); notice("连接配置已复制"); }
                    catch (Exception exception) { reportError(exception); }
                })));
        }
    }

    private void showDevices(JSONArray devices) {
        StringBuilder list = new StringBuilder();
        for (int index = 0; index < devices.length(); index++) {
            JSONObject device = devices.optJSONObject(index);
            if (device == null) continue;
            if (list.length() > 0) list.append('\n');
            list.append(device.optString("name", device.optString("deviceName", device.optString("id"))));
        }
        new AlertDialog.Builder(this).setTitle("已配对设备").setMessage(list.length() == 0 ? "暂无设备" : list)
            .setPositiveButton("关闭", null).show();
    }

    private void renderClipboard() {
        section("剪贴板");
        if (!app.settings.paired()) {
            hint("请先在设置中配对设备");
            return;
        }
        page.addView(actions(
            button("发送剪贴板", () -> run(() -> {
                String clip = app.readClipboard();
                if (clip.isEmpty()) throw new IllegalArgumentException("剪贴板里没有文本");
                return app.relay.sendClipboard(clip);
            }, ignored -> notice("已发送"))),
            button("接收并复制", () -> run(() -> {
                JSONObject clip = app.relay.clipboard();
                app.writeClipboard(clip.optString("text"));
                return clip;
            }, ignored -> notice("已复制")))
        ));
        page.addView(button("查看同步文本", () -> run(app.relay::clipboard, clip ->
            new AlertDialog.Builder(this).setTitle("同步文本").setMessage(clip.optString("text")).setPositiveButton("关闭", null).show())));
    }

    private void renderFiles() {
        section("共享文件");
        if (!app.settings.paired()) {
            hint("请先在设置中配对设备");
            return;
        }
        EditText folder = field("上传目录", uploadFolder, InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        page.addView(folder);
        page.addView(actions(
            button("添加文件", () -> {
                uploadFolder = value(folder);
                Intent pick = new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE);
                startActivityForResult(pick, PICK_UPLOAD);
            }),
            button("刷新", () -> run(app.relay::files, this::showFiles))
        ));
        if (files.length() == 0) hint("暂无共享文件");
        for (int index = 0; index < files.length(); index++) {
            JSONObject item = files.optJSONObject(index);
            if (item == null) continue;
            String name = item.optString("name", "文件");
            String id = item.optString("id");
            String folderPath = item.optString("folder", "");
            section(folderPath.isEmpty() ? name : folderPath + " / " + name);
            hint(item.optLong("size") + " B");
            page.addView(actions(
                button("下载", () -> {
                    pendingDownload = id;
                    Intent save = new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/octet-stream")
                        .addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_TITLE, name);
                    startActivityForResult(save, PICK_DOWNLOAD);
                }),
                button("删除", () -> confirm("删除共享文件？", () -> run(() -> {
                    app.relay.deleteFile(id);
                    return app.relay.files();
                }, this::showFiles)))
            ));
        }
    }

    private void showFiles(JSONArray incoming) {
        files = incoming;
        renderIf(FILES);
    }

    @Override
    protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        String download = pendingDownload;
        if (request == PICK_DOWNLOAD) pendingDownload = null;
        if (result != RESULT_OK || data == null || data.getData() == null) return;
        Uri uri = data.getData();
        if (request == PICK_UPLOAD) {
            String folder = uploadFolder;
            run(() -> {
                try (InputStream input = getContentResolver().openInputStream(uri)) {
                    if (input == null) throw new IllegalStateException("无法读取所选文件");
                    app.relay.upload(fileName(uri), folder, input, fileSize(uri));
                }
                return app.relay.files();
            }, this::showFiles);
        } else if (request == PICK_DOWNLOAD && download != null) {
            run(() -> {
                try (OutputStream output = getContentResolver().openOutputStream(uri, "wt")) {
                    if (output == null) throw new IllegalStateException("无法写入所选位置");
                    app.relay.download(download, output, Long.MAX_VALUE);
                }
                return null;
            }, ignored -> notice("已下载"));
        }
    }

    private String fileName(Uri uri) {
        try (Cursor data = getContentResolver().query(uri, new String[]{OpenableColumns.DISPLAY_NAME}, null, null, null)) {
            if (data != null && data.moveToFirst() && !data.isNull(0)) return data.getString(0);
        }
        return "附件";
    }

    private long fileSize(Uri uri) {
        try (Cursor data = getContentResolver().query(uri, new String[]{OpenableColumns.SIZE}, null, null, null)) {
            return data != null && data.moveToFirst() && !data.isNull(0) ? data.getLong(0) : -1;
        }
    }

    private void showWorkspace(JSONObject incoming) {
        workspace = incoming;
        renderIf(SPACE);
    }

    private void renderWorkspace() {
        section("工作空间");
        page.addView(actions(
            button("新建空间", () -> edit("空间名称", "", false, name -> run(() -> {
                JSONObject space = app.workspace.createSpace(name);
                String id = space.getString("id");
                app.settings.selectSpace(id);
                return new Object[]{id, app.workspace.state()};
            }, created -> {
                activeSpace = (String) created[0];
                showWorkspace((JSONObject) created[1]);
            }))),
            button("刷新本机", () -> run(app.workspace::state, this::showWorkspace))
        ));
        if (app.settings.paired()) {
            page.addView(actions(button("拉取", () -> run(app.workspace::pull, this::showWorkspace)),
                button("同步", () -> run(app.workspace::push, this::showWorkspace))));
        }
        if (workspace == null) return;
        if (workspace.optBoolean("dirty")) hint("本机有待同步的修改");
        if (workspace.has("remote")) {
            hint("发现同步冲突，已保留本机和服务器版本");
            page.addView(actions(
                button("保留本机版本", () -> confirm("保留本机版本？下次同步将提交本机内容。",
                    () -> run(() -> app.workspace.resolve("local"), this::showWorkspace))),
                button("采用服务器版本", () -> confirm("用服务器版本替换本机内容？",
                    () -> run(() -> app.workspace.resolve("remote"), this::showWorkspace)))
            ));
        }
        try {
            JSONArray spaces = WorkspaceStore.spaces(workspace);
            JSONObject selected = chooseSpace(spaces);
            if (selected == null) hint("暂无空间");
            else renderSelectedSpace(spaces, selected);
            renderSsh(workspace.getJSONObject("document").optJSONArray("sshHosts"));
        } catch (Exception error) { reportError(error); }
    }

    private JSONObject chooseSpace(JSONArray spaces) {
        for (int i = 0; i < spaces.length(); i++) {
            JSONObject space = spaces.optJSONObject(i);
            if (space != null && space.optString("id").equals(activeSpace)) return space;
        }
        JSONObject first = spaces.optJSONObject(0);
        if (first != null) activeSpace = first.optString("id");
        return first;
    }

    private void renderSelectedSpace(JSONArray spaces, JSONObject selected) {
        String spaceId = selected.optString("id");
        String name = selected.optString("name", "空间");
        page.addView(button(name + " ▾", () -> {
            String[] names = new String[spaces.length()];
            for (int i = 0; i < names.length; i++) names[i] = spaces.optJSONObject(i).optString("name", "空间");
            new AlertDialog.Builder(this).setTitle("切换空间").setItems(names, (dialog, which) -> {
                String target = spaces.optJSONObject(which).optString("id");
                run(() -> {
                    app.settings.selectSpace(target);
                    return app.workspace.state();
                }, opened -> {
                    activeSpace = target;
                    showWorkspace(opened);
                });
            }).show();
        }));
        page.addView(actions(
            button("重命名", () -> edit("空间名称", name, false, label -> run(() -> app.workspace.renameSpace(spaceId, label), this::showWorkspace))),
            button("添加便签", () -> addComponent(spaceId, "note", "便签")),
            button("添加待办", () -> addComponent(spaceId, "todo", "待办"))
        ));
        JSONArray items = selected.optJSONArray("items");
        if (items == null) return;
        for (int index = 0; index < items.length(); index++) {
            JSONObject item = items.optJSONObject(index);
            if (item == null) continue;
            JSONObject data = item.optJSONObject("data");
            if (data == null) data = new JSONObject();
            switch (item.optString("kind")) {
                case "note" -> renderNote(spaceId, item, data);
                case "todo" -> renderTodo(spaceId, item, data);
                case "board" -> {
                    section(item.optString("title", data.optString("title", "画板")));
                    page.addView(new BoardView(data.optJSONArray("strokes")), new LinearLayout.LayoutParams(-1, dp(220)));
                }
            }
        }
    }

    private void addComponent(String spaceId, String kind, String title) {
        edit("组件名称", title, false, name -> run(() -> {
            app.workspace.createItem(spaceId, kind, name);
            return app.workspace.state();
        }, this::showWorkspace));
    }

    private void renderNote(String space, JSONObject item, JSONObject data) {
        String title = item.optString("title", data.optString("title", "便签"));
        if (title.isEmpty()) title = "便签";
        String note = data.optString("text");
        section(title);
        hint(note.isEmpty() ? "空便签" : note);
        page.addView(button("编辑便签", () -> edit("编辑便签", note, true, content ->
            run(() -> app.workspace.writeNote(space, item.optString("id"), content), this::showWorkspace))));
    }

    private void renderTodo(String space, JSONObject item, JSONObject data) {
        String title = item.optString("title", data.optString("title", "待办"));
        section(title.isEmpty() ? "待办" : title);
        String itemId = item.optString("id");
        JSONArray tasks = data.optJSONArray("tasks");
        if (tasks != null) {
            for (int index = 0; index < tasks.length(); index++) {
                JSONObject task = tasks.optJSONObject(index);
                if (task == null) continue;
                String taskId = task.optString("id");
                String taskText = task.optString("text");
                LinearLayout line = row();
                CheckBox check = new CheckBox(this);
                check.setText(taskText);
                check.setChecked(task.optBoolean("completed"));
                check.setOnCheckedChangeListener((view, checked) -> saveTask(space, itemId, taskId, null, checked));
                line.addView(check, new LinearLayout.LayoutParams(0, -2, 1));
                line.addView(button("编辑", () -> edit("待办内容", taskText, false, value -> saveTask(space, itemId, taskId, value, null))));
                line.addView(button("删除", () -> confirm("删除这项待办？",
                    () -> run(() -> app.workspace.removeTask(space, itemId, taskId), this::showWorkspace))));
                page.addView(line);
            }
        }
        page.addView(button("新增待办", () -> edit("待办内容", "", false, task -> saveTask(space, itemId, "", task, null))));
    }

    private void saveTask(String space, String item, String task, String text, Boolean completed) {
        run(() -> {
            app.workspace.writeTask(space, item, task, text, completed);
            return app.workspace.state();
        }, this::showWorkspace);
    }

    private void renderSsh(JSONArray hosts) {
        if (hosts == null || hosts.length() == 0) return;
        section("SSH 主机");
        for (int index = 0; index < hosts.length(); index++) {
            JSONObject host = hosts.optJSONObject(index);
            if (host == null) continue;
            hint(host.optString("name") + " · " + host.optString("username") + "@"
                + host.optString("address") + ":" + host.optInt("port", 22));
        }
    }

    private final class BoardView extends View {
        private final JSONArray strokes;
        private final Paint ink = new Paint(Paint.ANTI_ALIAS_FLAG);

        BoardView(JSONArray strokes) {
            super(MainActivity.this);
            this.strokes = strokes;
            setContentDescription("画板");
            setBackgroundColor(Color.WHITE);
            ink.setStyle(Paint.Style.STROKE);
            ink.setStrokeWidth(dp(2));
            ink.setStrokeCap(Paint.Cap.ROUND);
            ink.setStrokeJoin(Paint.Join.ROUND);
        }

        @Override
        public void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            if (strokes == null) return;
            float maxX = 1;
            float maxY = 1;
            for (int index = 0; index < strokes.length(); index++) {
                JSONObject stroke = strokes.optJSONObject(index);
                if (stroke == null) continue;
                JSONArray points = stroke.optJSONArray("points");
                if (points == null) continue;
                for (int i = 0; i < points.length(); i++) {
                    JSONObject point = points.optJSONObject(i);
                    if (point == null) continue;
                    maxX = Math.max(maxX, (float) point.optDouble("x", 0));
                    maxY = Math.max(maxY, (float) point.optDouble("y", 0));
                }
            }
            float inset = dp(9);
            float scale = Math.min((getWidth() - inset * 2) / maxX, (getHeight() - inset * 2) / maxY);
            for (int index = 0; index < strokes.length(); index++) {
                JSONObject stroke = strokes.optJSONObject(index);
                if (stroke == null) continue;
                JSONArray points = stroke.optJSONArray("points");
                if (points == null) continue;
                try { ink.setColor(Color.parseColor(stroke.optString("color", "#3949ab"))); }
                catch (IllegalArgumentException error) { ink.setColor(Color.rgb(57, 73, 171)); }
                // Windows stores eraser actions alongside the ordinary strokes.
                boolean erase = stroke.optBoolean("erase");
                if (erase) ink.setColor(Color.WHITE);
                ink.setStrokeWidth(Math.max(1, scale * (erase ? 14 : 3)));
                Path path = new Path();
                boolean started = false;
                int count = 0;
                float lastX = 0;
                float lastY = 0;
                for (int i = 0; i < points.length(); i++) {
                    JSONObject point = points.optJSONObject(i);
                    if (point == null) continue;
                    lastX = (float) point.optDouble("x", 0) * scale + inset;
                    lastY = (float) point.optDouble("y", 0) * scale + inset;
                    if (started) path.lineTo(lastX, lastY);
                    else path.moveTo(lastX, lastY);
                    started = true;
                    count++;
                }
                if (count == 1) canvas.drawPoint(lastX, lastY, ink);
                else canvas.drawPath(path, ink);
            }
        }
    }

    private <T> void run(Callable<T> action, Consumer<T> success) {
        pendingRequests++;
        header.setText("CommandTray · 处理中");
        work.execute(() -> {
            try {
                T result;
                synchronized (app.workLock) { result = action.call(); }
                runOnUiThread(() -> {
                    finishRequest();
                    if (!isFinishing() && !isDestroyed()) success.accept(result);
                });
            } catch (Exception error) {
                runOnUiThread(() -> {
                    finishRequest();
                    if (!isFinishing() && !isDestroyed()) reportError(error);
                });
            }
        });
    }

    private void finishRequest() {
        if (--pendingRequests == 0) header.setText("CommandTray");
    }

    private void renderIf(int expected) {
        if (selection == expected) render();
    }

    private void reportError(Exception error) {
        String message = error.getMessage();
        notice(message == null ? "操作未完成" : message);
        if (selection == SETTINGS) render();
    }

    private void edit(String title, String current, boolean multiline, Consumer<String> save) {
        EditText input = field(title, current, InputType.TYPE_CLASS_TEXT | (multiline ? InputType.TYPE_TEXT_FLAG_MULTI_LINE : 0));
        input.setSingleLine(!multiline);
        LinearLayout box = column();
        box.setPadding(dp(20), 0, dp(20), 0);
        box.addView(input);
        new AlertDialog.Builder(this).setTitle(title).setView(box).setPositiveButton("保存", (dialog, which) -> save.accept(value(input)))
            .setNegativeButton("取消", null).show();
    }

    private void confirm(String text, Runnable action) {
        new AlertDialog.Builder(this).setMessage(text).setPositiveButton("确认", (dialog, which) -> action.run())
            .setNegativeButton("取消", null).show();
    }

    private void notice(String text) {
        Toast.makeText(this, text, Toast.LENGTH_SHORT).show();
    }

    private void section(String title) {
        TextView label = text(title, 19);
        label.setPadding(0, dp(12), 0, dp(4));
        page.addView(label);
    }

    private void hint(String value) {
        TextView label = text(value, 14);
        label.setPadding(0, dp(3), 0, dp(4));
        page.addView(label);
    }

    private TextView text(String content, int size) {
        TextView label = new TextView(this);
        label.setText(content);
        label.setTextSize(size);
        return label;
    }

    private EditText field(String hint, String value, int type) {
        EditText field = new EditText(this);
        field.setHint(hint);
        field.setInputType(type);
        field.setText(value);
        return field;
    }

    private Button button(String label, Runnable action) {
        Button button = new Button(this);
        button.setText(label);
        button.setAllCaps(false);
        button.setOnClickListener(view -> action.run());
        return button;
    }

    private LinearLayout actions(Button... buttons) {
        LinearLayout result = row();
        for (Button button : buttons) result.addView(button, new LinearLayout.LayoutParams(0, -2, 1));
        return result;
    }

    private LinearLayout row() {
        LinearLayout view = new LinearLayout(this);
        view.setOrientation(LinearLayout.HORIZONTAL);
        view.setGravity(Gravity.CENTER_VERTICAL);
        return view;
    }

    private LinearLayout column() {
        LinearLayout view = new LinearLayout(this);
        view.setOrientation(LinearLayout.VERTICAL);
        return view;
    }

    private static String value(EditText field) {
        return field.getText().toString();
    }

    private int dp(int amount) {
        return Math.round(getResources().getDisplayMetrics().density * amount);
    }
}
