using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SonicRoute.AndroidAudio;

/// <summary>Parsed initial scrcpy video stream header (device metadata + codec id + initial dimensions).</summary>
public readonly record struct ScrcpyVideoStreamHeader(uint CodecId, int Width, int Height, string DeviceName)
{
    public const uint CodecH264 = 0x68323634; // "h264"

    public static async Task<ScrcpyVideoStreamHeader> ReadAsync(
        Stream stream, bool expectDeviceMeta, CancellationToken cancellationToken)
    {
        string deviceName = string.Empty;
        if (expectDeviceMeta)
        {
            byte[] meta = new byte[64];
            await stream.ReadExactlyAsync(meta, cancellationToken).ConfigureAwait(false);
            int nul = Array.IndexOf(meta, (byte)0);
            int len = nul >= 0 ? nul : meta.Length;
            deviceName = Encoding.UTF8.GetString(meta, 0, len);
        }

        byte[] header = new byte[12];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        uint codecId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        return new ScrcpyVideoStreamHeader(codecId, width, height, deviceName);
    }
}

public readonly record struct ScrcpyVideoPacketHeader(
    ulong RawPtsFlags,
    int PayloadLength,
    bool IsConfig,
    bool IsKeyFrame,
    long PtsUs)
{
    public const ulong ConfigFlag = 1UL << 63;
    public const ulong KeyFrameFlag = 1UL << 62;
    public const ulong PtsMask = KeyFrameFlag - 1;
    public const int MaxPacketBytes = 32 * 1024 * 1024;

    public static ScrcpyVideoPacketHeader Parse(ReadOnlySpan<byte> header12)
    {
        ulong ptsFlags = BinaryPrimitives.ReadUInt64BigEndian(header12[..8]);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header12.Slice(8, 4));
        if (size == 0 || size > MaxPacketBytes)
            throw new InvalidDataException($"Invalid H.264 packet size: {size}.");
        bool isConfig = (ptsFlags & ConfigFlag) != 0;
        bool isKey = (ptsFlags & KeyFrameFlag) != 0;
        long pts = (long)(ptsFlags & PtsMask);
        return new ScrcpyVideoPacketHeader(ptsFlags, (int)size, isConfig, isKey, pts);
    }
}

/// <summary>
/// Matches scrcpy <c>sc_packet_merger</c>: caches SPS/PPS Annex-B config packet and prepends it to the
/// next non-config packet so Media Foundation always receives in-band parameter sets with keyframes.
/// </summary>
public sealed class ScrcpyPacketMerger
{
    private byte[]? _pendingConfig;
    private byte[] _mergedScratch = Array.Empty<byte>();

    public bool HasPendingConfig => _pendingConfig is { Length: > 0 };

    public void Reset() => _pendingConfig = null;

    /// <summary>
    /// If <paramref name="isConfig"/> is true, caches the config bytes and returns an empty span.
    /// Otherwise returns either <paramref name="payload"/> directly or <c>[pendingConfig][payload]</c>.
    /// </summary>
    public ReadOnlySpan<byte> Process(ReadOnlySpan<byte> payload, bool isConfig)
    {
        if (isConfig)
        {
            _pendingConfig = payload.ToArray();
            return ReadOnlySpan<byte>.Empty;
        }

        if (_pendingConfig is not { Length: > 0 } cfg)
            return payload;

        int total = cfg.Length + payload.Length;
        if (_mergedScratch.Length < total)
            _mergedScratch = new byte[Math.Max(total, 65536)];

        cfg.CopyTo(_mergedScratch);
        payload.CopyTo(_mergedScratch.AsSpan(cfg.Length));
        _pendingConfig = null;
        return _mergedScratch.AsSpan(0, total);
    }
}

/// <summary>Big-endian scrcpy + SonicRoute custom resize control message serializer.</summary>
public static class ScrcpyControlMessageWriter
{
    public const byte TypeInjectKeycode = 0;
    public const byte TypeInjectText = 1;
    public const byte TypeInjectTouchEvent = 2;
    public const byte TypeInjectScrollEvent = 3;
    public const byte TypeBackOrScreenOn = 4;
    public const byte TypeExpandNotificationPanel = 5;
    public const byte TypeExpandSettingsPanel = 6;
    public const byte TypeSetDisplayPower = 10;
    public const byte TypeStartApp = 16;
    public const byte TypeResizeDisplay = 18; // packaging/AndroidLab/native/resize_packet.h
    public const byte TypeInjectGamepadState = 19;

    public const byte KeyActionDown = 0;
    public const byte KeyActionUp = 1;

    public const byte MotionActionDown = 0;
    public const byte MotionActionUp = 1;
    public const byte MotionActionMove = 2;
    public const byte MotionActionHoverMove = 7;

    public const int ButtonPrimary = 1;
    public const int ButtonSecondary = 2;
    public const int ButtonTertiary = 4;
    public const int ButtonBack = 8;
    public const int ButtonForward = 16;

    public const ulong PointerIdMouse = 0xFFFF_FFFF_FFFF_FFFF; // (uint64_t)-1
    public const ulong PointerIdGenericFinger = 0xFFFF_FFFF_FFFF_FFFE; // (uint64_t)-2

    public const int MaxInjectTextUtf8Bytes = 300;
    public const int MaxStartAppUtf8Bytes = 255;

    public static ushort FloatToUInt16FixedPoint(float value)
    {
        float clamped = Math.Clamp(value, 0.0f, 1.0f);
        uint scaled = (uint)(clamped * 65536.0f);
        return scaled >= 0xFFFFu ? (ushort)0xFFFF : (ushort)scaled;
    }

