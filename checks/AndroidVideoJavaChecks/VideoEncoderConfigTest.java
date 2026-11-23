// SPDX-License-Identifier: GPL-3.0-only

import android.media.MediaFormat;
import java.io.ByteArrayInputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.IntPredicate;
import org.sonicroute.android.control.InputController;
import org.sonicroute.android.core.MediaCodecParams;
import org.sonicroute.android.io.ControlProtocol;
import org.sonicroute.android.io.PacketWriter;
import org.sonicroute.android.video.VideoCaptureSource;
import org.sonicroute.android.video.VideoEncoderConfig;
import org.sonicroute.android.video.VideoEncoderConfig.FallbackSelection;
import org.sonicroute.android.video.VideoEncoderConfig.FormatConsumer;
import org.sonicroute.android.video.VideoEncoderConfig.ResolvedConfig;
import org.sonicroute.android.video.VideoEncoderConfig.StreamClockTracker;
import org.sonicroute.android.video.VideoEncoderConfig.SyncRequestGate;
import org.sonicroute.android.video.VideoEncoderRunner;
import org.sonicroute.android.video.VideoEncoderRunner.RuntimeBitrateHook;

/**
 * Pure desktop JVM unit test suite covering {@link VideoEncoderConfig},
 * {@link VideoEncoderRunner} contract signatures, and {@link ControlProtocol} sync-frame parsing.
 */
public final class VideoEncoderConfigTest {
    private VideoEncoderConfigTest() {
    }

    public static void main(String[] args) throws Exception {
        testTargetFpsPresetsAndDefault();
        testFrameRateClamping();
        testFallbackSizeSelection();
        testIFrameIntervalAndRepeatTimeout();
        testCodecOptionsOverridePrecedence();
        testBitrateClampingAndHookContract();
        testLowLatencyEnhancementsAndFallbackFlag();
        testSyncRateLimitAndStreamClockTracker();
        testControlProtocolSyncFrame();
        System.out.println("PASS all VideoEncoderConfig, RuntimeBitrateHook, and ControlProtocol JVM checks");
    }

    private static void testTargetFpsPresetsAndDefault() {
        float[] presets = { 15f, 24f, 30f, 60f, 120f };
        for (float fps : presets) {
            checkClose(fps, VideoEncoderConfig.resolveTargetFps(fps), 0.001f, "preset fps " + fps);
            ResolvedConfig cfg = VideoEncoderConfig.resolve(
                    33, fps, 8_000_000, false, true, 1.0, 120.0, 100_000, 50_000_000, true);
            checkClose(fps, cfg.getEffectiveFps(), 0.001f, "resolved fps " + fps);
            check(cfg.getFrameRateInt() == Math.round(fps), "integer frame rate " + fps);
            check(cfg.isIncludeMaxFpsToEncoder(), "max-fps-to-encoder enabled for positive maxFps=" + fps);

            Map<String, Object> sinkMap = captureFormatMap(cfg, MediaFormat.MIMETYPE_VIDEO_AVC, 1080, 2400, 33, null);
            check(Integer.valueOf(Math.round(fps)).equals(sinkMap.get(MediaFormat.KEY_FRAME_RATE)),
                    "KEY_FRAME_RATE matches " + fps);
            checkClose(fps, ((Float) sinkMap.get(VideoEncoderConfig.KEY_MAX_FPS_TO_ENCODER)), 0.001f,
                    "KEY_MAX_FPS_TO_ENCODER matches " + fps);
        }

        for (float unset : new float[] { 0f, -1f, Float.NaN, Float.POSITIVE_INFINITY }) {
            checkClose(60f, VideoEncoderConfig.resolveTargetFps(unset), 0.001f, "unset fps defaults to 60");
            ResolvedConfig cfg = VideoEncoderConfig.resolve(
                    33, unset, 8_000_000, false, true, 1.0, 120.0, 100_000, 50_000_000, true);
            checkClose(60f, cfg.getEffectiveFps(), 0.001f, "unset resolved fps defaults to 60");
            check(cfg.getFrameRateInt() == 60, "unset integer frame rate defaults to 60");
            check(!cfg.isIncludeMaxFpsToEncoder(), "max-fps-to-encoder omitted when maxFps <= 0");

            Map<String, Object> sinkMap = captureFormatMap(cfg, MediaFormat.MIMETYPE_VIDEO_AVC, 1080, 2400, 33, null);
            check(Integer.valueOf(60).equals(sinkMap.get(MediaFormat.KEY_FRAME_RATE)),
                    "unset KEY_FRAME_RATE defaults to 60");
            check(!sinkMap.containsKey(VideoEncoderConfig.KEY_MAX_FPS_TO_ENCODER),
                    "KEY_MAX_FPS_TO_ENCODER absent when maxFps <= 0");
        }
    }

