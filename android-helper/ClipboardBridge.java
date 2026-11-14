package org.sonicroute.android;

import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import org.json.JSONObject;
import java.io.BufferedReader;
import java.io.InputStreamReader;

/** Opt-in text clipboard stream. No polling, history, clipboard logging or initial clipboard upload. */
public final class ClipboardBridge {
    private static final int MAX_TEXT_BYTES = 65536;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ClipboardManager clipboard;
    private String last;
    private long revision, timestamp;
    private String pendingText;
    private long pendingRevision;
    private boolean commandPending;
    private boolean pending;
    private final ClipboardManager.OnPrimaryClipChangedListener listener = () -> {
        if (!pending) { pending = true; handler.post(this::emit); }
    };
    private ClipboardBridge(Context context) {
        clipboard = (ClipboardManager) context.getSystemService(Context.CLIPBOARD_SERVICE);
        if (clipboard == null) throw new IllegalStateException("Clipboard unavailable");
        android.content.ClipDescription description = clipboard.getPrimaryClipDescription();
        timestamp = description == null ? 0 : description.getTimestamp();
    }
    private static boolean valid(String value) {
        return value != null && value.length() <= MAX_TEXT_BYTES
            && value.getBytes(java.nio.charset.StandardCharsets.UTF_8).length <= MAX_TEXT_BYTES;
    }
    private static void send(JSONObject value) { System.out.println(value.toString()); System.out.flush(); }
    private void emit() {
        pending = false;
        try {
            ClipData clip = clipboard.getPrimaryClip();
            if (clip == null || clip.getItemCount() == 0) return;
            CharSequence text = clip.getItemAt(0).getText();
            if (text == null || text.length() > MAX_TEXT_BYTES) return;
            String value = text.toString();
            if (!valid(value) || value.equals(last)) return;
            last = value; timestamp = clip.getDescription().getTimestamp(); ++revision;
            send(new JSONObject().put("text", value).put("revision", revision));
        } catch (Exception ignored) { reportError(); }
    }
    private void command(String line) {
        try {
            JSONObject message = new JSONObject(line);
            String value = message.getString("text");
            if (!valid(value) || value.equals(last)) return;
            // A phone copy that happened while a computer command was in flight wins.
            android.content.ClipDescription description = clipboard.getPrimaryClipDescription();
            if (description != null && description.getTimestamp() != timestamp) emit();
            if (message.getLong("revision") != revision) return;
            // Record before writing so the resulting device callback is not sent back.
            last = value;
            clipboard.setPrimaryClip(ClipData.newPlainText("SonicRoute", value));
            description = clipboard.getPrimaryClipDescription();
            timestamp = description == null ? 0 : description.getTimestamp();
        } catch (Exception ignored) { reportError(); }
    }
    private synchronized void queueCommand(String line) {
        // Keep one pending text, even if many changes arrive while Android is busy.
        try {
            JSONObject message = new JSONObject(line);
            String value = message.getString("text");
            if (!valid(value)) return;
            pendingText = value; pendingRevision = message.getLong("revision");
            if (!commandPending) {
                commandPending = true;
                handler.post(() -> {
                    String text; long expected;
                    synchronized (ClipboardBridge.this) {
                        text = pendingText; expected = pendingRevision; pendingText = null; commandPending = false;
                    }
                    try { command(new JSONObject().put("text", text).put("revision", expected).toString()); }
                    catch (Exception ignored) { reportError(); }
                });
            }
        } catch (Exception ignored) { reportError(); }
    }
    private static void reportError() {
        try { send(new JSONObject().put("error", "手机暂时无法同步剪贴板，请检查系统权限。")); }
        catch (Exception ignored) { }
    }
    private void close() {
        clipboard.removePrimaryClipChangedListener(listener);
        handler.removeCallbacksAndMessages(null);
        last = pendingText = null;
        System.exit(0);
    }
    public static void main(String[] args) {
        try {
            Looper.prepareMainLooper();
            AndroidWorkarounds.apply();
            Context context = ShellContext.get();
            ClipboardBridge bridge = new ClipboardBridge(context);
            bridge.clipboard.addPrimaryClipChangedListener(bridge.listener);
            send(new JSONObject().put("ready", true).put("revision", 0));
            Thread input = new Thread(() -> {
                try {
                    BufferedReader reader = new BufferedReader(new InputStreamReader(System.in, "UTF-8"));
                    String line;
                    while ((line = reader.readLine()) != null) {
                        if ("quit".equals(line)) break;
                        if (line.length() > MAX_TEXT_BYTES * 6 + 32) break;
                        bridge.queueCommand(line);
                    }
                } catch (Exception ignored) { }
                bridge.handler.post(bridge::close);
            }, "sonicroute-clipboard-input");
            input.setDaemon(true); input.start();
            Looper.loop();
        } catch (Throwable ignored) { reportError(); System.exit(1); }
    }
}