    public static short FloatToInt16FixedPoint(float value)
    {
        float clamped = Math.Clamp(value, -1.0f, 1.0f);
        int scaled = (int)(clamped * 32768.0f);
        if (scaled >= 0x7FFF) return 0x7FFF;
        if (scaled <= -0x8000) return -0x8000;
        return (short)scaled;
    }

    public static int WriteInjectKeycode(Span<byte> dst, byte action, int keycode, int repeat, int metaState)
    {
        dst[0] = TypeInjectKeycode;
        dst[1] = action;
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(2, 4), keycode);
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(6, 4), repeat);
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(10, 4), metaState);
        return 14;
    }

    public static int WriteInjectText(Span<byte> dst, ReadOnlySpan<char> text)
    {
        int maxBytes = Math.Min(MaxInjectTextUtf8Bytes, dst.Length - 5);
        if (maxBytes < 0) throw new ArgumentException("Destination span too small.", nameof(dst));
        System.Text.Unicode.Utf8.FromUtf16(text, dst.Slice(5, maxBytes), out _, out int written);
        dst[0] = TypeInjectText;
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(1, 4), (uint)written);
        return 5 + written;
    }

    public static byte[] BuildInjectTextPacket(string text)
    {
        Span<byte> scratch = stackalloc byte[5 + MaxInjectTextUtf8Bytes];
        int len = WriteInjectText(scratch, text.AsSpan());
        return scratch[..len].ToArray();
    }

    public static int WriteInjectTouch(
        Span<byte> dst,
        byte action,
        ulong pointerId,
        int x,
        int y,
        int screenWidth,
        int screenHeight,
        float pressure,
        int actionButton,
        int buttons)
    {
        dst[0] = TypeInjectTouchEvent;
        dst[1] = action;
        BinaryPrimitives.WriteUInt64BigEndian(dst.Slice(2, 8), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(10, 4), x);
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(14, 4), y);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(18, 2), (ushort)Math.Clamp(screenWidth, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(20, 2), (ushort)Math.Clamp(screenHeight, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(22, 2), FloatToUInt16FixedPoint(pressure));
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(24, 4), actionButton);
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(28, 4), buttons);
        return 32;
    }

    public static int WriteInjectScroll(
        Span<byte> dst,
        int x,
        int y,
        int screenWidth,
        int screenHeight,
        float hScroll,
        float vScroll,
        int buttons)
    {
        dst[0] = TypeInjectScrollEvent;
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(1, 4), x);
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(5, 4), y);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(9, 2), (ushort)Math.Clamp(screenWidth, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(11, 2), (ushort)Math.Clamp(screenHeight, 0, ushort.MaxValue));
        short hFp = FloatToInt16FixedPoint(Math.Clamp(hScroll / 16.0f, -1.0f, 1.0f));
        short vFp = FloatToInt16FixedPoint(Math.Clamp(vScroll / 16.0f, -1.0f, 1.0f));
        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(13, 2), hFp);
        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(15, 2), vFp);
        BinaryPrimitives.WriteInt32BigEndian(dst.Slice(17, 4), buttons);
        return 21;
    }

    public static int WriteBackOrScreenOn(Span<byte> dst, byte action)
    {
        dst[0] = TypeBackOrScreenOn;
        dst[1] = action;
        return 2;
    }

    public static int WriteSimpleCommand(Span<byte> dst, byte type)
    {
        dst[0] = type;
        return 1;
    }

    public static int WriteSetDisplayPower(Span<byte> dst, bool on)
    {
        dst[0] = TypeSetDisplayPower;
        dst[1] = on ? (byte)1 : (byte)0;
        return 2;
    }

    public static byte[] BuildStartAppPacket(string packageName)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(packageName);
        if (utf8.Length > MaxStartAppUtf8Bytes)
            throw new ArgumentException("Package name exceeds 255 UTF-8 bytes.", nameof(packageName));
        byte[] packet = new byte[2 + utf8.Length];
        packet[0] = TypeStartApp;
        packet[1] = (byte)utf8.Length;
        utf8.CopyTo(packet.AsSpan(2));
        return packet;
    }

    public static int WriteResizeDisplay(Span<byte> dst, int width, int height)
    {
        int w = FlexResizeMath.AlignAndClamp(width);
        int h = FlexResizeMath.AlignAndClamp(height);
        dst[0] = TypeResizeDisplay;
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(1, 2), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(3, 2), (ushort)h);
        return 5;
    }

    public static int WriteInjectGamepadState(Span<byte> dst, GamepadInputState state)
    {
        if (dst.Length < 14) throw new ArgumentException("Destination span too small.", nameof(dst));
        dst[0] = TypeInjectGamepadState;
        dst[1] = (byte)(state.Slot & 0x03);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2, 2), state.Buttons);
        dst[4] = state.LeftTrigger;
        dst[5] = state.RightTrigger;
        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(6, 2), state.ThumbLX);
        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(8, 2), state.ThumbLY);
        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(10, 2), state.ThumbRX);
        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(12, 2), state.ThumbRY);
        return 14;
    }

    /// <summary>Drains server->client device messages on the control socket so the server buffer never stalls.</summary>
    public static async Task DrainDeviceMessagesAsync(Stream controlStream, CancellationToken cancellationToken)
    {
        if (!controlStream.CanRead) return;
        byte[] header = new byte[8];
        byte[] discard = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await controlStream.ReadExactlyAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                byte msgType = header[0];
                int remaining;
                switch (msgType)
                {
                    case 0: // DEVICE_MSG_TYPE_CLIPBOARD: 4-byte length + utf8
                        await controlStream.ReadExactlyAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
                        remaining = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)), 4 * 1024 * 1024);
                        break;
                    case 1: // DEVICE_MSG_TYPE_ACK_CLIPBOARD: uint64 sequence
                        await controlStream.ReadExactlyAsync(header.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
                        remaining = 0;
                        break;
                    case 2: // DEVICE_MSG_TYPE_UHID_OUTPUT: uint16 id + uint16 size + bytes
                        await controlStream.ReadExactlyAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
                        remaining = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
                        break;
                    default:
                        return;
                }
                while (remaining > 0)
                {
                    int chunk = Math.Min(remaining, discard.Length);
                    await controlStream.ReadExactlyAsync(discard.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                    remaining -= chunk;
                }
            }
        }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }
}

