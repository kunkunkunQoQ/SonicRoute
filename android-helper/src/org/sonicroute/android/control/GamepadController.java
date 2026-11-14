// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.control;

import android.os.SystemClock;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import org.sonicroute.android.io.ControlProtocol;
import org.sonicroute.android.platform.AndroidSystemPlatform;
import org.sonicroute.android.util.ServerLog;

/**
 * Translates virtual XInput controller packets into Android {@link MotionEvent} joystick frames
 * and {@link KeyEvent} gamepad button transitions per controller slot.
 */
public final class GamepadController {
    public static final int MAX_SLOTS = 4;

    public static final int XINPUT_DPAD_UP = 0x0001;
    public static final int XINPUT_DPAD_DOWN = 0x0002;
    public static final int XINPUT_DPAD_LEFT = 0x0004;
    public static final int XINPUT_DPAD_RIGHT = 0x0008;
    public static final int XINPUT_START = 0x0010;
    public static final int XINPUT_BACK = 0x0020;
    public static final int XINPUT_LEFT_THUMB = 0x0040;
    public static final int XINPUT_RIGHT_THUMB = 0x0080;
    public static final int XINPUT_LEFT_SHOULDER = 0x0100;
    public static final int XINPUT_RIGHT_SHOULDER = 0x0200;
    public static final int XINPUT_GUIDE = 0x0400;
    public static final int XINPUT_A = 0x1000;
    public static final int XINPUT_B = 0x2000;
    public static final int XINPUT_X = 0x4000;
    public static final int XINPUT_Y = 0x8000;

    public static final int TRIGGER_PRESS_THRESHOLD = 128;
    public static final int TRIGGER_RELEASE_THRESHOLD = 100;

    static final int GAMEPAD_KEY_SOURCE = InputDevice.SOURCE_GAMEPAD | InputDevice.SOURCE_KEYBOARD;

    // Digital gamepad buttons mapped to KeyEvents. D-Pad bits (0x0001..0x0008) are emitted via
    // AXIS_HAT_X/Y on SOURCE_JOYSTICK MotionEvents because Android ViewRootImpl.SyntheticJoystickHandler
    // automatically synthesizes single-step KEYCODE_DPAD_* events from HAT axes (verified on Android 16).
    static final int[][] DIGITAL_BUTTON_KEYCODES = {
            { XINPUT_START, KeyEvent.KEYCODE_BUTTON_START },
            { XINPUT_BACK, KeyEvent.KEYCODE_BUTTON_SELECT },
            { XINPUT_LEFT_THUMB, KeyEvent.KEYCODE_BUTTON_THUMBL },
            { XINPUT_RIGHT_THUMB, KeyEvent.KEYCODE_BUTTON_THUMBR },
            { XINPUT_LEFT_SHOULDER, KeyEvent.KEYCODE_BUTTON_L1 },
            { XINPUT_RIGHT_SHOULDER, KeyEvent.KEYCODE_BUTTON_R1 },
            { XINPUT_GUIDE, KeyEvent.KEYCODE_BUTTON_MODE },
            { XINPUT_A, KeyEvent.KEYCODE_BUTTON_A },
            { XINPUT_B, KeyEvent.KEYCODE_BUTTON_B },
            { XINPUT_X, KeyEvent.KEYCODE_BUTTON_X },
            { XINPUT_Y, KeyEvent.KEYCODE_BUTTON_Y },
    };

    private static final class SlotState {
        int prevButtons;
        boolean prevL2Down;
        boolean prevR2Down;
        boolean hasSentMotion;
        long downTimeMs;
        float axisX;
        float axisY;
        float axisZ;
        float axisRz;
        float axisLt;
        float axisRt;
        float axisHatX;
        float axisHatY;

        boolean hasNonZeroAxes() {
            return axisX != 0f || axisY != 0f || axisZ != 0f || axisRz != 0f
                    || axisLt != 0f || axisRt != 0f || axisHatX != 0f || axisHatY != 0f;
        }

        void resetAxes() {
            axisX = 0f;
            axisY = 0f;
            axisZ = 0f;
            axisRz = 0f;
            axisLt = 0f;
            axisRt = 0f;
            axisHatX = 0f;
            axisHatY = 0f;
            hasSentMotion = false;
            downTimeMs = 0L;
        }
    }

    private final SlotState[] slots = new SlotState[MAX_SLOTS];
    private final MotionEvent.PointerProperties[] pointerProps = new MotionEvent.PointerProperties[1];
    private final MotionEvent.PointerCoords[] pointerCoords = new MotionEvent.PointerCoords[1];

    public GamepadController() {
        for (int i = 0; i < slots.length; i++) {
            slots[i] = new SlotState();
        }
        pointerProps[0] = new MotionEvent.PointerProperties();
        pointerCoords[0] = new MotionEvent.PointerCoords();
    }

