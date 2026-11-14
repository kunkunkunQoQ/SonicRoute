// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.control;

import android.content.Intent;
import android.net.LocalSocket;
import android.os.SystemClock;
import android.view.InputDevice;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;
import java.io.Closeable;
import java.io.EOFException;
import java.io.IOException;
import java.text.Normalizer;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;
import org.sonicroute.android.ShellContext;
import org.sonicroute.android.core.CleanupProcess;
import org.sonicroute.android.core.ServerOptions;
import org.sonicroute.android.io.ControlProtocol;
import org.sonicroute.android.io.ControlProtocol.ClientMessage;
import org.sonicroute.android.model.Geometry;
import org.sonicroute.android.model.Geometry.DisplayInfo;
import org.sonicroute.android.model.Geometry.Point;
import org.sonicroute.android.model.Geometry.Size;
import org.sonicroute.android.platform.AndroidDisplayPlatform;
import org.sonicroute.android.platform.AndroidSystemPlatform;
import org.sonicroute.android.util.ServerLog;
import org.sonicroute.android.video.VideoCaptureSource;
import org.sonicroute.android.video.VirtualDisplaySource;

/**
 * Handles binary client control packets and clipboard synchronization over the control socket.
 */
public final class InputController {
    private static final long POINTER_ID_MOUSE = -1L;

    private final ControlProtocol.Reader reader;
    private final ControlProtocol.Writer writer;
    private final ServerOptions options;
    private final CleanupProcess cleanup;
    private final Runnable onFinish;

