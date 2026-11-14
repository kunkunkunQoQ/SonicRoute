// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.io;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

/**
 * Binary control protocol reader (client -> server) and device message writer (server -> client)
 * matching {@code ScrcpyControlMessageWriter} and {@code resize_packet.h}.
 */
public final class ControlProtocol {
    public static final int TYPE_INJECT_KEYCODE = 0;
    public static final int TYPE_INJECT_TEXT = 1;
    public static final int TYPE_INJECT_TOUCH_EVENT = 2;
    public static final int TYPE_INJECT_SCROLL_EVENT = 3;
    public static final int TYPE_BACK_OR_SCREEN_ON = 4;
    public static final int TYPE_EXPAND_NOTIFICATION_PANEL = 5;
    public static final int TYPE_EXPAND_SETTINGS_PANEL = 6;
    public static final int TYPE_COLLAPSE_PANELS = 7;
    public static final int TYPE_GET_CLIPBOARD = 8;
    public static final int TYPE_SET_CLIPBOARD = 9;
    public static final int TYPE_SET_DISPLAY_POWER = 10;
    public static final int TYPE_ROTATE_DEVICE = 11;
    public static final int TYPE_OPEN_HARD_KEYBOARD_SETTINGS = 15;
    public static final int TYPE_START_APP = 16;
    public static final int TYPE_RESET_VIDEO = 17;
    public static final int TYPE_RESIZE_DISPLAY = 18;
    public static final int TYPE_INJECT_GAMEPAD_STATE = 19;

    public static final int COPY_KEY_NONE = 0;
    public static final int COPY_KEY_COPY = 1;
    public static final int COPY_KEY_CUT = 2;

    public static final int DEVICE_MSG_CLIPBOARD = 0;
    public static final int DEVICE_MSG_ACK_CLIPBOARD = 1;

    public static final int MAX_TEXT_BYTES = 1 << 18; // 256 KiB

    private ControlProtocol() {
    }

    public static final class ClientMessage {
        public final int type;
        public int action;
        public int keycode;
        public int repeat;
        public int metaState;
        public long pointerId;
        public int x;
        public int y;
        public int screenWidth;
        public int screenHeight;
        public float pressure;
        public int actionButton;
        public int buttons;
        public float hScroll;
        public float vScroll;
        public int copyKey;
        public long sequence;
        public boolean paste;
        public boolean on;
        public String text;
        public int width;
        public int height;
        public int gamepadSlot;
        public int gamepadButtons;
        public int leftTrigger;
        public int rightTrigger;
        public short thumbLX;
        public short thumbLY;
        public short thumbRX;
        public short thumbRY;

        public ClientMessage(int type) {
            this.type = type;
        }
    }

    public static final class Reader {
        private final DataInputStream in;

        public Reader(InputStream raw) {
            this.in = new DataInputStream(new BufferedInputStream(raw));
        }

