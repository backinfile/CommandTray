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
import android.widget.ImageView;
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
 * Space and transfer content lead each screen. Dark green identifies the active space and
 * shared text. Bottom navigation and the wider-screen rail give tools a stable location.
 */
public final class MainActivity extends Activity {
    private static final int PICK_UPLOAD = 10;
    private static final int PICK_DOWNLOAD = 11;
    private static final int SPACE = 0;
    private static final int FILES = 1;
    private static final int CLIPBOARD = 2;
    private static final int SETTINGS = 3;
    private final ExecutorService work = Executors.newSingleThreadExecutor();
    private final TrayUi.Destination[] navigation = new TrayUi.Destination[4];
    private CommandTrayApp app;
    private TrayUi ui;
    private LinearLayout page;
    private ScrollView scroll;
    private TextView pairingStatus;
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
        root.addView(createAppBar());
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
        page.setPadding(dp(20), dp(12), dp(20), dp(24));
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

    private LinearLayout createAppBar() {
        LinearLayout bar = ui.row();
        bar.setPadding(dp(20), dp(8), dp(20), dp(8));
        ImageView mark = new ImageView(this);
        mark.setImageResource(R.drawable.tray_mark);
        mark.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        bar.addView(mark, new LinearLayout.LayoutParams(dp(30), dp(30)));
        ui.gap(bar, 9);
        TextView name = ui.text("CommandTray", 17, tray_text, true);
        name.setSingleLine(true);
        name.setEllipsize(TextUtils.TruncateAt.END);
        ui.stretch(bar, name);
        pairingStatus = ui.status("", false);
        bar.addView(pairingStatus);
        return bar;
    }

