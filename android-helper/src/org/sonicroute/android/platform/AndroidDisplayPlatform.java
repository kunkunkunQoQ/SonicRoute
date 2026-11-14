// SPDX-License-Identifier: MIT
package org.sonicroute.android.platform;

import android.content.Context;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.hardware.display.VirtualDisplayConfig;
import android.os.Binder;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.IInterface;
import android.os.Parcel;
import android.os.RemoteException;
import android.system.Os;
import android.view.Display;
import android.view.Surface;
import java.io.ByteArrayOutputStream;
import java.io.Closeable;
import java.io.IOException;
import java.io.InputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.nio.charset.StandardCharsets;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.sonicroute.android.ShellContext;
import org.sonicroute.android.model.Geometry.DisplayInfo;
import org.sonicroute.android.model.Geometry.Size;
import org.sonicroute.android.util.ServerLog;

/**
 * Reflection wrappers for Android display creation, virtual display flags, size/rotation events,
 * display IME policy, and physical panel power control across Android 11–16.
 */
public final class AndroidDisplayPlatform {
    private static final long EVENT_FLAG_DISPLAY_CHANGED = 1L << 2;
    private static final int FLAG_SUPPORTS_PROTECTED_BUFFERS = 0x00000001;
    private static final int FLAG_STEAL_TOP_FOCUS_DISABLED_API36 = 1 << 16;
    private static final int POWER_MODE_OFF = 0;
    private static final int POWER_MODE_NORMAL = 2;

    private static final Object INIT_LOCK = new Object();
    private static Object displayManagerGlobal;
    private static IInterface windowManager;
    private static IInterface powerManager;

    private AndroidDisplayPlatform() {
    }

    public static final class MirrorHandle implements Closeable {
        private final VirtualDisplay virtualDisplay;
        private final IBinder surfaceControlDisplay;

        MirrorHandle(VirtualDisplay virtualDisplay, IBinder surfaceControlDisplay) {
            this.virtualDisplay = virtualDisplay;
            this.surfaceControlDisplay = surfaceControlDisplay;
        }

        @Override
        public void close() {
            if (virtualDisplay != null) {
                virtualDisplay.release();
            }
            if (surfaceControlDisplay != null) {
                try {
                    Class<?> sc = Class.forName("android.view.SurfaceControl");
                    sc.getMethod("destroyDisplay", IBinder.class).invoke(null, surfaceControlDisplay);
                } catch (Throwable error) {
                    ServerLog.w("Failed to destroy SurfaceControl mirror display", error);
                }
            }
        }
    }

    public static boolean supportsProtectedBuffers(DisplayInfo info) {
        return info != null && (info.getFlags() & FLAG_SUPPORTS_PROTECTED_BUFFERS) != 0;
    }

    private static Object getDisplayManagerGlobal() throws ReflectiveOperationException {
        synchronized (INIT_LOCK) {
            if (displayManagerGlobal == null) {
                Class<?> dmgClass = Class.forName("android.hardware.display.DisplayManagerGlobal");
                displayManagerGlobal = dmgClass.getDeclaredMethod("getInstance").invoke(null);
            }
            return displayManagerGlobal;
        }
    }

    private static IInterface getWindowManager() {
        synchronized (INIT_LOCK) {
            if (windowManager == null) {
                windowManager = AndroidSystemPlatform.getBinderService("window", "android.view.IWindowManager");
            }
            return windowManager;
        }
    }

    private static IInterface getPowerManager() {
        synchronized (INIT_LOCK) {
            if (powerManager == null) {
                powerManager = AndroidSystemPlatform.getBinderService("power", "android.os.IPowerManager");
            }
            return powerManager;
        }
    }

