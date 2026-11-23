// SPDX-License-Identifier: GPL-3.0-only
package org.sonicroute.android.video;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.os.Build;
import android.util.Range;
import java.util.List;
import org.sonicroute.android.core.MediaCodecParams;
import org.sonicroute.android.model.Geometry.Size;

/**
 * Pure encoder configuration decisions, capability clamping, fallback size selection,
 * and low-latency profile construction for {@link VideoEncoderRunner}.
 */
public final class VideoEncoderConfig {
    public static final float DEFAULT_FRAME_RATE = 60.0f;
    public static final int DEFAULT_I_FRAME_INTERVAL_SEC = 2;
    public static final int LOW_FPS_I_FRAME_INTERVAL_SEC = 3;
    public static final float LOW_FPS_THRESHOLD = 24.0f;
    public static final long MIN_REPEAT_PREVIOUS_FRAME_AFTER_US = 100_000L;
    public static final long SYNC_FRAME_MIN_INTERVAL_NS = 250_000_000L;
    public static final int DEFAULT_MIN_BITRATE_BPS = 100_000;
    public static final int DEFAULT_MAX_BITRATE_BPS = 100_000_000;
    public static final int DEFAULT_FALLBACK_BITRATE_BPS = 4_000_000;

    public static final String KEY_MAX_FPS_TO_ENCODER = "max-fps-to-encoder";
    public static final String KEY_LOW_LATENCY = "low-latency";
    public static final String KEY_PRIORITY = "priority";
    public static final String KEY_MAX_B_FRAMES = "max-bframes";
    public static final String KEY_OPERATING_RATE = "operating-rate";

    public static final int[] DEFAULT_DOWNSIZE_FALLBACKS = { 2560, 1920, 1600, 1280, 1024, 800 };

    private VideoEncoderConfig() {
    }

    /**
     * Size-support check abstraction so fallback selection can run both against live
     * {@link MediaCodecInfo.VideoCapabilities} and host-side JVM unit tests.
     */
    @FunctionalInterface
    public interface SizeSupportPredicate {
        boolean isSupported(int width, int height);
    }

    /**
     * Format property consumer decoupling format assembly from {@link MediaFormat} instantiation.
     */
    public interface FormatConsumer {
        void setInteger(String key, int value);

        void setLong(String key, long value);

        void setFloat(String key, float value);

        void setString(String key, String value);
    }

    /**
     * Result of selecting a supported downsize fallback from {@link #DEFAULT_DOWNSIZE_FALLBACKS}.
     */
    public static final class FallbackSelection {
        private final int maxSize;
        private final int width;
        private final int height;
        private final int nextIndex;

        public FallbackSelection(int maxSize, int width, int height, int nextIndex) {
            this.maxSize = maxSize;
            this.width = width;
            this.height = height;
            this.nextIndex = nextIndex;
        }

        public int getMaxSize() {
            return maxSize;
        }

        public int getWidth() {
            return width;
        }

        public int getHeight() {
            return height;
        }

        public int getNextIndex() {
            return nextIndex;
        }
    }

    /**
     * Immutable resolved encoder configuration covering base safe fields and optional low-latency
     * enhancements, with an in-place fallback view via {@link #withoutEnhancements()}.
     */
    public static final class ResolvedConfig {
        private final float targetFps;
        private final float effectiveFps;
        private final int frameRateInt;
        private final boolean includeMaxFpsToEncoder;
        private final int requestedBitRateBps;
        private final int clampedBitRateBps;
        private final int minSupportedBitRateBps;
        private final int maxSupportedBitRateBps;
        private final int iFrameIntervalSec;
        private final long repeatPreviousFrameAfterUs;
        private final boolean wifiTransport;
        private final boolean useLowLatency;
        private final boolean useRealtimePriority;
        private final boolean useZeroBFrames;
        private final int operatingRateFps;

