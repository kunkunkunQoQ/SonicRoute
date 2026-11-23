// SPDX-License-Identifier: GPL-3.0-only
package org.sonicroute.android.video;

import java.util.Locale;
import java.util.Objects;
import java.util.function.IntPredicate;
import org.sonicroute.android.util.ServerLog;

/**
 * Event-driven runtime adaptive video bitrate controller prioritizing low latency over queue buildup.
 * Runs entirely in the packet-write callback with O(1) arithmetic, zero threads, and zero per-packet allocations.
 */
public final class AdaptiveBitrateController {
    public enum TransportMode {
        USB,
        WIFI
    }

    private static final float DEFAULT_FPS = 60.0f;
    private static final int MIN_DEADBAND_BPS = 100_000;
    private static final float DEADBAND_RATIO = 0.05f;
    private static final int MIN_SAMPLES_FOR_TREND = 3;

    private static final double EWMA_ALPHA_NORMAL = 0.30;
    private static final double EWMA_ALPHA_KEYFRAME = 0.14;
    private static final double EWMA_ALPHA_THROUGHPUT_DOWN = 0.32;
    private static final double EWMA_ALPHA_THROUGHPUT_UP = 0.15;

    private static final class TransportPolicy {
        final double floorFactor;
        final int minFloorBps;
        final double downshiftFactor;
        final double emergencyDownshiftFactor;
        final double throughputSafetyScale;
        final double minStepFloorFactor;
        final double upshiftFactor;
        final long normalDownshiftCooldownNs;
        final long emergencyCooldownNs;
        final long upshiftStabilityWindowNs;
        final double highWriteRatio;
        final double highWriteRatioMinMs;
        final double highWriteMs;
        final double highFrameAgeMs;
        final long emergencyWriteNs;
        final double emergencyFrameAgeMs;
        final double lowWriteRatio;
        final double lowFrameAgeMs;

        TransportPolicy(
                double floorFactor,
                int minFloorBps,
                double downshiftFactor,
                double emergencyDownshiftFactor,
                double throughputSafetyScale,
                double minStepFloorFactor,
                double upshiftFactor,
                long normalDownshiftCooldownNs,
                long emergencyCooldownNs,
                long upshiftStabilityWindowNs,
                double highWriteRatio,
                double highWriteRatioMinMs,
                double highWriteMs,
                double highFrameAgeMs,
                long emergencyWriteNs,
                double emergencyFrameAgeMs,
                double lowWriteRatio,
                double lowFrameAgeMs) {
            this.floorFactor = floorFactor;
            this.minFloorBps = minFloorBps;
            this.downshiftFactor = downshiftFactor;
            this.emergencyDownshiftFactor = emergencyDownshiftFactor;
            this.throughputSafetyScale = throughputSafetyScale;
            this.minStepFloorFactor = minStepFloorFactor;
            this.upshiftFactor = upshiftFactor;
            this.normalDownshiftCooldownNs = normalDownshiftCooldownNs;
            this.emergencyCooldownNs = emergencyCooldownNs;
            this.upshiftStabilityWindowNs = upshiftStabilityWindowNs;
            this.highWriteRatio = highWriteRatio;
            this.highWriteRatioMinMs = highWriteRatioMinMs;
            this.highWriteMs = highWriteMs;
            this.highFrameAgeMs = highFrameAgeMs;
            this.emergencyWriteNs = emergencyWriteNs;
            this.emergencyFrameAgeMs = emergencyFrameAgeMs;
            this.lowWriteRatio = lowWriteRatio;
            this.lowFrameAgeMs = lowFrameAgeMs;
        }
    }

    private static final TransportPolicy USB_POLICY = new TransportPolicy(
            0.25,
            500_000,
            0.80,
            0.72,
            0.90,
            0.50,
            1.12,
            600_000_000L,
            250_000_000L,
            2_500_000_000L,
            0.55,
            6.0,
            30.0,
            65.0,
            120_000_000L,
            120.0,
            0.25,
            30.0);

    private static final TransportPolicy WIFI_POLICY = new TransportPolicy(
            0.15,
            400_000,
            0.72,
            0.62,
            0.85,
            0.45,
            1.08,
            450_000_000L,
            200_000_000L,
            3_500_000_000L,
            0.42,
            4.5,
            22.0,
            50.0,
            90_000_000L,
            90.0,
            0.18,
            24.0);

