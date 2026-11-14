// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.core;

import android.os.BatteryManager;
import android.os.Looper;
import java.io.Closeable;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.concurrent.TimeUnit;
import org.sonicroute.android.AndroidWorkarounds;
import org.sonicroute.android.platform.AndroidDisplayPlatform;
import org.sonicroute.android.platform.AndroidSystemPlatform;
import org.sonicroute.android.util.ServerLog;

/**
 * Out-of-process, pipe-EOF-driven cleanup guard restoring global Android settings and display power.
 * Blocks on {@code System.in.read()} in the child process so crash or {@code SIGKILL} triggers
 * immediate restoration without background polling.
 */
public final class CleanupProcess implements Closeable {
    private static final int CMD_RESTORE_POWER_ON = 1;
    private static final int CMD_NO_RESTORE_POWER_ON = 2;

    private final int displayId;
    private final boolean forcePowerOffOnClose;
    private final int restoreImePolicy;
    private final String restoreShowTouches;
    private final String restoreStayAwake;
    private final String restoreScreenOffTimeout;

    private final Process childProcess;
    private final OutputStream pipeToChild;
    private boolean restoreDisplayPowerOn;
    private boolean closed;

    private CleanupProcess(
            int displayId,
            boolean forcePowerOffOnClose,
            int restoreImePolicy,
            String restoreShowTouches,
            String restoreStayAwake,
            String restoreScreenOffTimeout,
            Process childProcess) {
        this.displayId = displayId;
        this.forcePowerOffOnClose = forcePowerOffOnClose;
        this.restoreImePolicy = restoreImePolicy;
        this.restoreShowTouches = restoreShowTouches;
        this.restoreStayAwake = restoreStayAwake;
        this.restoreScreenOffTimeout = restoreScreenOffTimeout;
        this.childProcess = childProcess;
        this.pipeToChild = childProcess != null ? childProcess.getOutputStream() : null;
    }

    public static CleanupProcess start(ServerOptions options) {
        if (!options.isCleanup()) {
            return new CleanupProcess(
                    Math.max(0, options.getDisplayId()),
                    options.isPowerOffScreenOnClose(),
                    -1,
                    null,
                    null,
                    null,
                    null);
        }

        String oldShowTouches = null;
        if (options.isShowTouches()) {
            oldShowTouches = getAndPutIfDifferent("system", "show_touches", "1");
        }

        String oldStayAwake = null;
        if (options.isStayAwake()) {
            int pluggedMask = BatteryManager.BATTERY_PLUGGED_AC
                    | BatteryManager.BATTERY_PLUGGED_USB
                    | BatteryManager.BATTERY_PLUGGED_WIRELESS;
            oldStayAwake = getAndPutIfDifferent("global", "stay_on_while_plugged_in", String.valueOf(pluggedMask));
        }

        String oldScreenOffTimeout = null;
        if (options.getScreenOffTimeout() >= 0) {
            oldScreenOffTimeout = getAndPutIfDifferent(
                    "system", "screen_off_timeout", String.valueOf(options.getScreenOffTimeout()));
        }

        boolean physicalVideo = options.isVideo() && options.getNewDisplay() == null;
        int displayId = physicalVideo ? Math.max(0, options.getDisplayId()) : 0;
        int oldImePolicy = -1;
        if (physicalVideo && options.getDisplayImePolicy() != ServerOptions.IME_POLICY_UNSET) {
            int prev = AndroidDisplayPlatform.getDisplayImePolicy(displayId);
            if (prev != -1 && prev != options.getDisplayImePolicy()) {
                oldImePolicy = prev;
            }
            AndroidDisplayPlatform.setDisplayImePolicy(displayId, options.getDisplayImePolicy());
        }

        boolean forcePowerOff = options.isPowerOffScreenOnClose();
        boolean needsChild = oldShowTouches != null
                || oldStayAwake != null
                || oldScreenOffTimeout != null
                || oldImePolicy != -1
                || forcePowerOff
                || options.isControl();

        Process child = null;
        if (needsChild) {
            child = spawnChildProcess(
                    displayId, forcePowerOff, oldImePolicy, oldShowTouches, oldStayAwake, oldScreenOffTimeout);
        }

        return new CleanupProcess(
                displayId, forcePowerOff, oldImePolicy, oldShowTouches, oldStayAwake, oldScreenOffTimeout, child);
    }

    public synchronized void setRestoreDisplayPower(boolean restorePowerOn) {
        if (closed) {
            return;
        }
        this.restoreDisplayPowerOn = restorePowerOn;
        if (pipeToChild != null) {
            try {
                pipeToChild.write(restorePowerOn ? CMD_RESTORE_POWER_ON : CMD_NO_RESTORE_POWER_ON);
                pipeToChild.flush();
            } catch (IOException error) {
                ServerLog.w("Failed to notify cleanup child about display power state", error);
            }
        }
    }

