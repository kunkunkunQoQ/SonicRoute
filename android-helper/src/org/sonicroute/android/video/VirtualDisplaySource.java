// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.video;

import android.hardware.display.VirtualDisplay;
import android.view.Surface;
import java.io.Closeable;
import java.io.IOException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.sonicroute.android.core.ServerOptions;
import org.sonicroute.android.model.Geometry;
import org.sonicroute.android.model.Geometry.DisplayInfo;
import org.sonicroute.android.model.Geometry.NewDisplaySpec;
import org.sonicroute.android.model.Geometry.Size;
import org.sonicroute.android.platform.AndroidDisplayPlatform;
import org.sonicroute.android.platform.AndroidSystemPlatform;
import org.sonicroute.android.util.ServerLog;

/**
 * Manages an isolated SonicRoute virtual display, its dynamic host-driven resizing,
 * display IME isolation policy, and event-driven {@link OwnedAppMonitor}.
 */
public final class VirtualDisplaySource extends VideoCaptureSource {
    private final NewDisplaySpec displaySpec;
    private final int initialMaxSize;
    private final boolean systemDecorations;
    private final boolean destroyContent;
    private final int displayImePolicy;
    private final String initialStartApp;

    private Size mainDisplayPortraitSize;
    private int mainDisplayDpi;

    private VirtualDisplay virtualDisplay;
    private int virtualDisplayId = ServerOptions.DISPLAY_ID_NONE;
    private int activeDpi;
    private int maxSize;
    private Size requestedVideoSize;
    private Size actualDisplaySize;
    private Size actualVideoSize;
    private boolean virtualDisplayMissing;

    private int previousImePolicy = -1;
    private boolean imePolicyOverridden;

    private Closeable displayChangeHandle;
    private OwnedAppMonitor appMonitor;
    private ExecutorService launchExecutor;

    public VirtualDisplaySource(ServerOptions options) {
        this.displaySpec = options.getNewDisplay();
        this.initialMaxSize = options.getMaxSize();
        this.maxSize = options.getMaxSize();
        this.systemDecorations = options.isVdSystemDecorations();
        this.destroyContent = options.isVdDestroyContent();
        this.displayImePolicy = options.getDisplayImePolicy();
        this.initialStartApp = options.getStartApp();
    }

    @Override
    public void init() throws IOException {
        DisplayInfo mainInfo = AndroidDisplayPlatform.getDisplayInfo(0);
        if (mainInfo != null) {
            Size s = mainInfo.getSize();
            if ((mainInfo.getRotation() & 1) != 0) {
                s = new Size(s.getHeight(), s.getWidth());
            }
            mainDisplayPortraitSize = s;
            mainDisplayDpi = mainInfo.getDpi() > 0 ? mainInfo.getDpi() : 320;
        } else {
            mainDisplayPortraitSize = new Size(1080, 1920);
            mainDisplayDpi = 320;
        }
        launchExecutor = Executors.newSingleThreadExecutor(r -> new Thread(r, "SonicRoute-Launcher"));
    }

    @Override
    public synchronized void prepare() throws IOException {
        if (virtualDisplay == null) {
            Size explicitSize = displaySpec != null ? displaySpec.getSize() : null;
            int explicitDpi = displaySpec != null ? displaySpec.getDpi() : 0;

            Size base = explicitSize != null
                    ? Geometry.fitVirtualDisplaySize(mainDisplayPortraitSize, explicitSize.getWidth(), explicitSize.getHeight())
                    : mainDisplayPortraitSize;
            base = base.limitMajor(maxSize).round8();
            activeDpi = explicitDpi > 0 ? explicitDpi : mainDisplayDpi;
            requestedVideoSize = null;

            try {
                virtualDisplay = AndroidDisplayPlatform.createOwnedVirtualDisplay(
                        "sonicroute",
                        base.getWidth(),
                        base.getHeight(),
                        activeDpi,
                        null,
                        systemDecorations,
                        destroyContent);
            } catch (Exception error) {
                throw new IOException("Failed to create SonicRoute virtual display", error);
            }
            virtualDisplayId = virtualDisplay.getDisplay().getDisplayId();
            actualDisplaySize = base;
            actualVideoSize = base;

            if (displayImePolicy != ServerOptions.IME_POLICY_UNSET) {
                int prev = AndroidDisplayPlatform.getDisplayImePolicy(virtualDisplayId);
                if (prev != -1 && prev != displayImePolicy) {
                    previousImePolicy = prev;
                    imePolicyOverridden = true;
                }
                AndroidDisplayPlatform.setDisplayImePolicy(virtualDisplayId, displayImePolicy);
            }

            displayChangeHandle = AndroidDisplayPlatform.registerDisplayChangeListener(
                    virtualDisplayId, this::onVirtualDisplayChanged);
            appMonitor = new OwnedAppMonitor(virtualDisplayId, this::relaunchOwnedApp);

            ServerLog.i("New virtual display created: id=" + virtualDisplayId
                    + " size=" + base + " dpi=" + activeDpi);

            if (initialStartApp != null && !initialStartApp.isEmpty()) {
                startOwnedApp(initialStartApp);
            }
            return;
        }

        Size target = requestedVideoSize != null ? requestedVideoSize : actualDisplaySize;
        requestedVideoSize = null;
        if (target != null) {
            target = target.limitMajor(maxSize).round8();
            if (!target.equals(actualDisplaySize)) {
                virtualDisplay.resize(target.getWidth(), target.getHeight(), activeDpi);
                actualDisplaySize = target;
            }
            actualVideoSize = target;
        }
    }