    private LinearLayout createNavigation(boolean rail) {
        LinearLayout bar = rail ? ui.column() : ui.row();
        bar.setBackgroundColor(ui.color(tray_surface));
        bar.setPadding(dp(8), dp(rail ? 10 : 2), dp(8), dp(4));
        String[] names = {"空间", "文件", "剪贴板", "设置"};
        TrayUi.Icon[] icons = {TrayUi.Icon.SPACE, FOLDER, TrayUi.Icon.CLIPBOARD, TrayUi.Icon.SETTINGS};
        for (int i = 0; i < names.length; i++) {
            int target = i;
            navigation[i] = ui.new Destination(names[i], icons[i], () -> switchPage(target));
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
        boolean paired = app.settings.paired();
        pairingStatus.setText(paired ? "已配对" : "未配对");
        pairingStatus.setTextColor(ui.color(paired ? tray_text : tray_muted));
        pairingStatus.setBackground(ui.fill(paired ? tray_selected : tray_tonal, 16));
        page.removeAllViews();
        if (selection == SPACE) renderWorkspace();
        else if (selection == FILES) renderFiles();
        else if (selection == CLIPBOARD) renderClipboard();
        else renderSettings();
    }
    private void renderSettings() {
        page.addView(pageTitle("设置"));
        pageHeading("中转服务器");
        LinearLayout relay = ui.surface(tray_surface, 16, 16);
        page.addView(relay);
        if (!app.settings.paired() || editingConnection) {
            renderConnectionFields(relay);
        } else {
            LinearLayout deviceRow = ui.row();
            deviceRow.addView(ui.iconView(SERVER, tray_primary, 22));
            ui.gap(deviceRow, 12);
            LinearLayout device = ui.column();
            device.addView(ui.heading(app.settings.deviceName(), 17, tray_text));
            ui.gap(device, 4);
            TextView address = ui.text(app.settings.server(), 13, tray_muted, false);
            address.setTypeface(Typeface.MONOSPACE);
            address.setTextIsSelectable(true);
            device.addView(address);
            ui.stretch(deviceRow, device);
            relay.addView(deviceRow);
            ui.gap(relay, 14);
            relay.addView(actions(
                ui.button("已配对设备", null, TONAL, () -> run(app.relay::devices, this::showDevices)),
                ui.button("编辑连接", null, TEXT, () -> {
                    editingConnection = true;
                    render();
                })
            ));
            Button unpair = ui.button("解除本机配对", null, TEXT, () -> confirm("解除本机配对？", () -> run(() -> {
                app.settings.unpair();
                return null;
            }, ignored -> resetConnectionView())));
            unpair.setTextColor(ui.color(tray_error));
            relay.addView(unpair, new LinearLayout.LayoutParams(-2, -2));
        }

        pageHeading("本地 MCP");
        LinearLayout mcp = ui.surface(tray_surface, 16, 16);
        Switch enable = new Switch(this);
        enable.setText("启用本地 MCP");
        enable.setTextSize(15);
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
        mcp.addView(enable, new LinearLayout.LayoutParams(-1, -2));
        if (!app.mcpError().isEmpty()) mcp.addView(ui.text(app.mcpError(), 14, tray_error, false));
        if (app.mcpRunning()) {
            ui.rule(mcp);
            ui.gap(mcp, 12);
            TextView endpoint = ui.text("http://127.0.0.1:" + AppSettings.MCP_PORT + "/mcp", 13, tray_text, false);
            endpoint.setTypeface(Typeface.MONOSPACE);
            endpoint.setTextIsSelectable(true);
            mcp.addView(endpoint);
            ui.gap(mcp, 8);
            mcp.addView(ui.button("复制连接配置", COPY, TONAL, () -> run(() ->
                new JSONObject().put("url", "http://127.0.0.1:" + AppSettings.MCP_PORT + "/mcp")
                    .put("headers", new JSONObject().put("Authorization", "Bearer " + app.settings.mcpToken())).toString(2),
                config -> {
                    try {
                        app.writeClipboard(config, true);
                        notice("连接配置已复制");
                    } catch (Exception exception) { reportError(exception); }
                })), new LinearLayout.LayoutParams(-2, -2));
        }
        page.addView(mcp);
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
        page.addView(pageTitle("剪贴板"));
        ui.gap(page, 16);
        if (!app.settings.paired()) {
            pairNotice(TrayUi.Icon.CLIPBOARD);
            return;
        }

        LinearLayout panel = ui.surface(tray_panel, 16, 18);
        LinearLayout label = ui.row();
        label.addView(ui.iconView(TrayUi.Icon.CLIPBOARD, tray_signal, 22));
        ui.gap(label, 8);
        ui.stretch(label, ui.heading("同步文本", 15, tray_panel_text));
        label.addView(ui.iconButton("刷新", SYNC, tray_panel_muted, () -> run(app.relay::clipboard, this::showClipboard)));
        panel.addView(label);
        String incoming = sharedClipboard == null ? "" : sharedClipboard.optString("text");
        String caption = sharedClipboard == null && pendingRequests > 0 ? "正在获取" : "暂无同步文本";
        TextView content = ui.text(incoming.isEmpty() ? caption : incoming, 19,
            incoming.isEmpty() ? tray_panel_muted : tray_panel_text, false);
        content.setLineSpacing(dp(3), 1);
        content.setMaxLines(9);
        content.setEllipsize(TextUtils.TruncateAt.END);
        panel.setMinimumHeight(dp(158));
        panel.addView(content);
        if (!incoming.isEmpty()) {
            content.setTextIsSelectable(true);
            String updated = localTime(sharedClipboard.optString("updatedAt"));
            if (!updated.isEmpty()) {
                ui.gap(panel, 12);
                panel.addView(ui.text(updated, 13, tray_panel_muted, false));
            }
            if (incoming.length() > 240 || incoming.split("\\n", 8).length > 6) {
                ui.gap(panel, 8);
                panel.addView(ui.button("查看全文", null, PANEL, () -> showDialog(dialog()
                    .setTitle("同步文本").setMessage(incoming).setPositiveButton("关闭", null))),
                    new LinearLayout.LayoutParams(-2, -2));
            }
        }
        page.addView(panel);
        ui.gap(page, 14);
        page.addView(actions(
            ui.button("发送本机", SEND, TONAL, () -> run(() -> {
                String clip = app.readClipboard();
                if (clip.isEmpty()) throw new IllegalArgumentException("剪贴板里没有文本");
                return app.relay.sendClipboard(clip);
            }, updated -> {
                showClipboard(updated);
                notice("已发送");
            })),
            ui.button("接收并复制", COPY, PRIMARY, () -> run(() -> {
                JSONObject clip = app.relay.clipboard();
                if (clip.optString("text").isEmpty()) throw new IllegalArgumentException("暂无同步文本");
                app.writeClipboard(clip.optString("text"));
                return clip;
            }, updated -> {
                showClipboard(updated);
                notice("已复制");
            }))
        ));
    }

    private void showClipboard(JSONObject value) {
        sharedClipboard = value;
        renderIf(CLIPBOARD);
    }

    private void renderFiles() {
        page.addView(pageTitle("共享文件"));
        ui.gap(page, 16);
        if (!app.settings.paired()) {
            pairNotice(FOLDER);
            return;
        }
        LinearLayout toolbar = ui.row();
        ui.stretch(toolbar, ui.text(files.length() + " 个文件", 14, tray_muted, false));
        toolbar.addView(ui.iconButton("刷新", SYNC, tray_primary, () -> run(app.relay::files, this::showFiles)));
        ui.gap(toolbar, 6);
        toolbar.addView(ui.button("添加文件", PLUS, PRIMARY, this::selectUpload));
        page.addView(toolbar);
        ui.gap(page, 8);
        if (files.length() == 0) {
            page.addView(empty(FOLDER, "暂无共享文件"));
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
                folder.addView(ui.iconView(FOLDER, tray_muted, 17));
                ui.gap(folder, 8);
                folder.addView(ui.heading(path, 14, tray_muted));
                page.addView(folder);
                ui.gap(page, 8);
            }
            LinearLayout list = ui.surface(tray_surface, 16, 4);
            boolean first = true;
            for (JSONObject item : group.getValue()) {
                if (!first) ui.rule(list);
                list.addView(fileRow(item));
                first = false;
            }
            page.addView(list);
            ui.gap(page, 14);
        }
    }

