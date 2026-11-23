// SPDX-License-Identifier: GPL-3.0-only
package org.sonicroute.android.video;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.os.Bundle;
import android.view.Surface;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.List;
import java.util.function.IntPredicate;
import org.sonicroute.android.core.MediaCodecParams;
import org.sonicroute.android.io.PacketWriter;
import org.sonicroute.android.model.Geometry.Size;
import org.sonicroute.android.util.ServerLog;
import org.sonicroute.android.video.VideoCaptureSource.ResetCoordinator;

/**
 * Surface encoder loop for H.264 and HEVC with capability clamping, low-latency fallback,
 * dynamic resize, sync-frame control, and runtime adaptive bitrate callbacks.
 * Reads encoded NALUs via blocking {@code MediaCodec.dequeueOutputBuffer(-1)}.
 */
public final class VideoEncoderRunner {
    /**
     * Runtime callback hook notified when an encoder session starts and after each packet write.
     */
    public interface RuntimeBitrateHook {
        void onSessionStarted(
                int initialBitRateBps,
                int minSupportedBps,
                int maxSupportedBps,
                float effectiveFps,
                IntPredicate bitrateApplier);

        void onPacketWritten(
                int packetBytes,
                long writeDurationNs,
                long frameAgeUs,
                boolean isKeyFrame,
                boolean isConfig,
                long nowNs);
    }

    private static final int[] DOWNSIZE_FALLBACKS = VideoEncoderConfig.DEFAULT_DOWNSIZE_FALLBACKS;

    private final VideoCaptureSource capture;
    private final PacketWriter writer;
    private final int bitRate;
    private final float maxFps;
    private final List<MediaCodecParams.Entry> codecOptions;
    private final boolean downsizeOnError;
    private final boolean isWifiTransport;
    private final RuntimeBitrateHook bitrateHook;
    private final Runnable onFinish;

    private final Object codecLock = new Object();
    private final VideoEncoderConfig.SyncRequestGate syncRequestGate = new VideoEncoderConfig.SyncRequestGate();

    private volatile boolean stopped;
    private int codecId;
    private String encoderName;
    private boolean headerSent;
    private MediaCodec activeCodec;
    private Thread thread;

    public VideoEncoderRunner(
            VideoCaptureSource capture,
            PacketWriter writer,
            int codecId,
            int bitRate,
            float maxFps,
            String explicitEncoder,
            List<MediaCodecParams.Entry> codecOptions,
            boolean downsizeOnError,
            Runnable onFinish) {
        this(
                capture,
                writer,
                codecId,
                bitRate,
                maxFps,
                explicitEncoder,
                codecOptions,
                downsizeOnError,
                false,
                null,
                onFinish);
    }

    public VideoEncoderRunner(
            VideoCaptureSource capture,
            PacketWriter writer,
            int codecId,
            int bitRate,
            float maxFps,
            String explicitEncoder,
            List<MediaCodecParams.Entry> codecOptions,
            boolean downsizeOnError,
            boolean isWifiTransport,
            RuntimeBitrateHook bitrateHook,
            Runnable onFinish) {
        this.capture = capture;
        this.writer = writer;
        this.codecId = codecId;
        this.bitRate = bitRate;
        this.maxFps = maxFps;
        this.encoderName = explicitEncoder;
        this.codecOptions = codecOptions;
        this.downsizeOnError = downsizeOnError;
        this.isWifiTransport = isWifiTransport;
        this.bitrateHook = bitrateHook;
        this.onFinish = onFinish;
    }

    public void start() {
        thread = new Thread(this::runSafe, "SonicRoute-Video");
        thread.start();
    }

    public void stop() {
        stopped = true;
        capture.requestReset();
    }

    public void join() throws InterruptedException {
        if (thread != null) {
            thread.join();
        }
    }

    /**
     * Requests an immediate IDR/sync frame from the active encoder session, rate-limited to at most
     * one request every 250 ms. Thread-safe across control and encoder threads.
     */
    public boolean requestSyncFrame() {
        synchronized (codecLock) {
            if (stopped || activeCodec == null) {
                return false;
            }
            if (!syncRequestGate.tryAcquire(System.nanoTime())) {
                return false;
            }
            return sendSyncFrameParameter(activeCodec);
        }
    }