    public static DisplayInfo getDisplayInfo(int displayId) {
        try {
            Object dmg = getDisplayManagerGlobal();
            Object raw = dmg.getClass().getMethod("getDisplayInfo", int.class).invoke(dmg, displayId);
            if (raw != null) {
                Class<?> cls = raw.getClass();
                int width = cls.getDeclaredField("logicalWidth").getInt(raw);
                int height = cls.getDeclaredField("logicalHeight").getInt(raw);
                int rotation = cls.getDeclaredField("rotation").getInt(raw);
                int layerStack = cls.getDeclaredField("layerStack").getInt(raw);
                int flags = cls.getDeclaredField("flags").getInt(raw);
                int dpi = cls.getDeclaredField("logicalDensityDpi").getInt(raw);
                return new DisplayInfo(displayId, new Size(width, height), rotation, layerStack, flags, dpi);
            }
        } catch (Throwable error) {
            ServerLog.w("DisplayManagerGlobal.getDisplayInfo(" + displayId + ") failed, falling back to dumpsys", error);
        }
        return parseDisplayInfoFromDumpsys(displayId);
    }

    public static DisplayInfo parseDisplayInfo(String dumpsysOutput, int displayId) {
        if (dumpsysOutput == null || dumpsysOutput.isEmpty()) {
            return null;
        }
        Pattern pattern = Pattern.compile(
                "^    mOverrideDisplayInfo=DisplayInfo\\{\".*?, displayId " + displayId
                        + ".*?(, FLAG_.*)?, real ([0-9]+) x ([0-9]+).*?, rotation ([0-9]+).*?, density ([0-9]+).*?, layerStack ([0-9]+)",
                Pattern.MULTILINE);
        Matcher matcher = pattern.matcher(dumpsysOutput);
        if (!matcher.find()) {
            return null;
        }
        int flags = parseDisplayFlags(matcher.group(1));
        int width = Integer.parseInt(matcher.group(2));
        int height = Integer.parseInt(matcher.group(3));
        int rotation = Integer.parseInt(matcher.group(4));
        int density = Integer.parseInt(matcher.group(5));
        int layerStack = Integer.parseInt(matcher.group(6));
        return new DisplayInfo(displayId, new Size(width, height), rotation, layerStack, flags, density);
    }

    private static DisplayInfo parseDisplayInfoFromDumpsys(int displayId) {
        try {
            String output = execReadOutput("dumpsys", "display");
            return parseDisplayInfo(output, displayId);
        } catch (Exception error) {
            ServerLog.e("Could not parse display info from dumpsys display", error);
            return null;
        }
    }

    private static int parseDisplayFlags(String text) {
        if (text == null) {
            return 0;
        }
        int flags = 0;
        Matcher matcher = Pattern.compile("FLAG_[A-Z_]+").matcher(text);
        while (matcher.find()) {
            try {
                flags |= Display.class.getDeclaredField(matcher.group()).getInt(null);
            } catch (ReflectiveOperationException ignored) {
            }
        }
        return flags;
    }

    public static int computeOwnedVirtualDisplayFlags(boolean systemDecorations, boolean destroyContent) {
        int flags = DisplayManager.VIRTUAL_DISPLAY_FLAG_PUBLIC
                | DisplayManager.VIRTUAL_DISPLAY_FLAG_OWN_CONTENT_ONLY
                | readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_SUPPORTS_TOUCH", 1 << 6)
                | readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_ROTATES_WITH_CONTENT", 1 << 7);
        if (systemDecorations) {
            flags |= readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_SHOULD_SHOW_SYSTEM_DECORATIONS", 1 << 9);
        }
        if (destroyContent) {
            flags |= readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_DESTROY_CONTENT_ON_REMOVAL", 1 << 8);
        }
        flags &= ~DisplayManager.VIRTUAL_DISPLAY_FLAG_PRESENTATION;
        if (Build.VERSION.SDK_INT >= 33) {
            flags |= readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_TRUSTED", 1 << 10)
                    | readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_OWN_DISPLAY_GROUP", 1 << 11)
                    | readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_ALWAYS_UNLOCKED", 1 << 12)
                    | readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_TOUCH_FEEDBACK_DISABLED", 1 << 13);
        }
        if (Build.VERSION.SDK_INT >= 34) {
            flags |= readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_OWN_FOCUS", 1 << 14)
                    | readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_DEVICE_DISPLAY_GROUP", 1 << 15);
        }
        if (Build.VERSION.SDK_INT >= 36) {
            flags |= readStaticIntField(DisplayManager.class, "VIRTUAL_DISPLAY_FLAG_STEAL_TOP_FOCUS_DISABLED", FLAG_STEAL_TOP_FOCUS_DISABLED_API36);
        }
        return flags;
    }

