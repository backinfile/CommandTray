package dev.commandtray.android;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.content.res.Configuration;
import android.database.Cursor;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Insets;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.OpenableColumns;
import android.text.InputType;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.PopupMenu;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;

import java.io.InputStream;
import java.io.OutputStream;
import java.time.Instant;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Locale;
import java.util.Map;
import java.util.TreeMap;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.function.Consumer;
import org.json.JSONArray;
import org.json.JSONObject;

import static dev.commandtray.android.R.color.*;
import static dev.commandtray.android.TrayUi.Icon.*;
import static dev.commandtray.android.TrayUi.Style.*;

/*
 * A single toolbar identifies the current space or page and holds its actions.
 * Notes, tasks, files and clipboard text begin directly beneath it.
 */
public final class MainActivity extends Activity {
    private static final int PICK_UPLOAD = 10;
    private static final int PICK_DOWNLOAD = 11;
    private static final int SPACE = 0;
    private static final int FILES = 1;
    private static final int CLIPBOARD = 2;
    private static final int SETTINGS = 3;
    private static final String[] PAGE_TITLES = {"工作空间", "共享文件", "剪贴板", "设置"};
    private final ExecutorService work = Executors.newSingleThreadExecutor();
    private final TrayUi.Destination[] navigation = new TrayUi.Destination[4];
    private CommandTrayApp app;
    private TrayUi ui;
    private LinearLayout toolbar;
    private LinearLayout page;
    private ScrollView scroll;
    private TextView feedback;
    private ProgressBar progress;
    private int selection;
    private JSONObject workspace;
    private JSONObject sharedClipboard;
    private JSONArray files = new JSONArray();
    private String activeSpace = "";
    private String pendingDownload;
    private String uploadFolder = "";
    private int pendingRequests;
    private boolean editingConnection;
    private final Runnable dismissFeedback = () -> feedback.setVisibility(View.GONE);

    @Override
    public void onCreate(Bundle saved) {
        super.onCreate(saved);
        app = (CommandTrayApp) getApplication();
        ui = new TrayUi(this);
        activeSpace = app.settings.activeSpace();
        if (saved != null) {
            selection = saved.getInt("selection");
            activeSpace = saved.getString("activeSpace", "");
            pendingDownload = saved.getString("pendingDownload");
            uploadFolder = saved.getString("uploadFolder", "");
        }

        boolean rail = getResources().getConfiguration().screenWidthDp >= 720;
        LinearLayout navigationBar = createNavigation(rail);
        LinearLayout root = ui.column();
        root.setBackgroundColor(ui.color(tray_canvas));
        toolbar = ui.row();
        toolbar.setMinimumHeight(dp(48));
        toolbar.setPadding(dp(16), 0, dp(8), 0);
        root.addView(toolbar);
        ui.rule(root);
        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setIndeterminate(true);
        progress.setIndeterminateTintList(ColorStateList.valueOf(ui.color(tray_primary)));
        progress.setVisibility(View.INVISIBLE);
        root.addView(progress, new LinearLayout.LayoutParams(-1, dp(2)));

        FrameLayout content = new FrameLayout(this);
        scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setClipToPadding(false);
        scroll.setVerticalScrollBarEnabled(false);
        page = ui.column();
        page.setPadding(dp(16), dp(8), dp(16), dp(16));
        int contentWidth = rail ? dp(Math.min(620, getResources().getConfiguration().screenWidthDp - 104)) : -1;
        scroll.addView(page, new FrameLayout.LayoutParams(contentWidth, -2, Gravity.TOP | Gravity.CENTER_HORIZONTAL));
        content.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
        feedback = ui.text("", 14, tray_panel_text, true);
        feedback.setBackground(ui.fill(tray_panel, 14));
        feedback.setPadding(dp(16), dp(12), dp(16), dp(12));
        feedback.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        feedback.setVisibility(View.GONE);
        FrameLayout.LayoutParams feedbackLayout = new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM);
        feedbackLayout.setMargins(dp(16), 0, dp(16), dp(12));
        content.addView(feedback, feedbackLayout);

        if (rail) {
            LinearLayout body = ui.row();
            body.setGravity(Gravity.TOP);
            body.addView(navigationBar, new LinearLayout.LayoutParams(dp(88), -1));
            body.addView(content, new LinearLayout.LayoutParams(0, -1, 1));
            root.addView(body, new LinearLayout.LayoutParams(-1, 0, 1));
        } else {
            root.addView(content, new LinearLayout.LayoutParams(-1, 0, 1));
            ui.rule(root);
            root.addView(navigationBar);
        }