        private ResolvedConfig(
                float targetFps,
                float effectiveFps,
                int frameRateInt,
                boolean includeMaxFpsToEncoder,
                int requestedBitRateBps,
                int clampedBitRateBps,
                int minSupportedBitRateBps,
                int maxSupportedBitRateBps,
                int iFrameIntervalSec,
                long repeatPreviousFrameAfterUs,
                boolean wifiTransport,
                boolean useLowLatency,
                boolean useRealtimePriority,
                boolean useZeroBFrames,
                int operatingRateFps) {
            this.targetFps = targetFps;
            this.effectiveFps = effectiveFps;
            this.frameRateInt = frameRateInt;
            this.includeMaxFpsToEncoder = includeMaxFpsToEncoder;
            this.requestedBitRateBps = requestedBitRateBps;
            this.clampedBitRateBps = clampedBitRateBps;
            this.minSupportedBitRateBps = minSupportedBitRateBps;
            this.maxSupportedBitRateBps = maxSupportedBitRateBps;
            this.iFrameIntervalSec = iFrameIntervalSec;
            this.repeatPreviousFrameAfterUs = repeatPreviousFrameAfterUs;
            this.wifiTransport = wifiTransport;
            this.useLowLatency = useLowLatency;
            this.useRealtimePriority = useRealtimePriority;
            this.useZeroBFrames = useZeroBFrames;
            this.operatingRateFps = operatingRateFps;
        }

        public float getTargetFps() {
            return targetFps;
        }

        public float getEffectiveFps() {
            return effectiveFps;
        }

        public int getFrameRateInt() {
            return frameRateInt;
        }

        public boolean isIncludeMaxFpsToEncoder() {
            return includeMaxFpsToEncoder;
        }

        public int getRequestedBitRateBps() {
            return requestedBitRateBps;
        }

        public int getClampedBitRateBps() {
            return clampedBitRateBps;
        }

        public int getMinSupportedBitRateBps() {
            return minSupportedBitRateBps;
        }

        public int getMaxSupportedBitRateBps() {
            return maxSupportedBitRateBps;
        }

        public int getIFrameIntervalSec() {
            return iFrameIntervalSec;
        }

        public long getRepeatPreviousFrameAfterUs() {
            return repeatPreviousFrameAfterUs;
        }

        public boolean isWifiTransport() {
            return wifiTransport;
        }

        public boolean isUseLowLatency() {
            return useLowLatency;
        }

        public boolean isUseRealtimePriority() {
            return useRealtimePriority;
        }

        public boolean isUseZeroBFrames() {
            return useZeroBFrames;
        }

        public int getOperatingRateFps() {
            return operatingRateFps;
        }

        public boolean isFrameRateClamped() {
            return Math.abs(effectiveFps - targetFps) > 0.01f;
        }

        public boolean isBitRateClamped() {
            return clampedBitRateBps != requestedBitRateBps;
        }

        public boolean hasLowLatencyEnhancements() {
            return useLowLatency || useRealtimePriority || useZeroBFrames || operatingRateFps > 0;
        }

        /**
         * Returns a base-safe copy with all optional low-latency enhancements disabled for
         * in-place retry when a device rejects enhanced {@code MediaCodec.configure} keys.
         */
        public ResolvedConfig withoutEnhancements() {
            if (!hasLowLatencyEnhancements()) {
                return this;
            }
            return new ResolvedConfig(
                    targetFps,
                    effectiveFps,
                    frameRateInt,
                    includeMaxFpsToEncoder,
                    requestedBitRateBps,
                    clampedBitRateBps,
                    minSupportedBitRateBps,
                    maxSupportedBitRateBps,
                    iFrameIntervalSec,
                    repeatPreviousFrameAfterUs,
                    wifiTransport,
                    false,
                    false,
                    false,
                    0);
        }
    }

    /**
     * Thread-safe monotonic rate limiter for sync-frame requests using {@code System.nanoTime()}
     * semantics while avoiding boot-near-zero and signed wraparound pitfalls.
     */
    public static final class SyncRequestGate {
        private final long minIntervalNs;
        private boolean hasLastRequest;
        private long lastRequestNs;