    private static void testFrameRateClamping() {
        // Device encoder caps 120fps request to 60fps.
        ResolvedConfig clamped120 = VideoEncoderConfig.resolve(
                33, 120f, 12_000_000, false, false, 1.0, 60.0, 100_000, 40_000_000, true);
        check(clamped120.isFrameRateClamped(), "120fps should report clamped when max=60");
        checkClose(60f, clamped120.getEffectiveFps(), 0.001f, "120fps clamped to 60");
        check(clamped120.getFrameRateInt() == 60, "clamped int frame rate = 60");
        check(clamped120.getOperatingRateFps() == 0, "operating-rate not enabled once clamped <= 60");

        // Fractional upper cap (e.g. 59.94 fps) must not round integer KEY_FRAME_RATE above cap.
        int cappedInt = VideoEncoderConfig.toEncoderFrameRateInt(59.94f, 59.94);
        check(cappedInt == 59, "59.94fps should floor to 59 rather than exceeding 59.94 cap");

        // High-FPS 120fps supported keeps 120 and enables operating-rate.
        ResolvedConfig full120 = VideoEncoderConfig.resolve(
                33, 120f, 12_000_000, true, true, 1.0, 120.0, 100_000, 40_000_000, true);
        check(!full120.isFrameRateClamped(), "120fps supported should not clamp");
        checkClose(120f, full120.getEffectiveFps(), 0.001f, "effectiveFps stays 120");
        check(full120.getOperatingRateFps() == 120, "operating-rate = 120 for > 60fps");

        // Lower bound clamping when size+rate unsupported below min.
        float raisedMin = VideoEncoderConfig.clampFrameRate(5f, false, 15.0, 60.0);
        checkClose(15f, raisedMin, 0.001f, "5fps raised to supported min 15fps");
    }

    private static void testFallbackSizeSelection() {
        // Requesting 1440x3200 when encoder only supports up to 1920 major side skips 2560 and picks 1920.
        FallbackSelection sel = VideoEncoderConfig.pickSupportedFallbackMaxSize(
                1440,
                3200,
                3200,
                0,
                VideoEncoderConfig.DEFAULT_DOWNSIZE_FALLBACKS,
                (w, h) -> Math.max(w, h) <= 1920 && (w & 7) == 0 && (h & 7) == 0);
        check(sel != null, "expected supported fallback at 1920");
        check(sel.getMaxSize() == 1920, "skipped unsupported 2560 and selected 1920");
        check(sel.getHeight() == 1920, "portrait major side = 1920");
        check(sel.getWidth() == 864, "scaled minor side aligned to 8 = 864");
        check(sel.getNextIndex() == 2, "nextIndex advances past 1920");

        // Already at minimum size (800): returns null so existing exception/HEVC->H.264 fallback handles it.
        FallbackSelection none = VideoEncoderConfig.pickSupportedFallbackMaxSize(
                360,
                800,
                800,
                0,
                VideoEncoderConfig.DEFAULT_DOWNSIZE_FALLBACKS,
                (w, h) -> false);
        check(none == null, "no smaller fallback below 800");

        // Subsequent fallback retry starting from startIndex = sel.getNextIndex().
        FallbackSelection nextSel = VideoEncoderConfig.pickSupportedFallbackMaxSize(
                864,
                1920,
                sel.getMaxSize(),
                sel.getNextIndex(),
                VideoEncoderConfig.DEFAULT_DOWNSIZE_FALLBACKS,
                (w, h) -> Math.max(w, h) <= 1280);
        check(nextSel != null && nextSel.getMaxSize() == 1280 && nextSel.getNextIndex() == 4,
                "subsequent fallback skips 1600 and selects 1280");
    }