    public static VirtualDisplay createOwnedVirtualDisplay(
            String name, int width, int height, int dpi, Surface surface, boolean systemDecorations, boolean destroyContent)
            throws Exception {
        int flags = computeOwnedVirtualDisplayFlags(systemDecorations, destroyContent);
        DisplayManager dm = obtainShellDisplayManager();
        if (Build.VERSION.SDK_INT >= 33) {
            try {
                VirtualDisplayConfig.Builder builder = new VirtualDisplayConfig.Builder(name, width, height, dpi)
                        .setFlags(flags);
                if (surface != null) {
                    builder.setSurface(surface);
                }
                if (Build.VERSION.SDK_INT >= 36) {
                    try {
                        Method ignoreSize = VirtualDisplayConfig.Builder.class
                                .getMethod("setIgnoreActivitySizeRestrictions", boolean.class);
                        ignoreSize.invoke(builder, true);
                    } catch (ReflectiveOperationException ignoreError) {
                        ServerLog.w("VirtualDisplayConfig.setIgnoreActivitySizeRestrictions unavailable", ignoreError);
                    }
                }
                VirtualDisplayConfig config = builder.build();
                if (Build.VERSION.SDK_INT >= 36) {
                    boolean ignoreEnabled = false;
                    try {
                        Method isIgnore = VirtualDisplayConfig.class
                                .getMethod("isIgnoreActivitySizeRestrictions");
                        ignoreEnabled = Boolean.TRUE.equals(isIgnore.invoke(config));
                    } catch (ReflectiveOperationException isIgnoreError) {
                        ServerLog.w("VirtualDisplayConfig.isIgnoreActivitySizeRestrictions unavailable", isIgnoreError);
                    }
                    ServerLog.i("VirtualDisplayConfig isIgnoreActivitySizeRestrictions=" + ignoreEnabled);
                }
                VirtualDisplay created;
                try {
                    Method createWithCallback = DisplayManager.class.getMethod(
                            "createVirtualDisplay",
                            VirtualDisplayConfig.class,
                            Handler.class,
                            VirtualDisplay.Callback.class);
                    created = (VirtualDisplay) createWithCallback.invoke(dm, config, null, null);
                } catch (NoSuchMethodException singleArgFallback) {
                    Method createWithConfig = DisplayManager.class.getMethod(
                            "createVirtualDisplay", VirtualDisplayConfig.class);
                    created = (VirtualDisplay) createWithConfig.invoke(dm, config);
                }
                if (created != null) {
                    return created;
                }
            } catch (Throwable configError) {
                ServerLog.w("VirtualDisplayConfig path unavailable, falling back to legacy createVirtualDisplay", configError);
            }
        }
        return dm.createVirtualDisplay(name, width, height, dpi, surface, flags);
    }

    private static DisplayManager obtainShellDisplayManager() throws Exception {
        try {
            Constructor<DisplayManager> ctor = DisplayManager.class.getDeclaredConstructor(Context.class);
            ctor.setAccessible(true);
            return ctor.newInstance(ShellContext.get());
        } catch (ReflectiveOperationException ignored) {
            return (DisplayManager) ShellContext.get().getSystemService(Context.DISPLAY_SERVICE);
        }
    }