public static class GamepadButtonMask
{
    public const ushort DpadUp = 0x0001;
    public const ushort DpadDown = 0x0002;
    public const ushort DpadLeft = 0x0004;
    public const ushort DpadRight = 0x0008;
    public const ushort Start = 0x0010;
    public const ushort Back = 0x0020;
    public const ushort LeftThumb = 0x0040;
    public const ushort RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightShoulder = 0x0200;
    public const ushort Guide = 0x0400;
    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;
    public const ushort ProtocolMask = 0xF7FF;
}

public readonly record struct GamepadInputState(
    byte Slot,
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short ThumbLX,
    short ThumbLY,
    short ThumbRX,
    short ThumbRY)
{
    public bool IsNeutral =>
        Buttons == 0 && LeftTrigger == 0 && RightTrigger == 0 &&
        ThumbLX == 0 && ThumbLY == 0 && ThumbRX == 0 && ThumbRY == 0;

    public static GamepadInputState Neutral(byte slot) => new((byte)(slot & 0x03), 0, 0, 0, 0, 0, 0, 0);
}

public enum HidGamepadLayoutKind
{
    GenericDirectInput = 0,
    GenericAnalogXbox = 1,
    Sony = 2,
    NintendoSwitch = 3
}

public enum HidAxisRole
{
    LeftX,
    LeftY,
    RightX,
    RightY,
    LeftTrigger,
    RightTrigger,
    HatSwitch
}

public static class GamepadMath
{
    public const short LeftThumbDeadzone = 7849;
    public const short RightThumbDeadzone = 8689;
    public const byte TriggerThreshold = 30;

    public static byte ApplyTriggerDeadzone(byte raw, byte threshold = TriggerThreshold)
    {
        if (raw <= threshold) return 0;
        int num = (raw - threshold) * 255;
        int den = 255 - threshold;
        return (byte)Math.Clamp((num + den / 2) / den, 0, 255);
    }

    public static (short X, short Y) ApplyRadialDeadzone(short rawX, short rawY, short deadzone)
    {
        double x = rawX;
        double y = rawY;
        double mag = Math.Sqrt(x * x + y * y);
        if (mag <= deadzone || mag <= 0.0)
            return (0, 0);

        double nx = x / mag;
        double ny = y / mag;
        double clampedMag = Math.Min(mag, 32767.0);
        double norm = (clampedMag - deadzone) / (32767.0 - deadzone);
        short outX = (short)Math.Clamp((int)Math.Round(nx * norm * 32767.0), short.MinValue, short.MaxValue);
        short outY = (short)Math.Clamp((int)Math.Round(ny * norm * 32767.0), short.MinValue, short.MaxValue);
        if (rawX == short.MinValue && outX == -32767) outX = short.MinValue;
        if (rawY == short.MinValue && outY == -32767) outY = short.MinValue;
        return (outX, outY);
    }

    public static GamepadInputState NormalizeState(
        byte slot,
        ushort rawButtons,
        byte rawLeftTrigger,
        byte rawRightTrigger,
        short rawThumbLX,
        short rawThumbLY,
        short rawThumbRX,
        short rawThumbRY)
    {
        ushort buttons = (ushort)(rawButtons & GamepadButtonMask.ProtocolMask);
        byte lt = ApplyTriggerDeadzone(rawLeftTrigger, TriggerThreshold);
        byte rt = ApplyTriggerDeadzone(rawRightTrigger, TriggerThreshold);
        var (lx, ly) = ApplyRadialDeadzone(rawThumbLX, rawThumbLY, LeftThumbDeadzone);
        var (rx, ry) = ApplyRadialDeadzone(rawThumbRX, rawThumbRY, RightThumbDeadzone);
        return new GamepadInputState((byte)(slot & 0x03), buttons, lt, rt, lx, ly, rx, ry);
    }

    public static HidGamepadLayoutKind ClassifyHidLayout(
        ushort vendorId, ushort productId, ushort maxButtonUsage, bool hasRz, bool hasAnalogTriggers)
    {
        _ = productId;
        if (vendorId == 0x054C) return HidGamepadLayoutKind.Sony;
        if (vendorId == 0x057E) return HidGamepadLayoutKind.NintendoSwitch;
        if ((vendorId == 0x045E || (hasAnalogTriggers && !hasRz)) && maxButtonUsage <= 11)
            return HidGamepadLayoutKind.GenericAnalogXbox;
        return HidGamepadLayoutKind.GenericDirectInput;
    }

    public static int SignExtendRawValue(uint rawUnsigned, int logicalMin, int bitSize)
    {
        if (logicalMin >= 0 || bitSize is <= 0 or >= 32)
            return unchecked((int)rawUnsigned);
        uint mask = 1u << (bitSize - 1);
        uint valueMask = (bitSize == 31) ? 0x7FFF_FFFFu : ((1u << bitSize) - 1u);
        uint masked = rawUnsigned & valueMask;
        if ((masked & mask) != 0)
            return unchecked((int)(masked | ~valueMask));
        return unchecked((int)masked);
    }

    public static long EffectiveLogicalMax(int logicalMin, int logicalMax, int bitSize)
    {
        if (logicalMin == 0 && logicalMax < 0 && bitSize is >= 1 and <= 32)
            return (1L << bitSize) - 1;
        return logicalMax;
    }