    private static void testIFrameIntervalAndRepeatTimeout() {
        // USB & Wi-Fi default to 2s for >= 24fps, 3s for < 24fps.
        check(VideoEncoderConfig.computeDefaultIFrameIntervalSec(15f, false) == 3, "USB 15fps -> 3s");
        check(VideoEncoderConfig.computeDefaultIFrameIntervalSec(15f, true) == 3, "Wi-Fi 15fps -> 3s");
        check(VideoEncoderConfig.computeDefaultIFrameIntervalSec(24f, false) == 2, "USB 24fps -> 2s");
        check(VideoEncoderConfig.computeDefaultIFrameIntervalSec(30f, true) == 2, "Wi-Fi 30fps -> 2s");
        check(VideoEncoderConfig.computeDefaultIFrameIntervalSec(60f, false) == 2, "USB 60fps -> 2s");
        check(VideoEncoderConfig.computeDefaultIFrameIntervalSec(120f, true) == 2, "Wi-Fi 120fps -> 2s");

        // Repeat previous frame timeout: max(100_000, (1_000_000 / effectiveFps) * 2).
        check(VideoEncoderConfig.computeRepeatPreviousFrameAfterUs(15f) == 133_332L, "15fps repeat = 133332us");
        check(VideoEncoderConfig.computeRepeatPreviousFrameAfterUs(24f) == 100_000L, "24fps repeat = 100000us");
        check(VideoEncoderConfig.computeRepeatPreviousFrameAfterUs(30f) == 100_000L, "30fps repeat = 100000us");
        check(VideoEncoderConfig.computeRepeatPreviousFrameAfterUs(60f) == 100_000L, "60fps repeat = 100000us");
        check(VideoEncoderConfig.computeRepeatPreviousFrameAfterUs(120f) == 100_000L, "120fps repeat = 100000us");
        check(VideoEncoderConfig.computeRepeatPreviousFrameAfterUs(10f) == 200_000L, "10fps repeat = 200000us");
    }

    private static void testCodecOptionsOverridePrecedence() {
        List<MediaCodecParams.Entry> options = MediaCodecParams.parse(
                "i-frame-interval=5,frame-rate=45,max-bframes=1,low-latency=0,bitrate=6000000");
        ResolvedConfig cfg = VideoEncoderConfig.resolve(
                33, 120f, 8_000_000, true, true, 1.0, 120.0, 100_000, 40_000_000, true);
        Map<String, Object> format = captureFormatMap(cfg, MediaFormat.MIMETYPE_VIDEO_HEVC, 1920, 1080, 33, options);

        check(Integer.valueOf(5).equals(format.get(MediaFormat.KEY_I_FRAME_INTERVAL)),
                "codecOptions overrides i-frame-interval");
        check(Integer.valueOf(45).equals(format.get(MediaFormat.KEY_FRAME_RATE)),
                "codecOptions overrides frame-rate");
        check(Integer.valueOf(1).equals(format.get(VideoEncoderConfig.KEY_MAX_B_FRAMES)),
                "codecOptions overrides max-bframes");
        check(Integer.valueOf(0).equals(format.get(VideoEncoderConfig.KEY_LOW_LATENCY)),
                "codecOptions overrides low-latency");
        check(Integer.valueOf(6_000_000).equals(format.get(MediaFormat.KEY_BIT_RATE)),
                "codecOptions overrides bitrate");
        check(!format.containsKey(MediaFormat.KEY_BITRATE_MODE),
                "KEY_BITRATE_MODE must never be forced when absent from codecOptions");
    }