        setContentView(root);
        // The window must initialize its decor before Android 14 exposes its insets controller.
        configureInsets(root, navigationBar, rail);
        root.requestApplyInsets();
        switchPage(selection);
    }

    private LinearLayout createNavigation(boolean rail) {
        LinearLayout bar = rail ? ui.column() : ui.row();
        bar.setBackgroundColor(ui.color(tray_surface));
        bar.setPadding(dp(4), dp(rail ? 8 : 2), dp(4), dp(2));
        boolean stacked = rail || getResources().getConfiguration().fontScale >= 1.3f;
        String[] names = {"空间", "文件", "剪贴板", "设置"};
        TrayUi.Icon[] icons = {TrayUi.Icon.SPACE, FOLDER, TrayUi.Icon.CLIPBOARD, TrayUi.Icon.SETTINGS};
        for (int i = 0; i < names.length; i++) {
            int target = i;
            navigation[i] = ui.new Destination(names[i], icons[i], stacked, () -> switchPage(target));
            if (rail) bar.addView(navigation[i], new LinearLayout.LayoutParams(-1, -2));
            else ui.stretch(bar, navigation[i]);
        }
        return bar;
    }

    private void configureInsets(LinearLayout root, LinearLayout navigationBar, boolean rail) {
        boolean light = (getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK)
            != Configuration.UI_MODE_NIGHT_YES;
        getWindow().setStatusBarColor(Color.TRANSPARENT);
        getWindow().setNavigationBarColor(Color.TRANSPARENT);
        getWindow().setNavigationBarContrastEnforced(false);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            getWindow().setDecorFitsSystemWindows(false);
            WindowInsetsController controller = getWindow().getInsetsController();
            int mask = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS
                | WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
            if (controller != null) controller.setSystemBarsAppearance(light ? mask : 0, mask);
            root.setOnApplyWindowInsetsListener((view, insets) -> {
                Insets system = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                Insets keyboard = insets.getInsets(WindowInsets.Type.ime());
                view.setPadding(system.left, system.top, system.right, Math.max(system.bottom, keyboard.bottom));
                if (!rail) navigationBar.setVisibility(insets.isVisible(WindowInsets.Type.ime()) ? View.GONE : View.VISIBLE);
                return insets;
            });
        } else {
            int layout = View.SYSTEM_UI_FLAG_LAYOUT_STABLE | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION;
            int appearance = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
            getWindow().getDecorView().setSystemUiVisibility(light ? layout | appearance : layout);
            root.setOnApplyWindowInsetsListener((view, insets) -> {
                view.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(),
                    insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
                return insets;
            });
        }
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
        if (app != null) app.setClipboardFocused(focused);
    }

    @Override
    protected void onPause() {
        app.setClipboardFocused(false);
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        if (hasWindowFocus()) app.setClipboardFocused(false);
        feedback.removeCallbacks(dismissFeedback);
        work.shutdown();
        super.onDestroy();
    }

    private void switchPage(int target) {
        if (selection != target) scroll.scrollTo(0, 0);
        selection = target;
        for (int i = 0; i < navigation.length; i++) navigation[i].showSelected(i == target);
        render();
        if (target == SPACE) {
            run(app.workspace::state, opened -> {
                String selected = app.settings.activeSpace();
                if (!selected.isEmpty()) activeSpace = selected;
                showWorkspace(opened);
            });
        } else if (target == FILES && app.settings.paired()) {
            run(app.relay::files, this::showFiles);
        } else if (target == CLIPBOARD && app.settings.paired()) {
            run(app.relay::clipboard, this::showClipboard);
        }
    }

    private void render() {
        toolbar.removeAllViews();
        page.removeAllViews();
        if (selection != SPACE) toolbarTitle(PAGE_TITLES[selection]);
        if (selection == SPACE) renderWorkspace();
        else if (selection == FILES) renderFiles();
        else if (selection == CLIPBOARD) renderClipboard();
        else renderSettings();
    }

    private void toolbarTitle(String title) {
        TextView heading = ui.heading(title, 18, tray_text);
        heading.setSingleLine(true);
        heading.setEllipsize(TextUtils.TruncateAt.END);
        ui.stretch(toolbar, heading);
    }
    private void renderSettings() {
        boolean paired = app.settings.paired();
        LinearLayout connection = ui.row();
        ui.stretch(connection, ui.heading("中转服务器", 16, tray_text));
        connection.addView(ui.text(paired ? "已配对" : "未配对", 13, paired ? tray_primary : tray_muted, true));
        connection.setPadding(0, dp(6), 0, dp(6));
        page.addView(connection);
        if (paired && !editingConnection) {
            LinearLayout deviceRow = ui.row();
            deviceRow.setPadding(0, dp(4), 0, dp(6));
            LinearLayout device = ui.column();
            device.addView(ui.text(app.settings.deviceName(), 16, tray_text, true));
            ui.gap(device, 4);
            device.addView(endpointLabel(app.settings.server()));
            ui.stretch(deviceRow, device);
            deviceRow.addView(ui.iconButton("编辑连接", EDIT, tray_primary, () -> {
                editingConnection = true;
                render();
            }));
            deviceRow.addView(ui.iconButton("连接操作", MORE, tray_muted, anchor -> {
                PopupMenu menu = new PopupMenu(this, anchor);
                menuAction(menu, "已配对设备", () -> run(app.relay::devices, this::showDevices));
                menuAction(menu, "解除本机配对", () -> confirm("解除本机配对？", () -> run(() -> {
                    app.settings.unpair();
                    return null;
                }, ignored -> resetConnectionView())));
                menu.show();
            }));
            page.addView(deviceRow);
        } else {
            ui.gap(page, 6);
            renderConnectionFields(page);
        }

        ui.gap(page, 8);
        ui.rule(page);
        Switch enable = new Switch(this);
        enable.setText("本地 MCP");
        enable.setTextSize(16);
        enable.setTextColor(ui.color(tray_text));
        enable.setMinHeight(dp(48));
        enable.setShowText(false);
        enable.setChecked(app.settings.mcpEnabled());
        enable.setOnClickListener(view -> {
            boolean requested = enable.isChecked();
            enable.setEnabled(false);
            run(() -> {
                app.setMcpEnabled(requested);
                return null;
            }, ignored -> renderIf(SETTINGS), error -> {
                renderIf(SETTINGS);
                reportError(error);
            });
        });
        page.addView(enable, new LinearLayout.LayoutParams(-1, -2));
        if (!app.mcpError().isEmpty()) page.addView(ui.text(app.mcpError(), 14, tray_error, false));
        if (app.mcpRunning()) {
            String address = "http://127.0.0.1:" + AppSettings.MCP_PORT + "/mcp";
            LinearLayout endpoint = ui.row();
            ui.stretch(endpoint, endpointLabel(address));
            endpoint.addView(ui.iconButton("复制连接配置", COPY, tray_primary, () -> run(() ->
                new JSONObject().put("url", address)
                    .put("headers", new JSONObject().put("Authorization", "Bearer " + app.settings.mcpToken())).toString(2),
                config -> {
                    try {
                        app.writeClipboard(config, true);
                        notice("连接配置已复制");
                    } catch (Exception exception) { reportError(exception); }
                })));
            page.addView(endpoint);
        }
    }

    private TextView endpointLabel(String address) {
        TextView label = ui.text(address, 13, tray_muted, false);
        label.setTypeface(Typeface.MONOSPACE);
        label.setTextIsSelectable(true);
        return label;
    }

    private void renderConnectionFields(LinearLayout panel) {
        EditText server = ui.field("https://", app.settings.server(), InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        EditText name = ui.field("", app.settings.deviceName(), InputType.TYPE_CLASS_TEXT);
        EditText code = ui.field("", "", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        ui.labeledField(panel, "服务器地址", server);
        ui.gap(panel, 12);
        ui.labeledField(panel, "设备名称", name);
        ui.gap(panel, 12);
        ui.labeledField(panel, "配对码", code);
        ui.gap(panel, 16);
        panel.addView(actions(
            ui.button("保存", null, TONAL, () -> {
                String url = value(server);
                String deviceName = value(name);
                run(() -> {
                    app.settings.saveSettings(url, deviceName);
                    return null;
                }, ignored -> {
                    resetConnectionView();
                    notice("设置已保存");
                });
            }),
            ui.button("配对", null, PRIMARY, () -> {
                String url = value(server);
                String deviceName = value(name);
                String pairing = value(code);
                run(() -> app.relay.pair(url, deviceName, pairing), ignored -> {
                    resetConnectionView();
                    notice("配对成功");
                });
            })
        ));
        if (editingConnection) panel.addView(ui.button("取消", null, TEXT, () -> {
            editingConnection = false;
            render();
        }));
    }

    private void resetConnectionView() {
        hideKeyboard();
        workspace = null;
        activeSpace = "";
        files = new JSONArray();
        sharedClipboard = null;
        editingConnection = false;
        render();
    }

    private void showDevices(JSONArray devices) {
        StringBuilder list = new StringBuilder();
        for (int index = 0; index < devices.length(); index++) {
            JSONObject device = devices.optJSONObject(index);
            if (device == null) continue;
            if (list.length() > 0) list.append('\n');
            list.append(device.optString("name", device.optString("deviceName", device.optString("id"))));
        }
        showDialog(dialog().setTitle("已配对设备").setMessage(list.length() == 0 ? "暂无设备" : list)
            .setPositiveButton("关闭", null));
    }

    private void renderClipboard() {
        if (!app.settings.paired()) {
            pairNotice();
            return;
        }
        toolbar.addView(ui.iconButton("刷新", SYNC, tray_muted, () -> run(app.relay::clipboard, this::showClipboard)));
        toolbar.addView(ui.iconButton("发送本机剪贴板", SEND, tray_primary, () -> run(() -> {
            String clip = app.readClipboard();
            if (clip.isEmpty()) throw new IllegalArgumentException("剪贴板里没有文本");
            return app.relay.sendClipboard(clip);
        }, updated -> {
            showClipboard(updated);
            notice("已发送");
        })));
        toolbar.addView(ui.iconButton("接收并复制", COPY, tray_primary, () -> run(() -> {
            JSONObject clip = app.relay.clipboard();
            if (clip.optString("text").isEmpty()) throw new IllegalArgumentException("暂无同步文本");
            app.writeClipboard(clip.optString("text"));
            return clip;
        }, updated -> {
            showClipboard(updated);
            notice("已复制");
        })));

        String incoming = sharedClipboard == null ? "" : sharedClipboard.optString("text");
        if (incoming.isEmpty()) {
            empty(sharedClipboard == null && pendingRequests > 0 ? "正在获取" : "暂无同步文本");
            return;
        }

        ui.gap(page, 6);
        int previewLimit = incoming.offsetByCodePoints(0, Math.min(incoming.codePointCount(0, incoming.length()), 4000));
        TextView content = ui.text(incoming.substring(0, previewLimit), 17, tray_text, false);
        content.setLineSpacing(dp(4), 1);
        content.setTextIsSelectable(true);
        page.addView(content);
        String updated = localTime(sharedClipboard.optString("updatedAt"));
        if (!updated.isEmpty()) {
            ui.gap(page, 8);
            page.addView(ui.text(updated, 12, tray_muted, false));
        }
        if (previewLimit < incoming.length()) {
            page.addView(ui.button("查看全文", null, TEXT, () -> showDialog(dialog()
                .setTitle("剪贴板").setMessage(incoming).setPositiveButton("关闭", null))),
                new LinearLayout.LayoutParams(-2, -2));
        }
    }

    private void showClipboard(JSONObject value) {
        sharedClipboard = value;
        renderIf(CLIPBOARD);
    }

    private void renderFiles() {
        if (!app.settings.paired()) {
            pairNotice();
            return;
        }
        toolbar.addView(ui.iconButton("刷新", SYNC, tray_muted, () -> run(app.relay::files, this::showFiles)));
        toolbar.addView(ui.iconButton("添加文件", PLUS, tray_primary, this::selectUpload));
        if (files.length() == 0) {
            empty("暂无共享文件");
            return;
        }

        Map<String, ArrayList<JSONObject>> groups = new TreeMap<>();
        for (int index = 0; index < files.length(); index++) {
            JSONObject item = files.optJSONObject(index);
            if (item == null) continue;
            groups.computeIfAbsent(item.optString("folder"), key -> new ArrayList<>()).add(item);
        }
        for (var group : groups.entrySet()) {
            String path = group.getKey();
            if (!path.isEmpty()) {
                LinearLayout folder = ui.row();
                folder.setPadding(0, dp(5), 0, dp(3));
                folder.addView(ui.iconView(FOLDER, tray_primary, 15));
                ui.gap(folder, 8);
                folder.addView(ui.heading(path, 14, tray_muted));
                page.addView(folder);
            }
            boolean first = true;
            for (JSONObject item : group.getValue()) {
                if (!first) ui.rule(page);
                page.addView(fileRow(item));
                first = false;
            }
            ui.gap(page, 10);
        }
    }

    private LinearLayout fileRow(JSONObject item) {
        String name = item.optString("name", "文件");
        String id = item.optString("id");
        LinearLayout row = ui.row();
        row.setMinimumHeight(dp(52));
        row.setPadding(0, dp(3), 0, dp(3));
        row.addView(ui.iconView(FILE, tray_primary, 19));
        ui.gap(row, 10);
        LinearLayout label = ui.column();
        TextView file = ui.text(name, 15, tray_text, true);
        file.setMaxLines(2);
        file.setEllipsize(TextUtils.TruncateAt.END);
        label.addView(file);
        ui.gap(label, 4);
        label.addView(ui.text(fileSizeLabel(item.optLong("size")), 12, tray_muted, false));
        ui.stretch(row, label);
        row.addView(ui.iconButton("下载", DOWNLOAD, tray_primary, () -> {
            pendingDownload = id;
            Intent save = new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/octet-stream")
                .addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_TITLE, name);
            startActivityForResult(save, PICK_DOWNLOAD);
        }));
        row.addView(ui.iconButton("文件操作", MORE, tray_muted, anchor -> {
            PopupMenu menu = new PopupMenu(this, anchor);
            menuAction(menu, "删除", () -> confirm("删除共享文件？", () -> run(() -> {
                app.relay.deleteFile(id);
                return app.relay.files();
            }, this::showFiles)));
            menu.show();
        }));
        return row;
    }

    private void selectUpload() {
        EditText folder = ui.field("", uploadFolder, InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        LinearLayout form = ui.column();
        form.setPadding(dp(20), dp(12), dp(20), 0);
        ui.labeledField(form, "上传目录（可选）", folder);
        showDialog(dialog().setTitle("添加文件").setView(form)
            .setNegativeButton("取消", null)
            .setPositiveButton("选择文件", (dialog, which) -> {
                uploadFolder = value(folder);
                Intent pick = new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE);
                startActivityForResult(pick, PICK_UPLOAD);
            }));
    }

    private void showFiles(JSONArray incoming) {
        files = incoming;
        renderIf(FILES);
    }

    private static String fileSizeLabel(long bytes) {
        if (bytes >= 1024 * 1024) return String.format(Locale.ROOT, "%.1f MB", bytes / (1024.0 * 1024));
        if (bytes >= 1024) return String.format(Locale.ROOT, "%.1f KB", bytes / 1024.0);
        return bytes + " B";
    }

    private String localTime(String value) {
        try {
            return DateTimeFormatter.ofPattern("MM-dd  HH:mm", Locale.getDefault())
                .withZone(ZoneId.systemDefault()).format(Instant.parse(value));
        } catch (java.time.DateTimeException error) {
            return "";
        }
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
        if (workspace == null) {
            workspaceToolbar(new JSONArray(), null);
            return;
        }
        try {
            JSONArray spaces = WorkspaceStore.spaces(workspace);
            JSONObject selected = chooseSpace(spaces);
            workspaceToolbar(spaces, selected);
            if (workspace.has("remote")) renderConflict();
            if (selected != null) renderComponents(selected);
            else empty("暂无空间");
            renderSsh(workspace.getJSONObject("document").optJSONArray("sshHosts"));
        } catch (Exception error) { reportError(error); }
    }

    private void createSpace() {
        edit("空间名称", "", false, name -> run(() -> {
            JSONObject space = app.workspace.createSpace(name);
            String id = space.getString("id");
            app.settings.selectSpace(id);
            return new Object[]{id, app.workspace.state()};
        }, created -> {
            activeSpace = (String) created[0];
            showWorkspace((JSONObject) created[1]);
        }));
    }

    private JSONObject chooseSpace(JSONArray spaces) {
        String selected = activeSpace.isEmpty()
            ? workspace.optJSONObject("document").optString("activeSpaceId")
            : activeSpace;
        for (int i = 0; i < spaces.length(); i++) {
            JSONObject space = spaces.optJSONObject(i);
            if (space != null && space.optString("id").equals(selected)) {
                activeSpace = selected;
                return space;
            }
        }
        JSONObject first = spaces.optJSONObject(0);
        if (first != null) activeSpace = first.optString("id");
        return first;
    }

    private void workspaceToolbar(JSONArray spaces, JSONObject selected) {
        if (selected == null) {
            toolbarTitle(PAGE_TITLES[SPACE]);
            toolbar.addView(ui.iconButton("新建空间", PLUS, tray_primary, this::createSpace));
        } else {
            String name = selected.optString("name", "空间");
            Button picker = ui.button(name, CHEVRON, TEXT, () -> showSpaces(spaces));
            picker.setTextSize(18);
            picker.setGravity(Gravity.CENTER_VERTICAL | Gravity.START);
            picker.setPadding(0, 0, dp(4), 0);
            picker.setSingleLine(true);
            picker.setEllipsize(TextUtils.TruncateAt.END);
            picker.setContentDescription("切换空间，" + name);
            ui.stretch(toolbar, picker);
            toolbar.addView(ui.iconButton("添加", PLUS, tray_primary, anchor -> {
                PopupMenu menu = new PopupMenu(this, anchor);
                String id = selected.optString("id");
                menuAction(menu, "添加便签", () -> addComponent(id, "note", "便签"));
                menuAction(menu, "添加待办", () -> addComponent(id, "todo", "待办"));
                menuAction(menu, "新建空间", this::createSpace);
                menu.show();
            }));
        }
        if (app.settings.paired()) {
            boolean dirty = workspace != null && workspace.optBoolean("dirty");
            var sync = ui.iconButton(dirty ? "同步（有待同步修改）" : "同步", SYNC, tray_primary, () ->
                run(app.workspace::push, updated -> {
                    showWorkspace(updated);
                    if (!updated.has("remote")) notice("已同步");
                }));
            if (dirty) sync.setBackground(ui.ripple(tray_selected, 16, tray_primary));
            toolbar.addView(sync);
        }
        toolbar.addView(ui.iconButton("空间操作", MORE, tray_muted, anchor -> {
            PopupMenu menu = new PopupMenu(this, anchor);
            if (selected != null) {
                String id = selected.optString("id");
                String name = selected.optString("name", "空间");
                menuAction(menu, "重命名空间", () -> edit("空间名称", name, false, label ->
                    run(() -> app.workspace.renameSpace(id, label), this::showWorkspace)));
            }
            if (app.settings.paired()) {
                menuAction(menu, "拉取", () -> run(app.workspace::pull, this::showWorkspace));
            } else {
                menuAction(menu, "连接中转服务器", () -> switchPage(SETTINGS));
            }
            menu.show();
        }));
    }

    private void showSpaces(JSONArray spaces) {
        String[] names = new String[spaces.length()];
        int checked = -1;
        for (int i = 0; i < names.length; i++) {
            JSONObject space = spaces.optJSONObject(i);
            names[i] = space.optString("name", "空间");
            if (activeSpace.equals(space.optString("id"))) checked = i;
        }
        showDialog(dialog().setTitle("切换空间").setSingleChoiceItems(names, checked, (dialog, which) -> {
            dialog.dismiss();
            String target = spaces.optJSONObject(which).optString("id");
            run(() -> {
                app.settings.selectSpace(target);
                return app.workspace.state();
            }, opened -> {
                activeSpace = target;
                scroll.scrollTo(0, 0);
                showWorkspace(opened);
            });
        }).setNegativeButton("取消", null));
    }

    private void renderConflict() {
        ui.gap(page, 12);
        LinearLayout conflict = ui.surface(tray_surface, 16, 14);
        conflict.addView(ui.heading("同步冲突", 17, tray_error));
        ui.gap(conflict, 10);
        conflict.addView(actions(
            ui.button("保留本机版本", null, TONAL, () -> confirm("保留本机版本？下次同步将提交本机内容。",
                () -> run(() -> app.workspace.resolve("local"), this::showWorkspace))),
            ui.button("采用服务器版本", null, TEXT, () -> confirm("用服务器版本替换本机内容？",
                () -> run(() -> app.workspace.resolve("remote"), this::showWorkspace)))
        ));
        page.addView(conflict);
    }

    private void renderComponents(JSONObject selected) {
        String spaceId = selected.optString("id");
        JSONArray items = selected.optJSONArray("items");
        int shown = 0;
        if (items != null) for (int index = 0; index < items.length(); index++) {
            JSONObject item = items.optJSONObject(index);
            if (item == null) continue;
            JSONObject data = item.optJSONObject("data");
            if (data == null) data = new JSONObject();
            View component = switch (item.optString("kind")) {
                case "note" -> renderNote(spaceId, item, data);
                case "todo" -> renderTodo(spaceId, item, data);
                case "board" -> renderBoard(item, data);
                default -> null;
            };
            if (component != null) {
                if (shown > 0) {
                    ui.rule(page);
                    ui.gap(page, 4);
                }
                page.addView(component);
                shown++;
            }
        }
        if (shown == 0) empty("暂无便签、待办或画板");
    }

    private void addComponent(String spaceId, String kind, String title) {
        edit("组件名称", title, false, name -> run(() -> {
            app.workspace.createItem(spaceId, kind, name);
            return app.workspace.state();
        }, this::showWorkspace));
    }

    private LinearLayout componentTitle(String title, TrayUi.Icon icon, int ink) {
        LinearLayout row = ui.row();
        row.addView(ui.iconView(icon, ink, 19));
        ui.gap(row, 8);
        ui.stretch(row, ui.heading(title, 16, ink));
        return row;
    }

    private LinearLayout renderNote(String space, JSONObject item, JSONObject data) {
        LinearLayout row = ui.row();
        row.setGravity(Gravity.TOP);
        row.setPadding(0, dp(4), 0, dp(12));
        LinearLayout noteContent = ui.column();
        noteContent.setPadding(0, dp(4), 0, 0);
        noteContent.addView(componentTitle(itemTitle(item, data, "便签"), NOTE, tray_note_text));
        ui.gap(noteContent, 6);
        String note = data.optString("text");
        TextView preview = ui.text(note.isEmpty() ? "空便签" : note, 16, note.isEmpty() ? tray_muted : tray_text, false);
        preview.setLineSpacing(dp(3), 1);
        preview.setTextIsSelectable(!note.isEmpty());
        preview.setMaxLines(14);
        preview.setEllipsize(TextUtils.TruncateAt.END);
        noteContent.addView(preview);
        ui.stretch(row, noteContent);
        row.addView(ui.iconButton("编辑便签", EDIT, tray_muted, () ->
            edit("编辑便签", note, true, content ->
                run(() -> app.workspace.writeNote(space, item.optString("id"), content), this::showWorkspace))));
        return row;
    }

    private LinearLayout renderTodo(String space, JSONObject item, JSONObject data) {
        LinearLayout card = ui.column();
        LinearLayout title = componentTitle(itemTitle(item, data, "待办"), TASKS, tray_primary);
        JSONArray tasks = data.optJSONArray("tasks");
        int done = 0;
        int total = 0;
        if (tasks != null) for (int index = 0; index < tasks.length(); index++) {
            JSONObject task = tasks.optJSONObject(index);
            if (task == null) continue;
            total++;
            if (task.optBoolean("completed")) done++;
        }
        String itemId = item.optString("id");
        if (total > 0) title.addView(ui.text(done + "/" + total, 13, tray_muted, true));
        title.addView(ui.iconButton("新增待办", PLUS, tray_primary, () ->
            edit("待办内容", "", false, task -> saveTask(space, itemId, "", task, null))));
        card.addView(title);
        if (tasks != null) for (int index = 0; index < tasks.length(); index++) {
            JSONObject task = tasks.optJSONObject(index);
            if (task != null) card.addView(taskRow(space, itemId, task));
        }
        return card;
    }

    private LinearLayout taskRow(String space, String itemId, JSONObject task) {
        String taskId = task.optString("id");
        String taskText = task.optString("text");
        LinearLayout line = ui.row();
        CheckBox check = new CheckBox(this);
        check.setText(taskText);
        check.setTextColor(ui.color(tray_text));
        check.setTextSize(15);
        check.setMinHeight(dp(48));
        check.setPadding(0, 0, dp(8), 0);
        check.setChecked(task.optBoolean("completed"));
        check.setOnCheckedChangeListener((view, checked) -> saveTask(space, itemId, taskId, null, checked));
        ui.stretch(line, check);
        line.addView(ui.iconButton("待办操作", MORE, tray_muted, view -> {
            PopupMenu menu = new PopupMenu(this, view);
            menuAction(menu, "编辑", () ->
                edit("待办内容", taskText, false, value -> saveTask(space, itemId, taskId, value, null)));
            menuAction(menu, "删除", () -> confirm("删除这项待办？", () ->
                run(() -> app.workspace.removeTask(space, itemId, taskId), this::showWorkspace)));
            menu.show();
        }));
        return line;
    }

    private LinearLayout renderBoard(JSONObject item, JSONObject data) {
        LinearLayout card = ui.column();
        ui.gap(card, 8);
        card.addView(componentTitle(itemTitle(item, data, "画板"), BOARD, tray_primary));
        ui.gap(card, 8);
        BoardView board = new BoardView(data.optJSONArray("strokes"));
        var paper = ui.fill(tray_board, 8);
        paper.setStroke(dp(1), ui.color(tray_outline));
        board.setBackground(paper);
        board.setClipToOutline(true);
        card.addView(board, new LinearLayout.LayoutParams(-1, dp(144)));
        return card;
    }

    private static String itemTitle(JSONObject item, JSONObject data, String fallback) {
        String result = item.optString("title", data.optString("title", fallback));
        return result.trim().isEmpty() ? fallback : result;
    }

    private void saveTask(String space, String item, String task, String text, Boolean completed) {
        run(() -> {
            app.workspace.writeTask(space, item, task, text, completed);
            return app.workspace.state();
        }, this::showWorkspace);
    }

    private void renderSsh(JSONArray hosts) {
        if (hosts == null || hosts.length() == 0) return;
        pageHeading("SSH 主机");
        LinearLayout list = ui.column();
        for (int index = 0; index < hosts.length(); index++) {
            JSONObject host = hosts.optJSONObject(index);
            if (host == null) continue;
            LinearLayout entry = componentTitle(host.optString("name"), KEY, tray_primary);
            entry.setPadding(0, dp(6), 0, dp(4));
            list.addView(entry);
            TextView address = ui.text(host.optString("username") + "@" + host.optString("address") + ":"
                + host.optInt("port", 22), 13, tray_muted, false);
            address.setTypeface(Typeface.MONOSPACE);
            address.setTextIsSelectable(true);
            address.setPadding(dp(27), 0, 0, dp(10));
            list.addView(address);
            if (index < hosts.length() - 1) ui.rule(list);
        }
        page.addView(list);
    }

    private final class BoardView extends View {
        private final JSONArray strokes;
        private final Paint ink = new Paint(Paint.ANTI_ALIAS_FLAG);

        BoardView(JSONArray strokes) {
            super(MainActivity.this);
            this.strokes = strokes;
            setContentDescription("画板");

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
                if (erase) ink.setColor(ui.color(tray_board));
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
        run(action, success, this::reportError);
    }

    private <T> void run(Callable<T> action, Consumer<T> success, Consumer<Exception> failure) {
        pendingRequests++;
        progress.setVisibility(View.VISIBLE);
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
                    if (!isFinishing() && !isDestroyed()) failure.accept(error);
                });
            }
        });
    }

    private void finishRequest() {
        if (--pendingRequests == 0) progress.setVisibility(View.INVISIBLE);
    }

    private void renderIf(int expected) {
        if (selection == expected) render();
    }

    private void reportError(Exception error) {
        String message = error.getMessage();
        notice(message == null || message.isEmpty() ? "操作未完成" : message);
    }

    private AlertDialog.Builder dialog() {
        return new AlertDialog.Builder(this);
    }

    private AlertDialog showDialog(AlertDialog.Builder builder) {
        AlertDialog dialog = builder.show();
        if (dialog.getWindow() != null) {
            dialog.getWindow().setBackgroundDrawable(ui.fill(tray_surface, 20));
            dialog.getWindow().setDimAmount(0.42f);
        }
        for (int id : new int[]{AlertDialog.BUTTON_POSITIVE, AlertDialog.BUTTON_NEGATIVE}) {
            Button button = dialog.getButton(id);
            if (button != null) {
                button.setTextColor(ui.color(tray_primary));
                button.setMinHeight(dp(48));
            }
        }
        return dialog;
    }

    private void edit(String title, String current, boolean multiline, Consumer<String> save) {
        EditText input = ui.field(title, current,
            InputType.TYPE_CLASS_TEXT | (multiline ? InputType.TYPE_TEXT_FLAG_MULTI_LINE : 0));
        input.setSingleLine(!multiline);
        input.setSelection(input.length());
        if (multiline) {
            input.setMinLines(4);
            input.setMaxLines(8);
            input.setGravity(Gravity.TOP | Gravity.START);
        }
        LinearLayout form = ui.column();
        form.setPadding(dp(20), dp(12), dp(20), dp(6));
        form.addView(input, new LinearLayout.LayoutParams(-1, -2));
        AlertDialog editor = showDialog(dialog().setTitle(title).setView(form)
            .setPositiveButton("保存", (dialog, which) -> save.accept(value(input)))
            .setNegativeButton("取消", null));
        input.requestFocus();
        if (editor.getWindow() != null) editor.getWindow().setSoftInputMode(
            WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE | WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
    }

    private void confirm(String text, Runnable action) {
        showDialog(dialog().setMessage(text).setPositiveButton("确认", (dialog, which) -> action.run())
            .setNegativeButton("取消", null));
    }

    private void hideKeyboard() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && getWindow().getInsetsController() != null) {
            getWindow().getInsetsController().hide(WindowInsets.Type.ime());
        } else {
            getSystemService(android.view.inputmethod.InputMethodManager.class)
                .hideSoftInputFromWindow(page.getWindowToken(), 0);
        }
    }

    private void menuAction(PopupMenu menu, String title, Runnable action) {
        menu.getMenu().add(title).setOnMenuItemClickListener(entry -> {
            action.run();
            return true;
        });
    }

    private void notice(String message) {
        feedback.removeCallbacks(dismissFeedback);
        feedback.setText(message);
        feedback.setVisibility(View.VISIBLE);
        feedback.postDelayed(dismissFeedback, 3800);
    }

    private void pageHeading(String title) {
        ui.gap(page, 18);
        page.addView(ui.heading(title, 18, tray_text));
        ui.gap(page, 9);
    }

    private void empty(String message) {
        TextView label = ui.text(message, 15, tray_muted, false);
        label.setPadding(0, dp(12), 0, dp(12));
        page.addView(label);
    }

    private void pairNotice() {
        empty("当前设备尚未配对");
        page.addView(ui.button("前往设置", null, TONAL, () -> switchPage(SETTINGS)),
            new LinearLayout.LayoutParams(-2, -2));
    }

    private LinearLayout actions(Button first, Button second) {
        LinearLayout row = ui.row();
        ui.stretch(row, first);
        ui.gap(row, 8);
        ui.stretch(row, second);
        return row;
    }

    private static String value(EditText field) {
        return field.getText().toString();
    }

    private int dp(int amount) { return ui.dp(amount); }
}
