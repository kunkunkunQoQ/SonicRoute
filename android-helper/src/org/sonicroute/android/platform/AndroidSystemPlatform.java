// SPDX-License-Identifier: MIT
package org.sonicroute.android.platform;

import android.app.ActivityOptions;
import android.content.AttributionSource;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.os.Binder;
import android.os.Build;
import android.os.Bundle;
import android.os.IBinder;
import android.os.IInterface;
import android.os.Parcel;
import android.os.RemoteException;
import android.os.SystemClock;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;
import java.io.Closeable;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import org.sonicroute.android.ShellContext;
import org.sonicroute.android.util.ServerLog;

/**
 * Reflection wrappers for Android Activity, ActivityTask, Package, Input, StatusBar,
 * Clipboard, and Settings system services under UID 2000 ({@code com.android.shell}).
 */
public final class AndroidSystemPlatform {
    public static final int INJECT_MODE_ASYNC = 0;
    public static final int INJECT_MODE_WAIT_FOR_RESULT = 1;
    public static final int INJECT_MODE_WAIT_FOR_FINISH = 2;

    private static final int USER_CURRENT = -2;
    private static final Object LOCK = new Object();

    private static Method getServiceMethod;
    private static IInterface activityManager;
    private static IInterface activityTaskManager;
    private static IInterface packageManager;
    private static IInterface statusBarService;
    private static android.hardware.input.InputManager inputManager;
    private static long lastInjectEventsWarnMs;

    private AndroidSystemPlatform() {
    }

    public static final class TaskSnapshot {
        public final int displayId;
        public final String packageName;
        public final String basePackageName;
        public final boolean visible;
        public final boolean focused;

        public TaskSnapshot(int displayId, String packageName, boolean visible, boolean focused) {
            this(displayId, packageName, null, visible, focused);
        }

        public TaskSnapshot(
                int displayId,
                String packageName,
                String basePackageName,
                boolean visible,
                boolean focused) {
            this.displayId = displayId;
            this.packageName = packageName;
            this.basePackageName = basePackageName;
            this.visible = visible;
            this.focused = focused;
        }

        public boolean matchesPackage(String targetPackage) {
            return targetPackage != null
                    && (targetPackage.equals(packageName) || targetPackage.equals(basePackageName));
        }
    }

    public static IInterface getBinderService(String serviceName, String interfaceType) {
        try {
            synchronized (LOCK) {
                if (getServiceMethod == null) {
                    getServiceMethod = Class.forName("android.os.ServiceManager").getDeclaredMethod("getService", String.class);
                }
            }
            IBinder binder = (IBinder) getServiceMethod.invoke(null, serviceName);
            if (binder == null) {
                return null;
            }
            Class<?> stub = Class.forName(interfaceType + "$Stub");
            return (IInterface) stub.getMethod("asInterface", IBinder.class).invoke(null, binder);
        } catch (Throwable error) {
            ServerLog.e("Could not obtain system service \"" + serviceName + "\"", error);
            return null;
        }
    }

    private static IInterface getActivityManager() {
        synchronized (LOCK) {
            if (activityManager == null) {
                try {
                    Class<?> amClass = Class.forName("android.app.ActivityManager");
                    activityManager = (IInterface) amClass.getMethod("getService").invoke(null);
                } catch (Throwable ignored) {
                    activityManager = getBinderService("activity", "android.app.IActivityManager");
                }
            }
            return activityManager;
        }
    }

    private static IInterface getActivityTaskManager() {
        synchronized (LOCK) {
            if (activityTaskManager == null) {
                try {
                    Class<?> atmClass = Class.forName("android.app.ActivityTaskManager");
                    activityTaskManager = (IInterface) atmClass.getMethod("getService").invoke(null);
                } catch (Throwable ignored) {
                    activityTaskManager = getBinderService("activity_task", "android.app.IActivityTaskManager");
                }
            }
            return activityTaskManager;
        }
    }

    private static IInterface getPackageManagerService() {
        synchronized (LOCK) {
            if (packageManager == null) {
                packageManager = getBinderService("package", "android.content.pm.IPackageManager");
            }
            return packageManager;
        }
    }

    private static IInterface getStatusBarService() {
        synchronized (LOCK) {
            if (statusBarService == null) {
                statusBarService = getBinderService("statusbar", "com.android.internal.statusbar.IStatusBarService");
            }
            return statusBarService;
        }
    }

    private static android.hardware.input.InputManager getInputManager() {
        synchronized (LOCK) {
            if (inputManager == null) {
                inputManager = (android.hardware.input.InputManager) ShellContext.get().getSystemService(Context.INPUT_SERVICE);
            }
            return inputManager;
        }
    }