    private static void testBitrateClampingAndHookContract() throws Exception {
        check(VideoEncoderConfig.clampBitrate(50_000_000, 100_000, 20_000_000) == 20_000_000,
                "bitrate clamped to upper bound");
        check(VideoEncoderConfig.clampBitrate(10_000, 100_000, 20_000_000) == 100_000,
                "bitrate clamped to lower bound");
        check(VideoEncoderConfig.clampBitrate(4_000_000, 100_000, 20_000_000) == 4_000_000,
                "in-range bitrate preserved");

        // Verify exact constructor & interface signatures shared with Worker C.
        Constructor<VideoEncoderRunner> ctor9 = VideoEncoderRunner.class.getConstructor(
                VideoCaptureSource.class,
                PacketWriter.class,
                int.class,
                int.class,
                float.class,
                String.class,
                List.class,
                boolean.class,
                Runnable.class);
        check(ctor9 != null, "9-arg VideoEncoderRunner constructor exists");

        Constructor<VideoEncoderRunner> ctor11 = VideoEncoderRunner.class.getConstructor(
                VideoCaptureSource.class,
                PacketWriter.class,
                int.class,
                int.class,
                float.class,
                String.class,
                List.class,
                boolean.class,
                boolean.class,
                RuntimeBitrateHook.class,
                Runnable.class);
        check(ctor11 != null, "11-arg VideoEncoderRunner constructor exists");

        Method requestSyncMethod = VideoEncoderRunner.class.getMethod("requestSyncFrame");
        check(requestSyncMethod.getReturnType() == boolean.class, "requestSyncFrame returns boolean");

        Method onSessionStarted = RuntimeBitrateHook.class.getMethod(
                "onSessionStarted", int.class, int.class, int.class, float.class, IntPredicate.class);
        check(onSessionStarted.getReturnType() == void.class, "onSessionStarted signature exact");

        Method onPacketWritten = RuntimeBitrateHook.class.getMethod(
                "onPacketWritten", int.class, long.class, long.class, boolean.class, boolean.class, long.class);
        check(onPacketWritten.getReturnType() == void.class, "onPacketWritten signature exact");

        Method setSyncRequester = InputController.class.getMethod("setSyncFrameRequester", Runnable.class);
        check(setSyncRequester.getReturnType() == void.class, "InputController.setSyncFrameRequester signature exact");

        // Exercise fake RuntimeBitrateHook + VideoEncoderRunner instantiation on JVM.
        AtomicInteger appliedBps = new AtomicInteger();
        AtomicBoolean packetRecorded = new AtomicBoolean();
        RuntimeBitrateHook fakeHook = new RuntimeBitrateHook() {
            @Override
            public void onSessionStarted(
                    int initialBitRateBps,
                    int minSupportedBps,
                    int maxSupportedBps,
                    float effectiveFps,
                    IntPredicate bitrateApplier) {
                check(initialBitRateBps == 8_000_000, "initialBitRateBps passed to hook");
                check(minSupportedBps == 100_000 && maxSupportedBps == 20_000_000, "bounds passed to hook");
                checkClose(60f, effectiveFps, 0.001f, "effectiveFps passed to hook");
                check(bitrateApplier.test(6_000_000), "fake bitrateApplier accepts new bitrate");
            }

            @Override
            public void onPacketWritten(
                    int packetBytes,
                    long writeDurationNs,
                    long frameAgeUs,
                    boolean isKeyFrame,
                    boolean isConfig,
                    long nowNs) {
                check(packetBytes == 4096 && writeDurationNs == 250_000L && frameAgeUs == 1_200L,
                        "packet metrics forwarded");
                check(isKeyFrame && !isConfig && nowNs > 0, "packet flags forwarded");
                packetRecorded.set(true);
            }
        };

        fakeHook.onSessionStarted(8_000_000, 100_000, 20_000_000, 60f, bps -> {
            appliedBps.set(bps);
            return true;
        });
        fakeHook.onPacketWritten(4096, 250_000L, 1_200L, true, false, 123_456_789L);
        check(appliedBps.get() == 6_000_000, "fake bitrate applier invoked");
        check(packetRecorded.get(), "fake onPacketWritten invoked");

        VideoEncoderRunner runner = new VideoEncoderRunner(
                null, null, PacketWriter.CODEC_ID_H264, 4_000_000, 60f, null, null, true, true, fakeHook, null);
        check(!runner.requestSyncFrame(), "requestSyncFrame returns false safely when no codec active");
    }

