// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.video;

import android.view.Surface;
import java.io.Closeable;
import java.io.IOException;
import org.sonicroute.android.model.Geometry.DisplayInfo;
import org.sonicroute.android.model.Geometry.Size;
import org.sonicroute.android.platform.AndroidDisplayPlatform;
import org.sonicroute.android.platform.AndroidDisplayPlatform.MirrorHandle;
import org.sonicroute.android.util.ServerLog;

/**
 * Mirrors an existing Android display (typically {@code displayId = 0}) onto an encoder surface
 * and triggers event-driven encoder resets on orientation or dimension changes.
 */
public final class PhysicalScreenSource extends VideoCaptureSource {
    private final int displayId;
    private int maxSize;

    private DisplayInfo displayInfo;
    private Size videoSize;
    private MirrorHandle mirrorHandle;
    private Closeable changeListener;

    public PhysicalScreenSource(int displayId, int maxSize) {
        this.displayId = displayId;
        this.maxSize = maxSize;
    }

    @Override
    public void init() {
        changeListener = AndroidDisplayPlatform.registerDisplayChangeListener(displayId, this::onDisplayChanged);
    }

    @Override
    public synchronized void prepare() throws IOException {
        DisplayInfo info = AndroidDisplayPlatform.getDisplayInfo(displayId);
        if (info == null) {
            throw new IOException("Display " + displayId + " does not exist");
        }
        if (!AndroidDisplayPlatform.supportsProtectedBuffers(info)) {
            ServerLog.w("Display " + displayId + " lacks FLAG_SUPPORTS_PROTECTED_BUFFERS; DRM frames may appear blank");
        }
        this.displayInfo = info;
        this.videoSize = info.getSize().limitMajor(maxSize).round8();
    }

    @Override
    public synchronized void start(Surface surface) throws IOException {
        stop();
        try {
            mirrorHandle = AndroidDisplayPlatform.createMirrorDisplay("sonicroute", videoSize, displayInfo, surface);
        } catch (Exception error) {
            throw new IOException("Failed to create mirror virtual display", error);
        }
    }

    @Override
    public synchronized void stop() {
        if (mirrorHandle != null) {
            mirrorHandle.close();
            mirrorHandle = null;
        }
    }

    @Override
    public void release() {
        stop();
        if (changeListener != null) {
            try {
                changeListener.close();
            } catch (IOException ignored) {
            }
            changeListener = null;
        }
    }

    @Override
    public synchronized Size getVideoSize() {
        return videoSize;
    }

    @Override
    public synchronized Size getDisplaySize() {
        return displayInfo != null ? displayInfo.getSize() : null;
    }

    @Override
    public int getDisplayId() {
        return displayId;
    }

    @Override
    public synchronized boolean setMaxSize(int newMaxSize) {
        this.maxSize = newMaxSize;
        return true;
    }

    private void onDisplayChanged() {
        DisplayInfo latest = AndroidDisplayPlatform.getDisplayInfo(displayId);
        boolean changed;
        synchronized (this) {
            Size oldSize = displayInfo != null ? displayInfo.getSize() : null;
            Size newSize = latest != null ? latest.getSize() : null;
            changed = newSize == null || !newSize.equals(oldSize);
        }
        if (changed) {
            requestReset();
        }
    }
}
