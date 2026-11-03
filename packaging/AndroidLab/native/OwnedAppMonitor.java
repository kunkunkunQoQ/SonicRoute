/* Event-only task ownership for one owned virtual display. SPDX-License-Identifier: Apache-2.0 */
package com.genymobile.scrcpy.control;

import android.app.ActivityManager;
import android.app.KeyguardManager;
import android.content.ComponentName;
import android.os.Binder;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.Parcel;
import android.os.RemoteException;

import com.genymobile.scrcpy.FakeContext;
import com.genymobile.scrcpy.device.Device;
import com.genymobile.scrcpy.util.Ln;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.List;

/** Uses the device's own hidden Binder interface, never hard-coded transaction IDs or polling. */
public final class OwnedAppMonitor implements AutoCloseable {
    private static final String DESCRIPTOR = "android.app.ITaskStackListener";
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final String packageName;
    private final int displayId;
    private final Runnable refresh = this::refresh;
    private final Runnable verifyLaunch = () -> { launching = false; refresh(); };
    private Object manager, listener;
    private Method unregister, getTasks;
    private Field taskDisplay, visible, focused;
    private volatile boolean closed, queued, launching;
    private volatile int trackedTask = -1;
    private volatile String state = "starting";
    private boolean everActive, autoAttempted;
    private int stackCode, movedCode, removedCode, displayCode, createdCode, focusCode;

    public OwnedAppMonitor(String packageName, int displayId) { this.packageName = packageName; this.displayId = displayId; }
    public String getPackageName() { return packageName; }