    private static void testLowLatencyEnhancementsAndFallbackFlag() {
        // API 30+ with FEATURE_LowLatency supported and 120fps.
        ResolvedConfig enhanced = VideoEncoderConfig.resolve(
                31, 120f, 8_000_000, false, true, 1.0, 120.0, 100_000, 40_000_000, true);
        check(enhanced.isUseLowLatency(), "low-latency enabled on SDK >= 30 when feature supported");
        check(enhanced.isUseRealtimePriority(), "realtime priority enabled on SDK >= 23");
        check(enhanced.isUseZeroBFrames(), "max-bframes=0 enabled on SDK >= 29");
        check(enhanced.getOperatingRateFps() == 120, "operating-rate=120 enabled for >60fps");
        check(enhanced.hasLowLatencyEnhancements(), "enhanced profile reports enhancements");

        Map<String, Object> enhancedMap = captureFormatMap(
                enhanced, MediaFormat.MIMETYPE_VIDEO_AVC, 1920, 1080, 31, null);
        check(Integer.valueOf(1).equals(enhancedMap.get(VideoEncoderConfig.KEY_LOW_LATENCY)), "low-latency=1");
        check(Integer.valueOf(0).equals(enhancedMap.get(VideoEncoderConfig.KEY_PRIORITY)), "priority=0");
        check(Integer.valueOf(0).equals(enhancedMap.get(VideoEncoderConfig.KEY_MAX_B_FRAMES)), "max-bframes=0");
        check(Integer.valueOf(120).equals(enhancedMap.get(VideoEncoderConfig.KEY_OPERATING_RATE)), "operating-rate=120");
        check(!enhancedMap.containsKey(MediaFormat.KEY_BITRATE_MODE), "CBR never forced");

        // Fallback strips all enhancements in-place while retaining frame-rate, bitrate, I-frame interval, etc.
        ResolvedConfig baseFallback = enhanced.withoutEnhancements();
        check(!baseFallback.hasLowLatencyEnhancements(), "fallback clears all enhancement flags");
        checkClose(120f, baseFallback.getEffectiveFps(), 0.001f, "fallback preserves effectiveFps");
        check(baseFallback.getIFrameIntervalSec() == 2, "fallback preserves I-frame interval");

        Map<String, Object> baseMap = captureFormatMap(
                baseFallback, MediaFormat.MIMETYPE_VIDEO_AVC, 1920, 1080, 31, null);
        check(!baseMap.containsKey(VideoEncoderConfig.KEY_LOW_LATENCY), "fallback removes low-latency");
        check(!baseMap.containsKey(VideoEncoderConfig.KEY_PRIORITY), "fallback removes priority");
        check(!baseMap.containsKey(VideoEncoderConfig.KEY_MAX_B_FRAMES), "fallback removes max-bframes");
        check(!baseMap.containsKey(VideoEncoderConfig.KEY_OPERATING_RATE), "fallback removes operating-rate");
        check(Integer.valueOf(120).equals(baseMap.get(MediaFormat.KEY_FRAME_RATE)), "fallback keeps frame-rate");

        // API 29 without FEATURE_LowLatency / SDK < 30: low-latency stays disabled.
        ResolvedConfig api29 = VideoEncoderConfig.resolve(
                29, 60f, 8_000_000, false, true, 1.0, 60.0, 100_000, 40_000_000, true);
        check(!api29.isUseLowLatency(), "low-latency disabled below API 30 even if flag passed");
        check(api29.isUseRealtimePriority(), "priority=0 on API 29");
        check(api29.isUseZeroBFrames(), "max-bframes=0 on API 29");
        check(api29.getOperatingRateFps() == 0, "operating-rate omitted at <= 60fps");

        // API 33 when FEATURE_LowLatency is NOT supported by codec.
        ResolvedConfig unsupportedLowLatency = VideoEncoderConfig.resolve(
                33, 60f, 8_000_000, false, true, 1.0, 60.0, 100_000, 40_000_000, false);
        check(!unsupportedLowLatency.isUseLowLatency(), "low-latency disabled when codec lacks FEATURE_LowLatency");
    }