    private LinearLayout fileRow(JSONObject item) {
        String name = item.optString("name", "文件");
        String id = item.optString("id");
        LinearLayout row = ui.row();
        row.setMinimumHeight(dp(66));
        row.setPadding(dp(8), dp(4), 0, dp(4));
        FrameLayout art = new FrameLayout(this);
        art.setBackground(ui.fill(tray_tonal, 10));
        art.addView(ui.iconView(FILE, tray_primary, 20), new FrameLayout.LayoutParams(dp(20), dp(20), Gravity.CENTER));
        row.addView(art, new LinearLayout.LayoutParams(dp(38), dp(40)));
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
        var more = ui.iconButton("文件操作", MORE, tray_muted, () -> {});
        more.setOnClickListener(view -> showMenu(view, "删除", () ->
            confirm("删除共享文件？", () -> run(() -> {
                app.relay.deleteFile(id);
                return app.relay.files();
            }, this::showFiles))));
        row.addView(more);
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
        LinearLayout heading = ui.row();
        ui.stretch(heading, pageTitle("工作空间"));
        heading.addView(ui.button("新建", PLUS, TONAL, this::createSpace));
        page.addView(heading);
        ui.gap(page, 12);
        if (workspace == null) return;
        try {
            JSONArray spaces = WorkspaceStore.spaces(workspace);
            JSONObject selected = chooseSpace(spaces);
            page.addView(spacePanel(spaces, selected));
            if (workspace.has("remote")) renderConflict();
            if (selected != null) renderComponents(selected);
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

    private LinearLayout spacePanel(JSONArray spaces, JSONObject selected) {
        LinearLayout panel = ui.surface(tray_panel, 16, 16);
        LinearLayout caption = ui.row();
        caption.addView(ui.iconView(TrayUi.Icon.SPACE, tray_signal, 19));
        ui.gap(caption, 8);
        ui.stretch(caption, ui.text("当前空间", 13, tray_panel_muted, true));
        String state = "";
        if (workspace.optBoolean("dirty")) state = "待同步";
        else if (workspace.optLong("revision") > 0) state = "已同步";
        if (!state.isEmpty()) caption.addView(ui.text(state, 13, tray_panel_muted, false));
        panel.addView(caption);
        if (selected == null) {
            ui.gap(panel, 14);
            panel.addView(ui.heading("暂无空间", 25, tray_panel_text));
        } else {
            String name = selected.optString("name", "空间");
            String id = selected.optString("id");
            LinearLayout space = ui.row();
            Button picker = ui.button(name, null, TEXT, () -> showSpaces(spaces));
            picker.setTextColor(ui.color(tray_panel_text));
            picker.setBackground(ui.ripple(0, 12, tray_panel_text));
            picker.setTextSize(25);
            picker.setGravity(Gravity.CENTER_VERTICAL | Gravity.START);
            picker.setPadding(0, dp(6), dp(8), dp(6));
            picker.setMaxLines(2);
            ui.stretch(space, picker);
            space.addView(ui.iconButton("切换空间", CHEVRON, tray_panel_text, () -> showSpaces(spaces)));
            space.addView(ui.iconButton("重命名空间", EDIT, tray_panel_muted, () ->
                edit("空间名称", name, false, label -> run(() -> app.workspace.renameSpace(id, label), this::showWorkspace))));
            panel.addView(space);
        }
        ui.gap(panel, 8);
        if (app.settings.paired()) {
            panel.addView(actions(
                ui.button("拉取", DOWNLOAD, PANEL, () -> run(app.workspace::pull, this::showWorkspace)),
                ui.button("同步", SYNC, SIGNAL, () -> run(app.workspace::push, this::showWorkspace))
            ));
        } else {
            panel.addView(ui.button("连接中转服务器", null, PANEL, () -> switchPage(SETTINGS)),
                new LinearLayout.LayoutParams(-2, -2));
        }
        return panel;
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
        ui.gap(page, 8);
        LinearLayout controls = ui.row();
        ui.stretch(controls, ui.heading("组件", 20, tray_text));
        controls.addView(ui.button("添加组件", PLUS, TEXT, () -> showDialog(dialog()
            .setTitle("添加组件")
            .setItems(new String[]{"便签", "待办"}, (dialog, which) -> {
                if (which == 0) addComponent(spaceId, "note", "便签");
                else addComponent(spaceId, "todo", "待办");
            }).setNegativeButton("取消", null))));
        page.addView(controls);
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
                if (shown > 0) ui.gap(page, 10);
                page.addView(component);
                shown++;
            }
        }
        if (shown == 0) page.addView(empty(TrayUi.Icon.SPACE, "暂无便签、待办或画板"));
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
        LinearLayout card = ui.surface(tray_note, 16, 14);
        card.setPadding(dp(14), dp(6), dp(14), dp(12));
        LinearLayout title = componentTitle(itemTitle(item, data, "便签"), NOTE, tray_note_text);
        String note = data.optString("text");
        title.addView(ui.iconButton("编辑便签", EDIT, tray_note_text, () ->
            edit("编辑便签", note, true, content ->
                run(() -> app.workspace.writeNote(space, item.optString("id"), content), this::showWorkspace))));
        card.addView(title);
        TextView preview = ui.text(note.isEmpty() ? "空便签" : note, 15, tray_note_text, false);
        preview.setMaxLines(6);
        preview.setEllipsize(TextUtils.TruncateAt.END);
        preview.setLineSpacing(dp(3), 1);
        card.addView(preview);
        return card;
    }