    private final TransportMode transportMode;
    private final TransportPolicy policy;
    private final boolean enabled;

    private IntPredicate bitrateApplier;
    private int baselineBitRateBps;
    private int minBitRateBps;
    private int maxBitRateBps;
    private int currentBitRateBps;
    private float effectiveFps = DEFAULT_FPS;
    private double frameIntervalNs = 1_000_000_000.0 / DEFAULT_FPS;
    private double frameIntervalMs = frameIntervalNs / 1_000_000.0;

    private boolean dynamicSupported;
    private int adjustmentCount;
    private int downshiftCount;
    private int upshiftCount;

    private boolean hasSample;
    private int sampleCount;
    private boolean hasLastAdjust;
    private long lastAdjustNs;
    private boolean hasLowPressureStart;
    private long lowPressureStartNs;

    private double smoothedWriteRatio;
    private double smoothedWriteMs;
    private double smoothedFrameAgeMs;
    private double smoothedThroughputBps;

    public AdaptiveBitrateController(TransportMode transportMode, boolean enabled) {
        this.transportMode = Objects.requireNonNull(transportMode, "transportMode");
        this.policy = transportMode == TransportMode.WIFI ? WIFI_POLICY : USB_POLICY;
        this.enabled = enabled;
    }

    /**
     * Initializes or resets ABR state when a video encoder session starts.
     * Matches {@code VideoEncoderRunner.RuntimeBitrateHook#onSessionStarted}.
     */
    public synchronized void resetSession(
            int initialBitRateBps,
            int minSupportedBps,
            int maxSupportedBps,
            float effectiveFps,
            IntPredicate bitrateApplier) {
        int safeMinSupported = Math.max(1, minSupportedBps);
        int safeMaxSupported = maxSupportedBps > 0 ? Math.max(safeMinSupported, maxSupportedBps) : Integer.MAX_VALUE;
        int clampedBaseline = clamp(Math.max(1, initialBitRateBps), safeMinSupported, safeMaxSupported);
        int policyFloor = Math.max(policy.minFloorBps, (int) Math.round(clampedBaseline * policy.floorFactor));
        int effectiveFloor = Math.max(safeMinSupported, policyFloor);

        this.baselineBitRateBps = clampedBaseline;
        this.maxBitRateBps = clampedBaseline;
        this.minBitRateBps = Math.min(this.maxBitRateBps, effectiveFloor);
        this.currentBitRateBps = clampedBaseline;

        this.effectiveFps = (Float.isFinite(effectiveFps) && effectiveFps > 0.0f)
                ? Math.min(240.0f, Math.max(1.0f, effectiveFps))
                : DEFAULT_FPS;
        this.frameIntervalNs = 1_000_000_000.0 / this.effectiveFps;
        this.frameIntervalMs = this.frameIntervalNs / 1_000_000.0;

        this.bitrateApplier = bitrateApplier;
        this.dynamicSupported = enabled && bitrateApplier != null;
        this.adjustmentCount = 0;
        this.downshiftCount = 0;
        this.upshiftCount = 0;

        this.hasSample = false;
        this.sampleCount = 0;
        this.hasLastAdjust = false;
        this.lastAdjustNs = 0L;
        this.hasLowPressureStart = false;
        this.lowPressureStartNs = 0L;

        this.smoothedWriteRatio = 0.0;
        this.smoothedWriteMs = 0.0;
        this.smoothedFrameAgeMs = 0.0;
        this.smoothedThroughputBps = clampedBaseline;
    }