    public static short NormalizeHidSignedAxis(uint rawUnsigned, int logicalMin, int logicalMax, byte bitSize, bool invertY)
    {
        long effMax = EffectiveLogicalMax(logicalMin, logicalMax, bitSize);
        if (effMax <= logicalMin) return 0;

        double val = logicalMin < 0
            ? SignExtendRawValue(rawUnsigned, logicalMin, bitSize)
            : rawUnsigned;
        double t = Math.Clamp((val - logicalMin) / (effMax - logicalMin), 0.0, 1.0);
        double centered = t * 2.0 - 1.0;
        if (invertY) centered = -centered;
        int scaled = (int)Math.Round(centered * 32767.0);
        if (scaled <= -32767 && ((!invertY && t <= 0.0001) || (invertY && t >= 0.9999)))
            return short.MinValue;
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    public static byte NormalizeHidTrigger(uint rawUnsigned, int logicalMin, int logicalMax, byte bitSize)
    {
        long effMax = EffectiveLogicalMax(logicalMin, logicalMax, bitSize);
        if (effMax <= logicalMin) return 0;

        double val = logicalMin < 0
            ? SignExtendRawValue(rawUnsigned, logicalMin, bitSize)
            : rawUnsigned;
        double t = Math.Clamp((val - logicalMin) / (effMax - logicalMin), 0.0, 1.0);
        return (byte)Math.Clamp((int)Math.Round(t * 255.0), 0, 255);
    }

    public static ushort DecodeHidHatSwitch(int rawValue, int logicalMin, int logicalMax)
    {
        int span = logicalMax - logicalMin;
        int dir = rawValue - logicalMin;
        if (dir < 0 || dir > span) return 0;

        if (span == 7)
        {
            return dir switch
            {
                0 => GamepadButtonMask.DpadUp,
                1 => (ushort)(GamepadButtonMask.DpadUp | GamepadButtonMask.DpadRight),
                2 => GamepadButtonMask.DpadRight,
                3 => (ushort)(GamepadButtonMask.DpadDown | GamepadButtonMask.DpadRight),
                4 => GamepadButtonMask.DpadDown,
                5 => (ushort)(GamepadButtonMask.DpadDown | GamepadButtonMask.DpadLeft),
                6 => GamepadButtonMask.DpadLeft,
                7 => (ushort)(GamepadButtonMask.DpadUp | GamepadButtonMask.DpadLeft),
                _ => 0
            };
        }

        if (span == 3)
        {
            return dir switch
            {
                0 => GamepadButtonMask.DpadUp,
                1 => GamepadButtonMask.DpadRight,
                2 => GamepadButtonMask.DpadDown,
                3 => GamepadButtonMask.DpadLeft,
                _ => 0
            };
        }

        return 0;
    }

    public static void MapHidButtonUsage(
        ushort buttonUsage,
        HidGamepadLayoutKind layout,
        out ushort buttonMask,
        out bool isLeftTriggerFallback,
        out bool isRightTriggerFallback)
    {
        buttonMask = 0;
        isLeftTriggerFallback = false;
        isRightTriggerFallback = false;

        switch (layout)
        {
            case HidGamepadLayoutKind.Sony:
                switch (buttonUsage)
                {
                    case 1: buttonMask = GamepadButtonMask.X; break;
                    case 2: buttonMask = GamepadButtonMask.A; break;
                    case 3: buttonMask = GamepadButtonMask.B; break;
                    case 4: buttonMask = GamepadButtonMask.Y; break;
                    case 5: buttonMask = GamepadButtonMask.LeftShoulder; break;
                    case 6: buttonMask = GamepadButtonMask.RightShoulder; break;
                    case 7: isLeftTriggerFallback = true; break;
                    case 8: isRightTriggerFallback = true; break;
                    case 9: buttonMask = GamepadButtonMask.Back; break;
                    case 10: buttonMask = GamepadButtonMask.Start; break;
                    case 11: buttonMask = GamepadButtonMask.LeftThumb; break;
                    case 12: buttonMask = GamepadButtonMask.RightThumb; break;
                    case 13: buttonMask = GamepadButtonMask.Guide; break;
                }
                break;

            case HidGamepadLayoutKind.GenericAnalogXbox:
                switch (buttonUsage)
                {
                    case 1: buttonMask = GamepadButtonMask.A; break;
                    case 2: buttonMask = GamepadButtonMask.B; break;
                    case 3: buttonMask = GamepadButtonMask.X; break;
                    case 4: buttonMask = GamepadButtonMask.Y; break;
                    case 5: buttonMask = GamepadButtonMask.LeftShoulder; break;
                    case 6: buttonMask = GamepadButtonMask.RightShoulder; break;
                    case 7: buttonMask = GamepadButtonMask.Back; break;
                    case 8: buttonMask = GamepadButtonMask.Start; break;
                    case 9: buttonMask = GamepadButtonMask.LeftThumb; break;
                    case 10: buttonMask = GamepadButtonMask.RightThumb; break;
                    case 11: buttonMask = GamepadButtonMask.Guide; break;
                }
                break;

            case HidGamepadLayoutKind.NintendoSwitch:
            case HidGamepadLayoutKind.GenericDirectInput:
            default:
                switch (buttonUsage)
                {
                    case 1: buttonMask = GamepadButtonMask.A; break;
                    case 2: buttonMask = GamepadButtonMask.B; break;
                    case 3: buttonMask = GamepadButtonMask.X; break;
                    case 4: buttonMask = GamepadButtonMask.Y; break;
                    case 5: buttonMask = GamepadButtonMask.LeftShoulder; break;
                    case 6: buttonMask = GamepadButtonMask.RightShoulder; break;
                    case 7: isLeftTriggerFallback = true; break;
                    case 8: isRightTriggerFallback = true; break;
                    case 9: buttonMask = GamepadButtonMask.Back; break;
                    case 10: buttonMask = GamepadButtonMask.Start; break;
                    case 11: buttonMask = GamepadButtonMask.LeftThumb; break;
                    case 12: buttonMask = GamepadButtonMask.RightThumb; break;
                    case 13: buttonMask = GamepadButtonMask.Guide; break;
                }
                break;
        }
    }

    public static GamepadInputState NormalizeHidInput(
        byte slot,
        HidGamepadLayoutKind layout,
        ReadOnlySpan<ushort> pressedButtonUsages,
        ushort hatDpadMask,
        byte analogLeftTrigger,
        byte analogRightTrigger,
        short rawThumbLX,
        short rawThumbLY,
        short rawThumbRX,
        short rawThumbRY)
    {
        ushort buttons = (ushort)(hatDpadMask & (GamepadButtonMask.DpadUp | GamepadButtonMask.DpadDown | GamepadButtonMask.DpadLeft | GamepadButtonMask.DpadRight));
        byte lt = analogLeftTrigger;
        byte rt = analogRightTrigger;

        foreach (ushort usage in pressedButtonUsages)
        {
            MapHidButtonUsage(usage, layout, out ushort mask, out bool l2Fallback, out bool r2Fallback);
            buttons |= mask;
            if (l2Fallback && lt == 0) lt = 255;
            if (r2Fallback && rt == 0) rt = 255;
        }

        return NormalizeState(slot, buttons, lt, rt, rawThumbLX, rawThumbLY, rawThumbRX, rawThumbRY);
    }
}

public readonly record struct VideoRect(int X, int Y, int Width, int Height);

public static class VideoCoordinateMapper
{
    public static VideoRect ComputeContentRect(int clientWidth, int clientHeight, int frameWidth, int frameHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
            return new VideoRect(0, 0, Math.Max(0, clientWidth), Math.Max(0, clientHeight));

        long left = (long)clientWidth * frameHeight;
        long right = (long)frameWidth * clientHeight;
        if (left == right) return new VideoRect(0, 0, clientWidth, clientHeight);
        if (left > right)
        {
            int w = (int)Math.Max(1, right / frameHeight);
            return new VideoRect((clientWidth - w) / 2, 0, w, clientHeight);
        }
        else
        {
            int h = (int)Math.Max(1, left / frameWidth);
            return new VideoRect(0, (clientHeight - h) / 2, clientWidth, h);
        }
    }