    public static MirrorHandle createMirrorDisplay(
            String name, Size videoSize, DisplayInfo targetInfo, Surface surface) throws Exception {
        int width = videoSize.getWidth();
        int height = videoSize.getHeight();
        int displayId = targetInfo.getDisplayId();
        try {
            Method createStatic = DisplayManager.class.getMethod(
                    "createVirtualDisplay", String.class, int.class, int.class, int.class, Surface.class);
            VirtualDisplay vd = (VirtualDisplay) createStatic.invoke(null, name, width, height, displayId, surface);
            return new MirrorHandle(vd, null);
        } catch (Exception dmError) {
            Class<?> sc = Class.forName("android.view.SurfaceControl");
            boolean secure = supportsProtectedBuffers(targetInfo);
            IBinder token = (IBinder) sc.getMethod("createDisplay", String.class, boolean.class).invoke(null, name, secure);
            try {
                sc.getMethod("openTransaction").invoke(null);
                try {
                    sc.getMethod("setDisplaySurface", IBinder.class, Surface.class).invoke(null, token, surface);
                    sc.getMethod("setDisplayProjection", IBinder.class, int.class, android.graphics.Rect.class, android.graphics.Rect.class)
                            .invoke(null, token, 0, targetInfo.getSize().toRect(), videoSize.toRect());
                    sc.getMethod("setDisplayLayerStack", IBinder.class, int.class).invoke(null, token, targetInfo.getLayerStack());
                } finally {
                    sc.getMethod("closeTransaction").invoke(null);
                }
                return new MirrorHandle(null, token);
            } catch (Throwable error) {
                try {
                    sc.getMethod("destroyDisplay", IBinder.class).invoke(null, token);
                } catch (Throwable ignored) {
                }
                throw error instanceof Exception ? (Exception) error : new RuntimeException(error);
            }
        }
    }

    public static int getDefaultRotation() {
        IInterface wm = getWindowManager();
        if (wm == null) {
            return 0;
        }
        try {
            return (int) wm.getClass().getMethod("getDefaultDisplayRotation").invoke(wm);
        } catch (NoSuchMethodException fallback) {
            try {
                return (int) wm.getClass().getMethod("getRotation").invoke(wm);
            } catch (ReflectiveOperationException e) {
                return 0;
            }
        } catch (ReflectiveOperationException e) {
            return 0;
        }
    }

    public static boolean isRotationFrozen(int displayId) {
        IInterface wm = getWindowManager();
        if (wm == null) {
            return false;
        }
        try {
            return (boolean) wm.getClass().getMethod("isDisplayRotationFrozen", int.class).invoke(wm, displayId);
        } catch (NoSuchMethodException fallback) {
            if (displayId != 0) {
                return false;
            }
            try {
                return (boolean) wm.getClass().getMethod("isRotationFrozen").invoke(wm);
            } catch (ReflectiveOperationException e) {
                return false;
            }
        } catch (ReflectiveOperationException e) {
            return false;
        }
    }

    public static void freezeRotation(int displayId, int rotation) {
        IInterface wm = getWindowManager();
        if (wm == null) {
            return;
        }
        try {
            wm.getClass().getMethod("freezeDisplayRotation", int.class, int.class, String.class)
                    .invoke(wm, displayId, rotation, "SonicRoute#freezeRotation");
        } catch (NoSuchMethodException e1) {
            try {
                wm.getClass().getMethod("freezeDisplayRotation", int.class, int.class).invoke(wm, displayId, rotation);
            } catch (NoSuchMethodException e2) {
                if (displayId == 0) {
                    try {
                        wm.getClass().getMethod("freezeRotation", int.class).invoke(wm, rotation);
                    } catch (ReflectiveOperationException ignored) {
                    }
                }
            } catch (ReflectiveOperationException ignored) {
            }
        } catch (ReflectiveOperationException ignored) {
        }
    }

    public static void thawRotation(int displayId) {
        IInterface wm = getWindowManager();
        if (wm == null) {
            return;
        }
        try {
            wm.getClass().getMethod("thawDisplayRotation", int.class, String.class)
                    .invoke(wm, displayId, "SonicRoute#thawRotation");
        } catch (NoSuchMethodException e1) {
            try {
                wm.getClass().getMethod("thawDisplayRotation", int.class).invoke(wm, displayId);
            } catch (NoSuchMethodException e2) {
                if (displayId == 0) {
                    try {
                        wm.getClass().getMethod("thawRotation").invoke(wm);
                    } catch (ReflectiveOperationException ignored) {
                    }
                }
            } catch (ReflectiveOperationException ignored) {
            }
        } catch (ReflectiveOperationException ignored) {
        }
    }

