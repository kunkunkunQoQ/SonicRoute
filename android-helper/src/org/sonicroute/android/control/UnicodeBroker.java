// SPDX-License-Identifier: MIT
package org.sonicroute.android.control;

import android.accessibilityservice.AccessibilityServiceInfo;
import android.app.UiAutomation;
import android.net.Credentials;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.Process;
import android.util.SparseArray;
import android.view.accessibility.AccessibilityNodeInfo;
import android.view.accessibility.AccessibilityWindowInfo;
import java.io.Closeable;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.FutureTask;
import org.sonicroute.android.util.ServerLog;

/**
 * MIT Unicode text commit bridge backed by a lazy shared {@link UiAutomation} connection
 * ({@code eventTypes = 0}) over {@code localabstract:sonicroute-unicode-v1} so concurrent
 * virtual display processes share a single shell accessibility slot without polling.
 */
public final class UnicodeBroker implements Closeable {
    public static final String SOCKET_NAME = "sonicroute-unicode-v1";
    private static final int FLAG_DONT_SUPPRESS_ACCESSIBILITY_SERVICES = 1;
    private static final int MAX_TEXT_BYTES = 16 * 1024;

    private final Object lock = new Object();
    private HandlerThread automationThread;
    private Handler automationHandler;
    private UiAutomation uiAutomation;
    private LocalServerSocket serverSocket;
    private Thread acceptThread;
    private boolean closed;

    public boolean commit(int displayId, String packageName, String text) {
        if (text == null || text.isEmpty()) {
            return true;
        }
        if (packageName == null || packageName.isEmpty()) {
            return false;
        }
        if (tryCommitViaExistingBroker(displayId, packageName, text)) {
            return true;
        }
        return commitLocally(displayId, packageName, text);
    }

    private boolean tryCommitViaExistingBroker(int displayId, String packageName, String text) {
        synchronized (lock) {
            if (closed || serverSocket != null) {
                return false;
            }
        }
        try (LocalSocket client = new LocalSocket()) {
            client.connect(new LocalSocketAddress(SOCKET_NAME));
            DataOutputStream out = new DataOutputStream(client.getOutputStream());
            DataInputStream in = new DataInputStream(client.getInputStream());
            out.writeInt(displayId);
            writeLengthPrefixedUtf8(out, packageName);
            writeLengthPrefixedUtf8(out, text);
            out.flush();
            return in.readUnsignedByte() == 1;
        } catch (IOException ignored) {
            return false;
        }
    }

    private boolean commitLocally(int displayId, String packageName, String text) {
        try {
            ensureLocalBrokerStarted();
            Handler handler;
            synchronized (lock) {
                if (closed || automationHandler == null) {
                    return false;
                }
                handler = automationHandler;
            }
            FutureTask<Boolean> task = new FutureTask<>((Callable<Boolean>) () -> applyTextOnFocusedNode(displayId, packageName, text));
            handler.post(task);
            return Boolean.TRUE.equals(task.get());
        } catch (Throwable error) {
            ServerLog.d("UnicodeBroker local commit failed: " + error.getMessage());
            return false;
        }
    }

    private void ensureLocalBrokerStarted() throws Exception {
        synchronized (lock) {
            if (closed) {
                return;
            }
            if (automationThread == null) {
                automationThread = new HandlerThread("SonicRoute-Unicode");
                automationThread.start();
                automationHandler = new Handler(automationThread.getLooper());
                FutureTask<UiAutomation> initTask = new FutureTask<>(() -> connectUiAutomation(automationThread.getLooper()));
                automationHandler.post(initTask);
                uiAutomation = initTask.get();
            }
            if (serverSocket == null) {
                try {
                    serverSocket = new LocalServerSocket(SOCKET_NAME);
                    acceptThread = new Thread(this::acceptLoop, "SonicRoute-UnicodeSocket");
                    acceptThread.setDaemon(true);
                    acceptThread.start();
                } catch (IOException ignored) {
                    // Another instance may have claimed the socket concurrently.
                }
            }
        }
    }