    public static bool IsOutsideContentRect(int px, int py, VideoRect rect)
        => rect.Width > 0 && rect.Height > 0
        && (px < rect.X || px >= rect.X + rect.Width || py < rect.Y || py >= rect.Y + rect.Height);

    public static (int X, int Y) ClientToFrame(int px, int py, VideoRect rect, int frameWidth, int frameHeight)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || frameWidth <= 0 || frameHeight <= 0)
            return (0, 0);
        int relX = Math.Clamp(px - rect.X, 0, rect.Width - 1);
        int relY = Math.Clamp(py - rect.Y, 0, rect.Height - 1);
        int fx = (int)((long)relX * frameWidth / rect.Width);
        int fy = (int)((long)relY * frameHeight / rect.Height);
        return (Math.Clamp(fx, 0, frameWidth - 1), Math.Clamp(fy, 0, frameHeight - 1));
    }
}

/// <summary>Matches <c>packaging/AndroidLab/native/flex_resize.h</c> geometry &amp; 100ms debounce logic.</summary>
public static class FlexResizeMath
{
    public const int MinSide = 240;
    public const int MaxSide = 8192;
    public const int CoalesceIntervalMs = 100;

    public static int AlignAndClamp(int value) => Math.Clamp(value, MinSide, MaxSide) & ~7;

    public static (int Width, int Height) AlignAndClamp(int width, int height)
        => (AlignAndClamp(width), AlignAndClamp(height));

    /// <summary>
    /// Matches <c>PhoneDisplaySizing.fit</c> / <c>Geometry.fitVirtualDisplaySize</c>:
    /// the desktop window supplies the aspect ratio and the phone screen supplies the pixel budget,
    /// scaled to touch the phone's native edge and aligned down to multiples of 8 within [240, 8192].
    /// </summary>
    public static (int Width, int Height) FitPhoneBudget(int phoneWidth, int phoneHeight, int requestedWidth, int requestedHeight)
    {
        if (requestedWidth <= 0 || requestedHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedWidth), "Invalid window shape.");
        int shortEdge = Math.Clamp(Math.Min(phoneWidth, phoneHeight), MinSide, MaxSide);
        int longEdge = Math.Max(shortEdge, Math.Clamp(Math.Max(phoneWidth, phoneHeight), MinSide, MaxSide));
        int maxWidth = requestedWidth >= requestedHeight ? longEdge : shortEdge;
        int maxHeight = requestedWidth >= requestedHeight ? shortEdge : longEdge;
        double scale = Math.Min((double)maxWidth / requestedWidth, (double)maxHeight / requestedHeight);
        int fittedWidth = Math.Max(MinSide, (int)(requestedWidth * scale) & ~7);
        int fittedHeight = Math.Max(MinSide, (int)(requestedHeight * scale) & ~7);
        return (fittedWidth, fittedHeight);
    }

    public static void ApplySizingEdge(
        ref int left, ref int top, ref int right, ref int bottom,
        int edge, int nonClientWidth, int nonClientHeight,
        int workLeft, int workTop, int workRight, int workBottom)
    {
        int rawW = (right - left) - nonClientWidth;
        int rawH = (bottom - top) - nonClientHeight;
        int w = AlignAndClamp(rawW);
        int h = AlignAndClamp(rawH);

        bool anchorRight = edge is 1 or 4 or 7; // WMSZ_LEFT, TOPLEFT, BOTTOMLEFT
        bool anchorBottom = edge is 3 or 4 or 5; // WMSZ_TOP, TOPLEFT, TOPRIGHT

        if (anchorRight) left = right - (w + nonClientWidth);
        else right = left + (w + nonClientWidth);

        if (anchorBottom) top = bottom - (h + nonClientHeight);
        else bottom = top + (h + nonClientHeight);

        if (workRight > workLeft && workBottom > workTop)
        {
            if (left < workLeft) { left = workLeft; if (!anchorRight) right = Math.Max(left + MinSide + nonClientWidth, right); }
            if (right > workRight) { right = workRight; if (anchorRight) left = Math.Min(right - MinSide - nonClientWidth, left); }
            if (top < workTop) { top = workTop; if (!anchorBottom) bottom = Math.Max(top + MinSide + nonClientHeight, bottom); }
            if (bottom > workBottom) { bottom = workBottom; if (anchorBottom) top = Math.Min(bottom - MinSide - nonClientHeight, top); }
        }
    }
}

