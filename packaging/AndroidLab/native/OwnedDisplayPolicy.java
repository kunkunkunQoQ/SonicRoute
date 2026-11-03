/* SonicRoute owned virtual-display sizing policy. SPDX-License-Identifier: Apache-2.0 */
package com.genymobile.scrcpy.wrappers;

import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.os.Build;
import android.os.Handler;
import android.view.Surface;

import com.genymobile.scrcpy.util.Ln;

/** Applies only to the display being created, never to global app compatibility settings. */
final class OwnedDisplayPolicy {
    private OwnedDisplayPolicy() { }

    static VirtualDisplay create(DisplayManager manager, String name, int width, int height, int dpi, Surface surface, int flags) {
        // AccessibilityManager excludes PRIVATE virtual displays entirely, including shell UIA.
        // PUBLIC allows the owned editor bridge; omit PRESENTATION so phone apps do not see
        // this application window as a casting/presentation destination.
        flags |= DisplayManager.VIRTUAL_DISPLAY_FLAG_PUBLIC;
        flags &= ~DisplayManager.VIRTUAL_DISPLAY_FLAG_PRESENTATION;
        if (Build.VERSION.SDK_INT >= 36) {
            // Requires the existing TRUSTED + OWN_FOCUS flags. Keep the physical phone's top focus.
            flags |= 1 << 16; // VIRTUAL_DISPLAY_FLAG_STEAL_TOP_FOCUS_DISABLED (Android 16)
        }
        // The system API is hidden and feature-gated. Probe it once at display creation;
        // older/OEM systems keep the existing supported creation path without polling.
        if (Build.VERSION.SDK_INT >= 36) {
            try {
                Class<?> configClass = Class.forName("android.hardware.display.VirtualDisplayConfig");
                Class<?> builderClass = Class.forName("android.hardware.display.VirtualDisplayConfig$Builder");
                Object builder = builderClass.getConstructor(String.class, int.class, int.class, int.class)
                        .newInstance(name, width, height, dpi);
                builderClass.getMethod("setFlags", int.class).invoke(builder, flags);
                builderClass.getMethod("setSurface", Surface.class).invoke(builder, surface);
                builderClass.getMethod("setIgnoreActivitySizeRestrictions", boolean.class).invoke(builder, true);
                Object config = builderClass.getMethod("build").invoke(builder);
                boolean enabled = (Boolean) configClass.getMethod("isIgnoreActivitySizeRestrictions").invoke(config);
                if (enabled) {
                    VirtualDisplay display = (VirtualDisplay) DisplayManager.class.getMethod("createVirtualDisplay", configClass,
                            Handler.class, VirtualDisplay.Callback.class).invoke(manager, config, null, null);
                    if (display != null) {
                        Ln.i("Owned display allows application resizing without fixed orientation or aspect restrictions");
                        return display;
                    }
                } else {
                    Ln.w("Owned display unrestricted app sizing is disabled by this Android system");
                }
            } catch (ReflectiveOperationException | RuntimeException e) {
                Ln.w("Owned display unrestricted app sizing is unavailable; using the standard display policy", e);
            }
        }
        return manager.createVirtualDisplay(name, width, height, dpi, surface, flags);
    }
}