        public SyncRequestGate() {
            this(SYNC_FRAME_MIN_INTERVAL_NS);
        }

        public SyncRequestGate(long minIntervalNs) {
            this.minIntervalNs = Math.max(0L, minIntervalNs);
        }

        public synchronized boolean tryAcquire(long nowNs) {
            if (hasLastRequest && (nowNs - lastRequestNs) < minIntervalNs) {
                return false;
            }
            hasLastRequest = true;
            lastRequestNs = nowNs;
            return true;
        }

        public synchronized void reset() {
            hasLastRequest = false;
            lastRequestNs = 0L;
        }
    }

    /**
     * Tracks monotonic stream PTS against wall-clock {@code System.nanoTime()} to compute
     * non-negative {@code frameAgeUs} while absorbing startup latency and guarding clock jumps.
     */
    public static final class StreamClockTracker {
        private static final long MAX_BACKWARD_PTS_JUMP_US = 500_000L;
        private static final long MAX_SANE_FRAME_AGE_US = 60_000_000L;

        private boolean initialized;
        private long firstPtsUs;
        private long firstSystemNs;
        private long lastPtsUs;

        public void reset() {
            initialized = false;
            firstPtsUs = 0L;
            firstSystemNs = 0L;
            lastPtsUs = 0L;
        }

        public long updateAndComputeFrameAgeUs(long ptsUs, long nowNs, boolean isConfig) {
            return updateAndComputeFrameAgeUs(ptsUs, nowNs, nowNs, isConfig);
        }

        public long updateAndComputeFrameAgeUs(long ptsUs, long writeStartNs, long nowNs, boolean isConfig) {
            if (isConfig) {
                return 0L;
            }
            if (!initialized || ptsUs < firstPtsUs || ptsUs < lastPtsUs - MAX_BACKWARD_PTS_JUMP_US) {
                initialized = true;
                firstPtsUs = ptsUs;
                firstSystemNs = writeStartNs;
                lastPtsUs = ptsUs;
                return Math.max(0L, (nowNs - writeStartNs) / 1000L);
            }
            lastPtsUs = ptsUs;
            long elapsedSystemUs = Math.max(0L, (nowNs - firstSystemNs) / 1000L);
            long elapsedPtsUs = ptsUs - firstPtsUs;
            long ageUs = elapsedSystemUs - elapsedPtsUs;
            if (ageUs < 0L || ageUs > MAX_SANE_FRAME_AGE_US) {
                firstPtsUs = ptsUs;
                firstSystemNs = nowNs;
                return 0L;
            }
            return ageUs;
        }
    }

    /**
     * Resolves requested {@code maxFps} into a valid target FPS, defaulting to 60 when unset ({@code <= 0}).
     */
    public static float resolveTargetFps(float requestedMaxFps) {
        if (!Float.isFinite(requestedMaxFps) || requestedMaxFps <= 0.0f) {
            return DEFAULT_FRAME_RATE;
        }
        return requestedMaxFps;
    }

    /**
     * Clamps target frame rate safely within the encoder's supported range for the active size.
     */
    public static float clampFrameRate(
            float targetFps,
            boolean sizeAndRateSupported,
            double supportedMinFps,
            double supportedMaxFps) {
        float effective = resolveTargetFps(targetFps);
        boolean hasMax = Double.isFinite(supportedMaxFps) && supportedMaxFps > 0.0;
        boolean hasMin = Double.isFinite(supportedMinFps) && supportedMinFps > 0.0;

        if (hasMax && effective > supportedMaxFps) {
            effective = (float) supportedMaxFps;
        }
        if (!sizeAndRateSupported && hasMin && effective < supportedMinFps && (!hasMax || supportedMinFps <= supportedMaxFps)) {
            effective = (float) supportedMinFps;
        }
        return effective;
    }