/// <summary>Matches <c>packaging/AndroidLab/native/mirror_resize.h</c> aspect-locked mirror geometry.</summary>
public static class MirrorAspectMath
{
    public const int MinClientWidth = 96;
    public const int MinClientHeight = 64;

    public static (int ClientWidth, int ClientHeight) FitAspect(int contentW, int contentH, int maxClientW, int maxClientH)
    {
        if (contentW <= 0 || contentH <= 0)
            return (Math.Max(MinClientWidth, maxClientW), Math.Max(MinClientHeight, maxClientH));

        maxClientW = Math.Max(1, maxClientW);
        maxClientH = Math.Max(1, maxClientH);
        if (maxClientW == (long)maxClientH * contentW / contentH
            || maxClientH == (long)maxClientW * contentH / contentW)
        {
            return (maxClientW, maxClientH);
        }

        int w, h;
        if ((long)maxClientW * contentH > (long)maxClientH * contentW)
        {
            w = Math.Max(1, (int)((long)maxClientH * contentW / contentH));
            h = maxClientH;
        }
        else
        {
            w = maxClientW;
            h = Math.Max(1, (int)((long)maxClientW * contentH / contentW));
        }
        return (Math.Min(maxClientW, w), Math.Min(maxClientH, h));
    }

    public static (int MinTrackW, int MinTrackH) ComputeMinTrackSize(int contentW, int contentH, int nonClientW, int nonClientH)
    {
        if (contentW <= 0 || contentH <= 0)
            return (MinClientWidth + nonClientW, MinClientHeight + nonClientH);
        int cw = MinClientWidth;
        int ch = Math.Max(1, (int)Math.Round((double)cw * contentH / contentW));
        if (ch < MinClientHeight)
        {
            ch = MinClientHeight;
            cw = Math.Max(1, (int)Math.Round((double)ch * contentW / contentH));
        }
        return (cw + nonClientW, ch + nonClientH);
    }

    public static void ApplySizingEdge(
        ref int left, ref int top, ref int right, ref int bottom,
        int edge, int contentW, int contentH,
        int nonClientW, int nonClientH, int maxTrackW, int maxTrackH)
    {
        if (contentW <= 0 || contentH <= 0) return;

        int clientW = Math.Max(1, (right - left) - nonClientW);
        int clientH = Math.Max(1, (bottom - top) - nonClientH);
        int maxClientW = Math.Max(1, maxTrackW - nonClientW);
        int maxClientH = Math.Max(1, maxTrackH - nonClientH);
        var (minTrackW, minTrackH) = ComputeMinTrackSize(contentW, contentH, nonClientW, nonClientH);
        int minClientW = Math.Max(1, minTrackW - nonClientW);
        int minClientH = Math.Max(1, minTrackH - nonClientH);

        bool horizontalOnly = edge is 1 or 2; // LEFT or RIGHT
        bool verticalOnly = edge is 3 or 6;   // TOP or BOTTOM

        int newW, newH;
        if (verticalOnly)
        {
            newH = Math.Clamp(clientH, minClientH, maxClientH);
            newW = Math.Max(1, (int)Math.Round((double)newH * contentW / contentH));
            if (newW > maxClientW) { newW = maxClientW; newH = Math.Max(1, (int)Math.Round((double)newW * contentH / contentW)); }
        }
        else if (horizontalOnly)
        {
            newW = Math.Clamp(clientW, minClientW, maxClientW);
            newH = Math.Max(1, (int)Math.Round((double)newW * contentH / contentW));
            if (newH > maxClientH) { newH = maxClientH; newW = Math.Max(1, (int)Math.Round((double)newH * contentW / contentH)); }
        }
        else
        {
            (newW, newH) = FitAspect(contentW, contentH, Math.Clamp(clientW, minClientW, maxClientW), Math.Clamp(clientH, minClientH, maxClientH));
        }

        int targetW = newW + nonClientW;
        int targetH = newH + nonClientH;
        bool anchorRight = edge is 1 or 4 or 7;
        bool anchorBottom = edge is 3 or 4 or 5;

        if (anchorRight) left = right - targetW; else right = left + targetW;
        if (anchorBottom) top = bottom - targetH; else bottom = top + targetH;
    }
}

/// <summary>Matches <c>packaging/AndroidLab/native/ime_routing.h</c> Win32 IME gating rules.</summary>
public sealed class ImeKeyRouter
{
    private readonly HashSet<uint> _blockedScancodes = [];
    public bool IsComposing { get; private set; }

    public void OnStartComposition() => IsComposing = true;
    public void OnEndComposition() => IsComposing = false;

    public void OnFocusLost()
    {
        IsComposing = false;
        _blockedScancodes.Clear();
    }

