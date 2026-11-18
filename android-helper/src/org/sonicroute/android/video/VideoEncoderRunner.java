// SPDX-License-Identifier: GPL-3.0-only
package org.sonicroute.android.video;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.os.Build;
import android.view.Surface;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.List;
import org.sonicroute.android.core.MediaCodecParams;
import org.sonicroute.android.io.PacketWriter;
import org.sonicroute.android.model.Geometry.Size;
import org.sonicroute.android.util.ServerLog;
import org.sonicroute.android.video.VideoCaptureSource.ResetCoordinator;

/**
 * Surface encoder loop for H.264 and HEVC with dynamic resize and downsize-on-error fallback.
 * Reads encoded NALUs via blocking {@code MediaCodec.dequeueOutputBuffer(-1)}.
 */
public final class VideoEncoderRunner {
    private static final String KEY_MAX_FPS_TO_ENCODER = "max-fps-to-encoder";
    private static final int DEFAULT_I_FRAME_INTERVAL_SEC = 10;
    private static final long REPEAT_PREVIOUS_FRAME_AFTER_US = 100_000L;
    private static final int[] DOWNSIZE_FALLBACKS = { 2560, 1920, 1600, 1280, 1024, 800 };

    private final VideoCaptureSource capture;
    private final PacketWriter writer;
    private final int bitRate;
    private final float maxFps;
    private final List<MediaCodecParams.Entry> codecOptions;
    private final boolean downsizeOnError;
    private final Runnable onFinish;

    private volatile boolean stopped;
    private int codecId;
    private String encoderName;
    private boolean headerSent;
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
        this.capture = capture;
        this.writer = writer;
        this.codecId = codecId;
        this.bitRate = bitRate;
        this.maxFps = maxFps;
        this.encoderName = explicitEncoder;
        this.codecOptions = codecOptions;
        this.downsizeOnError = downsizeOnError;
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
        String mime = codecId == PacketWriter.CODEC_ID_H265 ? MediaFormat.MIMETYPE_VIDEO_HEVC : MediaFormat.MIMETYPE_VIDEO_AVC;
        MediaFormat format = buildVideoFormat(mime);
        int currentMaxSide = videoSize.getMaxSide();
        int fallbackIdx = 0;

        while (!stopped) {
            MediaCodec codec = null;
            Surface surface = null;
            boolean codecStarted = false;
            boolean captureStarted = false;
            Size activeSize = capture.getVideoSize();
            try {
                codec = MediaCodecParams.createEncoder(mime, encoderName);
                format.setInteger(MediaFormat.KEY_WIDTH, activeSize.getWidth());
                format.setInteger(MediaFormat.KEY_HEIGHT, activeSize.getHeight());

                codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                surface = codec.createInputSurface();
                codec.start();
                codecStarted = true;
                if (stopped) {
                    return false;
                }
                capture.start(surface);
                captureStarted = true;
                reset.setActiveCodec(codec);
                if (!headerSent) {
                    // Report the negotiated codec and size only once the encoder can drive the capture surface.
                    writer.writeVideoHeader(codecId, activeSize.getWidth(), activeSize.getHeight());
                    headerSent = true;
                    ServerLog.i("Texture: " + activeSize.getWidth() + "x" + activeSize.getHeight());
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
                    format = buildVideoFormat(mime);
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
                        writer.writePacket(out, info);
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

    private MediaFormat buildVideoFormat(String mime) {
        MediaFormat format = new MediaFormat();
        format.setString(MediaFormat.KEY_MIME, mime);
        if (MediaFormat.MIMETYPE_VIDEO_HEVC.equals(mime)) {
            format.setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.HEVCProfileMain);
        }
        format.setInteger(MediaFormat.KEY_BIT_RATE, bitRate);
        format.setInteger(MediaFormat.KEY_FRAME_RATE, 60);
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        if (Build.VERSION.SDK_INT >= 24) {
            format.setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED);
        }
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, DEFAULT_I_FRAME_INTERVAL_SEC);
        format.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, REPEAT_PREVIOUS_FRAME_AFTER_US);
        if (maxFps > 0) {
            format.setFloat(KEY_MAX_FPS_TO_ENCODER, maxFps);
        }
        MediaCodecParams.apply(format, codecOptions);
        return format;
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