    private LinearLayout renderTodo(String space, JSONObject item, JSONObject data) {
        LinearLayout card = ui.surface(tray_surface, 16, 14);
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
        if (total > 0) title.addView(ui.text(done + "/" + total, 13, tray_muted, true));
        card.addView(title);
        String itemId = item.optString("id");
        if (tasks != null) for (int index = 0; index < tasks.length(); index++) {
            JSONObject task = tasks.optJSONObject(index);
            if (task != null) card.addView(taskRow(space, itemId, task));
        }
        card.addView(ui.button("新增待办", PLUS, TEXT, () ->
            edit("待办内容", "", false, task -> saveTask(space, itemId, "", task, null))),
            new LinearLayout.LayoutParams(-2, -2));
        return card;
    }

    private LinearLayout taskRow(String space, String itemId, JSONObject task) {
        String taskId = task.optString("id");
        String taskText = task.optString("text");
        LinearLayout line = ui.row();
        CheckBox check = new CheckBox(this);
        check.setText(taskText);
        check.setTextColor(ui.color(tray_text));
        check.setMinHeight(dp(48));
        check.setPadding(0, 0, dp(8), 0);
        check.setChecked(task.optBoolean("completed"));
        check.setOnCheckedChangeListener((view, checked) -> saveTask(space, itemId, taskId, null, checked));
        ui.stretch(line, check);
        var more = ui.iconButton("待办操作", MORE, tray_muted, () -> {});
        more.setOnClickListener(view -> {
            PopupMenu menu = new PopupMenu(this, view);
            menu.getMenu().add("编辑").setOnMenuItemClickListener(entry -> {
                edit("待办内容", taskText, false, value -> saveTask(space, itemId, taskId, value, null));
                return true;
            });
            menu.getMenu().add("删除").setOnMenuItemClickListener(entry -> {
                confirm("删除这项待办？", () ->
                    run(() -> app.workspace.removeTask(space, itemId, taskId), this::showWorkspace));
                return true;
            });
            menu.show();
        });
        line.addView(more);
        return line;
    }

