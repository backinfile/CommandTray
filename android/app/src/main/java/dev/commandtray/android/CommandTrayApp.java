package dev.commandtray.android;

import android.app.Application;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.os.Handler;
import android.os.Looper;

import java.util.concurrent.Callable;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;

public final class CommandTrayApp extends Application {
    final Object workLock = new Object();
    AppSettings settings;
    RelayClient relay;
    WorkspaceStore workspace;
    private McpServer mcp;
    private String mcpError = "";
    private volatile boolean clipboardFocused;
    private final Handler main = new Handler(Looper.getMainLooper());

    @Override
    public void onCreate() {
        super.onCreate();
        settings = new AppSettings(this);
        relay = new RelayClient(settings);
        workspace = new WorkspaceStore(this, relay, settings);
        if (settings.mcpEnabled()) {
            try { startMcp(); }
            catch (Exception exception) { mcpError = "无法启动本地 MCP"; }
        }
    }

    synchronized void setMcpEnabled(boolean enabled) throws Exception {
        if (enabled) {
            settings.enableMcp(true);
            try {
                startMcp();
            } catch (Exception error) {
                settings.enableMcp(false);
                throw error;
            }
        } else {
            settings.enableMcp(false);
            if (mcp != null) mcp.close();
            mcp = null;
        }
        mcpError = "";
    }

    private void startMcp() throws Exception {
        if (mcp == null) mcp = new McpServer(new McpTools(this), settings.mcpToken());
    }

    synchronized boolean mcpRunning() {
        return mcp != null;
    }

    synchronized String mcpError() {
        return mcpError;
    }

    void setClipboardFocused(boolean focused) {
        clipboardFocused = focused;
    }

    String readClipboard() throws Exception {
        return onMain(() -> {
            requireClipboardFocus();
            ClipboardManager clipboard = getSystemService(ClipboardManager.class);
            ClipData clip = clipboard.getPrimaryClip();
            return clip == null || clip.getItemCount() == 0 || clip.getItemAt(0).getText() == null
                ? "" : clip.getItemAt(0).getText().toString();
        });
    }

    void writeClipboard(String text) throws Exception {
        onMain(() -> {
            requireClipboardFocus();
            getSystemService(ClipboardManager.class).setPrimaryClip(ClipData.newPlainText("CommandTray", text));
            return null;
        });
    }

    private void requireClipboardFocus() throws Exception {
        if (!clipboardFocused) throw new IllegalStateException("请将 CommandTray 切到前台再操作系统剪贴板");
    }

    private <T> T onMain(Callable<T> work) throws Exception {
        if (Looper.myLooper() == main.getLooper()) return work.call();
        FutureTask<T> task = new FutureTask<>(work);
        main.post(task);
        try { return task.get(6, TimeUnit.SECONDS); }
        catch (java.util.concurrent.ExecutionException error) {
            if (error.getCause() instanceof Exception cause) throw cause;
            throw error;
        } catch (java.util.concurrent.TimeoutException error) {
            task.cancel(false);
            throw error;
        }
    }
}