    public static float normalizeSignedAxis(short raw) {
        if (raw == 0) {
            return 0.0f;
        }
        return raw > 0 ? (raw / 32767.0f) : (raw / 32768.0f);
    }

    public static float normalizeNegatedSignedAxis(short raw) {
        if (raw == 0) {
            return 0.0f;
        }
        return -normalizeSignedAxis(raw);
    }

    public static float normalizeTrigger(int raw) {
        if (raw <= 0) {
            return 0.0f;
        }
        if (raw >= 255) {
            return 1.0f;
        }
        return raw / 255.0f;
    }

    public static float dpadHatAxis(int buttons, int negativeBit, int positiveBit) {
        boolean neg = (buttons & negativeBit) != 0;
        boolean pos = (buttons & positiveBit) != 0;
        if (neg == pos) {
            return 0.0f;
        }
        return neg ? -1.0f : 1.0f;
    }

    public static boolean nextTriggerDigitalState(boolean wasDown, int rawTrigger) {
        if (!wasDown && rawTrigger >= TRIGGER_PRESS_THRESHOLD) {
            return true;
        }
        if (wasDown && rawTrigger < TRIGGER_RELEASE_THRESHOLD) {
            return false;
        }
        return wasDown;
    }

    public void injectState(ControlProtocol.ClientMessage msg, int targetDisplayId) {
        int slot = msg.gamepadSlot & 3;
        SlotState state = slots[slot];
        int buttons = msg.gamepadButtons & 0xFFFF;

        float axisX = normalizeSignedAxis(msg.thumbLX);
        float axisY = normalizeNegatedSignedAxis(msg.thumbLY);
        float axisZ = normalizeSignedAxis(msg.thumbRX);
        float axisRz = normalizeNegatedSignedAxis(msg.thumbRY);
        float axisLt = normalizeTrigger(msg.leftTrigger);
        float axisRt = normalizeTrigger(msg.rightTrigger);
        float axisHatX = dpadHatAxis(buttons, XINPUT_DPAD_LEFT, XINPUT_DPAD_RIGHT);
        float axisHatY = dpadHatAxis(buttons, XINPUT_DPAD_UP, XINPUT_DPAD_DOWN);

        if (!state.hasSentMotion
                || state.axisX != axisX
                || state.axisY != axisY
                || state.axisZ != axisZ
                || state.axisRz != axisRz
                || state.axisLt != axisLt
                || state.axisRt != axisRt
                || state.axisHatX != axisHatX
                || state.axisHatY != axisHatY) {
            state.axisX = axisX;
            state.axisY = axisY;
            state.axisZ = axisZ;
            state.axisRz = axisRz;
            state.axisLt = axisLt;
            state.axisRt = axisRt;
            state.axisHatX = axisHatX;
            state.axisHatY = axisHatY;
            state.hasSentMotion = true;
            injectJoystickMotion(slot, state, targetDisplayId);
        }

        int changed = state.prevButtons ^ buttons;
        if (changed != 0) {
            emitChangedKeys(DIGITAL_BUTTON_KEYCODES, changed, buttons, GAMEPAD_KEY_SOURCE, targetDisplayId);
            state.prevButtons = buttons;
        }

        boolean nextL2Down = nextTriggerDigitalState(state.prevL2Down, msg.leftTrigger);
        if (nextL2Down != state.prevL2Down) {
            state.prevL2Down = nextL2Down;
            injectGamepadKey(nextL2Down ? KeyEvent.ACTION_DOWN : KeyEvent.ACTION_UP,
                    KeyEvent.KEYCODE_BUTTON_L2, GAMEPAD_KEY_SOURCE, targetDisplayId);
        }

        boolean nextR2Down = nextTriggerDigitalState(state.prevR2Down, msg.rightTrigger);
        if (nextR2Down != state.prevR2Down) {
            state.prevR2Down = nextR2Down;
            injectGamepadKey(nextR2Down ? KeyEvent.ACTION_DOWN : KeyEvent.ACTION_UP,
                    KeyEvent.KEYCODE_BUTTON_R2, GAMEPAD_KEY_SOURCE, targetDisplayId);
        }
    }