    @Override
    public synchronized void start(Surface surface) throws IOException {
        if (virtualDisplay == null) {
            throw new IOException("VirtualDisplay not prepared");
        }
        virtualDisplay.setSurface(surface);
    }

    @Override
    public synchronized void stop() {
        // Keep the virtual display surface attached during encoder resets so DisplayManagerService
        // does not mark the display STATE_OFF and falsely pause/hide its running task.
    }

    @Override
    public void release() {
        OwnedAppMonitor monitor;
        Closeable displayHandle;
        VirtualDisplay vd;
        ExecutorService executor;
        int vdId;
        boolean restoreIme;
        int oldIme;
        synchronized (this) {
            monitor = appMonitor;
            appMonitor = null;
            displayHandle = displayChangeHandle;
            displayChangeHandle = null;
            vd = virtualDisplay;
            virtualDisplay = null;
            executor = launchExecutor;
            launchExecutor = null;
            vdId = virtualDisplayId;
            restoreIme = imePolicyOverridden;
            oldIme = previousImePolicy;
        }
        if (monitor != null) {
            monitor.close();
        }
        if (displayHandle != null) {
            try {
                displayHandle.close();
            } catch (IOException ignored) {
            }
        }
        if (restoreIme && vdId > 0 && oldIme != -1) {
            AndroidDisplayPlatform.setDisplayImePolicy(vdId, oldIme);
        }
        if (vd != null) {
            vd.release();
        }
        if (executor != null) {
            executor.shutdownNow();
        }
    }

    @Override
    public synchronized Size getVideoSize() {
        return actualVideoSize;
    }

    @Override
    public synchronized Size getDisplaySize() {
        return actualDisplaySize;
    }

    @Override
    public synchronized int getDisplayId() {
        return virtualDisplayId;
    }

    @Override
    public synchronized boolean setMaxSize(int newMaxSize) {
        if (initialMaxSize > 0 && newMaxSize > initialMaxSize) {
            return false;
        }
        this.maxSize = newMaxSize;
        return true;
    }

    @Override
    public synchronized boolean isClosed() {
        return virtualDisplayMissing;
    }

    public void requestResize(int width, int height) {
        synchronized (this) {
            if (mainDisplayPortraitSize == null) {
                return;
            }
            Size fitted = Geometry.fitVirtualDisplaySize(mainDisplayPortraitSize, width, height);
            if (maxSize > 0) {
                fitted = fitted.limitMajor(maxSize).round8();
            }
            if (requestedVideoSize == null
                    ? fitted.equals(actualVideoSize)
                    : fitted.equals(requestedVideoSize)) {
                return;
            }
            requestedVideoSize = fitted;
        }
        requestReset();
    }

    public void startOwnedApp(String packageSpec) {
        final int targetId;
        final OwnedAppMonitor monitor;
        final ExecutorService executor;
        synchronized (this) {
            targetId = virtualDisplayId;
            monitor = appMonitor;
            executor = launchExecutor;
        }
        if (targetId <= 0 || packageSpec == null || packageSpec.isEmpty()) {
            return;
        }
        if (monitor != null) {
            monitor.setOwnedPackage(packageSpec);
        }
        if (executor != null && !executor.isShutdown()) {
            executor.submit(() -> {
                AndroidSystemPlatform.startApp(packageSpec, targetId, false);
                if (monitor != null) {
                    monitor.scheduleEvaluate();
                }
            });
        }
    }

    public synchronized String getOwnedPackage() {
        return appMonitor != null ? appMonitor.getOwnedPackage() : null;
    }

    private void relaunchOwnedApp() {
        String pkg = getOwnedPackage();
        int targetId = getDisplayId();
        ExecutorService executor;
        synchronized (this) {
            executor = launchExecutor;
        }
        if (pkg != null && targetId > 0 && executor != null && !executor.isShutdown()) {
            executor.submit(() -> AndroidSystemPlatform.startApp(pkg, targetId, false));
        }
    }

    private void onVirtualDisplayChanged() {
        int id = getDisplayId();
        if (id <= 0) {
            return;
        }
        DisplayInfo info = AndroidDisplayPlatform.getDisplayInfo(id);
        synchronized (this) {
            if (info == null) {
                virtualDisplayMissing = true;
                requestReset();
                return;
            }
            Size current = info.getSize();
            if (!current.equals(actualDisplaySize) && requestedVideoSize == null) {
                actualDisplaySize = current;
                actualVideoSize = current.limitMajor(maxSize).round8();
                requestReset();
            }
        }
    }

    public static int scaleDpi(Size initialSize, int initialDpi, Size targetSize) {
        int baseDpi = initialDpi > 0 ? initialDpi : 160;
        if (initialSize == null || targetSize == null) {
            return Math.max(120, baseDpi);
        }
        int initialMajor = initialSize.getMaxSide();
        int targetMajor = targetSize.getMaxSide();
        if (initialMajor <= 0 || targetMajor <= 0) {
            return Math.max(120, baseDpi);
        }
        int scaled = (int) ((long) baseDpi * targetMajor / initialMajor);
        return Math.max(120, scaled);
    }
}