    public static bool IsImeToggleCombo(uint virtualKey, bool ctrl, bool win, bool alt)
    {
        if (virtualKey == 0x20 /* VK_SPACE */ && (ctrl || win) && !alt) return true;
        return virtualKey is 0x15 /* VK_KANA/HANGUL */ or 0x16 /* VK_IME_ON */
            or 0x19 /* VK_HANJA/KANJI */ or 0x1A /* VK_IME_OFF */
            or 0xE5 /* VK_PROCESSKEY */;
    }

    public bool ShouldBlockKeyMessage(bool isKeyDown, uint virtualKey, uint scanCode, bool ctrl, bool win, bool alt)
    {
        bool toggle = IsImeToggleCombo(virtualKey, ctrl, win, alt);
        if (isKeyDown)
        {
            if (IsComposing || toggle)
            {
                if (scanCode != 0) _blockedScancodes.Add(scanCode);
                return true;
            }
            return false;
        }
        if (scanCode != 0 && _blockedScancodes.Remove(scanCode))
            return true;
        return toggle || IsComposing;
    }
}

/// <summary>Maps Win32 VK_* + modifier bits to Android KeyEvent AKEYCODE_* and AMETA_*.</summary>
public static class AndroidKeycodeMapper
{
    // Subset + full standard Windows keys matching Android KeyEvent constants.
    public const int AKeycodeHome = 3;
    public const int AKeycodeBack = 4;
    public const int AKeycode0 = 7;
    public const int AKeycodeA = 29;
    public const int AKeycodeComma = 55;
    public const int AKeycodePeriod = 56;
    public const int AKeycodeAltLeft = 57;
    public const int AKeycodeAltRight = 58;
    public const int AKeycodeShiftLeft = 59;
    public const int AKeycodeShiftRight = 60;
    public const int AKeycodeTab = 61;
    public const int AKeycodeSpace = 62;
    public const int AKeycodeEnter = 66;
    public const int AKeycodeDel = 67;
    public const int AKeycodeGrave = 68;
    public const int AKeycodeMinus = 69;
    public const int AKeycodeEquals = 70;
    public const int AKeycodeLeftBracket = 71;
    public const int AKeycodeRightBracket = 72;
    public const int AKeycodeBackslash = 73;
    public const int AKeycodeSemicolon = 74;
    public const int AKeycodeApostrophe = 75;
    public const int AKeycodeSlash = 76;
    public const int AKeycodePageUp = 92;
    public const int AKeycodePageDown = 93;
    public const int AKeycodeEscape = 111;
    public const int AKeycodeForwardDel = 112;
    public const int AKeycodeCtrlLeft = 113;
    public const int AKeycodeCtrlRight = 114;
    public const int AKeycodeCapsLock = 115;
    public const int AKeycodeScrollLock = 116;
    public const int AKeycodeMetaLeft = 117;
    public const int AKeycodeMetaRight = 118;
    public const int AKeycodeSysRq = 120;
    public const int AKeycodeBreak = 121;
    public const int AKeycodeMoveHome = 122;
    public const int AKeycodeMoveEnd = 123;
    public const int AKeycodeInsert = 124;
    public const int AKeycodeF1 = 131;
    public const int AKeycodeNumLock = 143;
    public const int AKeycodeNumpad0 = 144;
    public const int AKeycodeNumpadDivide = 154;
    public const int AKeycodeNumpadMultiply = 155;
    public const int AKeycodeNumpadSubtract = 156;
    public const int AKeycodeNumpadAdd = 157;
    public const int AKeycodeNumpadDot = 158;
    public const int AKeycodeNumpadEnter = 160;
    public const int AKeycodeVolumeUp = 24;
    public const int AKeycodeVolumeDown = 25;
    public const int AKeycodeVolumeMute = 164;
    public const int AKeycodeAppSwitch = 187;
    public const int AKeycodeDpadUp = 19;
    public const int AKeycodeDpadDown = 20;
    public const int AKeycodeDpadLeft = 21;
    public const int AKeycodeDpadRight = 22;
    public const int AKeycodeButtonA = 96;
    public const int AKeycodeButtonB = 97;
    public const int AKeycodeButtonC = 98;
    public const int AKeycodeButtonX = 99;
    public const int AKeycodeButtonY = 100;
    public const int AKeycodeButtonZ = 101;
    public const int AKeycodeButtonL1 = 102;
    public const int AKeycodeButtonR1 = 103;
    public const int AKeycodeButtonL2 = 104;
    public const int AKeycodeButtonR2 = 105;
    public const int AKeycodeButtonThumbl = 106;
    public const int AKeycodeButtonThumbr = 107;
    public const int AKeycodeButtonStart = 108;
    public const int AKeycodeButtonSelect = 109;
    public const int AKeycodeButtonMode = 110;

    public const int AMetaAltOn = 0x02;
    public const int AMetaAltLeftOn = 0x10;
    public const int AMetaAltRightOn = 0x20;
    public const int AMetaShiftOn = 0x01;
    public const int AMetaShiftLeftOn = 0x40;
    public const int AMetaShiftRightOn = 0x80;
    public const int AMetaCtrlOn = 0x1000;
    public const int AMetaCtrlLeftOn = 0x2000;
    public const int AMetaCtrlRightOn = 0x4000;
    public const int AMetaMetaOn = 0x10000;
    public const int AMetaMetaLeftOn = 0x20000;
    public const int AMetaMetaRightOn = 0x40000;
    public const int AMetaCapsLockOn = 0x100000;
    public const int AMetaNumLockOn = 0x200000;
    public const int AMetaScrollLockOn = 0x400000;