    /**
     * Converts a clamped floating-point frame rate to an integer {@code MediaFormat.KEY_FRAME_RATE}
     * without rounding above {@code supportedMaxFps}.
     */
    public static int toEncoderFrameRateInt(float effectiveFps, double supportedMaxFps) {
        float safe = resolveTargetFps(effectiveFps);
        int rounded = Math.max(1, Math.round(safe));
        if (Double.isFinite(supportedMaxFps) && supportedMaxFps >= 1.0 && rounded > supportedMaxFps) {
            return Math.max(1, (int) Math.floor(supportedMaxFps));
        }
        return rounded;
    }

    /**
     * Computes adaptive balanced I-frame interval in seconds: 2s default for USB/Wi-Fi,
     * or 3s when frame rate is below 24 fps.
     */
    public static int computeDefaultIFrameIntervalSec(float effectiveFps, boolean isWifiTransport) {
        float safe = resolveTargetFps(effectiveFps);
        if (safe < LOW_FPS_THRESHOLD) {
            return LOW_FPS_I_FRAME_INTERVAL_SEC;
        }
        return DEFAULT_I_FRAME_INTERVAL_SEC;
    }

    /**
     * Computes {@code KEY_REPEAT_PREVIOUS_FRAME_AFTER} in microseconds: at least 100ms and
     * at least two frame periods at the effective FPS.
     */
    public static long computeRepeatPreviousFrameAfterUs(float effectiveFps) {
        float safe = resolveTargetFps(effectiveFps);
        return Math.max(MIN_REPEAT_PREVIOUS_FRAME_AFTER_US, (long) (1_000_000L / safe) * 2L);
    }

    /**
     * Clamps requested video bitrate into encoder-supported {@code [minSupportedBps, maxSupportedBps]}.
     */
    public static int clampBitrate(int requestedBitRateBps, int minSupportedBps, int maxSupportedBps) {
        int safeMin = Math.max(1, minSupportedBps);
        int safeMax = Math.max(safeMin, maxSupportedBps);
        int target = requestedBitRateBps > 0 ? requestedBitRateBps : DEFAULT_FALLBACK_BITRATE_BPS;
        return Math.max(safeMin, Math.min(safeMax, target));
    }

    /**
     * Picks the first smaller fallback max-side from {@code fallbacks} whose scaled, 8-pixel-aligned
     * dimensions satisfy {@code supportCheck}.
     */
    public static FallbackSelection pickSupportedFallbackMaxSize(
            int width,
            int height,
            int currentMaxSide,
            int startIndex,
            int[] fallbacks,
            SizeSupportPredicate supportCheck) {
        if (width <= 0 || height <= 0 || fallbacks == null || supportCheck == null) {
            return null;
        }
        for (int i = Math.max(0, startIndex); i < fallbacks.length; ++i) {
            int candidateMax = fallbacks[i];
            if (candidateMax >= currentMaxSide) {
                continue;
            }
            Size scaled = new Size(width, height).limitMajor(candidateMax).round8();
            if (supportCheck.isSupported(scaled.getWidth(), scaled.getHeight())) {
                return new FallbackSelection(candidateMax, scaled.getWidth(), scaled.getHeight(), i + 1);
            }
        }
        return null;
    }