    private void runSafe() {
        try {
            streamLoop();
        } catch (IOException error) {
            if (!stopped) {
                ServerLog.d("Video stream ended: " + error.getMessage());
            }
        } catch (Throwable error) {
            ServerLog.e("Video encoder failed", error);
        } finally {
            if (onFinish != null) {
                onFinish.run();
            }
        }
    }

    private void streamLoop() throws IOException {
        capture.init();
        ResetCoordinator reset = capture.getResetCoordinator();
        try {
            boolean alive;
            do {
                if (stopped || capture.isClosed()) {
                    break;
                }
                reset.consumeReset();
                capture.prepare();
                Size videoSize = capture.getVideoSize();
                if (videoSize == null) {
                    throw new IOException("Capture size unavailable");
                }
                alive = encodeSingleSession(videoSize, reset);
            } while (alive && !stopped);
        } finally {
            capture.release();
        }
    }

    private boolean encodeSingleSession(Size videoSize, ResetCoordinator reset) throws IOException {
        String mime = codecId == PacketWriter.CODEC_ID_H265
                ? MediaFormat.MIMETYPE_VIDEO_HEVC
                : MediaFormat.MIMETYPE_VIDEO_AVC;
        int currentMaxSide = videoSize.getMaxSide();
        int fallbackIdx = 0;

        while (!stopped) {
            MediaCodec codec = null;
            Surface surface = null;
            boolean codecStarted = false;
            boolean captureStarted = false;
            Size activeSize = capture.getVideoSize();
            if (activeSize == null) {
                throw new IOException("Capture size unavailable");
            }
            try {
                codec = MediaCodecParams.createEncoder(mime, encoderName);
                MediaCodecInfo.CodecCapabilities caps = VideoEncoderConfig.getCapabilitiesQuietly(codec, mime);
                MediaCodecInfo.VideoCapabilities videoCaps = caps != null ? caps.getVideoCapabilities() : null;

                int width = activeSize.getWidth();
                int height = activeSize.getHeight();
                if (videoCaps != null && !VideoEncoderConfig.isSizeSupportedSafe(videoCaps, width, height)) {
                    ServerLog.w("Encoder " + safeCodecName(codec, mime) + " reports unsupported size "
                            + activeSize + " for " + mime);
                    if (downsizeOnError && (headerSent || codecId != PacketWriter.CODEC_ID_H265)) {
                        VideoEncoderConfig.FallbackSelection selection = VideoEncoderConfig.pickSupportedFallbackMaxSize(
                                width,
                                height,
                                currentMaxSide,
                                fallbackIdx,
                                DOWNSIZE_FALLBACKS,
                                (fw, fh) -> VideoEncoderConfig.isSizeSupportedSafe(videoCaps, fw, fh));
                        if (selection != null && capture.setMaxSize(selection.getMaxSize())) {
                            currentMaxSide = selection.getMaxSize();
                            fallbackIdx = selection.getNextIndex();
                            ServerLog.w("Pre-selecting supported fallback size <= " + currentMaxSide
                                    + " (" + selection.getWidth() + "x" + selection.getHeight() + ")");
                            capture.prepare();
                            continue;
                        }
                    }
                }

                VideoEncoderConfig.ResolvedConfig resolvedConfig = VideoEncoderConfig.resolveForCodec(
                        caps, width, height, maxFps, bitRate, isWifiTransport);
                if (resolvedConfig.isFrameRateClamped()) {
                    ServerLog.w("Clamped video frame rate for " + safeCodecName(codec, mime) + " (" + activeSize
                            + ") from " + resolvedConfig.getTargetFps() + " to "
                            + resolvedConfig.getEffectiveFps() + " fps");
                }
                if (resolvedConfig.isBitRateClamped()) {
                    ServerLog.w("Clamped video bitrate for " + safeCodecName(codec, mime) + " (" + activeSize
                            + ") from " + bitRate + " to " + resolvedConfig.getClampedBitRateBps()
                            + " bps (supported=[" + resolvedConfig.getMinSupportedBitRateBps() + ", "
                            + resolvedConfig.getMaxSupportedBitRateBps() + "])");
                }

                VideoEncoderConfig.ResolvedConfig activeConfig = resolvedConfig;
                MediaFormat format = buildVideoFormat(mime, activeConfig, width, height);
                try {
                    codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                    surface = codec.createInputSurface();
                    codec.start();
                } catch (IllegalStateException | IllegalArgumentException enhancedError) {
                    if (!activeConfig.hasLowLatencyEnhancements()) {
                        throw enhancedError;
                    }
                    ServerLog.w("Low-latency enhanced format rejected for " + mime + " at " + activeSize
                            + ", retrying with base safe format: " + enhancedError.getMessage());
                    if (surface != null) {
                        surface.release();
                        surface = null;
                    }
                    codec = resetOrRecreateCodec(codec, mime, encoderName);
                    activeConfig = activeConfig.withoutEnhancements();
                    MediaFormat baseFormat = buildVideoFormat(mime, activeConfig, width, height);
                    codec.configure(baseFormat, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                    surface = codec.createInputSurface();
                    codec.start();
                }

                codecStarted = true;
                if (stopped) {
                    return false;
                }
                capture.start(surface);
                captureStarted = true;
                setActiveCodec(codec);
                reset.setActiveCodec(codec);
                if (!headerSent) {
                    // Report the negotiated codec and size only once the encoder can drive the capture surface.
                    writer.writeVideoHeader(codecId, activeSize.getWidth(), activeSize.getHeight());
                    headerSent = true;
                    ServerLog.i("Texture: " + activeSize.getWidth() + "x" + activeSize.getHeight());
                }
                requestSyncFrame();
                if (bitrateHook != null) {
                    final MediaCodec sessionCodec = codec;
                    bitrateHook.onSessionStarted(
                            activeConfig.getClampedBitRateBps(),
                            activeConfig.getMinSupportedBitRateBps(),
                            activeConfig.getMaxSupportedBitRateBps(),
                            activeConfig.getEffectiveFps(),
                            bps -> applyRuntimeBitrate(sessionCodec, bps));
                }

                boolean eof = drainOutputs(codec);
                return eof && reset.consumeReset() && !capture.isClosed() && !stopped;
            } catch (IllegalStateException | IllegalArgumentException | IOException error) {
                if (stopped) {
                    return false;
                }
                if (!codecStarted && !headerSent && codecId == PacketWriter.CODEC_ID_H265) {
                    ServerLog.w("HEVC encoder unavailable at " + activeSize + ", switching to H.264: " + error.getMessage());
                    codecId = PacketWriter.CODEC_ID_H264;
                    encoderName = null;
                    mime = MediaFormat.MIMETYPE_VIDEO_AVC;
                    continue;
                }
                if (error instanceof IOException) {
                    throw (IOException) error;
                }
                if (codec == null) {
                    throw error;
                }
                if (!downsizeOnError) {
                    throw new IOException("Video encoder configuration failed: " + error.getMessage(), error);
                }
                Integer nextLimit = pickSmallerMaxSize(currentMaxSide, fallbackIdx++);
                if (nextLimit == null || !capture.setMaxSize(nextLimit)) {
                    throw new IOException("Video encoder failed even after downsize attempts", error);
                }
                currentMaxSide = nextLimit;
                ServerLog.w("Encoder error at size " + activeSize + ", downsizing to <= " + nextLimit + ": " + error.getMessage());
                capture.prepare();
            } finally {
                clearActiveCodec(codec);
                reset.setActiveCodec(null);
                if (captureStarted) {
                    capture.stop();
                }
                if (codecStarted) {
                    try {
                        codec.stop();
                    } catch (Throwable ignored) {
                    }
                }
                if (codec != null) {
                    try {
                        codec.release();
                    } catch (Throwable ignored) {
                    }
                }
                if (surface != null) {
                    surface.release();
                }
            }
        }
        return false;
    }

    private boolean drainOutputs(MediaCodec codec) throws IOException {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        VideoEncoderConfig.StreamClockTracker clockTracker =
                bitrateHook != null ? new VideoEncoderConfig.StreamClockTracker() : null;
        while (!stopped) {
            int index = codec.dequeueOutputBuffer(info, -1);
            if (index < 0) {
                continue;
            }
            boolean eos = (info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
            try {
                if (info.size > 0) {
                    ByteBuffer out = codec.getOutputBuffer(index);
                    if (out != null) {
                        out.position(info.offset);
                        out.limit(info.offset + info.size);
                        if (bitrateHook != null) {
                            int packetBytes = info.size;
                            boolean isConfig = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                            boolean isKeyFrame = (info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0;
                            long writeStartNs = System.nanoTime();
                            writer.writePacket(out, info);
                            long nowNs = System.nanoTime();
                            long writeDurationNs = Math.max(0L, nowNs - writeStartNs);
                            long frameAgeUs = clockTracker.updateAndComputeFrameAgeUs(
                                    info.presentationTimeUs, writeStartNs, nowNs, isConfig);
                            bitrateHook.onPacketWritten(
                                    packetBytes, writeDurationNs, frameAgeUs, isKeyFrame, isConfig, nowNs);
                        } else {
                            writer.writePacket(out, info);
                        }
                    }
                }
            } finally {
                codec.releaseOutputBuffer(index, false);
            }
            if (eos) {
                return true;
            }
        }
        return false;
    }

    private MediaFormat buildVideoFormat(
            String mime,
            VideoEncoderConfig.ResolvedConfig config,
            int width,
            int height) {
        return VideoEncoderConfig.buildMediaFormat(config, mime, width, height, codecOptions);
    }

    private void setActiveCodec(MediaCodec codec) {
        synchronized (codecLock) {
            this.activeCodec = codec;
            this.syncRequestGate.reset();
        }
    }

    private void clearActiveCodec(MediaCodec codec) {
        synchronized (codecLock) {
            if (this.activeCodec == codec) {
                this.activeCodec = null;
            }
        }
    }

    private boolean applyRuntimeBitrate(MediaCodec codec, int targetBitRateBps) {
        if (codec == null || targetBitRateBps <= 0) {
            return false;
        }
        synchronized (codecLock) {
            if (stopped || activeCodec != codec) {
                return false;
            }
            Bundle params = new Bundle();
            params.putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, targetBitRateBps);
            try {
                codec.setParameters(params);
                return true;
            } catch (IllegalStateException | IllegalArgumentException | UnsupportedOperationException error) {
                ServerLog.w("Failed to apply runtime video bitrate " + targetBitRateBps + " bps", error);
                return false;
            }
        }
    }

    private static boolean sendSyncFrameParameter(MediaCodec codec) {
        Bundle params = new Bundle();
        params.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
        try {
            codec.setParameters(params);
            return true;
        } catch (IllegalStateException | IllegalArgumentException | UnsupportedOperationException error) {
            ServerLog.w("Failed to request sync frame", error);
            return false;
        }
    }

    private static MediaCodec resetOrRecreateCodec(
            MediaCodec codec,
            String mime,
            String requestedName) throws IOException {
        if (codec != null) {
            try {
                codec.reset();
                return codec;
            } catch (Throwable ignored) {
                try {
                    codec.release();
                } catch (Throwable ignoredRelease) {
                }
            }
        }
        return MediaCodecParams.createEncoder(mime, requestedName);
    }

    private static String safeCodecName(MediaCodec codec, String fallbackMime) {
        if (codec == null) {
            return fallbackMime;
        }
        try {
            return codec.getName();
        } catch (Throwable ignored) {
            return fallbackMime;
        }
    }

    private static Integer pickSmallerMaxSize(int currentMax, int startIndex) {
        for (int i = startIndex; i < DOWNSIZE_FALLBACKS.length; ++i) {
            if (DOWNSIZE_FALLBACKS[i] < currentMax) {
                return DOWNSIZE_FALLBACKS[i];
            }
        }
        return null;
    }
}