    public void start() {
        try {
            Class<?> type = Class.forName(DESCRIPTOR);
            Class<?> stub = Class.forName(DESCRIPTOR + "$Stub");
            stackCode = transaction(stub, "onTaskStackChanged");
            movedCode = transaction(stub, "onTaskMovedToFront");
            removedCode = transaction(stub, "onTaskRemoved");
            displayCode = transaction(stub, "onTaskDisplayChanged");
            createdCode = transaction(stub, "onTaskCreated");
            try { focusCode = transaction(stub, "onTaskFocusChanged"); } catch (NoSuchFieldException ignored) { }
            Binder binder = new Binder() {
                @Override protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) throws RemoteException {
                    if (code == INTERFACE_TRANSACTION) { if (reply != null) reply.writeString(DESCRIPTOR); return true; }
                    if (closed) return true;
                    if (code == stackCode || code == movedCode || code == removedCode || code == displayCode || code == createdCode || code == focusCode) {
                        data.enforceInterface(DESCRIPTOR);
                        // These callbacks are oneway. Read only stable scalar fields when needed;
                        // the runtime interface determines transaction numbers and parcel ownership.
                        if (code == displayCode || code == removedCode) {
                            if (data.readInt() == trackedTask) requestRefresh();
                        } else if (code == createdCode || !"active".equals(state)) requestRefresh();
                        if (reply != null && (flags & IBinder.FLAG_ONEWAY) == 0) reply.writeNoException();
                        return true;
                    }
                    return super.onTransact(code, data, reply, flags);
                }
            };
            listener = Proxy.newProxyInstance(type.getClassLoader(), new Class<?>[]{type}, (proxy, method, args) -> {
                if ("asBinder".equals(method.getName())) return binder;
                if ("toString".equals(method.getName())) return "SonicRoute owned display listener";
                if ("hashCode".equals(method.getName())) return System.identityHashCode(proxy);
                if ("equals".equals(method.getName())) return proxy == args[0];
                return null;
            });
            binder.attachInterface((android.os.IInterface) listener, DESCRIPTOR);
            manager = Class.forName("android.app.ActivityTaskManager").getMethod("getService").invoke(null);
            Class<?> service = Class.forName("android.app.IActivityTaskManager");
            unregister = service.getMethod("unregisterTaskStackListener", type);
            getTasks = service.getMethod("getTasks", int.class, boolean.class, boolean.class, int.class);
            taskDisplay = Class.forName("android.app.TaskInfo").getField("displayId");
            visible = Class.forName("android.app.TaskInfo").getField("isVisible");
            try { focused = Class.forName("android.app.TaskInfo").getField("isFocused"); } catch (NoSuchFieldException ignored) { }
            service.getMethod("registerTaskStackListener", type).invoke(manager, listener);
            requestRefresh();
            Ln.i("SonicRoute task events: enabled display=" + displayId);
        } catch (ReflectiveOperationException | RuntimeException e) {
            closeRegistration();
            setState("unknown");
            Ln.w("Owned display task events unavailable; manual restore remains available", e);
        }
    }

    private static int transaction(Class<?> stub, String name) throws ReflectiveOperationException {
        Field field = stub.getDeclaredField("TRANSACTION_" + name); field.setAccessible(true); return field.getInt(null);
    }
    private synchronized void requestRefresh() {
        if (closed || queued || getTasks == null) return;
        queued = true; handler.postDelayed(refresh, 100); // One coalesced event burst, never a periodic job.
    }
    @SuppressWarnings("unchecked") private void refresh() {
        synchronized (this) { queued = false; }
        if (closed || getTasks == null) return;
        try {
            List<ActivityManager.RunningTaskInfo> tasks = (List<ActivityManager.RunningTaskInfo>) getTasks.invoke(manager, 32, false, false, -1);
            boolean owned = false, phone = false;
            int ownedTask = -1, phoneTask = -1;
            for (ActivityManager.RunningTaskInfo task : tasks) {
                int id = taskDisplay.getInt(task);
                ComponentName base = task.baseActivity, top = task.topActivity;
                boolean match = (base != null && packageName.equals(base.getPackageName())) || (top != null && packageName.equals(top.getPackageName()));
                if (!match) continue;
                if (id == displayId && visible.getBoolean(task)) { ownedTask = task.taskId; owned = true; }
                if (id == 0 && visible.getBoolean(task) && (focused == null || focused.getBoolean(task))) { phoneTask = task.taskId; phone = true; }
            }
            if (owned) {
                trackedTask = ownedTask;
                everActive = true; autoAttempted = false; launching = false;
                handler.removeCallbacks(verifyLaunch); setState("active");
            } else if (!launching) {
                if (phone) { trackedTask = phoneTask; setState("phone"); }
                else if (everActive && !autoAttempted && !isLocked()) {
                    autoAttempted = true; launch(false);
                } else if (everActive || autoAttempted) setState("missing");
            }
        } catch (ReflectiveOperationException | RuntimeException e) {
            setState("unknown"); Ln.w("Owned task state unavailable", e); closeRegistration();
        }
    }
    private boolean isLocked() {
        KeyguardManager keyguard = (KeyguardManager) FakeContext.get().getSystemService("keyguard");
        return keyguard != null && keyguard.isKeyguardLocked();
    }
    /** Called for an explicit start-app command, including the initial launch. */
    public void launching() { handler.post(() -> { if (!closed) { autoAttempted = true; beginLaunch(); } }); }
    private void beginLaunch() {
        launching = true; if (everActive || !"starting".equals(state)) setState("recovering");
        handler.removeCallbacks(verifyLaunch); handler.postDelayed(verifyLaunch, 2500);
    }
    private void launch(boolean explicit) {
        beginLaunch();
        Ln.i("SonicRoute task recovery: display=" + displayId + " automatic=" + !explicit);
        Device.startApp(packageName, displayId, false);
    }
    private void setState(String next) {
        if (closed || next.equals(state)) return;
        state = next; Ln.i("SonicRoute app-state: display=" + displayId + " state=" + next);
    }
    private void closeRegistration() {
        if (unregister != null && manager != null && listener != null) {
            try { unregister.invoke(manager, listener); } catch (ReflectiveOperationException | RuntimeException ignored) { }
        }
        unregister = null; listener = null; getTasks = null;
    }
    @Override public void close() {
        closed = true; handler.removeCallbacks(refresh); handler.removeCallbacks(verifyLaunch); closeRegistration();
    }
}