    /**
     * Resolves complete encoder configuration from pure capability inputs (no Android runtime objects).
     */
    public static ResolvedConfig resolve(
            int sdkInt,
            float requestedMaxFps,
            int requestedBitRateBps,
            boolean isWifiTransport,
            boolean sizeAndRateSupported,
            double supportedMinFps,
            double supportedMaxFps,
            int minSupportedBitRateBps,
            int maxSupportedBitRateBps,
            boolean lowLatencyFeatureSupported) {
        boolean includeMaxFps = Float.isFinite(requestedMaxFps) && requestedMaxFps > 0.0f;
        float targetFps = resolveTargetFps(requestedMaxFps);
        float effectiveFps = clampFrameRate(targetFps, sizeAndRateSupported, supportedMinFps, supportedMaxFps);
        int frameRateInt = toEncoderFrameRateInt(effectiveFps, supportedMaxFps);

        int safeMinBps = minSupportedBitRateBps > 0
                ? minSupportedBitRateBps
                : Math.min(requestedBitRateBps > 0 ? requestedBitRateBps : DEFAULT_MIN_BITRATE_BPS, DEFAULT_MIN_BITRATE_BPS);
        int safeMaxBps = maxSupportedBitRateBps > 0
                ? Math.max(safeMinBps, maxSupportedBitRateBps)
                : Math.max(safeMinBps, Math.max(requestedBitRateBps, DEFAULT_MAX_BITRATE_BPS));
        int clampedBitRate = clampBitrate(requestedBitRateBps, safeMinBps, safeMaxBps);

        int iFrameIntervalSec = computeDefaultIFrameIntervalSec(effectiveFps, isWifiTransport);
        long repeatAfterUs = computeRepeatPreviousFrameAfterUs(effectiveFps);

        boolean useLowLatency = sdkInt >= 30 && lowLatencyFeatureSupported;
        boolean useRealtimePriority = sdkInt >= 23;
        boolean useZeroBFrames = sdkInt >= 29;
        int operatingRateFps = (sdkInt >= 23 && effectiveFps > 60.0f) ? Math.max(1, Math.round(effectiveFps)) : 0;

        return new ResolvedConfig(
                targetFps,
                effectiveFps,
                frameRateInt,
                includeMaxFps,
                requestedBitRateBps,
                clampedBitRate,
                safeMinBps,
                safeMaxBps,
                iFrameIntervalSec,
                repeatAfterUs,
                isWifiTransport,
                useLowLatency,
                useRealtimePriority,
                useZeroBFrames,
                operatingRateFps);
    }

    /**
     * Resolves encoder configuration by querying {@link MediaCodec} capabilities for the given size.
     */
    public static ResolvedConfig resolveForCodec(
            MediaCodecInfo.CodecCapabilities caps,
            int width,
            int height,
            float requestedMaxFps,
            int requestedBitRateBps,
            boolean isWifiTransport) {
        MediaCodecInfo.VideoCapabilities videoCaps = caps != null ? caps.getVideoCapabilities() : null;
        float targetFps = resolveTargetFps(requestedMaxFps);
        boolean sizeAndRateSupported = true;
        double supportedMinFps = 0.0;
        double supportedMaxFps = 0.0;
        int minBitRate = Math.min(requestedBitRateBps > 0 ? requestedBitRateBps : DEFAULT_MIN_BITRATE_BPS, DEFAULT_MIN_BITRATE_BPS);
        int maxBitRate = Math.max(requestedBitRateBps, DEFAULT_MAX_BITRATE_BPS);

        if (videoCaps != null) {
            try {
                sizeAndRateSupported = videoCaps.areSizeAndRateSupported(width, height, targetFps);
            } catch (IllegalArgumentException ignored) {
                sizeAndRateSupported = false;
            }
            Range<Double> rateRange = null;
            try {
                rateRange = videoCaps.getSupportedFrameRatesFor(width, height);
            } catch (IllegalArgumentException ignored) {
            }
            if (rateRange != null) {
                supportedMinFps = rateRange.getLower();
                supportedMaxFps = rateRange.getUpper();
            } else {
                Range<Integer> globalRates = videoCaps.getSupportedFrameRates();
                if (globalRates != null) {
                    supportedMinFps = globalRates.getLower();
                    supportedMaxFps = globalRates.getUpper();
                }
            }
            Range<Integer> bitrateRange = videoCaps.getBitrateRange();
            if (bitrateRange != null) {
                minBitRate = Math.max(1, bitrateRange.getLower());
                maxBitRate = Math.max(minBitRate, bitrateRange.getUpper());
            }
        }

        boolean lowLatencySupported = caps != null
                && Build.VERSION.SDK_INT >= 30
                && caps.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency);