    /**
     * Updates runtime pressure metrics from a completed packet write and adjusts encoder bitrate when warranted.
     * Matches {@code VideoEncoderRunner.RuntimeBitrateHook#onPacketWritten}.
     */
    public synchronized void onPacketSent(
            int packetBytes,
            long writeDurationNs,
            long frameAgeUs,
            boolean isKeyFrame,
            boolean isConfig,
            long nowNs) {
        if (!enabled || !dynamicSupported || bitrateApplier == null || isConfig || packetBytes <= 0 || maxBitRateBps <= 0) {
            return;
        }

        final long safeWriteNs = Math.max(0L, writeDurationNs);
        final long safeFrameAgeUs = Math.max(0L, frameAgeUs);
        final double writeMs = safeWriteNs / 1_000_000.0;
        final double frameAgeMs = safeFrameAgeUs / 1_000.0;
        final double writeRatio = safeWriteNs / frameIntervalNs;
        final double clampedWriteRatio = Math.min(writeRatio, 8.0);
        final boolean writeBlocked = (writeRatio >= 0.40 && safeWriteNs >= 2_000_000L) || safeWriteNs >= 15_000_000L;

        final double observedThroughputBps;
        if (writeBlocked) {
            double rawBps = (packetBytes * 8.0 * 1_000_000_000.0) / Math.max(1L, safeWriteNs);
            observedThroughputBps = Math.max(100_000.0, Math.min(maxBitRateBps * 2.0, rawBps));
        } else {
            double nominalPacketBps = (packetBytes * 8.0 * 1_000_000_000.0) / Math.max(1.0, frameIntervalNs);
            observedThroughputBps = Math.min(maxBitRateBps * 1.5, Math.max(currentBitRateBps * 1.25, nominalPacketBps));
        }

        if (!hasSample) {
            hasSample = true;
            sampleCount = 1;
            if (isKeyFrame) {
                // Keep a single initial IDR frame from triggering a premature non-emergency downshift.
                smoothedWriteRatio = Math.min(clampedWriteRatio, 0.30);
                smoothedWriteMs = Math.min(writeMs, 10.0);
                smoothedFrameAgeMs = Math.min(frameAgeMs, 40.0);
                smoothedThroughputBps = currentBitRateBps;
            } else {
                smoothedWriteRatio = clampedWriteRatio;
                smoothedWriteMs = writeMs;
                smoothedFrameAgeMs = frameAgeMs;
                smoothedThroughputBps = observedThroughputBps;
            }
        } else {
            sampleCount++;
            final double alpha = isKeyFrame ? EWMA_ALPHA_KEYFRAME : EWMA_ALPHA_NORMAL;
            smoothedWriteRatio += alpha * (clampedWriteRatio - smoothedWriteRatio);
            smoothedWriteMs += alpha * (writeMs - smoothedWriteMs);
            smoothedFrameAgeMs += alpha * (frameAgeMs - smoothedFrameAgeMs);
            final double tpAlpha = writeBlocked ? EWMA_ALPHA_THROUGHPUT_DOWN : EWMA_ALPHA_THROUGHPUT_UP;
            smoothedThroughputBps += tpAlpha * (observedThroughputBps - smoothedThroughputBps);
        }

        final boolean emergency = (safeWriteNs > policy.emergencyWriteNs) || (frameAgeMs > policy.emergencyFrameAgeMs);
        final boolean writeCongested = (smoothedWriteRatio >= policy.highWriteRatio && smoothedWriteMs >= policy.highWriteRatioMinMs)
                || smoothedWriteMs >= policy.highWriteMs;
        final boolean highPressure = emergency
                || (sampleCount >= MIN_SAMPLES_FOR_TREND && (writeCongested || smoothedFrameAgeMs >= policy.highFrameAgeMs));

        if (highPressure) {
            hasLowPressureStart = false;
            if (currentBitRateBps <= minBitRateBps) {
                return;
            }
            final long cooldownNs = emergency ? policy.emergencyCooldownNs : policy.normalDownshiftCooldownNs;
            if (hasLastAdjust && (nowNs - lastAdjustNs) < cooldownNs) {
                return;
            }
            final double factor = emergency ? policy.emergencyDownshiftFactor : policy.downshiftFactor;
            int target = (int) Math.round(currentBitRateBps * factor);
            if (writeCongested || safeWriteNs > policy.emergencyWriteNs) {
                int throughputTarget = (int) Math.round(smoothedThroughputBps * policy.throughputSafetyScale);
                if (throughputTarget > 0 && throughputTarget < target) {
                    int minStepTarget = (int) Math.round(currentBitRateBps * policy.minStepFloorFactor);
                    target = Math.max(minStepTarget, throughputTarget);
                }
            }
            target = clamp(target, minBitRateBps, maxBitRateBps);
            if (target < currentBitRateBps) {
                tryApplyBitrate(target, nowNs, false, emergency ? "emergency" : "congestion");
            }
            return;
        }

        final boolean lowPressure = smoothedWriteRatio <= policy.lowWriteRatio
                && smoothedWriteMs <= Math.max(4.0, frameIntervalMs * policy.lowWriteRatio)
                && smoothedFrameAgeMs <= policy.lowFrameAgeMs;
        if (!lowPressure || currentBitRateBps >= maxBitRateBps) {
            // Do not break stability recovery on a single periodic non-congesting keyframe.
            if (!(isKeyFrame && hasLowPressureStart && currentBitRateBps < maxBitRateBps)) {
                hasLowPressureStart = false;
            }
            return;
        }

        if (!hasLowPressureStart || nowNs < lowPressureStartNs) {
            hasLowPressureStart = true;
            lowPressureStartNs = nowNs;
            return;
        }

        if ((nowNs - lowPressureStartNs) >= policy.upshiftStabilityWindowNs
                && (!hasLastAdjust || (nowNs - lastAdjustNs) >= policy.upshiftStabilityWindowNs)) {
            int minDelta = computeMinDeltaBps(currentBitRateBps);
            int scaledTarget = (int) Math.round(currentBitRateBps * policy.upshiftFactor);
            int target = Math.min(maxBitRateBps, Math.max(currentBitRateBps + minDelta, scaledTarget));
            if (tryApplyBitrate(target, nowNs, true, "recovery")) {
                lowPressureStartNs = nowNs;
            }
        }
    }