    private static void testSyncRateLimitAndStreamClockTracker() {
        SyncRequestGate gate = new SyncRequestGate(250_000_000L);
        // Boot-near-zero timestamp (e.g. 1_000_000L = 1ms after boot) must succeed on first call!
        check(gate.tryAcquire(1_000_000L), "first sync request allowed even near nanoTime=0");
        check(!gate.tryAcquire(100_000_000L), "sync request inside 250ms rejected");
        check(!gate.tryAcquire(250_999_999L), "sync request at 249.999999ms rejected");
        check(gate.tryAcquire(251_000_000L), "sync request at exactly 250ms allowed");
        gate.reset();
        check(gate.tryAcquire(252_000_000L), "reset clears throttle for new session");

        // Signed long wraparound check near Long.MAX_VALUE.
        SyncRequestGate wrapGate = new SyncRequestGate(250_000_000L);
        long nearMax = Long.MAX_VALUE - 100_000_000L;
        check(wrapGate.tryAcquire(nearMax), "acquire near Long.MAX_VALUE");
        check(!wrapGate.tryAcquire(nearMax + 200_000_000L), "wrapped <250ms rejected");
        check(wrapGate.tryAcquire(nearMax + 250_000_000L), "wrapped >=250ms allowed");

        // StreamClockTracker monotonic frameAgeUs checks.
        StreamClockTracker clock = new StreamClockTracker();
        // CODEC_CONFIG packets often carry PTS=0 and must NOT anchor the stream clock.
        check(clock.updateAndComputeFrameAgeUs(0L, 1_000_000L, 1_100_000L, true) == 0L,
                "config packet age=0 and does not anchor baseline");
        // First real frame at PTS=1_000_000us, write took 500us.
        check(clock.updateAndComputeFrameAgeUs(1_000_000L, 10_000_000_000L, 10_000_500_000L, false) == 500L,
                "first frame reports write duration as age");
        // Second frame 16_667us later in PTS, written 26_667us later in wall clock -> 10_000us queue/write age.
        long age2 = clock.updateAndComputeFrameAgeUs(1_016_667L, 10_026_000_000L, 10_026_667_000L, false);
        check(age2 == 10_000L, "second frame captures 10ms latency: got " + age2);
        // Faster frame resulting in negative raw delta must re-anchor and never return negative.
        long age3 = clock.updateAndComputeFrameAgeUs(1_100_000L, 10_050_000_000L, 10_050_100_000L, false);
        check(age3 == 0L, "negative raw age re-anchors to 0 instead of negative: got " + age3);
        // Backward PTS reset jump must re-anchor cleanly instead of exploding to huge age.
        long age4 = clock.updateAndComputeFrameAgeUs(50_000L, 10_070_000_000L, 10_070_200_000L, false);
        check(age4 >= 0L && age4 <= 1_000L, "backward PTS jump re-anchors safely: got " + age4);
    }

    private static void testControlProtocolSyncFrame() throws Exception {
        check(ControlProtocol.TYPE_REQUEST_SYNC_FRAME == 20, "TYPE_REQUEST_SYNC_FRAME == 20");
        byte[] wire = new byte[] {
                (byte) ControlProtocol.TYPE_RESET_VIDEO,
                (byte) ControlProtocol.TYPE_REQUEST_SYNC_FRAME,
                (byte) ControlProtocol.TYPE_RESIZE_DISPLAY, 0x05, 0x00, 0x02, (byte) 0xD0,
                (byte) ControlProtocol.TYPE_REQUEST_SYNC_FRAME
        };
        ControlProtocol.Reader reader = new ControlProtocol.Reader(new ByteArrayInputStream(wire));
        check(reader.read().type == ControlProtocol.TYPE_RESET_VIDEO, "reads reset video");
        check(reader.read().type == ControlProtocol.TYPE_REQUEST_SYNC_FRAME, "reads first sync-frame request");
        ControlProtocol.ClientMessage resize = reader.read();
        check(resize.type == ControlProtocol.TYPE_RESIZE_DISPLAY && resize.width == 1280 && resize.height == 720,
                "reads resize following sync-frame without framing drift");
        check(reader.read().type == ControlProtocol.TYPE_REQUEST_SYNC_FRAME, "reads second sync-frame request");
    }

    private static Map<String, Object> captureFormatMap(
            ResolvedConfig config,
            String mime,
            int width,
            int height,
            int sdkInt,
            List<MediaCodecParams.Entry> codecOptions) {
        Map<String, Object> map = new LinkedHashMap<>();
        VideoEncoderConfig.populateFormat(config, mime, width, height, sdkInt, codecOptions, new FormatConsumer() {
            @Override
            public void setInteger(String key, int value) {
                map.put(key, value);
            }

            @Override
            public void setLong(String key, long value) {
                map.put(key, value);
            }

            @Override
            public void setFloat(String key, float value) {
                map.put(key, value);
            }

            @Override
            public void setString(String key, String value) {
                map.put(key, value);
            }
        });
        return map;
    }

    private static void check(boolean condition, String message) {
        if (!condition) {
            throw new AssertionError("Check failed: " + message);
        }
    }

    private static void checkClose(float expected, float actual, float tolerance, String message) {
        if (Math.abs(expected - actual) > tolerance) {
            throw new AssertionError(message + " expected=" + expected + " actual=" + actual);
        }
    }
}
