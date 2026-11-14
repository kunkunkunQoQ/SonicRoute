using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckVideoStage2Async()
    {
        CheckVideoProtocolAndControl();
        await CheckGamepadProtocolAndZeroIdleWindowAsync();
        await CheckPhoneDisplayBudgetAndWindowSizingAsync();
        CheckWicPhoneIcon();
        CheckMediaFoundationH264Decode();
        await CheckHwndForegroundDisambiguationAsync();
        await CheckStage3VideoAndAudioTransportsAsync();
    }

    private static async Task CheckPhoneDisplayBudgetAndWindowSizingAsync()
    {
        Check(FlexResizeMath.FitPhoneBudget(1080, 2400, 820, 560) == (1576, 1080)
            && FlexResizeMath.FitPhoneBudget(1080, 2400, 1347, 822) == (1768, 1080)
            && FlexResizeMath.FitPhoneBudget(1080, 2400, 1344, 816) == (1776, 1080)
            && FlexResizeMath.FitPhoneBudget(1080, 2400, 604, 1344) == (1072, 2400)
            && FlexResizeMath.FitPhoneBudget(1080, 2400, 1080, 2400) == (1080, 2400),
            "FlexResizeMath.FitPhoneBudget scales desktop window aspect ratio up to phone physical short/long edges");

        var contentRect = VideoCoordinateMapper.ComputeContentRect(1347, 822, 1768, 1080);
        var mappedCorner = VideoCoordinateMapper.ClientToFrame(1346, 821, contentRect, 1768, 1080);
        Check(mappedCorner.X >= 1760 && mappedCorner.Y >= 1075,
            "smaller PC window client coordinates map across full phone native budget frame dimensions");

        await using var winA = new NativeVideoWindow("Alpha", isMirrorMode: false, preferText: true, iconPng: null,
            x: 100, y: 100, initialClientWidth: 613, initialClientHeight: 419,
            appUserModelId: "SonicRoute.Display.dev1.cn.test.alpha");
        await using var winB = new NativeVideoWindow("Beta", isMirrorMode: false, preferText: true, iconPng: null,
            x: 120, y: 120, initialClientWidth: 520, initialClientHeight: 360,
            appUserModelId: "SonicRoute.Display.dev1.cn.test.beta");
        await using var winMirror = new NativeVideoWindow("Mirror", isMirrorMode: true, preferText: false, iconPng: null,
            x: 140, y: 140, initialClientWidth: 400, initialClientHeight: 800,
            appUserModelId: "SonicRoute.Mirror.dev1");
        nint hwndA = await winA.CreateAsync(default);
        nint hwndB = await winB.CreateAsync(default);
        nint hwndMirror = await winMirror.CreateAsync(default);

        Check(GetTestClientRect(hwndA, out var crA) && (crA.Right - crA.Left) == 613 && (crA.Bottom - crA.Top) == 419,
            "non-mirror NativeVideoWindow preserves exact non-8-aligned initial client dimensions");

        winA.UpdateContentSize(1776, 1080);
        winMirror.UpdateContentSize(1080, 2400);
        var mirrorInputRect = winMirror.GetInputContentRect();
        Check(winA.GetInputContentRect() == new VideoRect(0, 0, 613, 419)
            && mirrorInputRect.Width == 360 && mirrorInputRect.Height == 800 && mirrorInputRect.X == 20,
            "non-mirror input rect fills full client area while mirror mode preserves aspect-locked letterboxing");

        (int syncW, int syncH) = (0, 0);
        void CaptureSync(int w, int h) { syncW = w; syncH = h; }
        winA.FlexResizeRequested += CaptureSync;
        bool skippedMatched = !winA.SyncInitialFlexSize(912, 624);
        bool triggeredMismatch = winA.SyncInitialFlexSize(1080, 2400);
        winA.FlexResizeRequested -= CaptureSync;
        winA.ResetFlexThrottle();
        Check(skippedMatched && triggeredMismatch && syncW == 608 && syncH == 416,
            "NativeVideoWindow.SyncInitialFlexSize skips matching aspect and emits aligned size when initial frame aspect diverges");

        var flexTcs = new TaskCompletionSource<(int W, int H)>(TaskCreationOptions.RunContinuationsAsynchronously);
        winA.FlexResizeRequested += (w, h) => flexTcs.TrySetResult((w, h));
        var before = new TestWin32Rect { Left = 120, Top = 140, Right = 120 + 733, Bottom = 140 + 511 };
        nint rectPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TestWin32Rect>());
        try
        {
            Marshal.StructureToPtr(before, rectPtr, false);
            SendTestMessageW(hwndA, 0x0214 /* WM_SIZING */, 2 /* WMSZ_RIGHT */, rectPtr);
            var after = Marshal.PtrToStructure<TestWin32Rect>(rectPtr);
            var (flexW, flexH) = await flexTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            Check(after.Left == before.Left && after.Top == before.Top && after.Right == before.Right && after.Bottom == before.Bottom
                && flexW > 0 && (flexW & 7) == 0 && (flexH & 7) == 0,
                "non-mirror WM_SIZING does not alter proposed drag rectangle and emits 8px-aligned FlexResizeRequested");
        }
        finally
        {
            Marshal.FreeHGlobal(rectPtr);
        }

        string? aumidA = ReadWindowAppUserModelId(hwndA);
        string? aumidB = ReadWindowAppUserModelId(hwndB);
        string? aumidMirror = ReadWindowAppUserModelId(hwndMirror);
        Check(string.Equals(aumidA, "SonicRoute.Display.dev1.cn.test.alpha", StringComparison.Ordinal)
            && string.Equals(aumidB, "SonicRoute.Display.dev1.cn.test.beta", StringComparison.Ordinal)
            && string.Equals(aumidMirror, "SonicRoute.Mirror.dev1", StringComparison.Ordinal)
            && !string.Equals(aumidA, aumidB, StringComparison.Ordinal)
            && !string.Equals(aumidA, aumidMirror, StringComparison.Ordinal),
            "each NativeVideoWindow sets a distinct per-window System.AppUserModel.ID for independent taskbar grouping");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreGetValueDelegate(nint store, ref TestPropertyKey key, out TestPropVariant pv);

    private static string? ReadWindowAppUserModelId(nint hwnd)
    {
        Guid iid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"); // IID_IPropertyStore
        int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out nint store);
        if (hr < 0 || store == 0) return null;
        try
        {
            var pkey = new TestPropertyKey
            {
                fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                pid = 5
            };
            nint vtbl = Marshal.ReadIntPtr(store);
            nint fnPtr = Marshal.ReadIntPtr(vtbl, 5 * IntPtr.Size); // IPropertyStore::GetValue
            var getValue = Marshal.GetDelegateForFunctionPointer<PropertyStoreGetValueDelegate>(fnPtr);
            int hrGet = getValue(store, ref pkey, out TestPropVariant pv);
            if (hrGet < 0) return null;
            try
            {
                return pv.vt == 31 && pv.pszVal != 0 ? Marshal.PtrToStringUni(pv.pszVal) : null;
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Marshal.Release(store);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestWin32Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestPropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestPropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint pszVal;
        public nint padding;
    }

    [DllImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    private static extern bool GetTestClientRect(nint hWnd, out TestWin32Rect lpRect);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendTestMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, out nint ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref TestPropVariant pvar);

    private static void CheckVideoProtocolAndControl()
    {
        // Header: 64B UTF-8 padded device name + codec "h264" + 1080x2400
        byte[] headerBytes = new byte[76];
        Encoding.UTF8.GetBytes("Pixel_Test").CopyTo(headerBytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(64, 4), ScrcpyVideoStreamHeader.CodecH264);
        BinaryPrimitives.WriteInt32BigEndian(headerBytes.AsSpan(68, 4), 1080);
        BinaryPrimitives.WriteInt32BigEndian(headerBytes.AsSpan(72, 4), 2400);
        var streamHeader = ScrcpyVideoStreamHeader.ReadAsync(new MemoryStream(headerBytes), expectDeviceMeta: true, default)
            .GetAwaiter().GetResult();
        Check(streamHeader.CodecId == ScrcpyVideoStreamHeader.CodecH264
            && streamHeader.Width == 1080 && streamHeader.Height == 2400
            && streamHeader.DeviceName == "Pixel_Test",
            "ScrcpyVideoStreamHeader parses device metadata, codec id and initial dimensions");

        // Packet header flags & merger
        byte[] pktHdr = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(pktHdr.AsSpan(0, 8), ScrcpyVideoPacketHeader.ConfigFlag | 12345UL);
        BinaryPrimitives.WriteUInt32BigEndian(pktHdr.AsSpan(8, 4), 4);
        var cfgHdr = ScrcpyVideoPacketHeader.Parse(pktHdr);
        Check(cfgHdr.IsConfig && !cfgHdr.IsKeyFrame && cfgHdr.PayloadLength == 4 && cfgHdr.PtsUs == 12345,
            "ScrcpyVideoPacketHeader decodes config flag and 62-bit PTS");

        BinaryPrimitives.WriteUInt32BigEndian(pktHdr.AsSpan(8, 4), ScrcpyVideoPacketHeader.MaxPacketBytes + 1);
        bool badSize = false;
        try { ScrcpyVideoPacketHeader.Parse(pktHdr); } catch (InvalidDataException) { badSize = true; }
        Check(badSize, "ScrcpyVideoPacketHeader rejects oversized frames before allocation");

        var merger = new ScrcpyPacketMerger();
        byte[] cfg = [0, 0, 0, 1, 0x67, 0x42];
        byte[] idr = [0, 0, 0, 1, 0x65, 0x88];
        Check(merger.Process(cfg, isConfig: true).IsEmpty && merger.HasPendingConfig,
            "ScrcpyPacketMerger caches Annex-B config packet");
        var combined = merger.Process(idr, isConfig: false);
        byte[] expected = [0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x65, 0x88];
        Check(!merger.HasPendingConfig && combined.SequenceEqual(expected),
            "ScrcpyPacketMerger prepends pending SPS/PPS to next frame");

        // Control packet serialization
        byte[] resize = new byte[5];
        int n = ScrcpyControlMessageWriter.WriteResizeDisplay(resize, 1280, 720);
        Check(n == 5 && resize[0] == ScrcpyControlMessageWriter.TypeResizeDisplay
            && BinaryPrimitives.ReadUInt16BigEndian(resize.AsSpan(1, 2)) == 1280
            && BinaryPrimitives.ReadUInt16BigEndian(resize.AsSpan(3, 2)) == 720,
            "ScrcpyControlMessageWriter encodes TypeResizeDisplay (18) in big-endian");

        byte[] touch = new byte[32];
        ScrcpyControlMessageWriter.WriteInjectTouch(touch, ScrcpyControlMessageWriter.MotionActionDown,
            ScrcpyControlMessageWriter.PointerIdMouse, 120, 340, 1080, 2400, 1.0f,
            ScrcpyControlMessageWriter.ButtonPrimary, ScrcpyControlMessageWriter.ButtonPrimary);
        Check(touch[0] == ScrcpyControlMessageWriter.TypeInjectTouchEvent
            && BinaryPrimitives.ReadInt32BigEndian(touch.AsSpan(10, 4)) == 120
            && BinaryPrimitives.ReadInt32BigEndian(touch.AsSpan(14, 4)) == 340
            && BinaryPrimitives.ReadUInt16BigEndian(touch.AsSpan(22, 2)) == 0xFFFF,
            "ScrcpyControlMessageWriter encodes fixed-point touch pressure and coordinates");
    }

    private static async Task CheckGamepadProtocolAndZeroIdleWindowAsync()
    {
        // 1. 14-byte big-endian TYPE_INJECT_GAMEPAD_STATE (19) wire encoding
        byte[] packet = new byte[14];
        var rawSample = new GamepadInputState(
            Slot: 2,
            Buttons: (ushort)(GamepadButtonMask.A | GamepadButtonMask.RightShoulder | GamepadButtonMask.LeftShoulder | GamepadButtonMask.DpadUp),
            LeftTrigger: 128,
            RightTrigger: 255,
            ThumbLX: -12345,
            ThumbLY: 32767,
            ThumbRX: -32768,
            ThumbRY: 456);
        int written = ScrcpyControlMessageWriter.WriteInjectGamepadState(packet, rawSample);
        Check(written == 14
            && ScrcpyControlMessageWriter.TypeInjectGamepadState == 19
            && packet[0] == 19
            && packet[1] == 2
            && BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2)) == 0x1301
            && packet[4] == 128
            && packet[5] == 255
            && BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(6, 2)) == -12345
            && BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(8, 2)) == 32767
            && BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(10, 2)) == -32768
            && BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(12, 2)) == 456,
            "ScrcpyControlMessageWriter encodes 14-byte big-endian TYPE_INJECT_GAMEPAD_STATE (19)");

        Check(GamepadInputState.Neutral(3).IsNeutral && !rawSample.IsNeutral,
            "GamepadInputState.IsNeutral distinguishes neutral slots from active states");

        // 2. XInput deadzones and smooth rescaling
        var deadzoneState = GamepadMath.NormalizeState(0, 0, 30, 15, 5000, 5000, -6000, 6000);
        var activeStick = GamepadMath.NormalizeState(0, GamepadButtonMask.B, 31, 255, 32767, 0, -32768, 0);
        Check(deadzoneState.IsNeutral
            && activeStick.Buttons == GamepadButtonMask.B
            && activeStick.LeftTrigger > 0
            && activeStick.RightTrigger == 255
            && activeStick.ThumbLX == 32767 && activeStick.ThumbLY == 0
            && activeStick.ThumbRX == -32768 && activeStick.ThumbRY == 0,
            "GamepadMath applies left/right radial stick deadzones and trigger thresholds smoothly");

        // 3. HID axis normalization, hat switch decoding, and Sony/Xbox layout normalization
        short hidTop = GamepadMath.NormalizeHidSignedAxis(0, 0, 255, 8, invertY: true);
        short hidBottom = GamepadMath.NormalizeHidSignedAxis(255, 0, 255, 8, invertY: true);
        Check(hidTop == 32767 && hidBottom == -32768
            && GamepadMath.DecodeHidHatSwitch(4, 0, 7) == GamepadButtonMask.DpadDown
            && GamepadMath.DecodeHidHatSwitch(8, 0, 7) == 0,
            "GamepadMath normalizes HID inverted Y axes and 8-way hat switches");

        var sonyNorm = GamepadMath.NormalizeHidInput(
            slot: 1,
            layout: HidGamepadLayoutKind.Sony,
            pressedButtonUsages: [1, 2, 7],
            hatDpadMask: GamepadButtonMask.DpadDown,
            analogLeftTrigger: 0,
            analogRightTrigger: 200,
            rawThumbLX: 0,
            rawThumbLY: 0,
            rawThumbRX: 0,
            rawThumbRY: 0);
        Check(sonyNorm.Slot == 1
            && sonyNorm.Buttons == (GamepadButtonMask.X | GamepadButtonMask.A | GamepadButtonMask.DpadDown)
            && sonyNorm.LeftTrigger == 255
            && sonyNorm.RightTrigger > 0,
            "GamepadMath maps DualSense HID button usages and digital L2 fallback cleanly");

        // 4. VK_GAMEPAD_* fallback mapping (including WinUser.h R1/L1 ordering)
        Check(AndroidKeycodeMapper.TryMapVirtualKey(0xC3, false, out int kcA) && kcA == AndroidKeycodeMapper.AKeycodeButtonA
            && AndroidKeycodeMapper.TryMapVirtualKey(0xC7, false, out int kcR1) && kcR1 == AndroidKeycodeMapper.AKeycodeButtonR1
            && AndroidKeycodeMapper.TryMapVirtualKey(0xC8, false, out int kcL1) && kcL1 == AndroidKeycodeMapper.AKeycodeButtonL1
            && AndroidKeycodeMapper.TryMapVirtualKey(0xCF, false, out int kcStart) && kcStart == AndroidKeycodeMapper.AKeycodeButtonStart
            && AndroidKeycodeMapper.TryMapVirtualKey(0xD0, false, out int kcBack) && kcBack == AndroidKeycodeMapper.AKeycodeButtonSelect,
            "AndroidKeycodeMapper maps VK_GAMEPAD_* fallback keys with exact WinUser shoulder ordering");

        // 5. NativeVideoWindow 0 Hz idle timer gating, dedup, and neutral reset on focus loss/minimize/unplug
        await using var win = new NativeVideoWindow("GamepadTest", isMirrorMode: false, preferText: true, iconPng: null,
            x: 100, y: 100, initialClientWidth: 480, initialClientHeight: 320,
            appUserModelId: "SonicRoute.Gamepad.Test");
        var events = new List<GamepadInputState>();
        win.GamepadStateChanged += s => events.Add(s);

        XInputSample slot0Sample = default;
        win.XInputProbeOverride = slot => slot == 0 ? slot0Sample : default;
        nint hwnd = await win.CreateAsync(default);

        Check(!win.HasGamepadFocus && !win.IsGamepadTimerActive && events.Count == 0,
            "NativeVideoWindow starts with gamepad polling off (0 Hz) when unfocused");

        // Focus with 0 controllers connected -> must stay 0 Hz
        SendTestMessageW(hwnd, 0x0007 /* WM_SETFOCUS */, 0, 0);
        Check(win.HasGamepadFocus && !win.IsGamepadTimerActive && events.Count == 0,
            "NativeVideoWindow keeps 0 Hz gamepad timer when focused with zero controllers connected");

        // Connect controller at idle jitter inside deadzone -> starts timer, emits zero packets
        slot0Sample = new XInputSample(true, 0, 15, 10, 1200, -900, 500, -500);
        SendTestMessageW(hwnd, 0x0219 /* WM_DEVICECHANGE */, 0x0007, 0);
        Check(win.IsGamepadTimerActive && events.Count == 0,
            "NativeVideoWindow enables gamepad timer on focused connect and suppresses deadzone jitter");

        // Active press -> single emit; duplicate poll -> suppressed
        slot0Sample = new XInputSample(true, GamepadButtonMask.A, 0, 0, 24000, 0, 0, 0);
        SendTestMessageW(hwnd, 0x0113 /* WM_TIMER */, 2 /* GamepadTimerId */, 0);
        SendTestMessageW(hwnd, 0x0113 /* WM_TIMER */, 2 /* GamepadTimerId */, 0);
        Check(events.Count == 1 && events[0].Buttons == GamepadButtonMask.A && events[0].ThumbLX > 0,
            "NativeVideoWindow suppresses duplicate non-neutral gamepad states");

        // Focus loss -> kills timer immediately and emits neutral reset
        SendTestMessageW(hwnd, 0x0008 /* WM_KILLFOCUS */, 0, 0);
        Check(!win.HasGamepadFocus && !win.IsGamepadTimerActive && events.Count == 2 && events[1].IsNeutral,
            "WM_KILLFOCUS stops gamepad polling timer immediately and emits neutral reset packet");

        // Re-focus -> active emits again; Minimize (WM_SIZE SIZE_MINIMIZED) -> kills timer + emits neutral
        SendTestMessageW(hwnd, 0x0007 /* WM_SETFOCUS */, 0, 0);
        Check(win.IsGamepadTimerActive && events.Count == 3 && !events[2].IsNeutral,
            "WM_SETFOCUS resumes gamepad timer and emits current held controller state");
        SendTestMessageW(hwnd, 0x0005 /* WM_SIZE */, 1 /* SIZE_MINIMIZED */, 0);
        Check(!win.IsGamepadTimerActive && events.Count == 4 && events[3].IsNeutral,
            "minimizing window kills gamepad timer and emits neutral reset packet");

        // Re-focus -> unplug controller -> kills timer + emits neutral
        SendTestMessageW(hwnd, 0x0007 /* WM_SETFOCUS */, 0, 0);
        Check(win.IsGamepadTimerActive && events.Count == 5 && !events[4].IsNeutral,
            "re-focusing resumes controller state before unplug check");
        slot0Sample = default;
        SendTestMessageW(hwnd, 0x0219 /* WM_DEVICECHANGE */, 0x0007, 0);
        Check(!win.IsGamepadTimerActive && events.Count == 6 && events[5].IsNeutral,
            "controller unplug while focused stops timer immediately and emits neutral reset");
    }

    private static void CheckWicPhoneIcon()
    {
        byte[]? png = WicIconLoader.LoadDefaultPhoneIconPng();
        Check(png is { Length: > 100 }, "embedded default phone icon loads from assembly resources");
        bool decoded = WicIconLoader.TryDecodePngToBgra32(png, 32, 32, out byte[] bgra, out int w, out int h);
        Check(decoded && w == 32 && h == 32 && bgra.Length == 32 * 32 * 4 && bgra.Any(b => b != 0),
            "WIC decodes embedded phone PNG icon to 32bpp BGRA pixels");
    }

    private static void CheckMediaFoundationH264Decode()
    {
        (byte[] configNal, byte[] idrNal) = BuildTestBaselineH264_64x64();
        var merger = new ScrcpyPacketMerger();
        Check(merger.Process(configNal, isConfig: true).IsEmpty, "MFT test stages SPS/PPS config NALs");
        byte[] mergedIdr = merger.Process(idrNal, isConfig: false).ToArray();

        using var decoder = new MediaFoundationH264Decoder(d3d11Device: 0, initialWidth: 64, initialHeight: 64);
        int frames = 0;
        int outW = 0, outH = 0, nv12Bytes = 0;
        byte ySample = 0;

        void CaptureFrame(ReadOnlySpan<byte> nv12, int stride, int codedH, int cropX, int cropY, int cropW, int cropH, VideoColorMatrix matrix)
        {
            frames++;
            outW = cropW;
            outH = cropH;
            nv12Bytes = nv12.Length;
            if (nv12.Length > 0) ySample = nv12[0];
        }

        decoder.DecodePacket(mergedIdr, ptsUs: 0, onHardwareFrame: null, onSoftwareFrame: CaptureFrame);
        if (frames == 0)
            decoder.Drain(onHardwareFrame: null, onSoftwareFrame: CaptureFrame);

        Check(frames >= 1 && outW == 64 && outH == 64 && nv12Bytes >= 64 * 64 * 3 / 2 && ySample > 60 && ySample < 140,
            "Windows Media Foundation H.264 MFT decodes synthetic Baseline 64x64 Annex-B stream to NV12");
    }

    private static async Task CheckHwndForegroundDisambiguationAsync()
    {
        var phone = new AndroidDevice("hwnd-dev", "Phone", "device", false);
        var appA = new AndroidApp("cn.test.alpha", "Alpha", 1001, "media");
        var appB = new AndroidApp("cn.test.beta", "Beta", 1002, "games");
        var fakeA = new HandleFakeDisplay(0x11110);
        var fakeB = new HandleFakeDisplay(0x22220);
        int created = 0;

        await using var mgr = new AndroidVirtualDisplayManager(() => Interlocked.Increment(ref created) == 1 ? fakeA : fakeB);
        await mgr.OpenAsync(phone, appA, 0, 0);
        await mgr.OpenAsync(phone, appB, 100, 100);

        Check(mgr.OwnsWindow(0x11110) && mgr.OwnsWindow(0x22220) && !mgr.OwnsWindow(0x99990),
            "AndroidVirtualDisplayManager tracks owned Win32 HWNDs accurately");
        Check(mgr.FindApplication(0x11110, Environment.ProcessId)?.App.Package == "cn.test.alpha"
            && mgr.FindApplication(0x22220, Environment.ProcessId)?.App.Package == "cn.test.beta",
            "same-process HWND foreground lookup distinguishes distinct virtual display windows");
        Check(mgr.FindApplication(0x99990, Environment.ProcessId) == null,
            "unrelated window handle sharing host PID never resolves as a virtual display app");
    }

    private static async Task CheckStage3VideoAndAudioTransportsAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sr-stage3-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string jarPath = Path.Combine(tempDir, "sonicroute-catalog.jar");
            await File.WriteAllBytesAsync(jarPath, Encoding.UTF8.GetBytes("STAGE3-MIT-JAR-FIXTURE"));

            var transport = new Stage3LoopbackTransport();
            var adb = new AdbClient(tempDir, transport);
            var phone = new AndroidDevice("stage3-phone", "Stage3", "device", true);
            var app = new AndroidApp("com.example.video", "Video", 10042, "media");

            // 1. Video transport over IAdbTransport + MIT SonicRouteServer + localabstract sonicroute_<scid>
            var videoTransport = new AdbVideoStreamTransport(adb);
            var session = await videoTransport.OpenAsync(
                new VideoStreamOpenRequest(phone, app, "12ab34cd", new VideoServerOptions()), default);

            Check(transport.PushedPaths.Contains("/data/local/tmp/sonicroute-video-server.jar"),
                "AdbVideoStreamTransport pushes catalog jar over IAdbTransport");
            Check(transport.OpenedShellCommands.Any(c => c.Contains("org.sonicroute.android.SonicRouteServer")
                && c.Contains("socket_prefix=sonicroute") && c.Contains("scid=12ab34cd")),
                "AdbVideoStreamTransport launches MIT SonicRouteServer with sonicroute prefix");
            Check(transport.LocalAbstractStreamsOpened.Count(s => s == "sonicroute_12ab34cd") == 2
                && transport.ForwardCallCount == 0,
                "AdbVideoStreamTransport opens video and control via direct localabstract without forward ports");
            Check(session.VideoStream != null && session.ControlStream != null,
                "AdbVideoStreamTransport provides both handshaken video and control streams");

            var logSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.LogLineReceived += line => { if (line.Contains("state=active")) logSeen.TrySetResult(line); };
            transport.ActiveShell?.EmitStdout("[server] INFO: SonicRoute app-state: display=7 state=active\n");
            string receivedLog = await logSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(VirtualDisplayClient.TryReadState(receivedLog) == VirtualDisplayState.Active,
                "AdbVideoStreamTransport streams server state logs from IAdbShellSession");

            await session.DisposeAsync();
            Check(transport.ExecutedCommands.Any(c => c.Contains("kill -TERM 4321")),
                "AdbVideoStreamTransport verifies cmdline and kills owned SonicRouteServer PID on dispose");

            // 2. Audio transport over IAdbTransport + MIT SonicRouteServer + localabstract sonicroute_<scid>
            await using var audioTransport = new ScrcpyAdbTransport(adb);
            using var audioStream = await audioTransport.ConnectAsync(phone, app, keepPhoneAudio: true, default);
            Check(transport.OpenedShellCommands.Any(c => c.Contains("org.sonicroute.android.SonicRouteServer")
                && c.Contains("audio=true") && c.Contains("video=false") && c.Contains("socket_prefix=sonicroute")),
                "ScrcpyAdbTransport launches MIT SonicRouteServer without legacy scrcpy-server");
            Check(transport.LocalAbstractStreamsOpened.Any(s => s.StartsWith("sonicroute_", StringComparison.Ordinal) && s != "sonicroute_12ab34cd"),
                "ScrcpyAdbTransport opens sonicroute_<scid> localabstract stream");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // Minimal valid Baseline Profile H.264 Annex-B 64x64 (4x4 MB) I_PCM IDR bitstream.
    private static (byte[] ConfigNal, byte[] IdrNal) BuildTestBaselineH264_64x64()
    {
        var sps = new H264BitWriter();
        sps.WriteByte(0x67); // NAL unit header: nal_ref_idc=3, type=7 (SPS)
        sps.WriteBits(66, 8); // profile_idc = Baseline
        sps.WriteBits(0xC0, 8); // constraint_set0_flag=1, constraint_set1_flag=1
        sps.WriteBits(30, 8); // level_idc = 3.0
        sps.WriteUe(0); // seq_parameter_set_id
        sps.WriteUe(0); // log2_max_frame_num_minus4 = 0 (frame_num uses 4 bits)
        sps.WriteUe(2); // pic_order_cnt_type = 2
        sps.WriteUe(1); // max_num_ref_frames = 1
        sps.WriteBit(0); // gaps_in_frame_num_value_allowed_flag
        sps.WriteUe(3); // pic_width_in_mbs_minus1 = 3 -> 4 MB = 64 px
        sps.WriteUe(3); // pic_height_in_map_units_minus1 = 3 -> 4 MB = 64 px
        sps.WriteBit(1); // frame_mbs_only_flag = 1
        sps.WriteBit(0); // direct_8x8_inference_flag = 0
        sps.WriteBit(0); // frame_cropping_flag = 0
        sps.WriteBit(0); // vui_parameters_present_flag = 0
        sps.WriteRbspTrailingBits();

        var pps = new H264BitWriter();
        pps.WriteByte(0x68); // NAL unit header: type=8 (PPS)
        pps.WriteUe(0); // pic_parameter_set_id
        pps.WriteUe(0); // seq_parameter_set_id
        pps.WriteBit(0); // entropy_coding_mode_flag = 0 (CAVLC)
        pps.WriteBit(0); // bottom_field_pic_order_in_frame_present_flag
        pps.WriteUe(0); // num_slice_groups_minus1
        pps.WriteUe(0); // num_ref_idx_l0_default_active_minus1
        pps.WriteUe(0); // num_ref_idx_l1_default_active_minus1
        pps.WriteBit(0); // weighted_pred_flag
        pps.WriteBits(0, 2); // weighted_bipred_idc
        pps.WriteSe(0); // pic_init_qp_minus26
        pps.WriteSe(0); // pic_init_qs_minus26
        pps.WriteSe(0); // chroma_qp_index_offset
        pps.WriteBit(0); // deblocking_filter_control_present_flag
        pps.WriteBit(0); // constrained_intra_pred_flag
        pps.WriteBit(0); // redundant_pic_cnt_present_flag
        pps.WriteRbspTrailingBits();

        byte[] spsBytes = sps.ToAnnexB();
        byte[] ppsBytes = pps.ToAnnexB();
        byte[] configNal = new byte[spsBytes.Length + ppsBytes.Length];
        spsBytes.CopyTo(configNal, 0);
        ppsBytes.CopyTo(configNal, spsBytes.Length);

        var idr = new H264BitWriter();
        idr.WriteByte(0x65); // NAL unit header: nal_ref_idc=3, type=5 (IDR)
        idr.WriteUe(0); // first_mb_in_slice = 0
        idr.WriteUe(7); // slice_type = 7 (All-I)
        idr.WriteUe(0); // pic_parameter_set_id = 0
        idr.WriteBits(0, 4); // frame_num (4 bits since log2_max_frame_num_minus4=0)
        idr.WriteUe(0); // idr_pic_id = 0
        idr.WriteBit(0); // no_output_of_prior_pics_flag
        idr.WriteBit(0); // long_term_reference_flag
        idr.WriteSe(0); // slice_qp_delta
        for (int mb = 0; mb < 16; mb++)
        {
            idr.WriteUe(25); // mb_type = 25 (I_PCM)
            idr.AlignToByteZero(); // pcm_alignment_zero_bit
            for (int y = 0; y < 256; y++) idr.WriteByteRaw(0x66);
            for (int c = 0; c < 128; c++) idr.WriteByteRaw(0x80);
        }
        idr.WriteRbspTrailingBits();

        return (configNal, idr.ToAnnexB());
    }

    private sealed class H264BitWriter
    {
        private readonly List<byte> _rbsp = new();
        private int _currByte;
        private int _bitPos;

        public void WriteBit(int bit)
        {
            _currByte = (_currByte << 1) | (bit & 1);
            if (++_bitPos == 8)
            {
                _rbsp.Add((byte)_currByte);
                _currByte = 0;
                _bitPos = 0;
            }
        }

        public void WriteBits(uint value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                WriteBit((int)((value >> i) & 1));
        }

        public void WriteByte(byte value) => WriteBits(value, 8);

        public void AlignToByteZero()
        {
            while (_bitPos != 0) WriteBit(0);
        }

        public void WriteByteRaw(byte value)
        {
            if (_bitPos == 0) _rbsp.Add(value);
            else WriteBits(value, 8);
        }

        public void WriteUe(uint value)
        {
            uint codeNum = value + 1;
            int len = 0;
            for (uint t = codeNum; t > 0; t >>= 1) len++;
            for (int i = 0; i < len - 1; i++) WriteBit(0);
            WriteBits(codeNum, len);
        }

        public void WriteSe(int value)
        {
            uint mapped = value <= 0 ? (uint)(-2 * value) : (uint)(2 * value - 1);
            WriteUe(mapped);
        }

        public void WriteRbspTrailingBits()
        {
            WriteBit(1);
            AlignToByteZero();
        }

        public byte[] ToAnnexB()
        {
            var output = new List<byte>(_rbsp.Count + 8) { 0, 0, 0, 1 };
            if (_rbsp.Count > 0) output.Add(_rbsp[0]); // NAL header never escaped
            int zeroCount = 0;
            for (int i = 1; i < _rbsp.Count; i++)
            {
                byte b = _rbsp[i];
                if (zeroCount == 2 && b <= 3)
                {
                    output.Add(3);
                    zeroCount = 0;
                }
                output.Add(b);
                zeroCount = b == 0 ? zeroCount + 1 : 0;
            }
            return output.ToArray();
        }
    }

    private sealed class HandleFakeDisplay(nint hwnd) : IVirtualDisplayClient
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public nint WindowHandle { get; } = hwnd;
        public int ProcessId => Environment.ProcessId;
        public Task Completion => _done.Task;
        public Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token) => Task.CompletedTask;
        public void Activate() { }
        public ValueTask DisposeAsync() { _done.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class Stage3LoopbackTransport : IAdbTransport
    {
        public List<string> PushedPaths { get; } = new();
        public List<string> OpenedShellCommands { get; } = new();
        public List<string> ExecutedCommands { get; } = new();
        public List<string> LocalAbstractStreamsOpened { get; } = new();
        public int ForwardCallCount { get; private set; }
        public FakeShellSession? ActiveShell { get; private set; }
        private string? _lastServerCmd;

        public Task<string> VersionAsync(CancellationToken token) => Task.FromResult("41");
        public Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<AndroidDevice>>([new AndroidDevice("stage3-phone", "Stage3", "device", true)]);
        public Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onChanged, CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public Task<IReadOnlyList<string>> FeaturesAsync(string serial, CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(["shell_v2"]);
        public Task<string> GetSerialNoAsync(string serial, CancellationToken token) => Task.FromResult("HW-STAGE3");
        public Task PushAsync(string serial, string localPath, string remotePath, CancellationToken token) { PushedPaths.Add(remotePath); return Task.CompletedTask; }
        public Task<int> ForwardTcpToLocalAbstractAsync(string serial, string name, CancellationToken token) { ForwardCallCount++; return Task.FromResult(0); }
        public Task RemoveForwardAsync(string serial, string localSpec, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<AdbForwardRule>> ListForwardsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<AdbForwardRule>>([]);
        public Task<string> PairAsync(string address, string password, CancellationToken token) => Task.FromResult("Successfully paired");
        public Task<string> ConnectAsync(string address, CancellationToken token) => Task.FromResult("connected");
        public Task<string> DisconnectAsync(string address, CancellationToken token) => Task.FromResult("disconnected");
        public Task<IReadOnlyList<AndroidMdnsService>> MdnsServicesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<AndroidMdnsService>>([]);

        public Task<AdbShellResult> RunShellAsync(string serial, string command, CancellationToken token)
        {
            ExecutedCommands.Add(command);
            if (command.Contains("ls -l")) return Task.FromResult(new AdbShellResult("MISSING\n", 0));
            if (command.Contains("getprop ro.build.version.sdk")) return Task.FromResult(new AdbShellResult("36\n", 0));
            if (command.Contains("pm list packages -U")) return Task.FromResult(new AdbShellResult("package:com.example.video uid:10042\n", 0));
            if (command.Contains("cat /proc/4321/cmdline")) return Task.FromResult(new AdbShellResult(_lastServerCmd ?? "", 0));
            if (command.Contains("kill -TERM 4321")) { ActiveShell?.CompleteExit(); return Task.FromResult(new AdbShellResult("", 0)); }
            return Task.FromResult(new AdbShellResult("", 0));
        }

        public Task<IAdbShellSession> OpenShellAsync(string serial, string command, bool redirectInput, CancellationToken token)
        {
            OpenedShellCommands.Add(command);
            _lastServerCmd = command;
            ActiveShell = new FakeShellSession();
            ActiveShell.EmitStdout("SR_PID:4321\n");
            return Task.FromResult<IAdbShellSession>(ActiveShell);
        }

        public Task<Stream> OpenLocalAbstractAsync(string serial, string name, CancellationToken token)
        {
            LocalAbstractStreamsOpened.Add(name);
            return Task.FromResult<Stream>(new MemoryStream([0x00]));
        }
    }

    private sealed class FakeShellSession : IAdbShellSession
    {
        private readonly PipeBufferStream _stdout = new();
        private readonly PipeBufferStream _stderr = new();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream OutputStream => _stdout;
        public Stream ErrorStream => _stderr;
        public Stream InputStream => Stream.Null;
        public bool HasExited => _exit.Task.IsCompleted;
        public int? ExitCode => _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null;

        public void EmitStdout(string text) => _stdout.Enqueue(Encoding.UTF8.GetBytes(text));
        public void CompleteExit()
        {
            _stdout.Complete();
            _stderr.Complete();
            _exit.TrySetResult(0);
        }

        public Task<int> WaitForExitAsync(CancellationToken token = default) => _exit.Task.WaitAsync(token);
        public Task CloseInputAsync(CancellationToken token = default) => Task.CompletedTask;
        public void Kill() => CompleteExit();
        public ValueTask DisposeAsync() { CompleteExit(); return ValueTask.CompletedTask; }
    }

    private sealed class PipeBufferStream : Stream
    {
        private readonly Queue<byte> _queue = new();
        private readonly object _lock = new();
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _completed;

        public void Enqueue(byte[] data)
        {
            lock (_lock)
            {
                foreach (byte b in data) _queue.Enqueue(b);
                _signal.TrySetResult();
            }
        }

        public void Complete()
        {
            lock (_lock)
            {
                _completed = true;
                _signal.TrySetResult();
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task wait;
                lock (_lock)
                {
                    if (_queue.Count > 0)
                    {
                        int n = Math.Min(buffer.Length, _queue.Count);
                        var span = buffer.Span;
                        for (int i = 0; i < n; i++) span[i] = _queue.Dequeue();
                        return n;
                    }
                    if (_completed) return 0;
                    if (_signal.Task.IsCompleted)
                        _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _signal.Task;
                }
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