    public static int getDisplayImePolicy(int displayId) {
        IInterface wm = getWindowManager();
        if (wm == null) {
            return -1;
        }
        try {
            if (Build.VERSION.SDK_INT >= 31) {
                return (int) wm.getClass().getMethod("getDisplayImePolicy", int.class).invoke(wm, displayId);
            }
            boolean showIme = (boolean) wm.getClass().getMethod("shouldShowIme", int.class).invoke(wm, displayId);
            return showIme ? 0 : 1;
        } catch (Throwable error) {
            ServerLog.w("getDisplayImePolicy failed for display " + displayId, error);
            return -1;
        }
    }

    public static void setDisplayImePolicy(int displayId, int policy) {
        IInterface wm = getWindowManager();
        if (wm == null) {
            return;
        }
        try {
            if (Build.VERSION.SDK_INT >= 31) {
                wm.getClass().getMethod("setDisplayImePolicy", int.class, int.class).invoke(wm, displayId, policy);
            } else if (policy != 2) {
                wm.getClass().getMethod("setShouldShowIme", int.class, boolean.class).invoke(wm, displayId, policy == 0);
            }
        } catch (Throwable error) {
            ServerLog.w("setDisplayImePolicy(" + displayId + ", " + policy + ") failed", error);
        }
    }

    public static boolean isScreenOn(int displayId) {
        IInterface pm = getPowerManager();
        if (pm == null) {
            return true;
        }
        try {
            if (Build.VERSION.SDK_INT >= 34) {
                return (boolean) pm.getClass().getMethod("isDisplayInteractive", int.class).invoke(pm, displayId);
            }
            return (boolean) pm.getClass().getMethod("isInteractive").invoke(pm);
        } catch (Throwable error) {
            ServerLog.w("isScreenOn failed", error);
            return false;
        }
    }

    public static boolean setDisplayPower(int displayId, boolean on) {
        if (Build.VERSION.SDK_INT >= 35) {
            try {
                Object dmg = getDisplayManagerGlobal();
                if ((boolean) dmg.getClass().getMethod("requestDisplayPower", int.class, boolean.class).invoke(dmg, displayId, on)) {
                    return true;
                }
            } catch (Throwable error) {
                ServerLog.w("requestDisplayPower failed, falling back to SurfaceControl", error);
            }
        }

        int mode = on ? POWER_MODE_NORMAL : POWER_MODE_OFF;
        if (Build.VERSION.SDK_INT >= 34) {
            Boolean dcResult = setDisplayPowerViaDisplayControl(mode);
            if (dcResult != null) {
                return dcResult;
            }
        }

        try {
            Class<?> sc = Class.forName("android.view.SurfaceControl");
            Method setPower = sc.getMethod("setDisplayPowerMode", IBinder.class, int.class);
            try {
                long[] ids = (long[]) sc.getMethod("getPhysicalDisplayIds").invoke(null);
                if (ids != null && ids.length > 0) {
                    Method getToken = sc.getMethod("getPhysicalDisplayToken", long.class);
                    boolean allOk = true;
                    for (long id : ids) {
                        IBinder token = (IBinder) getToken.invoke(null, id);
                        if (token != null) {
                            setPower.invoke(null, token, mode);
                        } else {
                            allOk = false;
                        }
                    }
                    return allOk;
                }
            } catch (NoSuchMethodException ignored) {
            }
            IBinder builtIn = (IBinder) sc.getMethod("getInternalDisplayToken").invoke(null);
            if (builtIn != null) {
                setPower.invoke(null, builtIn, mode);
                return true;
            }
        } catch (Throwable error) {
            ServerLog.w("SurfaceControl.setDisplayPowerMode failed", error);
        }
        return false;
    }