        return resolve(
                Build.VERSION.SDK_INT,
                requestedMaxFps,
                requestedBitRateBps,
                isWifiTransport,
                sizeAndRateSupported,
                supportedMinFps,
                supportedMaxFps,
                minBitRate,
                maxBitRate,
                lowLatencySupported);
    }

    public static MediaCodecInfo.CodecCapabilities getCapabilitiesQuietly(MediaCodec codec, String mime) {
        if (codec == null || mime == null) {
            return null;
        }
        try {
            MediaCodecInfo info = codec.getCodecInfo();
            return info != null ? info.getCapabilitiesForType(mime) : null;
        } catch (IllegalArgumentException | IllegalStateException ignored) {
            return null;
        }
    }

    public static boolean isSizeSupportedSafe(MediaCodecInfo.VideoCapabilities videoCaps, int width, int height) {
        if (videoCaps == null) {
            return true;
        }
        try {
            return videoCaps.isSizeSupported(width, height);
        } catch (IllegalArgumentException ignored) {
            return false;
        }
    }

    /**
     * Populates base parameters, optional low-latency enhancements, and user {@code codecOptions}
     * (in precedence order so explicit user options always win) into {@code sink}.
     */
    public static void populateFormat(
            ResolvedConfig config,
            String mime,
            int width,
            int height,
            int sdkInt,
            List<MediaCodecParams.Entry> codecOptions,
            FormatConsumer sink) {
        sink.setString(MediaFormat.KEY_MIME, mime);
        if (MediaFormat.MIMETYPE_VIDEO_HEVC.equals(mime)) {
            sink.setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.HEVCProfileMain);
        }
        sink.setInteger(MediaFormat.KEY_WIDTH, width);
        sink.setInteger(MediaFormat.KEY_HEIGHT, height);
        sink.setInteger(MediaFormat.KEY_BIT_RATE, config.getClampedBitRateBps());
        sink.setInteger(MediaFormat.KEY_FRAME_RATE, config.getFrameRateInt());
        sink.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        if (sdkInt >= 24) {
            sink.setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED);
        }
        sink.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, config.getIFrameIntervalSec());
        sink.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, config.getRepeatPreviousFrameAfterUs());
        if (config.isIncludeMaxFpsToEncoder()) {
            sink.setFloat(KEY_MAX_FPS_TO_ENCODER, config.getEffectiveFps());
        }

        if (config.isUseLowLatency()) {
            sink.setInteger(KEY_LOW_LATENCY, 1);
        }
        if (config.isUseRealtimePriority()) {
            sink.setInteger(KEY_PRIORITY, 0);
        }
        if (config.isUseZeroBFrames()) {
            sink.setInteger(KEY_MAX_B_FRAMES, 0);
        }
        if (config.getOperatingRateFps() > 0) {
            sink.setInteger(KEY_OPERATING_RATE, config.getOperatingRateFps());
        }

        applyOptionsToSink(sink, codecOptions);
    }

    public static MediaFormat buildMediaFormat(
            ResolvedConfig config,
            String mime,
            int width,
            int height,
            List<MediaCodecParams.Entry> codecOptions) {
        final MediaFormat format = new MediaFormat();
        populateFormat(config, mime, width, height, Build.VERSION.SDK_INT, null, new FormatConsumer() {
            @Override
            public void setInteger(String key, int value) {
                format.setInteger(key, value);
            }

            @Override
            public void setLong(String key, long value) {
                format.setLong(key, value);
            }

            @Override
            public void setFloat(String key, float value) {
                format.setFloat(key, value);
            }

            @Override
            public void setString(String key, String value) {
                format.setString(key, value);
            }
        });
        MediaCodecParams.apply(format, codecOptions);
        return format;
    }

    private static void applyOptionsToSink(FormatConsumer sink, List<MediaCodecParams.Entry> options) {
        if (options == null || options.isEmpty()) {
            return;
        }
        for (MediaCodecParams.Entry entry : options) {
            Object v = entry.getValue();
            if (v instanceof Integer) {
                sink.setInteger(entry.getKey(), (Integer) v);
            } else if (v instanceof Long) {
                sink.setLong(entry.getKey(), (Long) v);
            } else if (v instanceof Float) {
                sink.setFloat(entry.getKey(), (Float) v);
            } else if (v instanceof String) {
                sink.setString(entry.getKey(), (String) v);
            }
        }
    }
}