    public void releaseAll(int targetDisplayId) {
        for (int slot = 0; slot < slots.length; slot++) {
            SlotState state = slots[slot];
            try {
                boolean hadHeldState = state.prevButtons != 0 || state.prevL2Down || state.prevR2Down
                        || (state.hasSentMotion && state.hasNonZeroAxes());
                boolean allOk = true;
                int releasedMask = state.prevButtons;
                boolean releasedL2 = state.prevL2Down;
                boolean releasedR2 = state.prevR2Down;
                if (state.prevButtons != 0) {
                    allOk &= emitReleaseKeys(DIGITAL_BUTTON_KEYCODES, state.prevButtons, GAMEPAD_KEY_SOURCE, targetDisplayId);
                    state.prevButtons = 0;
                }
                if (state.prevL2Down) {
                    state.prevL2Down = false;
                    allOk &= injectGamepadKey(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_BUTTON_L2, GAMEPAD_KEY_SOURCE, targetDisplayId);
                }
                if (state.prevR2Down) {
                    state.prevR2Down = false;
                    allOk &= injectGamepadKey(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_BUTTON_R2, GAMEPAD_KEY_SOURCE, targetDisplayId);
                }
                boolean resetMotion = false;
                if (state.hasSentMotion && state.hasNonZeroAxes()) {
                    state.resetAxes();
                    resetMotion = true;
                    allOk &= injectJoystickMotion(slot, state, targetDisplayId);
                    state.hasSentMotion = false;
                    state.downTimeMs = 0L;
                } else {
                    state.resetAxes();
                }
                if (hadHeldState) {
                    ServerLog.d("Gamepad releaseAll: slot=" + slot
                            + " displayId=" + targetDisplayId
                            + " buttons=0x" + Integer.toHexString(releasedMask)
                            + " l2=" + releasedL2
                            + " r2=" + releasedR2
                            + " resetMotion=" + resetMotion
                            + " ok=" + allOk);
                }
            } catch (RuntimeException error) {
                ServerLog.w("Gamepad releaseAll failed: slot=" + slot, error);
                state.prevButtons = 0;
                state.prevL2Down = false;
                state.prevR2Down = false;
                state.resetAxes();
            }
        }
    }

    private boolean injectJoystickMotion(int slot, SlotState state, int targetDisplayId) {
        long now = SystemClock.uptimeMillis();
        if (state.downTimeMs == 0L) {
            state.downTimeMs = now;
        }
        MotionEvent.PointerProperties prop = pointerProps[0];
        prop.clear();
        prop.id = slot;
        prop.toolType = MotionEvent.TOOL_TYPE_UNKNOWN;

        MotionEvent.PointerCoords coords = pointerCoords[0];
        coords.clear();
        coords.setAxisValue(MotionEvent.AXIS_X, state.axisX);
        coords.setAxisValue(MotionEvent.AXIS_Y, state.axisY);
        coords.setAxisValue(MotionEvent.AXIS_Z, state.axisZ);
        coords.setAxisValue(MotionEvent.AXIS_RZ, state.axisRz);
        coords.setAxisValue(MotionEvent.AXIS_LTRIGGER, state.axisLt);
        coords.setAxisValue(MotionEvent.AXIS_RTRIGGER, state.axisRt);
        coords.setAxisValue(MotionEvent.AXIS_BRAKE, state.axisLt);
        coords.setAxisValue(MotionEvent.AXIS_GAS, state.axisRt);
        coords.setAxisValue(MotionEvent.AXIS_HAT_X, state.axisHatX);
        coords.setAxisValue(MotionEvent.AXIS_HAT_Y, state.axisHatY);

        MotionEvent event = MotionEvent.obtain(
                state.downTimeMs,
                now,
                MotionEvent.ACTION_MOVE,
                1,
                pointerProps,
                pointerCoords,
                0,
                0,
                1.0f,
                1.0f,
                0,
                0,
                InputDevice.SOURCE_JOYSTICK,
                0);
        try {
            return AndroidSystemPlatform.injectInputEvent(
                    event, targetDisplayId, AndroidSystemPlatform.INJECT_MODE_ASYNC);
        } finally {
            event.recycle();
        }
    }

    private static void emitChangedKeys(
            int[][] map, int changedMask, int buttons, int source, int targetDisplayId) {
        for (int i = 0; i < map.length; i++) {
            int bit = map[i][0];
            if ((changedMask & bit) == 0) {
                continue;
            }
            int action = (buttons & bit) != 0 ? KeyEvent.ACTION_DOWN : KeyEvent.ACTION_UP;
            injectGamepadKey(action, map[i][1], source, targetDisplayId);
        }
    }

    private static boolean emitReleaseKeys(int[][] map, int heldMask, int source, int targetDisplayId) {
        boolean allOk = true;
        for (int i = 0; i < map.length; i++) {
            if ((heldMask & map[i][0]) != 0) {
                allOk &= injectGamepadKey(KeyEvent.ACTION_UP, map[i][1], source, targetDisplayId);
            }
        }
        return allOk;
    }

    private static boolean injectGamepadKey(int action, int keyCode, int source, int targetDisplayId) {
        return AndroidSystemPlatform.injectKeyEvent(
                action, keyCode, 0, 0, source, targetDisplayId, AndroidSystemPlatform.INJECT_MODE_ASYNC);
    }
}