        public ClientMessage read() throws IOException {
            int type = in.readUnsignedByte();
            ClientMessage msg = new ClientMessage(type);
            switch (type) {
                case TYPE_INJECT_KEYCODE:
                    msg.action = in.readUnsignedByte();
                    msg.keycode = in.readInt();
                    msg.repeat = in.readInt();
                    msg.metaState = in.readInt();
                    return msg;
                case TYPE_INJECT_TEXT:
                    msg.text = readUtf8String(in.readInt());
                    return msg;
                case TYPE_INJECT_TOUCH_EVENT:
                    msg.action = in.readUnsignedByte();
                    msg.pointerId = in.readLong();
                    msg.x = in.readInt();
                    msg.y = in.readInt();
                    msg.screenWidth = in.readUnsignedShort();
                    msg.screenHeight = in.readUnsignedShort();
                    msg.pressure = u16FixedToFloat(in.readUnsignedShort());
                    msg.actionButton = in.readInt();
                    msg.buttons = in.readInt();
                    return msg;
                case TYPE_INJECT_SCROLL_EVENT:
                    msg.x = in.readInt();
                    msg.y = in.readInt();
                    msg.screenWidth = in.readUnsignedShort();
                    msg.screenHeight = in.readUnsignedShort();
                    msg.hScroll = i16FixedToFloat(in.readShort()) * 16.0f;
                    msg.vScroll = i16FixedToFloat(in.readShort()) * 16.0f;
                    msg.buttons = in.readInt();
                    return msg;
                case TYPE_BACK_OR_SCREEN_ON:
                    msg.action = in.readUnsignedByte();
                    return msg;
                case TYPE_GET_CLIPBOARD:
                    msg.copyKey = in.readUnsignedByte();
                    return msg;
                case TYPE_SET_CLIPBOARD:
                    msg.sequence = in.readLong();
                    msg.paste = in.readUnsignedByte() != 0;
                    msg.text = readUtf8String(in.readInt());
                    return msg;
                case TYPE_SET_DISPLAY_POWER:
                    msg.on = in.readUnsignedByte() != 0;
                    return msg;
                case TYPE_START_APP:
                    msg.text = readUtf8String(in.readUnsignedByte());
                    return msg;
                case TYPE_RESIZE_DISPLAY:
                    msg.width = in.readUnsignedShort();
                    msg.height = in.readUnsignedShort();
                    return msg;
                case TYPE_INJECT_GAMEPAD_STATE:
                    msg.gamepadSlot = in.readUnsignedByte();
                    msg.gamepadButtons = in.readUnsignedShort();
                    msg.leftTrigger = in.readUnsignedByte();
                    msg.rightTrigger = in.readUnsignedByte();
                    msg.thumbLX = in.readShort();
                    msg.thumbLY = in.readShort();
                    msg.thumbRX = in.readShort();
                    msg.thumbRY = in.readShort();
                    return msg;
                case TYPE_EXPAND_NOTIFICATION_PANEL:
                case TYPE_EXPAND_SETTINGS_PANEL:
                case TYPE_COLLAPSE_PANELS:
                case TYPE_ROTATE_DEVICE:
                case TYPE_OPEN_HARD_KEYBOARD_SETTINGS:
                case TYPE_RESET_VIDEO:
                    return msg;
                default:
                    throw new IOException("Unsupported control message type: " + type);
            }
        }

        private String readUtf8String(int length) throws IOException {
            if (length < 0 || length > MAX_TEXT_BYTES) {
                throw new IOException("Control message text length out of range: " + length);
            }
            byte[] data = new byte[length];
            in.readFully(data);
            return new String(data, StandardCharsets.UTF_8);
        }
    }

    public static final class Writer {
        private static final int CLIPBOARD_MAX_BYTES = MAX_TEXT_BYTES - 5;
        private final DataOutputStream out;

        public Writer(OutputStream raw) {
            this.out = new DataOutputStream(new BufferedOutputStream(raw));
        }

        public synchronized void sendClipboard(String text) throws IOException {
            String safeText = text != null ? text : "";
            byte[] raw = safeText.getBytes(StandardCharsets.UTF_8);
            int len = StreamSession.truncateUtf8(raw, CLIPBOARD_MAX_BYTES);
            out.writeByte(DEVICE_MSG_CLIPBOARD);
            out.writeInt(len);
            out.write(raw, 0, len);
            out.flush();
        }

        public synchronized void sendAckClipboard(long sequence) throws IOException {
            out.writeByte(DEVICE_MSG_ACK_CLIPBOARD);
            out.writeLong(sequence);
            out.flush();
        }
    }

    public static float u16FixedToFloat(int unsignedShort) {
        int masked = unsignedShort & 0xFFFF;
        return masked == 0xFFFF ? 1.0f : (masked / 65536.0f);
    }

    public static float i16FixedToFloat(short signedShort) {
        return signedShort == 0x7FFF ? 1.0f : (signedShort / 32768.0f);
    }
}