    public static int resolvePackageUid(String packageName) {
        if (packageName == null || packageName.isEmpty()) {
            return -1;
        }
        IInterface pm = getPackageManagerService();
        if (pm == null) {
            return -1;
        }
        try {
            ApplicationInfo appInfo = null;
            try {
                Method m = pm.getClass().getMethod("getApplicationInfo", String.class, long.class, int.class);
                appInfo = (ApplicationInfo) m.invoke(pm, packageName, 0L, 0);
            } catch (NoSuchMethodException fallback) {
                Method m = pm.getClass().getMethod("getApplicationInfo", String.class, int.class, int.class);
                appInfo = (ApplicationInfo) m.invoke(pm, packageName, 0, 0);
            }
            return appInfo != null ? appInfo.uid : -1;
        } catch (Throwable error) {
            ServerLog.d("Could not resolve UID for " + packageName + ": " + error.getMessage());
            return -1;
        }
    }

    public static boolean startApp(String packageSpec, int displayId, boolean defaultForceStop) {
        if (packageSpec == null || packageSpec.isEmpty() || displayId < 0) {
            return false;
        }
        boolean forceStop = defaultForceStop;
        String packageName = packageSpec;
        if (packageSpec.startsWith("+")) {
            forceStop = true;
            packageName = packageSpec.substring(1);
        } else if (packageSpec.startsWith("?")) {
            // Query mode unused on server launch; strip prefix cleanly.
            packageName = packageSpec.substring(1);
        }
        if (packageName.isEmpty()) {
            return false;
        }

        PackageManager pm = ShellContext.get().getPackageManager();
        Intent launchIntent = pm.getLaunchIntentForPackage(packageName);
        if (launchIntent == null) {
            ServerLog.w("No launcher activity found for package: " + packageName);
            return false;
        }
        launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);

        Bundle options = null;
        if (Build.VERSION.SDK_INT >= 26) {
            ActivityOptions ao = ActivityOptions.makeBasic();
            ao.setLaunchDisplayId(displayId);
            options = ao.toBundle();
        }