    private boolean tryApplyBitrate(int targetBitrate, long nowNs, boolean upshift, String reason) {
        int clamped = clamp(targetBitrate, minBitRateBps, maxBitRateBps);
        if (!shouldApplyBitrateDelta(currentBitRateBps, clamped, minBitRateBps, maxBitRateBps)) {
            return false;
        }
        final int prev = currentBitRateBps;
        boolean accepted;
        try {
            accepted = bitrateApplier.test(clamped);
        } catch (RuntimeException error) {
            accepted = false;
        }
        if (!accepted) {
            dynamicSupported = false;
            ServerLog.w("Video ABR [" + transportMode + "]: runtime bitrate change rejected at "
                    + clamped + " bps, falling back to fixed " + prev + " bps");
            return false;
        }
        currentBitRateBps = clamped;
        lastAdjustNs = nowNs;
        hasLastAdjust = true;
        adjustmentCount++;
        if (upshift) {
            upshiftCount++;
        } else {
            downshiftCount++;
        }
        ServerLog.i("Video ABR [" + transportMode + "]: " + prev + " -> " + clamped + " bps ("
                + reason + ", writeRatio=" + String.format(Locale.ENGLISH, "%.2f", smoothedWriteRatio)
                + ", frameAgeMs=" + String.format(Locale.ENGLISH, "%.1f", smoothedFrameAgeMs) + ")");
        return true;
    }

    public static boolean shouldApplyBitrateDelta(int currentBps, int targetBps, int minBps, int maxBps) {
        if (targetBps == currentBps) {
            return false;
        }
        if (targetBps == minBps || targetBps == maxBps) {
            return true;
        }
        return Math.abs(targetBps - currentBps) >= computeMinDeltaBps(currentBps);
    }

    private static int computeMinDeltaBps(int currentBps) {
        return Math.max(MIN_DEADBAND_BPS, Math.round(currentBps * DEADBAND_RATIO));
    }

    private static int clamp(int value, int min, int max) {
        return Math.max(min, Math.min(max, value));
    }

    public TransportMode getTransportMode() {
        return transportMode;
    }

    public boolean isEnabled() {
        return enabled;
    }

    public synchronized int getBaselineBitRateBps() {
        return baselineBitRateBps;
    }

    public synchronized int getMinBitRateBps() {
        return minBitRateBps;
    }

    public synchronized int getMaxBitRateBps() {
        return maxBitRateBps;
    }

    public synchronized int getCurrentBitRateBps() {
        return currentBitRateBps;
    }

    public synchronized int getAdjustmentCount() {
        return adjustmentCount;
    }

    public synchronized int getDownshiftCount() {
        return downshiftCount;
    }

    public synchronized int getUpshiftCount() {
        return upshiftCount;
    }

    public synchronized boolean isDynamicSupported() {
        return dynamicSupported;
    }

    public synchronized double getSmoothedWriteRatio() {
        return smoothedWriteRatio;
    }

    public synchronized double getSmoothedWriteDurationMs() {
        return smoothedWriteMs;
    }

    public synchronized double getSmoothedFrameAgeMs() {
        return smoothedFrameAgeMs;
    }

    public synchronized double getSmoothedThroughputBps() {
        return smoothedThroughputBps;
    }
}