    public static bool TryMapVirtualKey(uint vk, bool extended, out int androidKeycode)
    {
        if (vk is >= 0x41 and <= 0x5A) { androidKeycode = AKeycodeA + (int)(vk - 0x41); return true; }
        if (vk is >= 0x30 and <= 0x39) { androidKeycode = AKeycode0 + (int)(vk - 0x30); return true; }
        if (vk is >= 0x60 and <= 0x69) { androidKeycode = AKeycodeNumpad0 + (int)(vk - 0x60); return true; }
        if (vk is >= 0x70 and <= 0x7B) { androidKeycode = AKeycodeF1 + (int)(vk - 0x70); return true; }

        androidKeycode = vk switch
        {
            0x08 => AKeycodeDel,
            0x09 => AKeycodeTab,
            0x0D => extended ? AKeycodeNumpadEnter : AKeycodeEnter,
            0x10 => AKeycodeShiftLeft,
            0xA0 => AKeycodeShiftLeft,
            0xA1 => AKeycodeShiftRight,
            0x11 => extended ? AKeycodeCtrlRight : AKeycodeCtrlLeft,
            0xA2 => AKeycodeCtrlLeft,
            0xA3 => AKeycodeCtrlRight,
            0x12 => extended ? AKeycodeAltRight : AKeycodeAltLeft,
            0xA4 => AKeycodeAltLeft,
            0xA5 => AKeycodeAltRight,
            0x13 => AKeycodeBreak,
            0x14 => AKeycodeCapsLock,
            0x1B => AKeycodeEscape,
            0x20 => AKeycodeSpace,
            0x21 => AKeycodePageUp,
            0x22 => AKeycodePageDown,
            0x23 => AKeycodeMoveEnd,
            0x24 => AKeycodeMoveHome,
            0x25 => AKeycodeDpadLeft,
            0x26 => AKeycodeDpadUp,
            0x27 => AKeycodeDpadRight,
            0x28 => AKeycodeDpadDown,
            0x2C => AKeycodeSysRq,
            0x2D => AKeycodeInsert,
            0x2E => AKeycodeForwardDel,
            0x5B => AKeycodeMetaLeft,
            0x5C => AKeycodeMetaRight,
            0x6A => AKeycodeNumpadMultiply,
            0x6B => AKeycodeNumpadAdd,
            0x6D => AKeycodeNumpadSubtract,
            0x6E => AKeycodeNumpadDot,
            0x6F => AKeycodeNumpadDivide,
            0x90 => AKeycodeNumLock,
            0x91 => AKeycodeScrollLock,
            0xAD => AKeycodeVolumeMute,
            0xAE => AKeycodeVolumeDown,
            0xAF => AKeycodeVolumeUp,
            0xBA => AKeycodeSemicolon,
            0xBB => AKeycodeEquals,
            0xBC => AKeycodeComma,
            0xBD => AKeycodeMinus,
            0xBE => AKeycodePeriod,
            0xBF => AKeycodeSlash,
            0xC0 => AKeycodeGrave,
            0xC3 => AKeycodeButtonA,       // VK_GAMEPAD_A
            0xC4 => AKeycodeButtonB,       // VK_GAMEPAD_B
            0xC5 => AKeycodeButtonX,       // VK_GAMEPAD_X
            0xC6 => AKeycodeButtonY,       // VK_GAMEPAD_Y
            0xC7 => AKeycodeButtonR1,      // VK_GAMEPAD_RIGHT_SHOULDER
            0xC8 => AKeycodeButtonL1,      // VK_GAMEPAD_LEFT_SHOULDER
            0xC9 => AKeycodeButtonL2,      // VK_GAMEPAD_LEFT_TRIGGER
            0xCA => AKeycodeButtonR2,      // VK_GAMEPAD_RIGHT_TRIGGER
            0xCB => AKeycodeDpadUp,        // VK_GAMEPAD_DPAD_UP
            0xCC => AKeycodeDpadDown,      // VK_GAMEPAD_DPAD_DOWN
            0xCD => AKeycodeDpadLeft,      // VK_GAMEPAD_DPAD_LEFT
            0xCE => AKeycodeDpadRight,     // VK_GAMEPAD_DPAD_RIGHT
            0xCF => AKeycodeButtonStart,   // VK_GAMEPAD_MENU
            0xD0 => AKeycodeButtonSelect,  // VK_GAMEPAD_VIEW
            0xD1 => AKeycodeButtonThumbl,  // VK_GAMEPAD_LEFT_THUMBSTICK_BUTTON
            0xD2 => AKeycodeButtonThumbr,  // VK_GAMEPAD_RIGHT_THUMBSTICK_BUTTON
            0xD3 => AKeycodeDpadUp,        // VK_GAMEPAD_LEFT_THUMBSTICK_UP
            0xD4 => AKeycodeDpadDown,      // VK_GAMEPAD_LEFT_THUMBSTICK_DOWN
            0xD5 => AKeycodeDpadRight,     // VK_GAMEPAD_LEFT_THUMBSTICK_RIGHT
            0xD6 => AKeycodeDpadLeft,      // VK_GAMEPAD_LEFT_THUMBSTICK_LEFT
            0xD7 => AKeycodeDpadUp,        // VK_GAMEPAD_RIGHT_THUMBSTICK_UP
            0xD8 => AKeycodeDpadDown,      // VK_GAMEPAD_RIGHT_THUMBSTICK_DOWN
            0xD9 => AKeycodeDpadRight,     // VK_GAMEPAD_RIGHT_THUMBSTICK_RIGHT
            0xDA => AKeycodeDpadLeft,      // VK_GAMEPAD_RIGHT_THUMBSTICK_LEFT
            0xDB => AKeycodeLeftBracket,
            0xDC => AKeycodeBackslash,
            0xDD => AKeycodeRightBracket,
            0xDE => AKeycodeApostrophe,
            _ => 0
        };
        return androidKeycode != 0;
    }
}