        IInterface am = getActivityManager();
        if (am == null) {
            return false;
        }
        if (forceStop) {
            try {
                am.getClass().getMethod("forceStopPackage", String.class, int.class).invoke(am, packageName, USER_CURRENT);
            } catch (Throwable error) {
                ServerLog.w("forceStopPackage failed for " + packageName, error);
            }
        }
        try {
            Class<?> appThreadClass = Class.forName("android.app.IApplicationThread");
            Class<?> profilerClass = Class.forName("android.app.ProfilerInfo");
            Method startMethod = am.getClass().getMethod(
                    "startActivityAsUser",
                    appThreadClass, String.class, Intent.class, String.class, IBinder.class, String.class,
                    int.class, int.class, profilerClass, Bundle.class, int.class);
            int res = (int) startMethod.invoke(
                    am, null, ShellContext.PACKAGE_NAME, launchIntent, null, null, null, 0, 0, null, options, USER_CURRENT);
            return res >= 0;
        } catch (Throwable error) {
            ServerLog.e("startActivityAsUser failed for " + packageName + " on display " + displayId, error);
            return false;
        }
    }

    public static List<TaskSnapshot> queryTasks() {
        IInterface atm = getActivityTaskManager();
        if (atm == null) {
            return Collections.emptyList();
        }
        try {
            List<?> rawTasks = invokeGetTasks(atm);
            if (rawTasks == null || rawTasks.isEmpty()) {
                return Collections.emptyList();
            }
            List<TaskSnapshot> result = new ArrayList<>(rawTasks.size());
            for (Object task : rawTasks) {
                if (task == null) {
                    continue;
                }
                int displayId = readIntFieldHierarchy(task, "displayId", -1);
                String topPkg = readComponentPackage(readObjectFieldHierarchy(task, "topActivity"));
                String basePkg = readComponentPackage(readObjectFieldHierarchy(task, "baseActivity"));
                if (topPkg == null && basePkg == null) {
                    topPkg = readComponentPackage(readObjectFieldHierarchy(task, "realActivity"));
                }
                boolean visible = readBooleanFieldHierarchy(task, "isVisible", true);
                boolean focused = readBooleanFieldHierarchy(task, "isFocused", false);
                result.add(new TaskSnapshot(displayId, topPkg, basePkg, visible, focused));
            }
            return result;
        } catch (Throwable error) {
            ServerLog.d("queryTasks failed: " + error.getMessage());
            return Collections.emptyList();
        }
    }

    @SuppressWarnings("unchecked")
    private static List<?> invokeGetTasks(IInterface atm) throws ReflectiveOperationException {
        Method threeArg = null;
        Method twoArg = null;
        Method oneArg = null;
        for (Method m : atm.getClass().getMethods()) {
            if (!"getTasks".equals(m.getName())) {
                continue;
            }
            Class<?>[] params = m.getParameterTypes();
            if (params.length == 4 && params[0] == int.class && params[1] == boolean.class
                    && params[2] == boolean.class && params[3] == int.class) {
                return (List<?>) m.invoke(atm, 64, false, false, -1);
            }
            if (params.length == 3 && params[0] == int.class && params[1] == boolean.class && params[2] == boolean.class) {
                threeArg = m;
            } else if (params.length == 2 && params[0] == int.class && params[1] == boolean.class) {
                twoArg = m;
            } else if (params.length == 1 && params[0] == int.class) {
                oneArg = m;
            }
        }
        if (threeArg != null) {
            return (List<?>) threeArg.invoke(atm, 64, false, false);
        }
        if (twoArg != null) {
            return (List<?>) twoArg.invoke(atm, 64, false);
        }
        if (oneArg != null) {
            return (List<?>) oneArg.invoke(atm, 64);
        }
        return Collections.emptyList();
    }

    private static String readComponentPackage(Object component) {
        return component instanceof ComponentName ? ((ComponentName) component).getPackageName() : null;
    }

    /**
     * Registers an event-driven {@code ITaskStackListener} without compile-time AIDL stubs.
     * Any matching task-stack/focus/display transaction notifies {@code onTaskEvent}.
     */
    public static Closeable registerTaskStackListener(Runnable onTaskEvent) {
        IInterface atm = getActivityTaskManager();
        if (atm == null) {
            return () -> {
            };
        }
        try {
            final String descriptor = "android.app.ITaskStackListener";
            Class<?> listenerInterface = Class.forName(descriptor);
            Class<?> stubClass = Class.forName(descriptor + "$Stub");
            final Set<Integer> relevantTransactions = collectTransactionCodes(stubClass);

            Binder binder = new Binder() {
                @Override
                protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) throws RemoteException {
                    if (code == INTERFACE_TRANSACTION && reply != null) {
                        reply.writeString(descriptor);
                        return true;
                    }
                    if (relevantTransactions.contains(code)) {
                        try {
                            data.enforceInterface(descriptor);
                        } catch (Throwable ignored) {
                            // Continue triggering task re-evaluation even if vendor ROM modifies parcel prefix.
                        }
                        onTaskEvent.run();
                        if ((flags & FLAG_ONEWAY) == 0 && reply != null) {
                            reply.writeNoException();
                        }
                        return true;
                    }
                    try {
                        data.enforceInterface(descriptor);
                        if ((flags & FLAG_ONEWAY) == 0 && reply != null) {
                            reply.writeNoException();
                        }
                        return true;
                    } catch (Throwable ignored) {
                        return super.onTransact(code, data, reply, flags);
                    }
                }
            };
            Object proxy = Proxy.newProxyInstance(
                    listenerInterface.getClassLoader(),
                    new Class<?>[] { listenerInterface },
                    (p, method, args) -> AndroidDisplayPlatform.handleInterfaceProxy(p, method, args, binder, descriptor));
            binder.attachInterface((IInterface) proxy, descriptor);

            atm.getClass().getMethod("registerTaskStackListener", listenerInterface).invoke(atm, proxy);
            return () -> {
                try {
                    atm.getClass().getMethod("unregisterTaskStackListener", listenerInterface).invoke(atm, proxy);
                } catch (Throwable ignored) {
                }
            };
        } catch (Throwable error) {
            ServerLog.w("registerTaskStackListener failed", error);
            return () -> {
            };
        }
    }

    private static Set<Integer> collectTransactionCodes(Class<?> stubClass) {
        Set<Integer> codes = new HashSet<>();
        String[] interesting = {
                "TRANSACTION_onTaskStackChanged",
                "TRANSACTION_onTaskCreated",
                "TRANSACTION_onTaskMovedToFront",
                "TRANSACTION_onTaskMovedToBack",
                "TRANSACTION_onTaskDisplayChanged",
                "TRANSACTION_onTaskFocusChanged",
                "TRANSACTION_onTaskRemovalStarted",
                "TRANSACTION_onTaskRemoved",
                "TRANSACTION_onActivityPinned",
                "TRANSACTION_onActivityUnpinned"
        };
        for (String name : interesting) {
            int tx = AndroidDisplayPlatform.readStaticIntField(stubClass, name, -1);
            if (tx > 0) {
                codes.add(tx);
            }
        }
        return codes;
    }

    public static boolean injectInputEvent(InputEvent event, int displayId, int mode) {
        if (displayId != 0) {
            try {
                InputEvent.class.getMethod("setDisplayId", int.class).invoke(event, displayId);
            } catch (Throwable error) {
                ServerLog.w("Could not bind input event to display " + displayId, error);
                return false;
            }
        }
        try {
            android.hardware.input.InputManager im = getInputManager();
            Method inject = android.hardware.input.InputManager.class.getMethod("injectInputEvent", InputEvent.class, int.class);
            return (boolean) inject.invoke(im, event, mode);
        } catch (InvocationTargetException error) {
            Throwable cause = error.getCause();
            if (cause instanceof SecurityException && cause.getMessage() != null
                    && cause.getMessage().contains("INJECT_EVENTS permission")) {
                long now = System.currentTimeMillis();
                if (now - lastInjectEventsWarnMs >= 3000) {
                    lastInjectEventsWarnMs = now;
                    ServerLog.e(cause.getMessage());
                }
                return false;
            }
            ServerLog.w("injectInputEvent failed", error);
            return false;
        } catch (Throwable error) {
            ServerLog.w("injectInputEvent failed", error);
            return false;
        }
    }

    public static boolean injectKeyEvent(int action, int keyCode, int repeat, int metaState, int displayId, int mode) {
        int source = KeyEvent.isGamepadButton(keyCode)
                ? (InputDevice.SOURCE_GAMEPAD | InputDevice.SOURCE_KEYBOARD)
                : InputDevice.SOURCE_KEYBOARD;
        return injectKeyEvent(action, keyCode, repeat, metaState, source, displayId, mode);
    }

    public static boolean injectKeyEvent(
            int action, int keyCode, int repeat, int metaState, int source, int displayId, int mode) {
        long now = SystemClock.uptimeMillis();
        KeyEvent event = new KeyEvent(
                now, now, action, keyCode, repeat, metaState,
                KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0, source);
        return injectInputEvent(event, displayId, mode);
    }

    public static boolean pressReleaseKeycode(int keyCode, int displayId, int mode) {
        return injectKeyEvent(KeyEvent.ACTION_DOWN, keyCode, 0, 0, displayId, mode)
                && injectKeyEvent(KeyEvent.ACTION_UP, keyCode, 0, 0, displayId, mode);
    }

    public static boolean setActionButton(MotionEvent event, int actionButton) {
        try {
            MotionEvent.class.getMethod("setActionButton", int.class).invoke(event, actionButton);
            return true;
        } catch (Throwable error) {
            ServerLog.w("MotionEvent.setActionButton failed", error);
            return false;
        }
    }

    public static void expandNotificationsPanel() {
        invokeStatusBar("expandNotificationsPanel");
    }

    public static void expandSettingsPanel() {
        IInterface sb = getStatusBarService();
        if (sb == null) {
            return;
        }
        try {
            sb.getClass().getMethod("expandSettingsPanel", String.class).invoke(sb, (Object) null);
        } catch (NoSuchMethodException fallback) {
            invokeStatusBar("expandSettingsPanel");
        } catch (Throwable error) {
            ServerLog.w("expandSettingsPanel failed", error);
        }
    }

    public static void collapsePanels() {
        invokeStatusBar("collapsePanels");
    }

    private static void invokeStatusBar(String methodName) {
        IInterface sb = getStatusBarService();
        if (sb == null) {
            return;
        }
        try {
            sb.getClass().getMethod(methodName).invoke(sb);
        } catch (Throwable error) {
            ServerLog.w("StatusBar." + methodName + " failed", error);
        }
    }

    public static CharSequence getClipboardText() {
        try {
            ClipboardManager cm = (ClipboardManager) ShellContext.get().getSystemService(Context.CLIPBOARD_SERVICE);
            if (cm == null) {
                return null;
            }
            ClipData clip = cm.getPrimaryClip();
            if (clip == null || clip.getItemCount() == 0) {
                return null;
            }
            return clip.getItemAt(0).getText();
        } catch (Throwable error) {
            ServerLog.w("getClipboardText failed", error);
            return null;
        }
    }

    public static boolean setClipboardText(CharSequence text) {
        try {
            ClipboardManager cm = (ClipboardManager) ShellContext.get().getSystemService(Context.CLIPBOARD_SERVICE);
            if (cm == null) {
                return false;
            }
            cm.setPrimaryClip(ClipData.newPlainText(null, text));
            return true;
        } catch (Throwable error) {
            ServerLog.w("setClipboardText failed", error);
            return false;
        }
    }

    public static Closeable addClipboardListener(ClipboardManager.OnPrimaryClipChangedListener listener) {
        try {
            ClipboardManager cm = (ClipboardManager) ShellContext.get().getSystemService(Context.CLIPBOARD_SERVICE);
            if (cm == null) {
                return () -> {
                };
            }
            cm.addPrimaryClipChangedListener(listener);
            return () -> {
                try {
                    cm.removePrimaryClipChangedListener(listener);
                } catch (Throwable ignored) {
                }
            };
        } catch (Throwable error) {
            ServerLog.w("addClipboardListener failed", error);
            return () -> {
            };
        }
    }

    public static String getSetting(String table, String key) {
        return invokeSettingsProvider(table, false, key, null);
    }

    public static void putSetting(String table, String key, String value) {
        invokeSettingsProvider(table, true, key, value);
    }

    public static String getAndPutSetting(String table, String key, String value) {
        String old = getSetting(table, key);
        if (!value.equals(old)) {
            putSetting(table, key, value);
        }
        return old;
    }

    private static String invokeSettingsProvider(String table, boolean put, String key, String value) {
        IInterface am = getActivityManager();
        if (am == null) {
            return null;
        }
        IBinder token = new Binder();
        try {
            Object holder;
            try {
                Method getExt = am.getClass().getMethod("getContentProviderExternal", String.class, int.class, IBinder.class, String.class);
                holder = getExt.invoke(am, "settings", 0, token, "*cmd*");
            } catch (NoSuchMethodException fallback) {
                Method getExt = am.getClass().getMethod("getContentProviderExternal", String.class, int.class, IBinder.class);
                holder = getExt.invoke(am, "settings", 0, token);
            }
            if (holder == null) {
                return null;
            }
            Field providerField = holder.getClass().getDeclaredField("provider");
            providerField.setAccessible(true);
            Object provider = providerField.get(holder);
            if (provider == null) {
                return null;
            }

            String methodName = (put ? "PUT_" : "GET_") + table;
            Bundle arg = new Bundle();
            arg.putInt("_user", 0);
            if (put) {
                arg.putString("value", value);
            }
            Bundle result = callProvider(provider, methodName, key, arg);
            return (!put && result != null) ? result.getString("value") : null;
        } catch (Throwable error) {
            ServerLog.w("Settings provider access failed (" + table + "/" + key + ")", error);
            return null;
        } finally {
            try {
                am.getClass().getMethod("removeContentProviderExternal", String.class, IBinder.class)
                        .invoke(am, "settings", token);
            } catch (Throwable ignored) {
            }
        }
    }

    private static Bundle callProvider(Object provider, String method, String key, Bundle arg)
            throws ReflectiveOperationException {
        if (Build.VERSION.SDK_INT >= 31) {
            Method call = provider.getClass().getMethod(
                    "call", AttributionSource.class, String.class, String.class, String.class, Bundle.class);
            return (Bundle) call.invoke(provider, ShellContext.get().getAttributionSource(), "settings", method, key, arg);
        }
        Method call = provider.getClass().getMethod(
                "call", String.class, String.class, String.class, String.class, String.class, Bundle.class);
        return (Bundle) call.invoke(provider, ShellContext.PACKAGE_NAME, null, "settings", method, key, arg);
    }

    private static Object readObjectFieldHierarchy(Object obj, String fieldName) {
        Class<?> c = obj.getClass();
        while (c != null) {
            try {
                Field f = c.getDeclaredField(fieldName);
                f.setAccessible(true);
                return f.get(obj);
            } catch (NoSuchFieldException ignored) {
                c = c.getSuperclass();
            } catch (IllegalAccessException e) {
                return null;
            }
        }
        return null;
    }

    private static int readIntFieldHierarchy(Object obj, String fieldName, int fallback) {
        Class<?> c = obj.getClass();
        while (c != null) {
            try {
                Field f = c.getDeclaredField(fieldName);
                f.setAccessible(true);
                return f.getInt(obj);
            } catch (NoSuchFieldException ignored) {
                c = c.getSuperclass();
            } catch (IllegalAccessException e) {
                return fallback;
            }
        }
        return fallback;
    }

    private static boolean readBooleanFieldHierarchy(Object obj, String fieldName, boolean fallback) {
        Class<?> c = obj.getClass();
        while (c != null) {
            try {
                Field f = c.getDeclaredField(fieldName);
                f.setAccessible(true);
                return f.getBoolean(obj);
            } catch (NoSuchFieldException ignored) {
                c = c.getSuperclass();
            } catch (IllegalAccessException e) {
                return fallback;
            }
        }
        return fallback;
    }
}