    @Override
    public void close() {
        Process proc;
        OutputStream out;
        boolean runInProcess;
        synchronized (this) {
            if (closed) {
                return;
            }
            closed = true;
            proc = childProcess;
            out = pipeToChild;
            runInProcess = (proc == null)
                    && (forcePowerOffOnClose
                    || restoreDisplayPowerOn
                    || restoreImePolicy != -1
                    || restoreShowTouches != null
                    || restoreStayAwake != null
                    || restoreScreenOffTimeout != null);
        }

        if (out != null) {
            try {
                out.close();
            } catch (IOException ignored) {
            }
        }
        if (proc != null) {
            try {
                if (!proc.waitFor(3, TimeUnit.SECONDS)) {
                    proc.destroy();
                }
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
                proc.destroy();
            }
            return;
        }

        if (runInProcess) {
            performRestore(
                    displayId,
                    forcePowerOffOnClose,
                    restoreDisplayPowerOn,
                    restoreImePolicy,
                    restoreShowTouches,
                    restoreStayAwake,
                    restoreScreenOffTimeout);
        }
    }

    public static void main(String[] args) {
        if (args.length < 6) {
            return;
        }
        try {
            Looper.prepareMainLooper();
        } catch (Throwable ignored) {
        }
        AndroidWorkarounds.apply();

        int displayId = parseIntOrZero(args[0]);
        boolean forcePowerOff = "1".equals(args[1]);
        int restoreImePolicy = parseIntOrDefault(args[2], -1);
        String restoreShowTouches = nonEmptyOrNull(args[3]);
        String restoreStayAwake = nonEmptyOrNull(args[4]);
        String restoreScreenOffTimeout = nonEmptyOrNull(args[5]);

        boolean restorePowerOn = false;
        InputStream in = System.in;
        try {
            int cmd;
            while ((cmd = in.read()) != -1) {
                if (cmd == CMD_RESTORE_POWER_ON) {
                    restorePowerOn = true;
                } else if (cmd == CMD_NO_RESTORE_POWER_ON) {
                    restorePowerOn = false;
                }
            }
        } catch (IOException ignored) {
        }

        performRestore(
                displayId,
                forcePowerOff,
                restorePowerOn,
                restoreImePolicy,
                restoreShowTouches,
                restoreStayAwake,
                restoreScreenOffTimeout);
    }

    private static void performRestore(
            int displayId,
            boolean forcePowerOff,
            boolean restorePowerOn,
            int restoreImePolicy,
            String restoreShowTouches,
            String restoreStayAwake,
            String restoreScreenOffTimeout) {
        if (restoreShowTouches != null) {
            try {
                AndroidSystemPlatform.putSetting("system", "show_touches", restoreShowTouches);
            } catch (Throwable error) {
                ServerLog.w("Could not restore show_touches", error);
            }
        }
        if (restoreStayAwake != null) {
            try {
                AndroidSystemPlatform.putSetting("global", "stay_on_while_plugged_in", restoreStayAwake);
            } catch (Throwable error) {
                ServerLog.w("Could not restore stay_on_while_plugged_in", error);
            }
        }
        if (restoreScreenOffTimeout != null) {
            try {
                AndroidSystemPlatform.putSetting("system", "screen_off_timeout", restoreScreenOffTimeout);
            } catch (Throwable error) {
                ServerLog.w("Could not restore screen_off_timeout", error);
            }
        }
        if (restoreImePolicy != -1) {
            try {
                AndroidDisplayPlatform.setDisplayImePolicy(displayId, restoreImePolicy);
            } catch (Throwable error) {
                ServerLog.w("Could not restore display IME policy", error);
            }
        }
        if (forcePowerOff) {
            AndroidDisplayPlatform.setDisplayPower(displayId, false);
        } else if (restorePowerOn) {
            AndroidDisplayPlatform.setDisplayPower(displayId, true);
        }
    }

    private static Process spawnChildProcess(
            int displayId,
            boolean forcePowerOff,
            int oldImePolicy,
            String oldShowTouches,
            String oldStayAwake,
            String oldScreenOffTimeout) {
        try {
            ProcessBuilder pb = new ProcessBuilder(
                    "app_process",
                    "/",
                    CleanupProcess.class.getName(),
                    String.valueOf(displayId),
                    forcePowerOff ? "1" : "0",
                    String.valueOf(oldImePolicy),
                    oldShowTouches != null ? oldShowTouches : "",
                    oldStayAwake != null ? oldStayAwake : "",
                    oldScreenOffTimeout != null ? oldScreenOffTimeout : "");
            String cp = System.getProperty("java.class.path");
            if (cp != null && !cp.isEmpty()) {
                pb.environment().put("CLASSPATH", cp);
            }
            File devNull = new File("/dev/null");
            if (devNull.exists()) {
                pb.redirectOutput(ProcessBuilder.Redirect.to(devNull));
                pb.redirectError(ProcessBuilder.Redirect.to(devNull));
            }
            return pb.start();
        } catch (IOException error) {
            ServerLog.w("Could not fork cleanup child process; using in-process fallback", error);
            return null;
        }
    }

    private static String getAndPutIfDifferent(String table, String key, String value) {
        String old = AndroidSystemPlatform.getAndPutSetting(table, key, value);
        if (old == null || value.equals(old)) {
            return null;
        }
        return old;
    }

    private static String nonEmptyOrNull(String value) {
        return (value == null || value.isEmpty()) ? null : value;
    }

    private static int parseIntOrZero(String value) {
        return parseIntOrDefault(value, 0);
    }

    private static int parseIntOrDefault(String value, int fallback) {
        if (value == null || value.isEmpty()) {
            return fallback;
        }
        try {
            return Integer.parseInt(value);
        } catch (NumberFormatException ignored) {
            return fallback;
        }
    }
}
