/* On-demand, shell-only Unicode editor bridge. SPDX-License-Identifier: Apache-2.0 */
package com.genymobile.scrcpy.control;

import android.accessibilityservice.AccessibilityServiceInfo;
import android.app.UiAutomation;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import android.os.Bundle;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.SystemClock;
import android.util.SparseArray;
import android.view.accessibility.AccessibilityNodeInfo;
import android.view.accessibility.AccessibilityWindowInfo;
import com.genymobile.scrcpy.util.Ln;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.util.List;

/** One lazy connection per phone, shared by video processes. No events or text are logged/stored. */
public final class UnicodeInputBridge {
    private static final String SOCKET = "sonicroute-unicode-v1";
    private static Server owned;
    private UnicodeInputBridge() { }

    public static int commit(int displayId, String packageName, String text) {
        if (displayId <= 0 || packageName == null || text.isEmpty()) return 0;
        byte[] bytes = text.getBytes(StandardCharsets.UTF_8);
        if (bytes.length > 4096) return 0;
        boolean sent = false;
        try (LocalSocket socket = connect()) {
            socket.setSoTimeout(6000);
            DataOutputStream output = new DataOutputStream(socket.getOutputStream());
            output.writeInt(displayId); output.writeUTF(packageName); output.writeLong(SystemClock.uptimeMillis() + 5000);
            output.writeInt(bytes.length); output.write(bytes); output.flush(); sent = true;
            return new DataInputStream(socket.getInputStream()).readInt();
        } catch (IOException | RuntimeException e) {
            Ln.w("Unicode editor connection unavailable"); return sent ? -1 : 0; // No replay after an uncertain commit.
        }
    }
    private static LocalSocket open() throws IOException {
        LocalSocket socket = new LocalSocket();
        try { socket.connect(new LocalSocketAddress(SOCKET)); return socket; }
        catch (IOException e) { socket.close(); throw e; }
    }
    private static synchronized LocalSocket connect() throws IOException {
        try { return open(); }
        catch (IOException first) {
            if (owned == null) {
                try { owned = new Server(); owned.start(); }
                catch (IOException collision) { /* Another video process may have just created the shared listener. */ }
            }
            return open();
        }
    }
    public static synchronized void closeOwned() {
        if (owned != null) { owned.close(); owned = null; }
    }
    private static final class Server {
        private final LocalServerSocket server = new LocalServerSocket(SOCKET);
        private final HandlerThread callbacks = new HandlerThread("unicode-editor-callbacks");
        private UiAutomation automation;
        private Method clearCache;
        private volatile boolean closed;
        private Thread receiver;
        Server() throws IOException { }
        void start() {
            receiver = new Thread(() -> {
                while (!closed) {
                    try (LocalSocket client = server.accept()) {
                        int uid = client.getPeerCredentials().getUid();
                        if (uid != 2000 && uid != 0) continue; // Never expose accessibility editing to ordinary apps.
                        client.setSoTimeout(6000);
                        DataInputStream input = new DataInputStream(client.getInputStream());
                        int display = input.readInt(); String pkg = input.readUTF(); long deadline = input.readLong(); int length = input.readInt();
                        if (display <= 0 || pkg.length() > 255 || length < 1 || length > 4096) continue;
                        byte[] data = new byte[length]; input.readFully(data);
                        int result = edit(display, pkg, new String(data, StandardCharsets.UTF_8), deadline);
                        DataOutputStream output = new DataOutputStream(client.getOutputStream()); output.writeInt(result); output.flush();
                    } catch (IOException | RuntimeException e) { if (!closed) Ln.d("Unicode editor request ended"); }
                }
            }, "unicode-editor-requests");
            receiver.setDaemon(true); receiver.start();
        }
        private synchronized UiAutomation automation() throws ReflectiveOperationException {
            if (automation == null) {
                if (!callbacks.isAlive()) callbacks.start();
                Class<?> connectionType = Class.forName("android.app.IUiAutomationConnection");
                Object connection = Class.forName("android.app.UiAutomationConnection").getConstructor().newInstance();
                UiAutomation candidate = UiAutomation.class.getConstructor(Looper.class, connectionType).newInstance(callbacks.getLooper(), connection);
                try {
                    UiAutomation.class.getMethod("connect", int.class).invoke(candidate, 1); // DONT_SUPPRESS_ACCESSIBILITY_SERVICES
                    AccessibilityServiceInfo info = candidate.getServiceInfo();
                    if (info == null) throw new IllegalStateException("No editor automation service");
                    info.eventTypes = 0;
                    info.flags |= AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
                            | AccessibilityServiceInfo.FLAG_INCLUDE_NOT_IMPORTANT_VIEWS | AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS;
                    candidate.setServiceInfo(info); automation = candidate;
                    try { clearCache = UiAutomation.class.getMethod("clearCache"); } catch (NoSuchMethodException ignored) { }
                    Ln.i("SonicRoute Unicode editor: connected, event delivery disabled");
                } catch (ReflectiveOperationException | RuntimeException e) {
                    try { UiAutomation.class.getMethod("disconnect").invoke(candidate); } catch (ReflectiveOperationException ignored) { }
                    throw e;
                }
            }
            return automation;
        }
        private synchronized int edit(int display, String pkg, String text, long deadline) {
            if (closed) return 0;
            try {
                UiAutomation editor = automation();
                if (clearCache != null) clearCache.invoke(editor); // No event subscription; refresh only at a text commit.
                SparseArray<List<AccessibilityWindowInfo>> displays = editor.getWindowsOnAllDisplays();
                if (SystemClock.uptimeMillis() >= deadline) return 0;
                List<AccessibilityWindowInfo> windows = displays.get(display);
                Ln.d("Unicode editor windows: display=" + display + " count=" + (windows == null ? 0 : windows.size()));
                if (windows == null) return 0;
                for (AccessibilityWindowInfo window : windows) {
                    AccessibilityNodeInfo root = window.getRoot();
                    if (root == null || root.getPackageName() == null || !pkg.contentEquals(root.getPackageName())) continue;
                    AccessibilityNodeInfo node = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
                    Ln.d("Unicode editor focus: display=" + display + " found=" + (node != null));
                    if (node == null || !node.refresh() || !node.isEditable() || node.isPassword()) continue;
                    CharSequence existing = node.getText(); String before = existing == null ? "" : existing.toString();
                    if (before.length() > 1024 * 1024) return 0;
                    int start = node.getTextSelectionStart(), end = node.getTextSelectionEnd();
                    Ln.d("Unicode editor selection: display=" + display + " start=" + start + " end=" + end + " length=" + before.length());
                    if (start < 0 || end < 0 || start > before.length() || end > before.length()) return 0;
                    int low = Math.min(start, end), high = Math.max(start, end);
                    String after = before.substring(0, low) + text + before.substring(high);
                    Bundle change = new Bundle(); change.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, after);
                    if (!node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, change)) return 0;
                    Bundle selection = new Bundle(); selection.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, low + text.length());
                    selection.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, low + text.length());
                    node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, selection);
                    boolean verified = node.refresh() && after.contentEquals(node.getText());
                    Ln.d("Unicode editor result: display=" + display + " verified=" + verified);
                    return text.length(); // Accepted action is reported separately from read-back verification.
                }
            } catch (ReflectiveOperationException | RuntimeException e) { Ln.w("Unicode editor unavailable", e); }
            return 0;
        }
        synchronized void close() {
            closed = true;
            if (automation != null) {
                try { UiAutomation.class.getMethod("disconnect").invoke(automation); } catch (ReflectiveOperationException | RuntimeException ignored) { }
                automation = null;
            }
            try { server.close(); } catch (IOException ignored) { }
            callbacks.quitSafely();
        }
    }
}