    private static Boolean setDisplayPowerViaDisplayControl(int mode) {
        try {
            Class<?> clFactory = Class.forName("com.android.internal.os.ClassLoaderFactory");
            Method createCl = clFactory.getDeclaredMethod(
                    "createClassLoader", String.class, String.class, String.class, ClassLoader.class, int.class, boolean.class, String.class);
            String sysClasspath = Os.getenv("SYSTEMSERVERCLASSPATH");
            ClassLoader cl = (ClassLoader) createCl.invoke(
                    null, sysClasspath, null, null, ClassLoader.getSystemClassLoader(), 0, true, null);
            Class<?> displayControl = cl.loadClass("com.android.server.display.DisplayControl");
            Method loadLibrary = Runtime.class.getDeclaredMethod("loadLibrary0", Class.class, String.class);
            loadLibrary.setAccessible(true);
            loadLibrary.invoke(Runtime.getRuntime(), displayControl, "android_servers");

            long[] ids = (long[]) displayControl.getMethod("getPhysicalDisplayIds").invoke(null);
            if (ids == null || ids.length == 0) {
                return null;
            }
            Method getToken = displayControl.getMethod("getPhysicalDisplayToken", long.class);
            Class<?> sc = Class.forName("android.view.SurfaceControl");
            Method setPower = sc.getMethod("setDisplayPowerMode", IBinder.class, int.class);
            boolean ok = true;
            for (long id : ids) {
                IBinder token = (IBinder) getToken.invoke(null, id);
                if (token != null) {
                    setPower.invoke(null, token, mode);
                } else {
                    ok = false;
                }
            }
            return ok;
        } catch (Throwable error) {
            ServerLog.d("DisplayControl power path unavailable: " + error.getMessage());
            return null;
        }
    }

    /**
     * Registers a non-polling listener for size/rotation changes on {@code targetDisplayId}.
     * Uses {@code IDisplayWindowListener} on Android 14+ and {@code DisplayManagerGlobal} earlier.
     */
    public static Closeable registerDisplayChangeListener(int targetDisplayId, Runnable onChanged) {
        if (Build.VERSION.SDK_INT >= 34) {
            Closeable wmListener = registerDisplayWindowListener(targetDisplayId, onChanged);
            if (wmListener != null) {
                return wmListener;
            }
        }
        return registerDisplayManagerListener(targetDisplayId, onChanged);
    }