    private void acceptLoop() {
        while (!closed) {
            LocalServerSocket ss;
            synchronized (lock) {
                ss = serverSocket;
            }
            if (ss == null) {
                break;
            }
            try (LocalSocket client = ss.accept()) {
                Credentials creds = client.getPeerCredentials();
                int uid = creds.getUid();
                if (uid != Process.SHELL_UID && uid != 0) {
                    ServerLog.w("Rejected UnicodeBroker client from unauthorized uid " + uid);
                    continue;
                }
                DataInputStream in = new DataInputStream(client.getInputStream());
                DataOutputStream out = new DataOutputStream(client.getOutputStream());
                int displayId = in.readInt();
                String pkg = readLengthPrefixedUtf8(in);
                String text = readLengthPrefixedUtf8(in);
                boolean ok = commitLocally(displayId, pkg, text);
                out.writeByte(ok ? 1 : 0);
                out.flush();
            } catch (IOException error) {
                if (!closed) {
                    ServerLog.d("UnicodeBroker accept exited: " + error.getMessage());
                }
                break;
            }
        }
    }

    private static UiAutomation connectUiAutomation(Looper looper) throws Exception {
        Class<?> connInterface = Class.forName("android.app.IUiAutomationConnection");
        Object connection = Class.forName("android.app.UiAutomationConnection").getConstructor().newInstance();
        Constructor<UiAutomation> ctor = UiAutomation.class.getDeclaredConstructor(Looper.class, connInterface);
        ctor.setAccessible(true);
        UiAutomation ua = ctor.newInstance(looper, connection);
        try {
            Method connectWithFlags = UiAutomation.class.getMethod("connect", int.class);
            connectWithFlags.invoke(ua, FLAG_DONT_SUPPRESS_ACCESSIBILITY_SERVICES);
        } catch (NoSuchMethodException fallback) {
            UiAutomation.class.getMethod("connect").invoke(ua);
        }
        AccessibilityServiceInfo info = ua.getServiceInfo();
        if (info == null) {
            info = new AccessibilityServiceInfo();
        }
        info.eventTypes = 0;
        info.feedbackType = AccessibilityServiceInfo.FEEDBACK_GENERIC;
        info.flags |= AccessibilityServiceInfo.FLAG_INCLUDE_NOT_IMPORTANT_VIEWS
                | AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS;
        ua.setServiceInfo(info);
        return ua;
    }

