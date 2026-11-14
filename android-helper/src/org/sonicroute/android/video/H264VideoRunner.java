// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
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
 * Hardware-surface H.264 encoder loop supporting dynamic resize resets and downsize-on-error fallback.
 * Reads encoded NALUs via blocking {@code MediaCodec.dequeueOutputBuffer(-1)}.
 */
public final class H264VideoRunner {
    private static final String MIME_AVC = MediaFormat.MIMETYPE_VIDEO_AVC;
    private static final String KEY_MAX_FPS_TO_ENCODER = "max-fps-to-encoder";
    private static final int DEFAULT_I_FRAME_INTERVAL_SEC = 10;
    private static final long REPEAT_PREVIOUS_FRAME_AFTER_US = 100_000L;
    private static final int[] DOWNSIZE_FALLBACKS = { 2560, 1920, 1600, 1280, 1024, 800 };

    private final VideoCaptureSource capture;
    private final PacketWriter writer;
    private final int bitRate;
    private final float maxFps;
    private final String explicitEncoder;
    private final List<MediaCodecParams.Entry> codecOptions;
    private final boolean downsizeOnError;
    private final Runnable onFinish;

    private volatile boolean stopped;
    private Thread thread;

    public H264VideoRunner(
            VideoCaptureSource capture,
            PacketWriter writer,
            int bitRate,
            float maxFps,
            String explicitEncoder,
            List<MediaCodecParams.Entry> codecOptions,
            boolean downsizeOnError,
            Runnable onFinish) {
        this.capture = capture;
        this.writer = writer;
        this.bitRate = bitRate;
        this.maxFps = maxFps;
        this.explicitEncoder = explicitEncoder;
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
        boolean headerSent = false;
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
                if (!headerSent) {
                    writer.writeVideoHeader(videoSize.getWidth(), videoSize.getHeight());
                    headerSent = true;
                    ServerLog.i("Texture: " + videoSize.getWidth() + "x" + videoSize.getHeight());
                }

                alive = encodeSingleSession(videoSize, reset);
            } while (alive && !stopped);
        } finally {
            capture.release();
        }
    }

    private boolean encodeSingleSession(Size videoSize, ResetCoordinator reset) throws IOException {
        MediaFormat format = buildVideoFormat();
        int currentMaxSide = videoSize.getMaxSide();
        int fallbackIdx = 0;

        while (!stopped) {
            MediaCodec codec = MediaCodecParams.createEncoder(MIME_AVC, explicitEncoder);
            Surface surface = null;
            boolean codecStarted = false;
            boolean captureStarted = false;
            Size activeSize = capture.getVideoSize();
            try {
                format.setInteger(MediaFormat.KEY_WIDTH, activeSize.getWidth());
                format.setInteger(MediaFormat.KEY_HEIGHT, activeSize.getHeight());

                codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                surface = codec.createInputSurface();
                capture.start(surface);
                captureStarted = true;
                codec.start();
                codecStarted = true;
                reset.setActiveCodec(codec);
                if (stopped) {
                    return false;
                }

                boolean eof = drainOutputs(codec);
                return eof && reset.consumeReset() && !capture.isClosed() && !stopped;
            } catch (IllegalStateException | IllegalArgumentException error) {
                if (stopped) {
                    return false;
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
                try {
                    codec.release();
                } catch (Throwable ignored) {
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

    private MediaFormat buildVideoFormat() {
        MediaFormat format = new MediaFormat();
        format.setString(MediaFormat.KEY_MIME, MIME_AVC);
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