    private LinearLayout renderBoard(JSONObject item, JSONObject data) {
        LinearLayout card = ui.surface(tray_surface, 16, 14);
        card.addView(componentTitle(itemTitle(item, data, "画板"), BOARD, tray_primary));
        ui.gap(card, 12);
        BoardView board = new BoardView(data.optJSONArray("strokes"));
        board.setBackground(ui.fill(tray_board, 8));
        board.setClipToOutline(true);
        card.addView(board, new LinearLayout.LayoutParams(-1, dp(190)));
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
        LinearLayout list = ui.surface(tray_surface, 16, 12);
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

    private void showMenu(View anchor, String title, Runnable action) {
        PopupMenu menu = new PopupMenu(this, anchor);
        menu.getMenu().add(title).setOnMenuItemClickListener(entry -> {
            action.run();
            return true;
        });
        menu.show();
    }

    private void notice(String message) {
        feedback.removeCallbacks(dismissFeedback);
        feedback.setText(message);
        feedback.setVisibility(View.VISIBLE);
        feedback.postDelayed(dismissFeedback, 3800);
    }

    private TextView pageTitle(String title) {
        TextView text = ui.heading(title, 30, tray_text);
        text.setLetterSpacing(-0.02f);
        return text;
    }

    private void pageHeading(String title) {
        ui.gap(page, 18);
        page.addView(ui.heading(title, 18, tray_text));
        ui.gap(page, 9);
    }

    private LinearLayout empty(TrayUi.Icon icon, String message) {
        LinearLayout area = ui.surface(tray_tonal, 16, 20);
        area.setGravity(Gravity.CENTER_HORIZONTAL);
        area.addView(ui.iconView(icon, tray_primary, 27));
        ui.gap(area, 12);
        TextView label = ui.text(message, 15, tray_muted, false);
        label.setGravity(Gravity.CENTER);
        area.addView(label);
        return area;
    }

    private void pairNotice(TrayUi.Icon icon) {
        LinearLayout state = empty(icon, "当前设备尚未配对");
        ui.gap(state, 14);
        state.addView(ui.button("前往设置", null, PRIMARY, () -> switchPage(SETTINGS)),
            new LinearLayout.LayoutParams(-2, -2));
        page.addView(state);
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