    private final KeyCharacterMap charMap = KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD);
    private final PointersState pointers = new PointersState();
    private final MotionEvent.PointerProperties[] pointerProps = new MotionEvent.PointerProperties[PointersState.MAX_POINTERS];
    private final MotionEvent.PointerCoords[] pointerCoords = new MotionEvent.PointerCoords[PointersState.MAX_POINTERS];
    private final UnicodeBroker unicodeBroker = new UnicodeBroker();
    private final GamepadController gamepadController = new GamepadController();
    private final AtomicBoolean settingClipboardInternally = new AtomicBoolean();
    private final ExecutorService senderExecutor = Executors.newSingleThreadExecutor(r -> new Thread(r, "SonicRoute-CtrlTx"));
    private final ExecutorService appLaunchExecutor = Executors.newSingleThreadExecutor(r -> new Thread(r, "SonicRoute-AppCmd"));

    private VideoCaptureSource captureSource;
    private Closeable clipboardListenerHandle;
    private long lastTouchDownMs;
    private volatile boolean stopped;
    private Thread thread;

    public InputController(
            LocalSocket controlSocket,
            ServerOptions options,
            CleanupProcess cleanup,
            Runnable onFinish) throws IOException {
        this.reader = new ControlProtocol.Reader(controlSocket.getInputStream());
        this.writer = new ControlProtocol.Writer(controlSocket.getOutputStream());
        this.options = options;
        this.cleanup = cleanup;
        this.onFinish = onFinish;
        for (int i = 0; i < PointersState.MAX_POINTERS; ++i) {
            pointerProps[i] = new MotionEvent.PointerProperties();
            pointerCoords[i] = new MotionEvent.PointerCoords();
        }
    }

    public void setCaptureSource(VideoCaptureSource captureSource) {
        this.captureSource = captureSource;
    }

    public void start() {
        if (options.isClipboardAutosync()) {
            clipboardListenerHandle = AndroidSystemPlatform.addClipboardListener(() -> {
                if (settingClipboardInternally.get()) {
                    return;
                }
                CharSequence text = AndroidSystemPlatform.getClipboardText();
                if (text != null) {
                    sendClipboardAsync(text.toString());
                }
            });
        }
        thread = new Thread(this::runLoop, "SonicRoute-Control");
        thread.start();
    }

    public void stop() {
        stopped = true;
        if (clipboardListenerHandle != null) {
            try {
                clipboardListenerHandle.close();
            } catch (IOException ignored) {
            }
            clipboardListenerHandle = null;
        }
        if (thread != null) {
            thread.interrupt();
        }
    }

    public void join() throws InterruptedException {
        if (thread != null) {
            thread.join();
        }
    }

    private void runLoop() {
        try {
            if (options.isPowerOn() && options.getNewDisplay() == null) {
                int target = actionDisplayId();
                if (!AndroidDisplayPlatform.isScreenOn(target)) {
                    AndroidSystemPlatform.pressReleaseKeycode(
                            KeyEvent.KEYCODE_POWER, target, AndroidSystemPlatform.INJECT_MODE_ASYNC);
                    SystemClock.sleep(500);
                }
            }
            while (!stopped) {
                ClientMessage msg = reader.read();
                dispatch(msg);
            }
        } catch (EOFException eof) {
            // Normal socket close by host.
        } catch (IOException error) {
            if (!stopped) {
                ServerLog.d("Control channel ended: " + error.getMessage());
            }
        } catch (Throwable error) {
            ServerLog.e("Control loop failed", error);
        } finally {
            gamepadController.releaseAll(actionDisplayId());
            unicodeBroker.close();
            senderExecutor.shutdownNow();
            appLaunchExecutor.shutdownNow();
            if (onFinish != null) {
                onFinish.run();
            }
        }
    }

    private void dispatch(ClientMessage msg) {
        switch (msg.type) {
            case ControlProtocol.TYPE_INJECT_KEYCODE:
                AndroidSystemPlatform.injectKeyEvent(
                        msg.action, msg.keycode, msg.repeat, msg.metaState,
                        actionDisplayId(), AndroidSystemPlatform.INJECT_MODE_ASYNC);
                break;
            case ControlProtocol.TYPE_INJECT_TEXT:
                injectText(msg.text);
                break;
            case ControlProtocol.TYPE_INJECT_TOUCH_EVENT:
                injectTouch(msg);
                break;
            case ControlProtocol.TYPE_INJECT_SCROLL_EVENT:
                injectScroll(msg);
                break;
            case ControlProtocol.TYPE_BACK_OR_SCREEN_ON:
                backOrScreenOn(msg.action);
                break;
            case ControlProtocol.TYPE_EXPAND_NOTIFICATION_PANEL:
                AndroidSystemPlatform.expandNotificationsPanel();
                break;
            case ControlProtocol.TYPE_EXPAND_SETTINGS_PANEL:
                AndroidSystemPlatform.expandSettingsPanel();
                break;
            case ControlProtocol.TYPE_COLLAPSE_PANELS:
                AndroidSystemPlatform.collapsePanels();
                break;
            case ControlProtocol.TYPE_GET_CLIPBOARD:
                handleGetClipboard(msg.copyKey);
                break;
            case ControlProtocol.TYPE_SET_CLIPBOARD:
                handleSetClipboard(msg.sequence, msg.paste, msg.text);
                break;
            case ControlProtocol.TYPE_SET_DISPLAY_POWER:
                handleSetDisplayPower(msg.on);
                break;
            case ControlProtocol.TYPE_ROTATE_DEVICE:
                handleRotateDevice();
                break;
            case ControlProtocol.TYPE_OPEN_HARD_KEYBOARD_SETTINGS:
                openKeyboardSettings();
                break;
            case ControlProtocol.TYPE_START_APP:
                handleStartApp(msg.text);
                break;
            case ControlProtocol.TYPE_RESET_VIDEO:
                if (captureSource != null) {
                    captureSource.requestReset();
                }
                break;
            case ControlProtocol.TYPE_RESIZE_DISPLAY:
                if (captureSource instanceof VirtualDisplaySource) {
                    ((VirtualDisplaySource) captureSource).requestResize(msg.width, msg.height);
                }
                break;
            case ControlProtocol.TYPE_INJECT_GAMEPAD_STATE:
                gamepadController.injectState(msg, actionDisplayId());
                break;
            default:
                break;
        }
    }

    private void injectText(String text) {
        if (text == null || text.isEmpty()) {
            return;
        }
        StringBuilder fallbackBuffer = new StringBuilder();
        for (int i = 0; i < text.length(); ++i) {
            char c = text.charAt(i);
            if (!tryInjectAsciiChar(c)) {
                fallbackBuffer.append(c);
            } else if (fallbackBuffer.length() > 0) {
                flushUnicodeOrPaste(fallbackBuffer.toString());
                fallbackBuffer.setLength(0);
            }
        }
        if (fallbackBuffer.length() > 0) {
            flushUnicodeOrPaste(fallbackBuffer.toString());
        }
    }

    private boolean tryInjectAsciiChar(char c) {
        if (c > 0xFF) {
            return false;
        }
        String decomposed = Normalizer.normalize(String.valueOf(c), Normalizer.Form.NFD);
        char[] chars = decomposed.toCharArray();
        KeyEvent[] events = charMap.getEvents(chars);
        if (events == null) {
            return false;
        }
        int target = actionDisplayId();
        for (KeyEvent event : events) {
            if (!AndroidSystemPlatform.injectInputEvent(event, target, AndroidSystemPlatform.INJECT_MODE_ASYNC)) {
                return false;
            }
        }
        return true;
    }

    private void flushUnicodeOrPaste(String text) {
        if (captureSource instanceof VirtualDisplaySource) {
            VirtualDisplaySource vds = (VirtualDisplaySource) captureSource;
            String ownedPkg = vds.getOwnedPackage();
            int vdId = vds.getDisplayId();
            if (ownedPkg != null && vdId > 0 && unicodeBroker.commit(vdId, ownedPkg, text)) {
                return;
            }
        }
        setClipboardPreservingLoop(text);
        AndroidSystemPlatform.pressReleaseKeycode(
                KeyEvent.KEYCODE_PASTE, actionDisplayId(), AndroidSystemPlatform.INJECT_MODE_ASYNC);
    }

    private void injectTouch(ClientMessage msg) {
        Point mapped = mapCoordinates(msg.x, msg.y, msg.screenWidth, msg.screenHeight);
        if (mapped == null) {
            return;
        }

        int pointerIndex = pointers.getPointerIndex(msg.pointerId);
        if (pointerIndex < 0) {
            return;
        }
        PointersState.Pointer pointer = pointers.get(pointerIndex);
        pointer.setPoint(mapped);
        pointer.setPressure(msg.pressure);

        int action = msg.action;
        int source;
        int toolType;
        if (msg.pointerId == POINTER_ID_MOUSE) {
            source = InputDevice.SOURCE_MOUSE;
            toolType = MotionEvent.TOOL_TYPE_MOUSE;
            pointer.setUp(msg.buttons == 0);
        } else {
            source = InputDevice.SOURCE_TOUCHSCREEN;
            toolType = MotionEvent.TOOL_TYPE_FINGER;
            pointer.setUp(action == MotionEvent.ACTION_UP);
        }

        int pointerCount = pointers.populateArrays(pointerProps, pointerCoords, toolType);
        long now = SystemClock.uptimeMillis();
        if (pointerCount == 1) {
            if (action == MotionEvent.ACTION_DOWN) {
                lastTouchDownMs = now;
            }
        } else {
            if (action == MotionEvent.ACTION_UP) {
                action = MotionEvent.ACTION_POINTER_UP | (pointerIndex << MotionEvent.ACTION_POINTER_INDEX_SHIFT);
            } else if (action == MotionEvent.ACTION_DOWN) {
                action = MotionEvent.ACTION_POINTER_DOWN | (pointerIndex << MotionEvent.ACTION_POINTER_INDEX_SHIFT);
            }
        }

        int target = actionDisplayId();
        if (source == InputDevice.SOURCE_MOUSE) {
            if (action == MotionEvent.ACTION_DOWN) {
                if (msg.actionButton == msg.buttons) {
                    MotionEvent down = MotionEvent.obtain(
                            lastTouchDownMs, now, MotionEvent.ACTION_DOWN, pointerCount, pointerProps, pointerCoords,
                            0, msg.buttons, 1f, 1f, 0, 0, source, 0);
                    if (!AndroidSystemPlatform.injectInputEvent(down, target, AndroidSystemPlatform.INJECT_MODE_ASYNC)) {
                        return;
                    }
                }
                MotionEvent press = MotionEvent.obtain(
                        lastTouchDownMs, now, MotionEvent.ACTION_BUTTON_PRESS, pointerCount, pointerProps, pointerCoords,
                        0, msg.buttons, 1f, 1f, 0, 0, source, 0);
                AndroidSystemPlatform.setActionButton(press, msg.actionButton);
                AndroidSystemPlatform.injectInputEvent(press, target, AndroidSystemPlatform.INJECT_MODE_ASYNC);
                return;
            }
            if (action == MotionEvent.ACTION_UP) {
                MotionEvent release = MotionEvent.obtain(
                        lastTouchDownMs, now, MotionEvent.ACTION_BUTTON_RELEASE, pointerCount, pointerProps, pointerCoords,
                        0, msg.buttons, 1f, 1f, 0, 0, source, 0);
                AndroidSystemPlatform.setActionButton(release, msg.actionButton);
                if (!AndroidSystemPlatform.injectInputEvent(release, target, AndroidSystemPlatform.INJECT_MODE_ASYNC)) {
                    return;
                }
                if (msg.buttons == 0) {
                    MotionEvent up = MotionEvent.obtain(
                            lastTouchDownMs, now, MotionEvent.ACTION_UP, pointerCount, pointerProps, pointerCoords,
                            0, msg.buttons, 1f, 1f, 0, 0, source, 0);
                    AndroidSystemPlatform.injectInputEvent(up, target, AndroidSystemPlatform.INJECT_MODE_ASYNC);
                }
                return;
            }
        }

        MotionEvent event = MotionEvent.obtain(
                lastTouchDownMs, now, action, pointerCount, pointerProps, pointerCoords,
                0, msg.buttons, 1f, 1f, 0, 0, source, 0);
        AndroidSystemPlatform.injectInputEvent(event, target, AndroidSystemPlatform.INJECT_MODE_ASYNC);
    }

    private void injectScroll(ClientMessage msg) {
        Point mapped = mapCoordinates(msg.x, msg.y, msg.screenWidth, msg.screenHeight);
        if (mapped == null) {
            return;
        }
        pointerProps[0].clear();
        pointerProps[0].id = 0;
        pointerProps[0].toolType = MotionEvent.TOOL_TYPE_MOUSE;

        pointerCoords[0].clear();
        pointerCoords[0].x = mapped.getX();
        pointerCoords[0].y = mapped.getY();
        pointerCoords[0].setAxisValue(MotionEvent.AXIS_HSCROLL, msg.hScroll);
        pointerCoords[0].setAxisValue(MotionEvent.AXIS_VSCROLL, msg.vScroll);

        long now = SystemClock.uptimeMillis();
        MotionEvent event = MotionEvent.obtain(
                lastTouchDownMs, now, MotionEvent.ACTION_SCROLL, 1, pointerProps, pointerCoords,
                0, msg.buttons, 1f, 1f, 0, 0, InputDevice.SOURCE_MOUSE, 0);
        AndroidSystemPlatform.injectInputEvent(event, actionDisplayId(), AndroidSystemPlatform.INJECT_MODE_ASYNC);
    }

    private Point mapCoordinates(int x, int y, int screenW, int screenH) {
        if (captureSource == null) {
            return null;
        }
        return Geometry.mapClientPoint(
                x, y, screenW, screenH, captureSource.getVideoSize(), captureSource.getDisplaySize());
    }

    private void backOrScreenOn(int action) {
        int target = actionDisplayId();
        if (action == KeyEvent.ACTION_DOWN && !AndroidDisplayPlatform.isScreenOn(target)) {
            AndroidSystemPlatform.pressReleaseKeycode(
                    KeyEvent.KEYCODE_POWER, target, AndroidSystemPlatform.INJECT_MODE_ASYNC);
            return;
        }
        AndroidSystemPlatform.injectKeyEvent(
                action, KeyEvent.KEYCODE_BACK, 0, 0, target, AndroidSystemPlatform.INJECT_MODE_ASYNC);
    }

    private void handleGetClipboard(int copyKey) {
        int target = actionDisplayId();
        if (copyKey == ControlProtocol.COPY_KEY_COPY) {
            AndroidSystemPlatform.pressReleaseKeycode(
                    KeyEvent.KEYCODE_COPY, target, AndroidSystemPlatform.INJECT_MODE_WAIT_FOR_FINISH);
        } else if (copyKey == ControlProtocol.COPY_KEY_CUT) {
            AndroidSystemPlatform.pressReleaseKeycode(
                    KeyEvent.KEYCODE_CUT, target, AndroidSystemPlatform.INJECT_MODE_WAIT_FOR_FINISH);
        }
        CharSequence text = AndroidSystemPlatform.getClipboardText();
        if (text != null) {
            sendClipboardAsync(text.toString());
        }
    }

    private void handleSetClipboard(long sequence, boolean paste, String text) {
        setClipboardPreservingLoop(text);
        if (paste) {
            AndroidSystemPlatform.pressReleaseKeycode(
                    KeyEvent.KEYCODE_PASTE, actionDisplayId(), AndroidSystemPlatform.INJECT_MODE_ASYNC);
        }
        if (sequence != 0L) {
            senderExecutor.submit(() -> {
                try {
                    writer.sendAckClipboard(sequence);
                } catch (IOException ignored) {
                }
            });
        }
    }

    private void setClipboardPreservingLoop(String text) {
        settingClipboardInternally.set(true);
        try {
            AndroidSystemPlatform.setClipboardText(text);
        } finally {
            settingClipboardInternally.set(false);
        }
    }

    private void handleSetDisplayPower(boolean on) {
        int target = actionDisplayId();
        if (AndroidDisplayPlatform.setDisplayPower(target, on) && cleanup != null) {
            cleanup.setRestoreDisplayPower(!on);
        }
    }

    private void handleRotateDevice() {
        int target = actionDisplayId();
        DisplayInfo info = AndroidDisplayPlatform.getDisplayInfo(target);
        if (info == null) {
            return;
        }
        boolean currentlyPortrait = (info.getRotation() & 1) == 0;
        AndroidDisplayPlatform.freezeRotation(target, currentlyPortrait ? 1 : 0);
    }

    private void openKeyboardSettings() {
        Intent intent = new Intent("android.settings.HARD_KEYBOARD_SETTINGS");
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try {
            ShellContext.get().startActivity(intent);
        } catch (Throwable error) {
            ServerLog.w("Unable to open hard keyboard settings", error);
        }
    }

    private void handleStartApp(String packageSpec) {
        if (packageSpec == null || packageSpec.isEmpty()) {
            return;
        }
        if (captureSource instanceof VirtualDisplaySource) {
            ((VirtualDisplaySource) captureSource).startOwnedApp(packageSpec);
            return;
        }
        int target = actionDisplayId();
        if (target >= 0 && !appLaunchExecutor.isShutdown()) {
            appLaunchExecutor.submit(() -> AndroidSystemPlatform.startApp(packageSpec, target, false));
        }
    }

    private int actionDisplayId() {
        if (captureSource != null && captureSource.getDisplayId() >= 0) {
            return captureSource.getDisplayId();
        }
        return options.getDisplayId() >= 0 ? options.getDisplayId() : 0;
    }

    private void sendClipboardAsync(String text) {
        if (!senderExecutor.isShutdown()) {
            senderExecutor.submit(() -> {
                try {
                    writer.sendClipboard(text);
                } catch (IOException ignored) {
                }
            });
        }
    }
}