    private static Closeable registerDisplayWindowListener(int targetDisplayId, Runnable onChanged) {
        IInterface wm = getWindowManager();
        if (wm == null) {
            return null;
        }
        try {
            final String descriptor = "android.view.IDisplayWindowListener";
            Class<?> listenerInterface = Class.forName(descriptor);
            Class<?> stubClass = Class.forName(descriptor + "$Stub");
            final int txConfig = readStaticIntField(stubClass, "TRANSACTION_onDisplayConfigurationChanged", -1);

            final Object[] proxyRef = new Object[1];
            Binder binder = new Binder() {
                @Override
                protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) throws RemoteException {
                    if (code == INTERFACE_TRANSACTION && reply != null) {
                        reply.writeString(descriptor);
                        return true;
                    }
                    if (txConfig != -1 && code == txConfig) {
                        data.enforceInterface(descriptor);
                        int eventDisplayId = data.readInt();
                        if (eventDisplayId == targetDisplayId) {
                            onChanged.run();
                        }
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
                    (p, method, args) -> handleInterfaceProxy(p, method, args, binder, descriptor));
            proxyRef[0] = proxy;
            binder.attachInterface((IInterface) proxy, descriptor);

            wm.getClass().getMethod("registerDisplayWindowListener", listenerInterface).invoke(wm, proxy);
            return () -> {
                try {
                    wm.getClass().getMethod("unregisterDisplayWindowListener", listenerInterface).invoke(wm, proxy);
                } catch (Throwable ignored) {
                }
            };
        } catch (Throwable error) {
            ServerLog.w("registerDisplayWindowListener failed", error);
            return null;
        }
    }

    private static Closeable registerDisplayManagerListener(int targetDisplayId, Runnable onChanged) {
        try {
            Object dmg = getDisplayManagerGlobal();
            Class<?> listenerInterface = Class.forName("android.hardware.display.DisplayManager$DisplayListener");
            HandlerThread thread = new HandlerThread("SonicRoute-DisplayListener");
            thread.start();
            Handler handler = new Handler(thread.getLooper());
            Object proxy = Proxy.newProxyInstance(
                    ClassLoader.getSystemClassLoader(),
                    new Class<?>[] { listenerInterface },
                    (p, method, args) -> {
                        if ("onDisplayChanged".equals(method.getName()) && args != null && args.length >= 1) {
                            if (((Integer) args[0]).intValue() == targetDisplayId) {
                                onChanged.run();
                            }
                        } else if ("toString".equals(method.getName())) {
                            return "SonicRouteDisplayListener";
                        }
                        return null;
                    });
            try {
                dmg.getClass().getMethod("registerDisplayListener", listenerInterface, Handler.class, long.class, String.class)
                        .invoke(dmg, proxy, handler, EVENT_FLAG_DISPLAY_CHANGED, ShellContext.PACKAGE_NAME);
            } catch (NoSuchMethodException e1) {
                try {
                    dmg.getClass().getMethod("registerDisplayListener", listenerInterface, Handler.class, long.class)
                            .invoke(dmg, proxy, handler, EVENT_FLAG_DISPLAY_CHANGED);
                } catch (NoSuchMethodException e2) {
                    dmg.getClass().getMethod("registerDisplayListener", listenerInterface, Handler.class)
                            .invoke(dmg, proxy, handler);
                }
            }
            return () -> {
                try {
                    dmg.getClass().getMethod("unregisterDisplayListener", listenerInterface).invoke(dmg, proxy);
                } catch (Throwable ignored) {
                }
                thread.quitSafely();
            };
        } catch (Throwable error) {
            ServerLog.w("registerDisplayManagerListener failed", error);
            return () -> {
            };
        }
    }

    static Object handleInterfaceProxy(Object proxy, Method method, Object[] args, IBinder binder, String descriptor) {
        String name = method.getName();
        if ("asBinder".equals(name)) {
            return binder;
        }
        if ("equals".equals(name)) {
            return Boolean.valueOf(args != null && args.length == 1 && proxy == args[0]);
        }
        if ("hashCode".equals(name)) {
            return Integer.valueOf(System.identityHashCode(proxy));
        }
        if ("toString".equals(name)) {
            return descriptor + "@Proxy";
        }
        Class<?> ret = method.getReturnType();
        if (ret == boolean.class) {
            return Boolean.FALSE;
        }
        if (ret.isPrimitive()) {
            return 0;
        }
        return null;
    }

    static int readStaticIntField(Class<?> owner, String fieldName, int fallback) {
        Class<?> current = owner;
        while (current != null) {
            try {
                Field f = current.getDeclaredField(fieldName);
                f.setAccessible(true);
                return f.getInt(null);
            } catch (Throwable ignored) {
                current = current.getSuperclass();
            }
        }
        try {
            return owner.getField(fieldName).getInt(null);
        } catch (Throwable ignored) {
            return fallback;
        }
    }

    private static String execReadOutput(String... cmd) throws IOException, InterruptedException {
        Process process = Runtime.getRuntime().exec(cmd);
        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        byte[] buf = new byte[4096];
        try (InputStream in = process.getInputStream()) {
            int n;
            while ((n = in.read(buf)) != -1) {
                baos.write(buf, 0, n);
            }
        }
        int exit = process.waitFor();
        if (exit != 0) {
            throw new IOException("Command exited with code " + exit);
        }
        return new String(baos.toByteArray(), StandardCharsets.UTF_8);
    }
}