    private boolean applyTextOnFocusedNode(int displayId, String packageName, String delta) {
        UiAutomation ua;
        synchronized (lock) {
            ua = uiAutomation;
        }
        if (ua == null) {
            return false;
        }
        AccessibilityNodeInfo target = findFocusedEditableNode(ua, displayId, packageName);
        if (target == null) {
            return false;
        }
        if (target.isPassword()) {
            target.recycle();
            return false;
        }
        try {
            CharSequence existing = target.getText();
            boolean showingHint = Build.VERSION.SDK_INT >= 26 && target.isShowingHintText();
            String base = (!showingHint && existing != null) ? existing.toString() : "";
            String updated = base + delta;

            Bundle setArgs = new Bundle();
            setArgs.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, updated);
            boolean setOk = target.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, setArgs);
            if (!setOk) {
                return false;
            }
            Bundle selArgs = new Bundle();
            selArgs.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, updated.length());
            selArgs.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, updated.length());
            target.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, selArgs);
            return true;
        } finally {
            target.recycle();
        }
    }

    private static AccessibilityNodeInfo findFocusedEditableNode(
            UiAutomation ua, int displayId, String packageName) {
        if (Build.VERSION.SDK_INT >= 30) {
            try {
                SparseArray<List<AccessibilityWindowInfo>> perDisplay = ua.getWindowsOnAllDisplays();
                List<AccessibilityWindowInfo> windows = perDisplay != null ? perDisplay.get(displayId) : null;
                AccessibilityNodeInfo match = searchWindowsForInputFocus(windows, packageName);
                if (match != null) {
                    return match;
                }
            } catch (Throwable ignored) {
            }
        }
        if (displayId == 0) {
            AccessibilityNodeInfo root = ua.getRootInActiveWindow();
            if (root != null) {
                AccessibilityNodeInfo focused = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
                if (isEditableMatch(focused, packageName)) {
                    root.recycle();
                    return focused;
                }
                if (focused != null) {
                    focused.recycle();
                }
                AccessibilityNodeInfo dfs = findFocusedRecursive(root, packageName);
                root.recycle();
                return dfs;
            }
        }
        return null;
    }

    private static AccessibilityNodeInfo searchWindowsForInputFocus(
            List<AccessibilityWindowInfo> windows, String packageName) {
        if (windows == null) {
            return null;
        }
        for (AccessibilityWindowInfo win : windows) {
            if (win == null) {
                continue;
            }
            AccessibilityNodeInfo root = win.getRoot();
            if (root == null) {
                continue;
            }
            AccessibilityNodeInfo focused = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
            if (isEditableMatch(focused, packageName)) {
                root.recycle();
                return focused;
            }
            if (focused != null) {
                focused.recycle();
            }
            AccessibilityNodeInfo dfs = findFocusedRecursive(root, packageName);
            root.recycle();
            if (dfs != null) {
                return dfs;
            }
        }
        return null;
    }

    private static AccessibilityNodeInfo findFocusedRecursive(AccessibilityNodeInfo node, String packageName) {
        if (node == null) {
            return null;
        }
        if (node.isFocused() && isEditableMatch(node, packageName)) {
            return AccessibilityNodeInfo.obtain(node);
        }
        int count = node.getChildCount();
        for (int i = 0; i < count; ++i) {
            AccessibilityNodeInfo child = node.getChild(i);
            if (child != null) {
                AccessibilityNodeInfo match = findFocusedRecursive(child, packageName);
                child.recycle();
                if (match != null) {
                    return match;
                }
            }
        }
        return null;
    }

    private static boolean isEditableMatch(AccessibilityNodeInfo node, String packageName) {
        if (node == null || !node.isEditable()) {
            return false;
        }
        CharSequence pkg = node.getPackageName();
        return pkg != null && packageName.contentEquals(pkg);
    }

    private static void writeLengthPrefixedUtf8(DataOutputStream out, String text) throws IOException {
        byte[] raw = text.getBytes(StandardCharsets.UTF_8);
        if (raw.length > MAX_TEXT_BYTES) {
            throw new IOException("Unicode text exceeds limit: " + raw.length);
        }
        out.writeInt(raw.length);
        out.write(raw);
    }

    private static String readLengthPrefixedUtf8(DataInputStream in) throws IOException {
        int len = in.readInt();
        if (len < 0 || len > MAX_TEXT_BYTES) {
            throw new IOException("Invalid Unicode text length: " + len);
        }
        byte[] buf = new byte[len];
        in.readFully(buf);
        return new String(buf, StandardCharsets.UTF_8);
    }

    @Override
    public void close() {
        LocalServerSocket ss;
        UiAutomation ua;
        Handler handler;
        HandlerThread thread;
        synchronized (lock) {
            if (closed) {
                return;
            }
            closed = true;
            ss = serverSocket;
            serverSocket = null;
            ua = uiAutomation;
            uiAutomation = null;
            handler = automationHandler;
            automationHandler = null;
            thread = automationThread;
            automationThread = null;
        }
        if (ss != null) {
            try {
                ss.close();
            } catch (IOException ignored) {
            }
        }
        if (ua != null && handler != null) {
            handler.post(() -> {
                try {
                    UiAutomation.class.getMethod("disconnect").invoke(ua);
                } catch (Throwable ignored) {
                }
                if (thread != null) {
                    thread.quitSafely();
                }
            });
        } else if (thread != null) {
            thread.quitSafely();
        }
    }
}
